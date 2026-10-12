using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales;

/// <summary>
/// Doc 08 §3.6's usage model: the two statements that move <c>marketing.PromoCodes.UsageCount</c>, and the
/// lock that makes the per-customer limit hold under a race (ADM-13).
/// <para>
/// <b>Taking a use and giving one back are in one file because they have to agree about one thing.</b>
/// Doc 08 §3.6 defines a <i>held</i> use as an order in <c>PendingPayment</c>, <c>Paid</c>,
/// <c>Processing</c>, <c>Shipped</c> or <c>Delivered</c>, or a <c>Refunded</c> order that was actually
/// paid — and <c>UsageCount</c> is a cache of that count. <see cref="TakeAsync"/>'s per-customer query
/// and <see cref="ReturnAsync"/>'s decrement are the two halves of keeping the cache true: if the
/// decrement ran for a status the count still treats as holding a use, every later checkout by that
/// customer would be measured against a figure that had drifted. Two files would be two places for that
/// definition to diverge, which is the argument <c>OrderStockRelease</c> makes for the release loop.
/// </para>
/// </summary>
internal static class OrderPromoUse
{
    /// <summary>
    /// Doc 11 P6 step 9 sub-step 2: "takes a promo use (total limit, then per-customer limit for this
    /// email)". Returns the refusal, or <see langword="null"/> when the use was taken.
    /// </summary>
    /// <remarks>
    /// <b>The order of the statements is the whole correctness argument, and it is not doc 11's order.</b>
    /// Doc 11 names the total limit first; this checks the per-customer limit first. The total limit is
    /// applied by an <c>UPDATE</c>, which takes an exclusive lock on the <c>PromoCodes</c> row and holds
    /// it to commit. Run first, that lock serialises every concurrent checkout using the code — and the
    /// per-customer count would then be correct <i>because of the row lock</i> rather than because of the
    /// app lock, so the app lock could be deleted with nothing going red and the guarantee would rest on
    /// a statement nobody wrote for that job. Measured and not assumed: that is what the first version of
    /// <c>CheckoutPromoRaceTests</c> found, and the numbers are in that file.
    /// <para>
    /// With the cheap read first, <c>sp_getapplock</c> is the <b>only</b> thing serialising two checkouts
    /// that share an email, and removing it makes a four-way race place four orders against a limit of
    /// one. Nothing is lost by the reordering: doc 11's sequence decides which <i>sentence</i> a shopper
    /// sees when both limits are breached at once, and this method answers
    /// <see cref="PromoRefusal.AlreadyUsedByCustomer"/> in that case rather than
    /// <see cref="PromoRefusal.FullyUsed"/> — both true, both refusals.
    /// </para>
    /// <para>
    /// <b>The app lock is <c>Transaction</c>-owned with the default infinite timeout</b>, the same shape
    /// <c>StaffUserRoleManagementStore</c> uses for doc 08 §3.1's last-Super-Admin guard: a second caller
    /// blocks until the first commits or rolls back and then runs its own, now up-to-date, count. It
    /// serialises rather than fails fast, so "two simultaneous checkouts and one use left" resolves to one
    /// success and one refusal — never two successes, and never two refusals. The transaction ending
    /// releases it, so a rollback releases it too and there is nothing to undo on the failure path.
    /// </para>
    /// <para>
    /// <b>It is taken after the stock reservation, not before,</b> which is doc 08 §5.3's lock order: the
    /// stock rows first, in ascending <c>VariantId</c>, then everything else. Taking the application lock
    /// first would not deadlock, because nothing else takes these two in the other order — but §5.3 is one
    /// order for every writer precisely so that nobody has to re-derive that argument per path.
    /// </para>
    /// </remarks>
    /// <param name="dbContext">The caller's context, already inside the placement transaction.</param>
    /// <param name="promo">The code the order is using.</param>
    /// <param name="emailNormalized">
    /// The order's <c>EmailNormalized</c> — upper-cased, as <c>sales.Orders</c> stores it. The limit is per
    /// normalised email (doc 11 line 468), so a guest reusing a code with a <i>different</i> address is
    /// allowed by design (doc 11 line 494) and is not something this method tries to prevent.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<PromoRefusal?> TakeAsync(
        AppDbContext dbContext,
        PromoTake promo,
        string emailNormalized,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(promo);

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            EXEC sp_getapplock
                 @Resource = {LockResource(promo.PromoCodeId, emailNormalized)},
                 @LockMode = N'Exclusive',
                 @LockOwner = N'Transaction';
            """,
            cancellationToken);

        // Doc 08 §3.6's per-customer limit, as **one** statement — so there is exactly one contended
        // statement, which is what `CheckoutPromoRaceTests` counts to prove the race is a race rather
        // than four attempts that each short-circuited before reaching it.
        //
        // `PerCustomerLimit` is read here rather than carried from the quote, so an admin who lowered it
        // a second ago is obeyed. Under READ COMMITTED that read is a shared lock released at statement
        // end, so it is not what serialises the contenders; the app lock above is.
        var held = await dbContext.Database
            .SqlQuery<PerCustomerUse>(
                $"""
                SELECT p.PerCustomerLimit AS PerCustomerLimit,
                       (SELECT COUNT_BIG(*)
                          FROM sales.Orders o
                         WHERE o.PromoCodeId = p.Id
                           AND o.EmailNormalized = {emailNormalized}
                           AND (o.Status IN (
                                    {(byte)OrderStatus.PendingPayment},
                                    {(byte)OrderStatus.Paid},
                                    {(byte)OrderStatus.Processing},
                                    {(byte)OrderStatus.Shipped},
                                    {(byte)OrderStatus.Delivered})
                                OR (o.Status = {(byte)OrderStatus.Refunded}
                                    AND o.PaidAtUtc IS NOT NULL))) AS HeldUses
                  FROM marketing.PromoCodes p
                 WHERE p.Id = {promo.PromoCodeId} AND p.IsDeleted = 0
                """)
            .SingleOrDefaultAsync(cancellationToken);

        if (held is null)
        {
            // Removed between the quote and here. "Not recognised" is the answer the quote would now give
            // too, and it is the one that does not tell a shopper the code used to exist.
            return PromoRefusal.NotRecognised;
        }

        if (held.PerCustomerLimit is { } perCustomerLimit && held.HeldUses >= perCustomerLimit)
        {
            return PromoRefusal.AlreadyUsedByCustomer;
        }

        // Doc 08 §3.6's placement row, verbatim, including "0 rows = code fully used". The condition and
        // the increment are one statement under one row lock, so two checkouts for the last use cannot
        // both read `UsageCount < TotalUsageLimit`: the second waits for the first's lock, re-evaluates
        // against the row as it now is, and matches nothing. The same shape as the stock reservation, and
        // for the same reason — there is no window between the read and the write for a race to live in.
        var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE marketing.PromoCodes
               SET UsageCount = UsageCount + 1
             WHERE Id = {promo.PromoCodeId}
               AND IsDeleted = 0
               AND (TotalUsageLimit IS NULL OR UsageCount < TotalUsageLimit);
            """,
            cancellationToken);

        return affected == 1 ? null : PromoRefusal.FullyUsed;
    }

    /// <summary>
    /// Doc 08 §5.6 step 4 and doc 11 P10 step 3: <c>UsageCount − 1</c> when an unpaid order expires or is
    /// cancelled, so the use goes back and the customer can use the code again.
    /// </summary>
    /// <remarks>
    /// <b>Doc 11 line 500 makes this a promise rather than a nicety:</b> "Our use counting explicitly
    /// gives a use back when an unpaid order expires or is cancelled." It is what makes doc 08 §3.6's
    /// definition of a held use true — an <c>Expired</c> or <c>Cancelled</c> order holds nothing, so a
    /// <c>UsageCount</c> that still counted it would refuse a code the customer is entitled to.
    /// <para>
    /// <b>One statement and no extra read</b>, written against the order rather than a promo id, so the
    /// caller does not have to have loaded <c>PromoCodeId</c> and cannot pass the wrong one. An order with
    /// no code matches nothing and the statement is a no-op, which is why there is no null check here and
    /// no "did this order have a code" branch at either call site.
    /// </para>
    /// <para>
    /// <b><c>UsageCount &gt; 0</c> is a guard against double-counting, not against a negative number</b>
    /// — <c>CK_PromoCodes_UsageCount</c> would catch that. Expiry and cancel are both reachable for one
    /// order (the job and a staff member can race), and each claims the order row first, so only one of
    /// them ever gets here; the predicate is what makes a replay after a lost acknowledgement harmless
    /// rather than a use silently handed back twice.
    /// </para>
    /// <para>
    /// <b>In the caller's transaction, last</b> (doc 08 §5.3's lock order: the order row, then the stock
    /// rows, then everything else), so the decrement commits with the status change or not at all. An
    /// expiry that released the stock and forgot the use would leave a code one use short for ever, with
    /// nothing to reconcile it against.
    /// </para>
    /// </remarks>
    public static Task<int> ReturnAsync(
        AppDbContext dbContext,
        int orderId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE marketing.PromoCodes
               SET UsageCount = UsageCount - 1
             WHERE Id = (SELECT PromoCodeId FROM sales.Orders WHERE Id = {orderId})
               AND UsageCount > 0;
            """,
            cancellationToken);
    }

    /// <summary>
    /// Doc 08 §3.6 (DB18): <c>'promo:' + {Id} + ':' + {hex SHA-256 of EmailNormalized}</c> — a fixed
    /// length, and no personal data in the lock DMVs.
    /// </summary>
    /// <remarks>
    /// The hash is of the <b>normalised</b> email, because the limit is per normalised email: two
    /// checkouts differing only in case must take the same lock, or the guard would not see them as one
    /// customer while the count that follows would. SHA-256 is a fixed-width name for a lock here and not
    /// a password hash, so there is no salt and no work factor to argue about. The digest is hex rather
    /// than base64 because <c>sp_getapplock</c>'s resource is compared under the database collation, and
    /// hex has no case-sensitive alphabet to lose.
    /// </remarks>
    private static string LockResource(int promoCodeId, string emailNormalized)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(emailNormalized));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"promo:{promoCodeId}:{Convert.ToHexString(digest)}");
    }

    /// <summary>The one row <see cref="TakeAsync"/>'s per-customer statement returns.</summary>
    private sealed record PerCustomerUse(int? PerCustomerLimit, long HeldUses);
}
