using System.Data;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Payments;

/// <summary>
/// Doc 08 §5.6, statement for statement.
/// <para>
/// <b>The claim is the only thing standing between two instances of this job, and it is enough.</b>
/// <c>ops.JobLeases</c> is in doc 08 §3.8 and is not in any migration, so there is no lease to take — and
/// none is needed: the conditional <c>UPDATE</c> on the order row means that of any number of callers
/// racing for one order exactly one affects a row and the rest affect none. A lease would make the job
/// run once; the claim makes each <i>order</i> expire once, which is the invariant that matters. That is
/// also why the batch read may safely hand the same id to two instances.
/// </para>
/// <para>
/// <b>What it does not do, and why that is not silently skipped.</b> Doc 08 §5.6 step 4 also writes
/// <c>Release</c> movements and returns the promo use. <c>inventory.InventoryMovements</c> does not exist
/// (#108, W1-05) and <c>marketing.PromoCodes</c> does not exist (ADM-13 / W3-05), so
/// <c>Orders.PromoCodeId</c> is null on every order ever placed and there is nothing to decrement. The
/// stock figures themselves are exact; what is missing is the per-movement trail. Doc 11 P7 step 7's
/// <c>OrderExpired</c> email needs <c>ops.EmailOutbox</c> (W3-06) and is not queued — and nothing anywhere
/// tells a shopper one was sent.
/// </para>
/// </summary>
internal sealed class OrderExpiryStore(AppDbContext dbContext) : IOrderExpiryStore
{
    /// <summary>
    /// Doc 08 §5.6 step 1's read.
    /// </summary>
    /// <remarks>
    /// <b><c>Status == OrderStatus.PendingPayment</c> is written as a compile-time constant on purpose
    /// (DB28).</b> <c>IX_Orders_Pending_Due</c> is filtered <c>WHERE Status = 0</c> and keyed on
    /// <c>PaymentDueAtUtc</c> with <c>CaptureLockedUntilUtc</c> included, so this predicate and this sort
    /// are the index's own shape — but only if the optimiser can prove the predicate implies the filter,
    /// which it can for a literal and cannot for a parameter. An enum member compiles to a literal; a
    /// captured local would not, and the query would scan <c>sales.Orders</c> every five minutes for ever
    /// with nothing failing.
    /// </remarks>
    public async Task<IReadOnlyList<int>> FindDueOrdersAsync(
        int batchSize,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        await dbContext.Orders
            .AsNoTracking()
            .Where(order =>
                order.Status == OrderStatus.PendingPayment
                && order.PaymentDueAtUtc < nowUtc
                && (order.CaptureLockedUntilUtc == null || order.CaptureLockedUntilUtc < nowUtc))

            // Oldest deadline first, so a backlog is worked in the order the stock was promised away —
            // and then by id, which is not decoration.
            //
            // **A full-suite run found this.** Two orders placed milliseconds apart can share a
            // `PaymentDueAtUtc` to the precision of `datetime2(3)`, and a sort on that column alone then
            // returns them in whatever order the engine finds them. The job does not care — any order
            // releases the same stock — but "oldest first" stops being a property anything can rely on or
            // assert, and a test that did assert it passed in isolation and failed in a full run, which is
            // the worst way for a non-determinism to announce itself.
            //
            // It is free: `IX_Orders_Pending_Due` is a filtered nonclustered index keyed on
            // `PaymentDueAtUtc`, and every row in it carries the clustered key as its locator, so
            // `(PaymentDueAtUtc, Id)` is the order the index is already in. `BankQueuePlanTests` asserts
            // the index is still matched.
            .OrderBy(order => order.PaymentDueAtUtc)
            .ThenBy(order => order.Id)
            .Take(batchSize)
            .Select(order => order.Id)
            .ToListAsync(cancellationToken);

    public async Task<bool> ExpireAsync(
        int orderId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

            try
            {
                // Doc 08 §4.1's expiry guard in full, including the capture lock: an order with a PayPal
                // capture in flight younger than CaptureLockHours is never expired, because the money may
                // be about to arrive and releasing its stock would oversell.
                var claimed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE sales.Orders
                       SET Status = {(byte)OrderStatus.Expired},
                           StockState = {(byte)OrderStockState.Released},
                           ClosedAtUtc = {nowUtc},
                           UpdatedAtUtc = {nowUtc}
                     WHERE Id = {orderId}
                       AND Status = {(byte)OrderStatus.PendingPayment}
                       AND StockState = {(byte)OrderStockState.Reserved}
                       AND PaymentDueAtUtc < {nowUtc}
                       AND (CaptureLockedUntilUtc IS NULL OR CaptureLockedUntilUtc < {nowUtc});
                    """,
                    cancellationToken);

                if (claimed != 1)
                {
                    // Doc 08 §5.6 step 2: "Zero rows means the order was paid, cancelled or is capturing:
                    // skip it." Not a failure, and nothing to log — it is the normal outcome of a staff
                    // approval landing in the same second.
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }

                await ReleaseStockAsync(orderId, nowUtc, cancellationToken);

                // Doc 08 §4.1: "open payments (0/1) -> Expired". Plural and set-based: UX_Payments_OneOpen
                // allows at most one today, but the statement is written for the rule rather than for the
                // index, so a future provider that opens two cannot leave one behind. The row moves out of
                // that index's filter, which is what lets a later Accept late payment settle it (doc 08
                // §4.3 N7).
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE sales.Payments
                       SET Status = {(byte)PaymentStatus.Expired},
                           UpdatedAtUtc = {nowUtc},
                           CompletedAtUtc = {nowUtc}
                     WHERE OrderId = {orderId}
                       AND Status IN ({(byte)PaymentStatus.Initiated}, {(byte)PaymentStatus.Pending});
                    """,
                    cancellationToken);

                dbContext.OrderStatusHistory.Add(new OrderStatusHistoryEntry
                {
                    OrderId = orderId,
                    FromStatus = OrderStatus.PendingPayment,
                    ToStatus = OrderStatus.Expired,

                    // Doc 08 §4: the System is a job, carries no staff permission and cannot be reached
                    // from the admin API. SystemJob, never Staff — an expiry nobody chose must not read as
                    // somebody's decision.
                    Source = OrderStatusChangeSource.SystemJob,
                    ChangedByUserId = null,
                    Note = "Payment was not confirmed before the deadline, so the items were released.",
                    OccurredAtUtc = nowUtc,
                });

                await dbContext.SaveChangesAsync(cancellationToken);
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
    /// Doc 08 §5.6 step 3: give the reservation back, line by line, in ascending <c>VariantId</c>.
    /// </summary>
    /// <remarks>
    /// The sort is doc 08 §5.3's lock order, shared with placement, approval and every admin stock change,
    /// so two of them can never take the same two rows in opposite orders.
    /// <para>
    /// <b>A row count other than 1 throws</b> (§5.6 step 3: "the row count must be 1, otherwise roll back
    /// and alert"). <c>Reserved &gt;= q</c> can only fail if the reservation this order made at placement
    /// has already been given back by something else, which means the figures have drifted — and releasing
    /// stock that was never held would invent inventory. <c>ops.AdminAlerts</c> does not exist, so the
    /// alert is the exception reaching the job's log, and doc 08 §5.9's nightly reconciliation is the other
    /// half of the signal. The order stays <c>PendingPayment</c> and the next run tries again, which is the
    /// right behaviour for a transient cause and a loud one for a persistent one.
    /// </para>
    /// </remarks>
    private async Task ReleaseStockAsync(int orderId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var lines = await dbContext.OrderLines
            .AsNoTracking()
            .Where(line => line.OrderId == orderId)
            .Select(line => new { line.VariantId, line.Quantity })
            .ToListAsync(cancellationToken);

        var merged = lines
            .GroupBy(line => line.VariantId)
            .Select(group => new { VariantId = group.Key, Quantity = group.Sum(line => line.Quantity) })
            .OrderBy(line => line.VariantId)
            .ToList();

        foreach (var line in merged)
        {
            var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE inventory.StockLevels
                   SET Reserved = Reserved - {line.Quantity},
                       UpdatedAtUtc = {nowUtc}
                 WHERE VariantId = {line.VariantId}
                   AND Reserved >= {line.Quantity};
                """,
                cancellationToken);

            if (affected != 1)
            {
                throw new InvalidOperationException(
                    $"Order {orderId} could not release {line.Quantity} of variant {line.VariantId} on "
                    + "expiry: the reservation made at placement is no longer there. Stock levels and the "
                    + "order lines have drifted (doc 08 §5.6, §5.9).");
            }
        }
    }
}
