using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// <c>ExpireUnpaidOrders</c> (doc 08 §4.1 / §5.6, doc 11 P7 step 7 and P10 flow B), and the half of
/// issue #107 that is a defect rather than a question.
/// <para>
/// <b>The clock is moved, never the deadline.</b> Every test here passes a "now" past the order's own
/// <c>PaymentDueAtUtc</c> instead of rewriting that column, because the column is the thing under test: a
/// test that moved the deadline would be checking the guard against a value it had just written, and would
/// pass against a job that ignored deadlines altogether.
/// </para>
/// </summary>
public sealed class OrderExpiryTests(SqlServerDatabaseFixture sqlServer) : BankTransferTestBase(sqlServer)
{
    [Fact]
    public async Task An_order_past_its_deadline_is_expired_and_its_stock_released()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 2, onHand: 5);
        var orderId = await OrderIdAsync(order.PublicId);

        var before = await StockAsync(variantId);
        Assert.Equal(2, before.Reserved);

        var now = order.PaymentDueAtUtc.AddMinutes(1);

        Assert.True(await ExpireAsync(orderId, now));

        var row = await ReadOrderAsync(order.PublicId);

        Assert.Equal(OrderStatus.Expired, row.Status);
        Assert.Equal(OrderStockState.Released, row.StockState);
        Assert.Equal(now, row.ClosedAtUtc);
        Assert.Null(row.PaidAtUtc);
        Assert.Null(row.InvoiceNumber);

        // Doc 08 §4.1: open payments (0/1) become Expired. The row leaves UX_Payments_OneOpen's filter,
        // which is what later lets Accept late payment settle it (doc 08 §4.3 N7).
        var payment = Assert.Single(row.Payments);
        Assert.Equal(PaymentStatus.Expired, payment.Status);
        Assert.False(payment.IsOrderSettlement);
        Assert.Equal(now, payment.CompletedAtUtc);

        // Doc 08 §5.6 step 3: the reservation goes back. OnHand is untouched — nothing was sold — so
        // Available rises by the quantity and the goods are on the shelf again.
        var after = await StockAsync(variantId);
        Assert.Equal(before.OnHand, after.OnHand);
        Assert.Equal(before.Reserved - 2, after.Reserved);
        Assert.Equal(before.Available + 2, after.Available);

        // Doc 08 §4: source SystemJob, with no user behind it. An expiry nobody chose must not read as
        // somebody's decision.
        var history = Assert.Single(row.StatusHistory, entry => entry.ToStatus == OrderStatus.Expired);
        Assert.Equal(OrderStatus.PendingPayment, history.FromStatus);
        Assert.Equal(OrderStatusChangeSource.SystemJob, history.Source);
        Assert.Null(history.ChangedByUserId);
        Assert.Equal(now, history.OccurredAtUtc);
    }

    [Fact]
    public async Task An_order_that_is_not_yet_due_is_not_expired()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 1, onHand: 3);
        var orderId = await OrderIdAsync(order.PublicId);

        var before = await StockAsync(variantId);

        // One second before the deadline. The guard is `PaymentDueAtUtc < now`, so this must not fire —
        // and the shopper still has their items.
        Assert.False(await ExpireAsync(orderId, order.PaymentDueAtUtc.AddSeconds(-1)));

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Equal(OrderStockState.Reserved, row.StockState);
        Assert.Null(row.ClosedAtUtc);
        Assert.Equal(PaymentStatus.Pending, Assert.Single(row.Payments).Status);
        Assert.Equal(before, await StockAsync(variantId));
    }

    [Fact]
    public async Task A_paid_order_is_skipped_rather_than_expired()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 1, onHand: 3);
        var orderId = await OrderIdAsync(order.PublicId);

        Assert.IsType<BankTransferDecisionResult.Applied>(await ApproveAsync(orderId));

        var paid = await StockAsync(variantId);

        // Doc 08 §5.6 step 2: "Zero rows means the order was paid, cancelled or is capturing: skip it."
        // Not a failure — and crucially it must not release stock that has already been sold, which would
        // hand the same goods to somebody else.
        Assert.False(await ExpireAsync(orderId));

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Paid, row.Status);
        Assert.Equal(OrderStockState.Committed, row.StockState);
        Assert.Equal(paid, await StockAsync(variantId));
    }

    [Fact]
    public async Task Expiring_twice_releases_the_stock_once()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 2, onHand: 4);
        var orderId = await OrderIdAsync(order.PublicId);

        var before = await StockAsync(variantId);

        Assert.True(await ExpireAsync(orderId));
        var once = await StockAsync(variantId);

        // The second call finds StockState = Released, so its claim affects no rows. Without that guard
        // the release statement would run again and `Reserved` would go negative — which
        // CK_StockLevels_Reserved refuses, so the symptom would be an exception rather than a silent
        // oversell. Either way it must not happen.
        Assert.False(await ExpireAsync(orderId));

        Assert.Equal(once, await StockAsync(variantId));
        Assert.Equal(before.Reserved - 2, once.Reserved);
    }

    [Fact]
    public async Task An_order_with_a_capture_lock_in_force_is_left_alone()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 1, onHand: 3);
        var orderId = await OrderIdAsync(order.PublicId);
        var now = order.PaymentDueAtUtc.AddHours(1);

        // Doc 08 §4.1: an order whose capture lock has not lapsed is never expired, because the money may
        // be about to arrive and releasing its stock would oversell. Nothing in this build sets the lock
        // (it is PayPal's, W4-01), so the guard is only reachable by setting the column — which is exactly
        // why it is worth a test now rather than when the capture path arrives.
        using (var scope = CreateScope())
        {
            var moved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
                .Where(candidate => candidate.Id == orderId)
                .ExecuteUpdateAsync(
                    candidate => candidate.SetProperty(
                        row => row.CaptureLockedUntilUtc,
                        now.AddHours(2)),
                    Token);

            Assert.Equal(1, moved);
        }

        var before = await StockAsync(variantId);

        Assert.False(await ExpireAsync(orderId, now));

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Equal(before, await StockAsync(variantId));

        // And once the lock lapses it expires normally, so the guard delays rather than exempts.
        Assert.True(await ExpireAsync(orderId, now.AddHours(3)));
        Assert.Equal(OrderStatus.Expired, (await ReadOrderAsync(order.PublicId)).Status);
    }

    [Fact]
    public async Task The_batch_read_finds_due_orders_oldest_deadline_first_and_stops_at_the_batch_size()
    {
        var first = await PlaceOrderAsync();
        var second = await PlaceOrderAsync();
        var third = await PlaceOrderAsync();

        var ids = new[]
        {
            await OrderIdAsync(first.Order.PublicId),
            await OrderIdAsync(second.Order.PublicId),
            await OrderIdAsync(third.Order.PublicId),
        };

        var due = await InScopeAsync(services => services
            .GetRequiredService<IOrderExpiryStore>()
            .FindDueOrdersAsync(50, DateTime.UtcNow.AddHours(49), Token));

        // **Asserted as a subsequence, not as the whole list.** The assembly shares one database and
        // orders are never deleted (doc 08 §1.6), so every unpaid order a neighbouring test placed is also
        // due. A test that expected exactly these three would be an assertion about test ordering, which
        // is the mistake this suite has made and fixed twice.
        var mine = due.Where(id => ids.Contains(id)).ToList();
        Assert.Equal(3, mine.Count);

        // Oldest deadline first. These three were placed in order a few milliseconds apart, so their
        // deadlines are in the same order.
        Assert.Equal(ids, mine);

        // And the batch size is a ceiling, which is what stops one bad afternoon's backlog holding stock
        // row locks for minutes while live checkouts wait (doc 08 §5.6 step 1).
        var capped = await InScopeAsync(services => services
            .GetRequiredService<IOrderExpiryStore>()
            .FindDueOrdersAsync(2, DateTime.UtcNow.AddHours(49), Token));

        Assert.Equal(2, capped.Count);
    }

    [Fact]
    public async Task Only_orders_past_their_own_deadline_are_in_the_batch()
    {
        var (order, _) = await PlaceOrderAsync();
        var orderId = await OrderIdAsync(order.PublicId);

        var due = await InScopeAsync(services => services
            .GetRequiredService<IOrderExpiryStore>()
            .FindDueOrdersAsync(500, order.PaymentDueAtUtc.AddSeconds(-1), Token));

        Assert.DoesNotContain(orderId, due);

        var later = await InScopeAsync(services => services
            .GetRequiredService<IOrderExpiryStore>()
            .FindDueOrdersAsync(500, order.PaymentDueAtUtc.AddSeconds(1), Token));

        Assert.Contains(orderId, later);
    }

    [Fact]
    public async Task Two_instances_of_the_job_expire_one_order_once()
    {
        const int Rounds = 6;
        const int Instances = 4;

        for (var round = 1; round <= Rounds; round++)
        {
            var (order, variantId) = await PlaceOrderAsync(quantity: 1, onHand: 3);
            var orderId = await OrderIdAsync(order.PublicId);
            var before = await StockAsync(variantId);
            var now = order.PaymentDueAtUtc.AddMinutes(1);

            // There is no ops.JobLeases table and the job takes no lease, so two containers really can
            // hand the same order id to this method at the same time. The conditional claim is the whole
            // protection, and "released once" is what it has to buy.
            var outcomes = await Task.WhenAll(
                Enumerable.Range(0, Instances)
                    .Select(_ => Task.Run(() => ExpireAsync(orderId, now), Token)));

            Assert.Single(outcomes, expired => expired);

            var after = await StockAsync(variantId);
            Assert.Equal(before.Reserved - 1, after.Reserved);
            Assert.Equal(before.OnHand, after.OnHand);

            var row = await ReadOrderAsync(order.PublicId);
            Assert.Equal(OrderStatus.Expired, row.Status);

            // One history row, not four: a second claim that affected no rows must write nothing at all.
            Assert.Single(row.StatusHistory, entry => entry.ToStatus == OrderStatus.Expired);
        }
    }

    [Fact]
    public async Task An_expiry_leaves_this_order_holding_nothing()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 3, onHand: 9);
        var orderId = await OrderIdAsync(order.PublicId);

        Assert.Equal(3, (await StockAsync(variantId)).Reserved);

        Assert.True(await ExpireAsync(orderId));

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Doc 08 §5.9 compares `StockLevels.Reserved` with the lines of orders in `StockState = 0`, and
        // this order is no longer in that set — which is the half of the invariant an expiry is
        // responsible for.
        var stillHeldByThisOrder = await db.OrderLines
            .AsNoTracking()
            .Where(line =>
                line.OrderId == orderId
                && line.Order!.StockState == OrderStockState.Reserved)
            .SumAsync(line => (int)line.Quantity, Token);

        Assert.Equal(0, stillHeldByThisOrder);

        var reserved = await db.StockLevels
            .AsNoTracking()
            .Where(level => level.VariantId == variantId)
            .Select(level => level.Reserved)
            .SingleAsync(Token);

        Assert.Equal(0, reserved);
    }

    // **The *global* §5.9 invariant is deliberately not asserted here, and the first version of this test
    // asserted it and failed.** `CheckoutTestBase.SetStockAsync` zeroes `Reserved` so a test can arrange a
    // known starting point, and its own remarks say the consequence out loud: the invariant is false across
    // a call to it, because orders a neighbouring test placed on the same variant still hold stock that is
    // no longer counted. The failure read `Expected: 3, Actual: 0` — three units genuinely held by
    // `BankTransferReviewTests`' unpaid orders against a row this test had just zeroed. Asserting it would
    // have been an assertion about which tests had run first. `CheckoutEndpointTests`'
    // `Reserved_stock_equals_the_lines_of_the_orders_that_hold_it` is where that invariant belongs: it
    // arranges its variants and then places every order it counts.
}
