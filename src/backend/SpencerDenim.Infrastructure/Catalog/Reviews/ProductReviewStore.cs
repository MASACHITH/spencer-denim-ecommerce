using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Catalog.Reviews;
using SpencerDenim.Application.Content.Admin;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Catalog.Reviews;

/// <summary>
/// The review writes (PDP-08, ADM-16; doc 11 P12; issue #189): a customer's create and edit, a moderator's approve
/// and hide, and the PDPA erasure — each one transaction that ends with the product's rating recomputed.
/// <para>
/// <b>Every write follows <see cref="ProductRatingSql"/>'s protocol</b>: the product's stats row is taken with
/// <c>UPDLOCK</c> before any review row, so two writers of one product's reviews run one after the other and the
/// recompute counts straight from the rows. That same lock is what makes "one review per customer per product"
/// a decision rather than a race: a second tab's create waits, then finds the first tab's review and answers
/// <see cref="ReviewWriteOutcome.AlreadyReviewed"/>. <c>UX_ProductReviews_Product_Customer</c> is the backstop.
/// </para>
/// <para>
/// <b>Moderation is a conditional <c>UPDATE</c> on the rowversion the moderator saw and on the statuses the action
/// may move from</b> — never a read and then a write (doc 08 §5.2). A moderator who loaded the review before
/// somebody else moderated it, or before its author edited it, gets <see cref="ReviewModerationOutcome.Stale"/>
/// with the review as it now is, and nothing is written.
/// </para>
/// </summary>
internal sealed partial class ProductReviewStore(
    AppDbContext dbContext,
    IAuditLogWriter auditLog,
    ILogger<ProductReviewStore> logger)
    : IProductReviewStore, IProductReviewErasure
{
    /// <summary>The audit row's <c>EntityType</c> for a moderation.</summary>
    public const string ReviewEntityType = "ProductReview";

    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;

    public Task<ReviewWriteOutcome> CreateAsync(ReviewWrite write, CancellationToken cancellationToken) =>
        WriteAsync(write, isEdit: false, cancellationToken);

    public Task<ReviewWriteOutcome> EditAsync(ReviewWrite write, CancellationToken cancellationToken) =>
        WriteAsync(write, isEdit: true, cancellationToken);

    public Task<ReviewModerationOutcome> ModerateAsync(ReviewModerationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return InTransactionAsync(async () =>
        {
            // The product a review belongs to never changes, so reading it without a lock is safe; the join to a
            // non-deleted product is repeated in the UPDATE below so the refusal and the write cannot disagree.
            var productId = await ScalarAsync<int?>(
                """
                SELECT review.ProductId
                  FROM catalog.ProductReviews AS review
                  JOIN catalog.Products AS product ON product.Id = review.ProductId
                 WHERE review.Id = @reviewId
                   AND product.IsDeleted = 0;
                """,
                cancellationToken,
                ("@reviewId", request.ReviewId));

            if (productId is not { } id)
            {
                return (Commit: false, Result: (ReviewModerationOutcome)new ReviewModerationOutcome.NotFound());
            }

            await ScalarAsync<int>(ProductRatingSql.LockOne, cancellationToken, ("@productId", id));

            var allowed = ReviewRules.AllowedFrom(request.Action);
            var target = ReviewRules.TargetOf(request.Action);

            var before = await ScalarAsync<byte?>(
                """
                UPDATE review
                   SET Status = @target,
                       ModeratedByUserId = @moderatorId,
                       ModeratedAtUtc = @now,
                       ModerationNote = @note
                OUTPUT deleted.Status
                  FROM catalog.ProductReviews AS review
                  JOIN catalog.Products AS product ON product.Id = review.ProductId
                 WHERE review.Id = @reviewId
                   AND review.RowVersion = @rowVersion
                   AND review.Status IN (@from1, @from2)
                   AND product.IsDeleted = 0;
                """,
                cancellationToken,
                ("@target", (byte)target),
                ("@moderatorId", (object?)request.Actor.UserId ?? DBNull.Value),
                ("@now", request.NowUtc),
                ("@note", (object?)request.Note ?? DBNull.Value),
                ("@reviewId", request.ReviewId),
                ("@rowVersion", request.ExpectedRowVersion),
                ("@from1", (byte)allowed[0]),
                ("@from2", (byte)allowed[1]));

            if (before is not { } previous)
            {
                var current = await ProductReviewReader.ReadAdminAsync(dbContext, request.ReviewId, cancellationToken);

                ReviewModerationOutcome refusal = current is null
                    ? new ReviewModerationOutcome.NotFound()
                    : current.RowVersion != Convert.ToBase64String(request.ExpectedRowVersion)
                        ? new ReviewModerationOutcome.Stale(current)
                        : new ReviewModerationOutcome.Unchanged(current);

                return (Commit: false, Result: refusal);
            }

            await RecomputeAsync(id, cancellationToken);

            // Status only. The note and the review's own text are personal data the PDPA erasure deletes with the
            // review; an audit row is not in doc 08 §3.1 step 2's list, so it must not carry a copy.
            auditLog.Enlist(ContentAuditTrail.Row(
                request.Action == ReviewModeration.Approve ? AuditActions.Approve : AuditActions.StatusChange,
                ReviewEntityType,
                request.ReviewId.ToString(CultureInfo.InvariantCulture),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["status"] = ((ReviewStatus)previous).ToString(),
                },
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["status"] = target.ToString(),
                    ["productId"] = id,
                    ["noteGiven"] = request.Note is not null,
                },
                request.Actor,
                request.NowUtc));

            await dbContext.SaveChangesAsync(cancellationToken);

            var saved = await ProductReviewReader.ReadAdminAsync(dbContext, request.ReviewId, cancellationToken)
                ?? throw new InvalidOperationException("A review moderated in this transaction could not be read back.");

            return (Commit: true, Result: (ReviewModerationOutcome)new ReviewModerationOutcome.Applied(saved));
        }, cancellationToken);
    }

    public async Task<int> EraseForCustomerAsync(int customerUserId, CancellationToken cancellationToken)
    {
        // Lock first, then delete, then recompute what was actually deleted (tester #189 note): the products are
        // read from the deleted rows themselves (OUTPUT), so a review this account created between any earlier read
        // and the DELETE cannot be removed without its product being recomputed. The first statement takes the stats
        // rows of every product the account has reviewed, in one statement, before any review row is touched — the
        // lock order every other review writer uses. The second lock statement only matters for that gap: a product
        // first reviewed after the first statement ran, whose writer has committed (the DELETE waited for it).
        const string sql =
            """
            DECLARE @locked int = (
                SELECT COUNT(*)
                  FROM catalog.ProductStats WITH (UPDLOCK, ROWLOCK)
                 WHERE ProductId IN (
                       SELECT ProductId FROM catalog.ProductReviews WITH (READCOMMITTEDLOCK)
                        WHERE CustomerUserId = @customerUserId));

            DECLARE @deleted TABLE (ProductId int NOT NULL);

            DELETE FROM catalog.ProductReviews
            OUTPUT deleted.ProductId INTO @deleted (ProductId)
             WHERE CustomerUserId = @customerUserId;

            DECLARE @affected TABLE (ProductId int NOT NULL PRIMARY KEY);
            INSERT INTO @affected (ProductId) SELECT DISTINCT ProductId FROM @deleted;

            SET @locked = (
                SELECT COUNT(*)
                  FROM catalog.ProductStats WITH (UPDLOCK, ROWLOCK)
                 WHERE ProductId IN (SELECT ProductId FROM @affected));
            """
            + ProductRatingSql.RecomputeWhere
            + """
             stats.ProductId IN (SELECT ProductId FROM @affected);

            SELECT COUNT(*) FROM @deleted;
            """;

        if (dbContext.Database.CurrentTransaction is not null)
        {
            // Joining the caller's transaction (the PDPA routine's one transaction, doc 08 §3.1).
            return await ScalarAsync<int>(sql, cancellationToken, ("@customerUserId", customerUserId));
        }

        return await InTransactionAsync(
            async () => (Commit: true, Result: await ScalarAsync<int>(sql, cancellationToken, ("@customerUserId", customerUserId))),
            cancellationToken);
    }

    private Task<ReviewWriteOutcome> WriteAsync(ReviewWrite write, bool isEdit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);

        return InTransactionAsync(async () =>
        {
            var customer = await dbContext.Users
                .AsNoTracking()
                .Where(user => user.Id == write.CustomerUserId
                    && user.UserType == UserType.Customer
                    && user.IsActive
                    && user.AnonymisedAtUtc == null)
                .Select(user => new { user.FirstName, user.LastName })
                .FirstOrDefaultAsync(cancellationToken);

            if (customer is null)
            {
                return (Commit: false, Result: (ReviewWriteOutcome)new ReviewWriteOutcome.NotACustomer());
            }

            var visible = await dbContext.Products
                .AsNoTracking()
                .Where(CatalogVisibility.VisibleProduct)
                .AnyAsync(product => product.Id == write.ProductId, cancellationToken);

            if (!visible)
            {
                return (Commit: false, Result: (ReviewWriteOutcome)new ReviewWriteOutcome.ProductNotFound());
            }

            await ScalarAsync<int>(ProductRatingSql.LockOne, cancellationToken, ("@productId", write.ProductId));

            var existing = await ProductReviewReader.ReadMineAsync(
                dbContext,
                write.CustomerUserId,
                write.ProductId,
                cancellationToken);

            if (!isEdit && existing is not null)
            {
                return (Commit: false, Result: (ReviewWriteOutcome)new ReviewWriteOutcome.AlreadyReviewed(existing));
            }

            if (isEdit && existing is null)
            {
                return (Commit: false, Result: (ReviewWriteOutcome)new ReviewWriteOutcome.NotReviewed());
            }

            var orderId = await ProductReviewReader.QualifyingOrderAsync(
                dbContext,
                write.CustomerUserId,
                write.ProductId,
                cancellationToken);

            var status = ReviewRules.StatusAfterCustomerWrite(existing?.Status, write.RequireApproval);

            if (existing is null)
            {
                try
                {
                    await ExecuteAsync(
                        """
                        INSERT INTO catalog.ProductReviews
                               (ProductId, CustomerUserId, OrderId, Rating, Title, Body, DisplayName, IsVerifiedBuyer, Status, CreatedAtUtc)
                        VALUES (@productId, @customerUserId, @orderId, @rating, @title, @body, @displayName, @verified, @status, @now);
                        """,
                        cancellationToken,
                        ("@productId", write.ProductId),
                        ("@customerUserId", write.CustomerUserId),
                        ("@orderId", (object?)orderId ?? DBNull.Value),
                        ("@rating", write.Review.Rating),
                        ("@title", (object?)write.Review.Title ?? DBNull.Value),
                        ("@body", (object?)write.Review.Body ?? DBNull.Value),
                        ("@displayName", ReviewRules.DisplayNameFor(customer.FirstName, customer.LastName)),
                        ("@verified", orderId is not null),
                        ("@status", (byte)status),
                        ("@now", write.NowUtc));
                }
                catch (SqlException exception) when (exception.Number is UniqueIndexViolation or UniqueConstraintViolation)
                {
                    // Unreachable while every writer takes the stats lock first; the index decides if one does not.
                    return (Commit: false, Result: (ReviewWriteOutcome)new ReviewWriteOutcome.AlreadyReviewed(
                        await ProductReviewReader.ReadMineAsync(dbContext, write.CustomerUserId, write.ProductId, cancellationToken)
                        ?? throw new InvalidOperationException("A duplicate review was refused but cannot be read.", exception)));
                }
            }
            else
            {
                // The display name stays the submission's snapshot (doc 11 P12). The badge is only ever added: a
                // review written before delivery gains it on an edit after delivery, and keeps it after a refund
                // (doc 11 §7 G-27).
                await ExecuteAsync(
                    """
                    UPDATE catalog.ProductReviews
                       SET Rating = @rating,
                           Title = @title,
                           Body = @body,
                           Status = @status,
                           UpdatedAtUtc = @now,
                           IsVerifiedBuyer = CASE WHEN IsVerifiedBuyer = 1 OR @orderId IS NOT NULL THEN 1 ELSE 0 END,
                           OrderId = COALESCE(OrderId, @orderId)
                     WHERE Id = @reviewId
                       AND CustomerUserId = @customerUserId;
                    """,
                    cancellationToken,
                    ("@rating", write.Review.Rating),
                    ("@title", (object?)write.Review.Title ?? DBNull.Value),
                    ("@body", (object?)write.Review.Body ?? DBNull.Value),
                    ("@status", (byte)status),
                    ("@now", write.NowUtc),
                    ("@orderId", (object?)orderId ?? DBNull.Value),
                    ("@reviewId", existing.Id),
                    ("@customerUserId", write.CustomerUserId));
            }

            await RecomputeAsync(write.ProductId, cancellationToken);

            var saved = await ProductReviewReader.ReadMineAsync(dbContext, write.CustomerUserId, write.ProductId, cancellationToken)
                ?? throw new InvalidOperationException("A review saved in this transaction could not be read back.");

            return (Commit: true, Result: (ReviewWriteOutcome)new ReviewWriteOutcome.Saved(saved));
        }, cancellationToken);
    }

    /// <summary>
    /// One read-committed transaction under the execution strategy; committed only when the body says so, so a
    /// refusal leaves nothing behind (not even the stats lock's no-op).
    /// </summary>
    private Task<T> InTransactionAsync<T>(Func<Task<(bool Commit, T Result)>> body, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();

        var strategy = dbContext.Database.CreateExecutionStrategy();

        return strategy.ExecuteAsync(async () =>
        {
            // Cleared inside the retry too: a replayed attempt must not inherit an audit row a failed one added.
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

            try
            {
                var (commit, result) = await body();

                if (commit)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                else
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                return result;
            }
            finally
            {
                dbContext.ChangeTracker.Clear();
            }
        });
    }

    /// <summary>
    /// Recomputes one product's rating, under the stats-row lock the caller already holds.
    /// </summary>
    /// <remarks>
    /// <b>A missing <c>ProductStats</c> row is logged, not created.</b> The row is 1:1 with its product and written
    /// in the product's own save (doc 08 §7.4), and every column but the rating is pricing: an "upsert" here would
    /// have to invent list and sale prices, and a product whose stats row holds zeros would appear on the
    /// storefront at Rs. 0 (<c>CatalogVisibility</c> treats the row as the product being listable). A customer
    /// cannot reach this path for such a product at all — submissions require a visible product, which requires the
    /// row — so it can only be a moderation of a product whose row went missing by hand, and the honest answer is a
    /// warning naming it, while the review's own status change still commits.
    /// </remarks>
    private async Task RecomputeAsync(int productId, CancellationToken cancellationToken)
    {
        if (await ExecuteAsync(ProductRatingSql.RecomputeOne, cancellationToken, ("@productId", productId)) == 0)
        {
            LogMissingStats(productId);
        }
    }

    [LoggerMessage(
        EventId = 7301,
        Level = LogLevel.Warning,
        Message = "Product {ProductId} has no catalog.ProductStats row, so its rating could not be recomputed after a "
            + "review change. Re-save the product in the admin panel to rebuild the row; the next review change will "
            + "then recompute the rating.")]
    private partial void LogMissingStats(int productId);

    private async Task<int> ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<T> ScalarAsync<T>(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken);

        if (value is null || value is DBNull)
        {
            return default!;
        }

        var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        return (T)Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A command on the context's connection, in its current transaction. Plain ADO.NET because <c>SqlQuery</c>
    /// composes its SQL into a subquery, which an <c>UPDATE … OUTPUT</c> cannot be (<c>InventoryStore</c>'s reason).
    /// </summary>
    private DbCommand Command(string sql, (string Name, object Value)[] parameters)
    {
        var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Review statements run inside a transaction.");

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;

            switch (value)
            {
                // An ADO DateTime defaults to the old datetime type; the columns are datetime2(3).
                case DateTime:
                    parameter.DbType = DbType.DateTime2;
                    break;

                // A rowversion is binary(8); without a size the comparison still works, but say what it is.
                case byte[] bytes:
                    parameter.DbType = DbType.Binary;
                    parameter.Size = bytes.Length;
                    break;

                // Title and body are Unicode: without this a Sinhala or Tamil review would arrive as question marks.
                case string:
                    parameter.DbType = DbType.String;
                    break;
            }

            command.Parameters.Add(parameter);
        }

        return command;
    }
}
