using Microsoft.EntityFrameworkCore;
using SpencerDenim.Domain.Inventory;
using SpencerDenim.Infrastructure.Inventory;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales;

/// <summary>
/// Doc 08 §5.6 step 3: give an unpaid order's reservation back, line by line, in ascending
/// <c>VariantId</c>.
/// </summary>
/// <remarks>
/// <b>One release, shared by expiry and cancel, because doc 08 §4.1 gives them the same side effect.</b>
/// The <c>PendingPayment → Expired</c> and <c>PendingPayment → Cancelled</c> rows of that table are
/// identical but for the status and the reason ("As Expired, reason Cancelled"), and a second copy of this
/// loop would be a second chance for one of them to drift — a lock order, a row-count check or a merge of
/// duplicate variants done differently in one of two places is a deadlock or an oversell that only one of
/// the two paths can produce. It was extracted from <c>OrderExpiryStore</c> rather than written again;
/// <c>OrderExpiryTests</c> and <c>OrderCancellationTests</c> both run it.
/// <para>
/// <b>"Both run it" and not "both prove it", because two of the three are not observable by a test and
/// saying otherwise was wrong.</b> The row-count check is tested on both paths. The <b>lock order</b> and
/// the <b>duplicate-variant merge</b> are not, and cannot easily be: measured in this branch, reversing
/// the sort and deleting the <c>GroupBy</c> each leave all of <c>OrderCancellationTests</c> and
/// <c>OrderExpiryTests</c> green — reversing the sort changes no figure, and two statements of 2 and 1
/// reach the same <c>Reserved</c> as one statement of 3. Both exist to prevent a deadlock, and a deadlock
/// needs an interleaving with another writer that a test cannot compel. So what makes them safe is that
/// there is <i>one</i> copy of this loop and not two, which is the whole argument for the extraction; the
/// tests are not the thing holding them up.
/// </para>
/// <para>
/// <b>The sort is doc 08 §5.3's lock order</b>, shared with placement, approval and every admin stock
/// change, so two of them can never take the same two rows in opposite orders. Duplicate variants in one
/// order are merged first, which §5.3 also requires: two lines of the same variant taken as two statements
/// would be two chances to deadlock with a third writer between them.
/// </para>
/// <para>
/// <b>A row count other than 1 throws</b> (§5.6 step 3: "the row count must be 1, otherwise roll back and
/// alert"). <c>Reserved &gt;= q</c> can only fail if the reservation this order made at placement has
/// already been given back by something else, which means the figures have drifted — and releasing stock
/// that was never held would invent inventory. <c>ops.AdminAlerts</c> does not exist, so the alert is the
/// exception reaching the caller; doc 08 §5.9's nightly reconciliation is the other half of the signal.
/// </para>
/// <para>
/// <b>The movements.</b> Doc 08 §5.6 step 4 also writes <c>Release</c> movements. ADM-12 (#190) created
/// <c>inventory.InventoryMovements</c> and each released line now writes one through
/// <see cref="StockLedger.AppendAsync"/>, straight after its update and under the same lock (#108).
/// <para>
/// <b>The promo decrement is no longer one of them.</b> It was a documented no-op in this file while
/// <c>marketing.PromoCodes</c> did not exist; W3-05 created the table and moved the statement to
/// <see cref="OrderPromoUse.ReturnAsync"/>, beside the <c>TakeAsync</c> it has to agree with about what
/// doc 08 §3.6 calls a held use. Both callers of this release call that too, in the same transaction and
/// after the stock rows — doc 08 §5.3's lock order.
/// </para>
/// </para>
/// </remarks>
internal static class OrderStockRelease
{
    /// <summary>
    /// Releases every line of <paramref name="orderId"/>, inside the caller's transaction.
    /// </summary>
    /// <param name="dbContext">The caller's context, already in a transaction with the order row claimed.</param>
    /// <param name="orderId">The order whose reservation goes back.</param>
    /// <param name="nowUtc">The caller's one instant, so <c>UpdatedAtUtc</c> matches the order's.</param>
    /// <param name="because">
    /// One word for the exception's sentence — "expiry" or "cancellation". It names which path found the
    /// drift, which is the first thing somebody reading the log needs.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task ReleaseAsync(
        AppDbContext dbContext,
        int orderId,
        DateTime nowUtc,
        string because,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

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
                    + $"{because}: the reservation made at placement is no longer there. Stock levels and "
                    + "the order lines have drifted (doc 08 §5.6, §5.9).");
            }

            // Doc 08 §5.6 step 4: the Release movement, under the lock the update above still holds (#108).
            await StockLedger.AppendAsync(
                dbContext,
                line.VariantId,
                InventoryMovementType.Release,
                onHandDelta: 0,
                reservedDelta: -line.Quantity,
                orderId,
                nowUtc,
                cancellationToken);
        }
    }
}
