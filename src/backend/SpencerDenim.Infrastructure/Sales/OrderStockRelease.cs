using Microsoft.EntityFrameworkCore;
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
/// <c>OrderExpiryTests</c> and <c>OrderCancellationTests</c> both prove it.
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
/// <b>What it does not do.</b> Doc 08 §5.6 step 4 also writes <c>Release</c> movements and returns the
/// promo use. <c>inventory.InventoryMovements</c> does not exist (#108, W1-05) and
/// <c>marketing.PromoCodes</c> does not exist (ADM-13 / W3-05) — checked in this branch against
/// <c>Migrations/</c>: the only table in the <c>marketing</c> schema any migration creates is
/// <c>NewsletterSubscribers</c>, and <c>OrderConfiguration</c> says in as many words that
/// <c>Orders.PromoCodeId</c> is a column with no foreign key for that reason. So the stock figures are
/// exact and the per-movement trail and the promo decrement are both no-ops with nothing to write to.
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
        }
    }

    /// <summary>
    /// Doc 08 §5.6 step 4's <c>UsageCount − 1</c>, which has nothing to decrement in this build.
    /// </summary>
    /// <remarks>
    /// <b>A method rather than a comment, so the shape is right the day W3-05 adds the table.</b> Doc 11
    /// P10 step 3 returns the promo use when an unpaid order closes, and doc 08 §5.6 step 4 gives the
    /// statement. Written against <paramref name="orderId"/> rather than a promo id, it is one statement
    /// and no extra read:
    /// <code>
    /// UPDATE marketing.PromoCodes SET UsageCount = UsageCount - 1
    ///  WHERE Id = (SELECT PromoCodeId FROM sales.Orders WHERE Id = @o) AND UsageCount &gt; 0;
    /// </code>
    /// <b>It is a no-op today because there is no table.</b> Checked in this branch rather than taken from
    /// the brief: <c>git grep</c> over <c>Migrations/</c> finds exactly one table in the <c>marketing</c>
    /// schema, <c>NewsletterSubscribers</c> (<c>20260924095858_HomeContentAndNewsletter</c>), and
    /// <c>OrderConfiguration</c>'s own remarks say <c>Orders.PromoCodeId</c> is a column with no foreign
    /// key because <c>marketing.PromoCodes</c> (ADM-13) has not been created by any migration. So
    /// <c>PromoCodeId</c> is null on every order ever placed and returning a use that was never taken is
    /// nothing to do — not a failure, and not worth a log line.
    /// <para>
    /// Both the expiry job and cancel call it, in the position doc 08 §5.3's lock order puts it: after the
    /// stock rows, last. When the table arrives the statement goes here once and both paths get it.
    /// </para>
    /// </remarks>
    public static Task ReturnPromoUseAsync(
        AppDbContext dbContext,
        int orderId,
        CancellationToken cancellationToken)
    {
        _ = dbContext;
        _ = orderId;
        _ = cancellationToken;

        return Task.CompletedTask;
    }
}
