using System.Data;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Media;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Payments;

/// <summary>
/// Doc 11 P7 step 3's writes: the customer's reference and their slip, both on to the open payment row
/// (doc 08 §3.5 N7, DB26).
/// </summary>
internal sealed class BankTransferProofStore(AppDbContext dbContext) : IBankTransferProofStore
{
    public async Task<bool> SetReferenceAsync(
        int paymentId,
        string? reference,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.Payments
               SET PayerReference = {reference},
                   UpdatedAtUtc = {nowUtc}
             WHERE Id = {paymentId}
               AND ProviderCode = {PaymentProviderCodes.BankTransfer}
               AND Status = {(byte)PaymentStatus.Pending};
            """,
            cancellationToken) == 1;

    public async Task<int?> CurrentProofAssetIdAsync(int paymentId, CancellationToken cancellationToken) =>
        await dbContext.Payments
            .AsNoTracking()
            .Where(payment => payment.Id == paymentId)
            .Select(payment => payment.ProofAssetId)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Points the payment at the new slip and soft-deletes the previous one, in one transaction.
    /// </summary>
    /// <remarks>
    /// <b>The order matters and so does the atomicity.</b> If the previous asset were soft-deleted first
    /// and the payment update then failed, the payment would point at a deleted asset and the staff queue
    /// would show no slip for a customer who had uploaded one twice. If the payment were repointed first
    /// and the delete failed, the old asset would stay live for ever with no owner and the media library
    /// could never remove it. One transaction makes both impossible.
    /// <para>
    /// <b>Soft-deleted, not deleted.</b> <c>Payments.ProofAssetId</c> is a real FK with <c>NO ACTION</c>,
    /// so a hard delete of a referenced asset would be refused by the database anyway — but the reason it
    /// is soft is doc 08 §3.3's own: the file follows the payment retention rule (§8.4) and a purge job
    /// (W3-08) removes the bytes later. Until then a replaced slip is still evidence.
    /// </para>
    /// </remarks>
    public async Task<bool> ReplaceProofAsync(
        int paymentId,
        Guid newAssetPublicId,
        int? previousAssetId,
        int? actingUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        // Resolved here, from the token, and narrowed to what a slip may be: private, Ready, not deleted.
        // The Application layer never handles internal media ids \u2014 the same rule the settings' logo
        // follows \u2014 and the narrowing is what stops a payment being pointed at a public library image,
        // which would publish it through a route that promises a private file.
        var asset = await dbContext.MediaAssets
            .AsNoTracking()
            .Where(candidate =>
                candidate.PublicId == newAssetPublicId
                && candidate.Visibility == MediaVisibility.Private
                && candidate.Status == MediaAssetStatus.Ready
                && !candidate.IsDeleted)
            .Select(candidate => (int?)candidate.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (asset is not { } newAssetId)
        {
            return false;
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

            try
            {
                var pointed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE sales.Payments
                       SET ProofAssetId = {newAssetId},
                           UpdatedAtUtc = {nowUtc}
                     WHERE Id = {paymentId}
                       AND ProviderCode = {PaymentProviderCodes.BankTransfer}
                       AND Status = {(byte)PaymentStatus.Pending};
                    """,
                    cancellationToken);

                if (pointed != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }

                if (previousAssetId is { } previous && previous != newAssetId)
                {
                    // **Only when nothing else points at it, and this guard closes a real hole in the
                    // schema rather than a theoretical one.** `UX_MediaAssets_Checksum_Ready` is unique on
                    // (Checksum, MediaType, Visibility) for live Ready rows, so two *identical* private
                    // files cannot both exist: the second upload is answered from the existing asset. If
                    // two orders ever end up sharing one slip asset that way, soft-deleting it here when
                    // one of them replaces their slip would make the other order's slip vanish from the
                    // staff queue — a customer's evidence destroyed by somebody else's upload.
                    //
                    // Identical bytes across two real slips is vanishingly unlikely (a photograph or a
                    // bank's PDF differs every time), and the deduplication is forced by the index, so the
                    // fix is this one read rather than a migration. It is doc 08 §3.3's own delete guard,
                    // asked of the FK that actually exists.
                    var stillReferenced = await dbContext.Payments
                        .AsNoTracking()
                        .AnyAsync(
                            candidate => candidate.ProofAssetId == previous && candidate.Id != paymentId,
                            cancellationToken);

                    if (!stillReferenced)
                    {
                        var asset = await dbContext.MediaAssets
                            .FirstOrDefaultAsync(candidate => candidate.Id == previous, cancellationToken);

                        // SoftDelete rather than an UPDATE, because the entity owns the rule: IsDeleted
                        // has a private setter and the audit interceptor stamps the row on SaveChanges.
                        asset?.SoftDelete(nowUtc, actingUserId);

                        await dbContext.SaveChangesAsync(cancellationToken);
                    }
                }

                await transaction.CommitAsync(cancellationToken);
                return true;
            }
            finally
            {
                dbContext.ChangeTracker.Clear();
            }
        });
    }

    /// <summary>
    /// The open <c>Pending</c> bank payment of a <c>PendingPayment</c> order, created if the previous
    /// attempt was rejected (doc 08 §3.5 N7).
    /// </summary>
    /// <remarks>
    /// <b>The unique-index violation is part of the design here, not an error path.</b>
    /// <c>UX_Payments_OneOpen</c> is unique on <c>OrderId</c> where <c>Status IN (0,1)</c>. Two concurrent
    /// resubmissions after a rejection — a double tap, two tabs, a retried request — would both find no
    /// open row and both insert; the index refuses the second, and the right answer is not an error but
    /// "somebody else created it, use theirs". So the violation is caught by the same
    /// <c>SqlUniqueViolation</c> predicate the rest of the sales code uses, and the loser re-reads.
    /// <para>
    /// <b>The amount comes from the order, not from the request.</b> <c>sales.Payments.Amount</c> is doc 08
    /// §3.5's "LKR = GrandTotal", and a resubmission must ask for the same sum the shopper agreed to
    /// (G-10) — never a figure a client could supply.
    /// </para>
    /// <para>
    /// <b>Why this exists at all, rather than the upload simply refusing after a rejection.</b> Doc 11 P7
    /// step 6b is explicit that after a rejection "the customer may transfer again or upload a new slip,
    /// which creates a new Pending payment record", and the order stays payable until its deadline. Without
    /// this method a rejected order would be unpayable for the rest of its 48 hours while still saying
    /// "awaiting payment", which is the worst of both answers.
    /// </para>
    /// </remarks>
    public async Task<int?> OpenPaymentAsync(
        Guid orderPublicId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            // READCOMMITTEDLOCK: the database runs READ_COMMITTED_SNAPSHOT (#163), where a plain read would
            // take the order's last committed status instead of waiting for an expiry, cancellation or
            // approval that holds the row, and a payment would be opened beside an order that is closing.
            // The hint keeps this decision waiting for that writer, as it did before the option was on.
            var order = await dbContext.Orders
                .FromSql($"SELECT * FROM sales.Orders WITH (READCOMMITTEDLOCK) WHERE PublicId = {orderPublicId}")
                .AsNoTracking()
                .Where(candidate =>
                    candidate.PublicId == orderPublicId
                    && candidate.PaymentProviderCode == PaymentProviderCodes.BankTransfer
                    && candidate.Status == OrderStatus.PendingPayment)
                .Select(candidate => new { candidate.Id, candidate.GrandTotal })
                .FirstOrDefaultAsync(cancellationToken);

            if (order is null)
            {
                return null;
            }

            var existing = await dbContext.Payments
                .AsNoTracking()
                .Where(payment =>
                    payment.OrderId == order.Id
                    && payment.ProviderCode == PaymentProviderCodes.BankTransfer
                    && payment.Status == PaymentStatus.Pending)
                .Select(payment => (int?)payment.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing is { } open)
            {
                return open;
            }

            var created = new Payment
            {
                OrderId = order.Id,
                ProviderCode = PaymentProviderCodes.BankTransfer,
                Status = PaymentStatus.Pending,
                IsOrderSettlement = false,
                Amount = order.GrandTotal,
                Currency = SalesCurrency.Lkr,
                CreatedAtUtc = nowUtc,
            };

            dbContext.Payments.Add(created);

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return created.Id;
            }
            catch (DbUpdateException exception) when (SqlUniqueViolation.Matches(exception))
            {
                // UX_Payments_OneOpen: somebody else opened the row between the read and the insert. The
                // next pass finds theirs. Two attempts is enough — the loser's second read cannot fail the
                // same way, because the winner's row is committed by the time the index refused us.
            }
            finally
            {
                dbContext.ChangeTracker.Clear();
            }
        }

        // The order's public id is deliberately not in this message: it is the order page's only credential
        // (issue #201), and the exception handler writes this text to the log at Error. The request's own
        // trace identifier is on the log line, and the payment row that refused the insert is in the database.
        throw new InvalidOperationException(
            "Could not open a bank payment after two attempts: UX_Payments_OneOpen refused both inserts.");
    }
}
