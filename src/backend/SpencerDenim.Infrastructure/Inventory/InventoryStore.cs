using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Content.Admin;
using SpencerDenim.Application.Inventory.Abstractions;
using SpencerDenim.Application.Inventory.Contracts;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Inventory;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Inventory;

/// <summary>
/// The inventory screen's writes (ADM-12, doc 11 P2 part A, doc 08 §5.8), and the first writer of
/// <c>inventory.InventoryMovements</c> (#108).
/// <para>
/// <b>Every stock change is one conditional <c>UPDATE</c> and its row count, never a read and then a
/// write</b> (doc 08 §5.2). The condition is evaluated under the row's own update lock, so an order that
/// reserves the same units a millisecond earlier is seen, and a lost update is impossible. Under RCSI (#183)
/// a plain <c>SELECT</c> would read the last committed version without waiting, which is exactly why nothing
/// here decides anything from one: the only reads are the diagnosis after a 0-row update, and that read
/// takes <c>READCOMMITTEDLOCK</c> so the figures it quotes are not stale either.
/// </para>
/// <para>
/// <b>The two shapes, and why both.</b>
/// <list type="bullet">
/// <item><description>
/// <b>Receive / Adjust by a delta</b>: <c>SET OnHand = OnHand + @d WHERE OnHand + @d &gt;= Reserved</c>.
/// A delta commutes, so two staff members adjusting the same variant at once both apply and neither is
/// lost: +5 and −2 from 10 is 13 whichever lands first. No figure from the screen is needed.
/// </description></item>
/// <item><description>
/// <b>Set count</b>: <c>SET OnHand = @new WHERE OnHand = @expected AND @new &gt;= Reserved</c>. An absolute
/// figure does not commute — it would silently erase a delivery booked while the stock-take was being
/// typed — so it applies only while on hand is still the figure the screen showed, and otherwise answers
/// <c>Changed</c> and asks the person to re-confirm (doc 08 §5.8, doc 11 P2 step 3).
/// </description></item>
/// </list>
/// </para>
/// <para>
/// <b>A variant of a removed (soft-deleted) product cannot be changed</b>: every statement joins to the
/// product and requires <c>IsDeleted = 0</c> in the same conditional statement, so the refusal and the
/// write cannot disagree (tester #190, D3). Before this, a threshold change committed and audited and then
/// answered 404, because the read-back filtered the product the write had not.
/// </para>
/// <para>
/// <b>The reserved floor.</b> Both shapes refuse to leave <c>OnHand</c> below <c>Reserved</c>, i.e. they
/// never take <c>Available</c> below 0 and never take away units an unpaid order is holding (doc 11 P2's
/// "12 units are reserved for unpaid orders"). <c>CK_StockLevels_ReservedLeOnHand</c> is the backstop that
/// would make a wrong predicate fail at commit rather than oversell.
/// </para>
/// <para>
/// <b>Bulk is per row, in one transaction.</b> Rows are applied in ascending <c>VariantId</c> — doc 08
/// §5.3's lock order, shared with placement, release and settlement, so this cannot deadlock against
/// them — and a row whose condition fails is reported and changes nothing while the others apply (doc 08
/// §5.8, doc 11 P2 step 7). One transaction rather than one per row so the batch's movements, audit rows
/// and figures commit together; a deadlock retry replays the whole batch from a clean slate, which is safe
/// because every statement is conditional.
/// </para>
/// </summary>
internal sealed class InventoryStore(AppDbContext dbContext, IAuditLogWriter auditLog) : IInventoryStore
{
    /// <summary>The audit row's <c>EntityType</c> for a stock change or a threshold change.</summary>
    public const string StockEntityType = "StockLevel";

    public Task<StockChangeResultDto> ApplyAsync(StockChangeBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        dbContext.ChangeTracker.Clear();

        var strategy = dbContext.Database.CreateExecutionStrategy();

        return strategy.ExecuteAsync(async () =>
        {
            // Cleared inside the retry as well: a replayed attempt must not inherit the movement and audit
            // rows a failed one added, or they would be written twice.
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

            try
            {
                var results = new Dictionary<int, StockChangeRowResult>();

                foreach (var request in batch.Rows.OrderBy(row => row.VariantId))
                {
                    results[request.VariantId] = await ApplyOneAsync(request, batch, cancellationToken);
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                var rows = batch.Rows.Select(row => results[row.VariantId]).ToList();
                var applied = rows.Count(row => row.Outcome == nameof(StockChangeOutcome.Applied));

                return new StockChangeResultDto(batch.BatchId, applied, rows.Count - applied, rows);
            }
            finally
            {
                dbContext.ChangeTracker.Clear();
            }
        });
    }

    public Task<ThresholdChangeOutcome> SetThresholdAsync(
        int variantId,
        int? threshold,
        AdminActor actor,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        dbContext.ChangeTracker.Clear();

        var strategy = dbContext.Database.CreateExecutionStrategy();

        return strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

            try
            {
                // One statement, and OUTPUT gives the value it replaced, so the audit row's "before" is the
                // row's and not a figure read beforehand. UpdatedAtUtc is the stock figures' timestamp and a
                // threshold is not a figure, so it is left alone.
                var change = await ReadPairAsync(
                    """
                    UPDATE stock
                       SET LowStockThreshold = @threshold
                    OUTPUT deleted.LowStockThreshold, inserted.LowStockThreshold
                      FROM inventory.StockLevels AS stock
                      JOIN catalog.ProductVariants AS variant ON variant.Id = stock.VariantId
                      JOIN catalog.Products AS product ON product.Id = variant.ProductId
                     WHERE stock.VariantId = @variantId
                       AND product.IsDeleted = 0;
                    """,
                    cancellationToken,
                    ("@threshold", (object?)threshold ?? DBNull.Value),
                    ("@variantId", variantId));

                if (change is not var (before, after))
                {
                    return ThresholdChangeOutcome.NotFound;
                }

                if (before == after)
                {
                    // Nothing moved, so nothing is audited; the rollback undoes a write of the same value.
                    await transaction.RollbackAsync(cancellationToken);
                    return ThresholdChangeOutcome.Unchanged;
                }

                auditLog.Enlist(ContentAuditTrail.Row(
                    AuditActions.Update,
                    StockEntityType,
                    variantId.ToString(CultureInfo.InvariantCulture),
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["lowStockThreshold"] = before },
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["lowStockThreshold"] = after },
                    actor,
                    nowUtc));

                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return ThresholdChangeOutcome.Applied;
            }
            finally
            {
                dbContext.ChangeTracker.Clear();
            }
        });
    }

    private async Task<StockChangeRowResult> ApplyOneAsync(
        StockChangeRequest request,
        StockChangeBatch batch,
        CancellationToken cancellationToken)
    {
        var variantId = request.VariantId;
        int onHandDelta;
        (int OnHand, int Reserved)? after;

        if (request.Kind == StockChangeKind.SetCount)
        {
            var expected = request.ExpectedOnHand
                ?? throw new InvalidOperationException("A SetCount reached the store without its expected figure.");

            onHandDelta = request.Quantity - expected;

            after = await ReadFiguresAsync(
                """
                UPDATE stock
                   SET OnHand = @newOnHand, UpdatedAtUtc = @now
                OUTPUT inserted.OnHand, inserted.Reserved
                  FROM inventory.StockLevels AS stock
                  JOIN catalog.ProductVariants AS variant ON variant.Id = stock.VariantId
                  JOIN catalog.Products AS product ON product.Id = variant.ProductId
                 WHERE stock.VariantId = @variantId
                   AND product.IsDeleted = 0
                   AND stock.OnHand = @expectedOnHand
                   AND @newOnHand >= stock.Reserved;
                """,
                cancellationToken,
                ("@newOnHand", request.Quantity),
                ("@expectedOnHand", expected),
                ("@now", batch.NowUtc),
                ("@variantId", variantId));
        }
        else
        {
            onHandDelta = request.Quantity;

            // bigint arithmetic: the validator caps the delta, but a row that was already near int.MaxValue
            // would otherwise overflow inside the predicate and fail the whole batch with an arithmetic error.
            after = await ReadFiguresAsync(
                """
                UPDATE stock
                   SET OnHand = stock.OnHand + @delta, UpdatedAtUtc = @now
                OUTPUT inserted.OnHand, inserted.Reserved
                  FROM inventory.StockLevels AS stock
                  JOIN catalog.ProductVariants AS variant ON variant.Id = stock.VariantId
                  JOIN catalog.Products AS product ON product.Id = variant.ProductId
                 WHERE stock.VariantId = @variantId
                   AND product.IsDeleted = 0
                   AND CAST(stock.OnHand AS bigint) + @delta >= stock.Reserved
                   AND CAST(stock.OnHand AS bigint) + @delta <= @maxOnHand;
                """,
                cancellationToken,
                ("@delta", onHandDelta),
                ("@now", batch.NowUtc),
                ("@variantId", variantId),
                ("@maxOnHand", StockChangeRules.MaxOnHand));
        }

        if (after is not { } figures)
        {
            return await DiagnoseAsync(request, cancellationToken);
        }

        var movementType = StockChangeRules.MovementTypeFor(request.Kind, onHandDelta);

        dbContext.InventoryMovements.Add(new InventoryMovement
        {
            VariantId = variantId,
            MovementType = movementType,
            OnHandDelta = onHandDelta,
            ReservedDelta = 0,
            OnHandAfter = figures.OnHand,
            ReservedAfter = figures.Reserved,
            BatchId = batch.BatchId,
            Reason = batch.Reason,
            CreatedByUserId = batch.Actor.UserId,
            CreatedAtUtc = batch.NowUtc,
        });

        auditLog.Enlist(ContentAuditTrail.Row(
            AuditActions.Update,
            StockEntityType,
            variantId.ToString(CultureInfo.InvariantCulture),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["onHand"] = figures.OnHand - onHandDelta },
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["onHand"] = figures.OnHand,
                ["movementType"] = movementType.ToString(),
                ["reason"] = batch.Reason,
                ["batchId"] = batch.BatchId?.ToString("D", CultureInfo.InvariantCulture),
            },
            batch.Actor,
            batch.NowUtc));

        return new StockChangeRowResult(
            variantId,
            nameof(StockChangeOutcome.Applied),
            null,
            onHandDelta,
            figures.OnHand,
            figures.Reserved,
            figures.OnHand - figures.Reserved);
    }

    /// <summary>
    /// Why a conditional update matched nothing, for the message only — the decision was the update's.
    /// <c>READCOMMITTEDLOCK</c> so that under RCSI this waits for a writer in flight rather than quoting the
    /// figures from before it.
    /// </summary>
    private async Task<StockChangeRowResult> DiagnoseAsync(StockChangeRequest request, CancellationToken cancellationToken)
    {
        var current = await ReadFiguresAsync(
            """
            SELECT stock.OnHand, stock.Reserved
              FROM inventory.StockLevels AS stock WITH (READCOMMITTEDLOCK)
              JOIN catalog.ProductVariants AS variant ON variant.Id = stock.VariantId
              JOIN catalog.Products AS product ON product.Id = variant.ProductId
             WHERE stock.VariantId = @variantId
               AND product.IsDeleted = 0;
            """,
            cancellationToken,
            ("@variantId", request.VariantId));

        // A removed product's variant reads as not there, exactly as the list and the history treat it: the
        // update's own join refused it, so nothing was written (tester #190, D3).
        if (current is not { } now)
        {
            return new StockChangeRowResult(
                request.VariantId,
                nameof(StockChangeOutcome.NotFound),
                "This variant does not exist or has no stock record, so nothing was changed. Reload the page.",
                0,
                null,
                null,
                null);
        }

        string outcome;
        string message;

        if (request.Kind == StockChangeKind.SetCount && now.OnHand != request.ExpectedOnHand)
        {
            outcome = nameof(StockChangeOutcome.Changed);
            message = Say(
                $"On hand is now {now.OnHand}, not the {request.ExpectedOnHand} you were looking at: an order or another change moved it. Nothing was changed; check the count and save it again.");
        }
        else
        {
            long target = request.Kind == StockChangeKind.SetCount
                ? request.Quantity
                : (long)now.OnHand + request.Quantity;

            outcome = nameof(StockChangeOutcome.Refused);

            if (target < now.Reserved)
            {
                message = now.Reserved == 1
                    ? "1 unit is reserved for an unpaid order, so on hand cannot go below 1. Nothing was changed."
                    : Say($"{now.Reserved} units are reserved for unpaid orders, so on hand cannot go below {now.Reserved}. Nothing was changed.");
            }
            else if (target > StockChangeRules.MaxOnHand)
            {
                message = "On hand cannot go above 1,000,000. Nothing was changed.";
            }
            else
            {
                // The condition holds now but did not when the update ran: something moved this row in
                // between. Saying so is more honest than guessing which rule it was.
                message = "The stock changed while this was being saved, so nothing was changed. Try again.";
            }
        }

        return new StockChangeRowResult(
            request.VariantId,
            outcome,
            message,
            0,
            now.OnHand,
            now.Reserved,
            now.OnHand - now.Reserved);
    }

    private async Task<(int OnHand, int Reserved)?> ReadFiguresAsync(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = Command(sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var figures = (reader.GetInt32(0), reader.GetInt32(1));

        if (await reader.ReadAsync(cancellationToken))
        {
            // VariantId is the primary key; two rows would mean the statement is not the one written here.
            throw new InvalidOperationException("A stock statement for one variant touched more than one row.");
        }

        return figures;
    }

    private async Task<(int? Before, int? After)?> ReadPairAsync(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = Command(sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (
            await reader.IsDBNullAsync(0, cancellationToken) ? null : reader.GetInt32(0),
            await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetInt32(1));
    }

    /// <summary>
    /// A command on the context's connection, in its current transaction. Plain ADO.NET because
    /// <c>SqlQuery</c> composes its SQL into a subquery, which an <c>UPDATE … OUTPUT</c> cannot be
    /// (<c>NumberSeriesAllocator</c>'s reason).
    /// </summary>
    private DbCommand Command(string sql, (string Name, object Value)[] parameters)
    {
        var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Stock statements run inside the store's transaction.");

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;

            // An ADO DateTime parameter defaults to the old datetime type and its 3.33 ms steps; the column
            // is datetime2(3).
            if (value is DateTime)
            {
                parameter.DbType = DbType.DateTime2;
            }
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private static string Say(FormattableString text) => FormattableString.Invariant(text);
}
