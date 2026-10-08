using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Orders;

/// <summary>
/// <c>AutoDeliverShipped</c>'s two statements (doc 11 P9 step 8, doc 08 §4.1's System path).
/// <para>
/// <b>The pickup exclusion is in both of them, and that is the single most important line in this
/// file.</b> Doc 08 §4.1 G-18: for a store pickup, <c>Shipped</c> means <i>ready for collection</i>, so an
/// order nobody has collected must never be recorded as delivered — it would also hand out a "Verified
/// buyer" badge for goods still on the shelf. The candidate query is read outside the transaction, so a
/// guard that lived only there would be one a concurrent change could walk past; the claim carries it too.
/// </para>
/// </summary>
internal sealed class AutoDeliverStore(AppDbContext dbContext) : IAutoDeliverStore
{
    public async Task<IReadOnlyList<int>> FindDueOrdersAsync(
        int batchSize,
        DateTime shippedBeforeUtc,
        CancellationToken cancellationToken) =>
        await dbContext.Orders
            .AsNoTracking()

            // **`Status == Shipped` is written as a compile-time enum member, not a captured variable, and
            // that is load-bearing (DB28).** `IX_Orders_Shipped` is filtered
            // `WHERE [Status] = 3`, and SQL Server matches a filtered index only when the query's predicate
            // provably implies the filter. A literal implies it; a parameter does not — so a field or a
            // local here would silently stop the index being used and the job would scan sales.Orders every
            // night, with nothing failing and nothing saying so. `OrderStatus.Shipped` is a compile-time
            // constant, so Roslyn bakes it in and EF emits a literal.
            .Where(order =>
                order.Status == OrderStatus.Shipped

                // Doc 08 §4.1 G-18. The delivery method code is a snapshot column on the order precisely
                // so a rule like this can be expressed without a join (see DeliverySnapshot).
                && order.Delivery.DeliveryMethodCode != DeliveryMethod.PickupCode
                && order.ShippedAtUtc != null
                && order.ShippedAtUtc < shippedBeforeUtc)

            // Oldest dispatch first, so a backlog is worked in the order it accumulated and the index's own
            // key order is the sort.
            .OrderBy(order => order.ShippedAtUtc)
            .Take(batchSize)
            .Select(order => order.Id)
            .ToListAsync(cancellationToken);

    public async Task<bool> AutoDeliverAsync(
        int orderId,
        DateTime shippedBeforeUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        // One transaction: the order's claim and its history row have to commit together, or the timeline
        // would be missing the one entry that says the system did this rather than a person.
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            // Doc 08 §4.1's System path, every condition of it:
            //   * Status = 3 (Shipped) — the transition's own guard;
            //   * ShippedAtUtc < now - AutoDeliverAfterDays — the age the caller computed;
            //   * DeliveryMethodCode <> 'PICKUP' — G-18.
            //
            // All three are in the statement rather than trusted from the candidate read. **The pickup one
            // is not redundant**: the candidate list is read outside this transaction, and a guard that
            // only exists in a finder is a guard a concurrent change can walk past. The other two are what
            // make a staff member marking the same order delivered a second earlier an ordinary lost race
            // rather than a double write.
            var claimed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE sales.Orders
                   SET Status = {(byte)OrderStatus.Delivered},
                       DeliveredAtUtc = {nowUtc},
                       UpdatedAtUtc = {nowUtc}
                 WHERE Id = {orderId}
                   AND Status = {(byte)OrderStatus.Shipped}
                   AND DeliveryMethodCode <> {DeliveryMethod.PickupCode}
                   AND ShippedAtUtc IS NOT NULL
                   AND ShippedAtUtc < {shippedBeforeUtc};
                """,
                cancellationToken);

            if (claimed != 1)
            {
                // An ordinary outcome, not a failure: a staff member won the race, or the order was
                // corrected. Nothing is written and the job says nothing.
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            dbContext.OrderStatusHistory.Add(new OrderStatusHistoryEntry
            {
                OrderId = orderId,
                FromStatus = OrderStatus.Shipped,
                ToStatus = OrderStatus.Delivered,

                // Doc 08 §4: "System" carries no staff permission, and ChangedByUserId is null because no
                // user did this. That nullability is exactly what this row exists to use.
                Source = OrderStatusChangeSource.SystemJob,
                ChangedByUserId = null,
                Note = "Marked delivered automatically, because nobody confirmed delivery",
                OccurredAtUtc = nowUtc,
            });

            // **No ops.AuditLogs row, and that is not an omission.** Doc 08 §3.8's audit log records who
            // changed what; this change has no actor, and doc 08 §4 writes a *history* row for every System
            // transition precisely so the order's own timeline is the record. An audit row with a null user
            // would add nothing the history row does not already say.
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return true;
        });
    }
}
