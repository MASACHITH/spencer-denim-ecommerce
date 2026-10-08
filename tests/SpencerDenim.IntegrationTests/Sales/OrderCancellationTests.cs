using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Doc 11 P10 flow A — cancelling an unpaid order — against a real SQL Server (W3-04b).
/// <para>
/// <b>Every assertion is on the rows, never on the return value alone.</b> The outcome says what the store
/// believes it did; <c>sales.Orders</c>, <c>sales.Payments</c>, <c>inventory.StockLevels</c>,
/// <c>sales.OrderStatusHistory</c> and <c>ops.AuditLogs</c> are what it did. That is the whole reason
/// these are integration tests: the thing that can go wrong is a statement that affected the wrong number
/// of rows, and no in-memory double can show it.
/// </para>
/// <para>
/// <b>The orders are placed through checkout and never inserted by hand</b> (<c>BankTransferTestBase</c>'s
/// rule). An order whose <c>Reserved</c> was set by a fixture would let a cancel "release" stock nobody
/// held, and every test here would pass while the thing it is about did not happen.
/// </para>
/// </summary>
public sealed class OrderCancellationTests(SqlServerDatabaseFixture sqlServer)
    : AdminOrderTestBase(sqlServer)
{
    [Fact]
    public async Task A_staff_cancel_closes_the_order_and_its_payment_and_releases_the_stock()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 2, onHand: 5);

        var before = await StockAsync(variantId);
        Assert.Equal(2, before.Reserved);

        // **Truncated to milliseconds, because the columns are `datetime2(3)`.** `DateTime.UtcNow` has
        // 100-nanosecond ticks, SQL Server rounds them on the way in, and asserting the un-rounded value
        // against the stored one fails by a fraction of a millisecond — which reads exactly like a
        // transaction writing a different instant from the one it was given. The *order* of magnitude is
        // the tell: 0.4 ms is rounding, 5 ms is a second call to a clock.
        var at = Millisecond(DateTime.UtcNow);
        var applied = Assert.IsType<CancelOrderOutcome.Applied>(
            await CancelAsync(order.PublicId, OrderCancelledBy.Staff, "Customer rang to say no.", at));

        Assert.Equal(order.OrderNumber, applied.OrderNumber);

        var row = await ReadOrderAsync(order.PublicId);

        // Doc 11 P10 step 3 and doc 08 §4.1's `PendingPayment -> Cancelled` row, field by field.
        Assert.Equal(OrderStatus.Cancelled, row.Status);
        Assert.Equal(OrderStockState.Released, row.StockState);
        Assert.Equal("Customer rang to say no.", row.CancelReason);
        Assert.Equal(at, row.ClosedAtUtc);

        // Not paid, and not invoiced: a cancel must not look like a settlement in any column.
        Assert.Null(row.PaidAtUtc);
        Assert.Null(row.InvoiceNumber);
        Assert.Equal(0m, row.RefundedAmount);

        // Doc 08 §4.3: "Cancelling an unpaid order closes its open payment."
        var payment = Assert.Single(row.Payments);
        Assert.Equal(PaymentStatus.Cancelled, payment.Status);
        Assert.False(payment.IsOrderSettlement);
        Assert.Equal(at, payment.CompletedAtUtc);

        // Doc 08 §5.6 step 3: the reservation goes back and OnHand is untouched — nothing was sold — so
        // Available rises by the quantity and the goods are on the shelf again.
        var after = await StockAsync(variantId);
        Assert.Equal(before.OnHand, after.OnHand);
        Assert.Equal(before.Reserved - 2, after.Reserved);
        Assert.Equal(before.Available + 2, after.Available);

        // Doc 11 P10 step 3's history row, source Staff with the staff member's id on it. **This row is
        // the only record of who closed the order**, which is why its source and its user are asserted
        // rather than merely its statuses.
        var history = Assert.Single(row.StatusHistory, entry => entry.ToStatus == OrderStatus.Cancelled);
        Assert.Equal(OrderStatus.PendingPayment, history.FromStatus);
        Assert.Equal(OrderStatusChangeSource.Staff, history.Source);
        Assert.Equal(7001, history.ChangedByUserId);
        Assert.Equal(at, history.OccurredAtUtc);
        Assert.Contains("Customer rang to say no.", history.Note!, StringComparison.Ordinal);

        await AssertAuditedAsync(order.PublicId, "cancelled");
    }

    /// <summary>
    /// <b>The source genuinely differs by caller</b>, which is the half of doc 11 P10 step 3 a mock cannot
    /// prove and the half that matters: doc 11 P10's own rule is that Expired means nobody paid and
    /// Cancelled means a person decided, and this row is where "which person" is recorded.
    /// </summary>
    [Fact]
    public async Task A_customer_cancel_is_recorded_as_the_customer_with_no_user_and_no_audit_row()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 1, onHand: 4);

        var before = await StockAsync(variantId);

        Assert.IsType<CancelOrderOutcome.Applied>(
            await CancelAsync(order.PublicId, OrderCancelledBy.Customer, reason: null));

        var row = await ReadOrderAsync(order.PublicId);

        Assert.Equal(OrderStatus.Cancelled, row.Status);
        Assert.Equal(OrderStockState.Released, row.StockState);
        Assert.Equal(PaymentStatus.Cancelled, Assert.Single(row.Payments).Status);
        Assert.Equal(before.Reserved - 1, (await StockAsync(variantId)).Reserved);

        // No reason, because the customer is not asked for one (doc 11 P10 step 1 asks the *staff* member).
        // Writing a sentence of our own into the column the admin screen prints under "Why it was
        // cancelled" would be putting words in a shopper's mouth.
        Assert.Null(row.CancelReason);

        var history = Assert.Single(row.StatusHistory, entry => entry.ToStatus == OrderStatus.Cancelled);
        Assert.Equal(OrderStatusChangeSource.Customer, history.Source);
        Assert.Null(history.ChangedByUserId);
        Assert.Contains("by the customer", history.Note!, StringComparison.OrdinalIgnoreCase);

        // And nothing in ops.AuditLogs, which records what *staff* did (doc 08 §3.8). A row with a null
        // user and a null email on it would read as a system act, which a customer cancel is the opposite
        // of — and the history row above is where the act is recorded.
        Assert.Empty(await AuditRowsAsync(order.PublicId));
    }

    /// <summary>
    /// Doc 11 P10's table: <b>a paid order is never cancelled; it is refunded.</b>
    /// </summary>
    /// <remarks>
    /// Refunds are deliberately not in this build (owner decision, 2026-10-08: manual bank transfer,
    /// recorded in the order's internal note), so the only correct behaviour is to refuse and say so. The
    /// test asserts the stock as well as the status, because the failure that would matter is not a wrong
    /// status — it is stock released from an order whose money the shop has, which is a real oversell.
    /// </remarks>
    [Fact]
    public async Task A_paid_order_cannot_be_cancelled_and_nothing_is_released()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 2, onHand: 6);
        var orderId = await OrderIdAsync(order.PublicId);

        Assert.IsType<BankTransferDecisionResult.Applied>(await ApproveAsync(orderId));

        var paid = await StockAsync(variantId);

        var refused = Assert.IsType<CancelOrderOutcome.Refused>(
            await CancelAsync(order.PublicId, OrderCancelledBy.Staff, "Changed their mind."));

        Assert.Equal(CancelOrderRefusalCodes.WrongStatus, refused.Code);
        Assert.Equal(OrderStatus.Paid, refused.Status);
        Assert.Equal("Paid", refused.StatusLabel);

        // The sentence has to say what to do instead, because there is no button for it.
        Assert.Contains("refunded instead", refused.Message, StringComparison.OrdinalIgnoreCase);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Paid, row.Status);
        Assert.Equal(OrderStockState.Committed, row.StockState);
        Assert.Null(row.CancelReason);
        Assert.Null(row.ClosedAtUtc);
        Assert.Equal(PaymentStatus.Succeeded, Assert.Single(row.Payments).Status);
        Assert.Equal(paid, await StockAsync(variantId));
        Assert.DoesNotContain(row.StatusHistory, entry => entry.ToStatus == OrderStatus.Cancelled);
    }

    [Fact]
    public async Task Cancelling_twice_releases_the_stock_once()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 3, onHand: 7);

        var before = await StockAsync(variantId);

        Assert.IsType<CancelOrderOutcome.Applied>(
            await CancelAsync(order.PublicId, OrderCancelledBy.Staff, "First."));

        var once = await StockAsync(variantId);

        // The second call finds `StockState = Released`, so its claim affects no rows. Without that guard
        // the release statement would run again and `Reserved` would go negative — which
        // CK_StockLevels_Reserved refuses ([Reserved] >= 0), so the symptom would be an exception rather
        // than a silent oversell. Either way it must not happen. (The constraint name was checked against
        // StockLevelConfiguration; an earlier version of this comment named CK_StockLevels_ReservedLeOnHand,
        // which is the *other* one — [Reserved] <= [OnHand] — and would not be the one to fire.)
        var refused = Assert.IsType<CancelOrderOutcome.Refused>(
            await CancelAsync(order.PublicId, OrderCancelledBy.Customer, reason: null));

        Assert.Equal(CancelOrderRefusalCodes.WrongStatus, refused.Code);
        Assert.Equal(OrderStatus.Cancelled, refused.Status);
        Assert.Contains("already been cancelled", refused.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(once, await StockAsync(variantId));
        Assert.Equal(before.Reserved - 3, once.Reserved);

        // One history row, and the first caller's reason still on the order: a refusal must change nothing.
        var row = await ReadOrderAsync(order.PublicId);
        Assert.Single(row.StatusHistory, entry => entry.ToStatus == OrderStatus.Cancelled);
        Assert.Equal("First.", row.CancelReason);
    }

    /// <summary>
    /// Doc 11 P10 step 2's capture-lock guard, which is doc 08 §4.1's: cancel and expiry share it.
    /// </summary>
    /// <remarks>
    /// Nothing in this build sets <c>CaptureLockedUntilUtc</c> — it is PayPal's, W4-01 — so the guard is
    /// only reachable by writing the column, which is exactly why it is worth a test now rather than when
    /// the capture path arrives. The second half matters as much as the first: the lock must <b>delay</b>
    /// a cancel rather than exempt the order from one for ever.
    /// </remarks>
    [Fact]
    public async Task An_order_with_a_capture_lock_in_force_cannot_be_cancelled_until_it_lapses()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 1, onHand: 3);
        var orderId = await OrderIdAsync(order.PublicId);
        var now = DateTime.UtcNow;

        await SetCaptureLockAsync(orderId, now.AddHours(2));

        var before = await StockAsync(variantId);

        var refused = Assert.IsType<CancelOrderOutcome.Refused>(
            await CancelAsync(order.PublicId, OrderCancelledBy.Customer, null, now));

        Assert.Equal(CancelOrderRefusalCodes.CaptureInProgress, refused.Code);
        Assert.Equal(OrderStatus.PendingPayment, refused.Status);
        Assert.Contains("being processed", refused.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(OrderStatus.PendingPayment, (await ReadOrderAsync(order.PublicId)).Status);
        Assert.Equal(before, await StockAsync(variantId));

        // Once the lock has lapsed it cancels normally.
        Assert.IsType<CancelOrderOutcome.Applied>(
            await CancelAsync(order.PublicId, OrderCancelledBy.Customer, null, now.AddHours(3)));

        Assert.Equal(OrderStatus.Cancelled, (await ReadOrderAsync(order.PublicId)).Status);
        Assert.Equal(before.Reserved - 1, (await StockAsync(variantId)).Reserved);
    }

    /// <summary>
    /// Doc 11 P10 step 2's "no settling payment", which is <b>not</b> <c>Status = Paid</c> spelled
    /// differently.
    /// </summary>
    /// <remarks>
    /// <c>UX_Payments_OneSettlement</c> stops a <i>second</i> settlement; it does not stop a settlement
    /// row beside an order still reading <c>PendingPayment</c>. <b>No path in this build writes that
    /// state</b> — §4.2 step 3.3's savepoint fall-through ends with the payment in status 7, not a
    /// settlement, which is a claim this task got wrong once and then checked — so the fixture writes it
    /// on to <b>this test's own</b> payment row. Without this test the guard could be deleted and every
    /// other test here would still pass; with it, the guard is the only thing standing between a cancel
    /// and an order whose money has arrived in a transaction of its own, which is what a webhook is.
    /// </remarks>
    [Fact]
    public async Task An_order_with_a_settling_payment_cannot_be_cancelled()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 1, onHand: 3);
        var orderId = await OrderIdAsync(order.PublicId);

        await MarkPaymentSettlingAsync(orderId);

        var before = await StockAsync(variantId);

        var refused = Assert.IsType<CancelOrderOutcome.Refused>(
            await CancelAsync(order.PublicId, OrderCancelledBy.Staff, "They asked."));

        Assert.Equal(CancelOrderRefusalCodes.PaymentSettling, refused.Code);
        Assert.Equal(OrderStatus.PendingPayment, refused.Status);
        Assert.Contains("already gone through", refused.Message, StringComparison.OrdinalIgnoreCase);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Equal(OrderStockState.Reserved, row.StockState);
        Assert.Null(row.ClosedAtUtc);
        Assert.Equal(before, await StockAsync(variantId));

        // And the payment is as the fixture left it: a refusal must not close the row it refused over.
        var payment = Assert.Single(row.Payments);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.True(payment.IsOrderSettlement);
    }

    /// <summary>
    /// Doc 11 P10's table and doc 08 §4.1 R1: an order with a PayPal payment in <b>PENDING</b> may be
    /// cancelled by <c>Orders.Approve</c> alone, after a warning — so neither the customer nor the
    /// <c>Orders.Edit</c> holder may.
    /// </summary>
    /// <remarks>
    /// <b>Unreachable in this build and tested anyway.</b> Nothing captures, PayPal's provider row is
    /// seeded disabled, and <c>OrderCancellationStore</c>'s remarks say the <c>Orders.Approve</c> branch
    /// is W4-01's and is not built. What <i>is</i> built is the guard, and it is the half that protects
    /// money: without it a shopper could close an order whose capture is about to land, and the shop would
    /// be holding a payment for an order nobody can settle. The fixture rewrites this test's own payment
    /// row rather than enabling the provider, which would change shared reference data a neighbouring test
    /// asserts the state of.
    /// </remarks>
    [Fact]
    public async Task An_order_with_a_pending_paypal_payment_is_refused_for_both_callers()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 1, onHand: 3);
        var orderId = await OrderIdAsync(order.PublicId);

        await MakePaymentPayPalPendingAsync(orderId);

        var before = await StockAsync(variantId);

        foreach (var caller in new[] { OrderCancelledBy.Customer, OrderCancelledBy.Staff })
        {
            var refused = Assert.IsType<CancelOrderOutcome.Refused>(
                await CancelAsync(order.PublicId, caller, caller == OrderCancelledBy.Staff ? "Asked." : null));

            Assert.Equal(CancelOrderRefusalCodes.PayPalPending, refused.Code);
            Assert.Equal(OrderStatus.PendingPayment, refused.Status);
            Assert.Contains("being confirmed", refused.Message, StringComparison.OrdinalIgnoreCase);
        }

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Equal(PaymentStatus.Pending, Assert.Single(row.Payments).Status);
        Assert.Equal(before, await StockAsync(variantId));
    }

    [Fact]
    public async Task An_unknown_token_is_not_found()
    {
        Assert.IsType<CancelOrderOutcome.NotFound>(
            await CancelAsync(Guid.NewGuid(), OrderCancelledBy.Customer, reason: null));
    }

    /// <summary>
    /// <b>The race this design exists for.</b> The customer presses Cancel in the second the expiry sweep
    /// takes their order.
    /// </summary>
    /// <remarks>
    /// Doc 08 §5.2: both run the same conditional <c>UPDATE</c> against the same row, SQL Server takes an
    /// update lock, the loser waits and re-evaluates <c>Status = 0 AND StockState = 0</c> against the row
    /// as the winner left it, affects 0 rows and stops. So <b>exactly one</b> closes the order and the
    /// stock is released <b>exactly once</b>.
    /// <para>
    /// <b>Which one wins is deliberately not asserted</b> — either is correct, and an assertion that
    /// picked one would be an assertion about scheduling. What is asserted is that exactly one did, that
    /// the status is whichever winner's it was, that the reservation moved by exactly the order's quantity,
    /// and that there is <b>one</b> history row: a second claim that affected no rows must write nothing
    /// at all, and the history row is where "nothing at all" is visible.
    /// </para>
    /// <para>
    /// Six rounds rather than one. A single round of a race proves nothing about a race; six is what the
    /// expiry job's own concurrency test uses, and it is enough to catch a release that ran twice.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Cancel_racing_the_expiry_job_has_one_winner_and_releases_the_stock_once()
    {
        const int Rounds = 6;

        for (var round = 1; round <= Rounds; round++)
        {
            var (order, variantId) = await PlaceOrderAsync(quantity: 1, onHand: 3);
            var orderId = await OrderIdAsync(order.PublicId);
            var before = await StockAsync(variantId);

            // One instant for both, past the order's own deadline, so the sweep's own
            // `PaymentDueAtUtc < now` guard is satisfied and the two really are racing rather than one
            // of them being refused for a reason of its own.
            var now = order.PaymentDueAtUtc.AddMinutes(1);

            var cancel = Task.Run(
                () => CancelAsync(order.PublicId, OrderCancelledBy.Customer, null, now),
                Token);
            var expire = Task.Run(() => ExpireAsync(orderId, now), Token);

            var outcome = await cancel;
            var expired = await expire;

            var cancelled = outcome is CancelOrderOutcome.Applied;

            // Exactly one, asserted as an exclusive-or rather than "at least one": both winning is the
            // defect, and "at least one" would not see it.
            Assert.True(
                cancelled ^ expired,
                $"Round {round}: cancelled={cancelled}, expired={expired}. Exactly one must win.");

            var row = await ReadOrderAsync(order.PublicId);
            Assert.Equal(cancelled ? OrderStatus.Cancelled : OrderStatus.Expired, row.Status);
            Assert.Equal(OrderStockState.Released, row.StockState);

            // Released once, which is the invariant the row count buys.
            var after = await StockAsync(variantId);
            Assert.Equal(before.Reserved - 1, after.Reserved);
            Assert.Equal(before.OnHand, after.OnHand);

            // One closing history row, not two.
            Assert.Single(
                row.StatusHistory,
                entry => entry.ToStatus is OrderStatus.Cancelled or OrderStatus.Expired);

            // And the loser left nothing of itself behind. A cancel that lost must not have written its
            // reason on to an order the sweep expired, which is the one field the two paths do not share.
            if (!cancelled)
            {
                Assert.Null(row.CancelReason);
            }

            // The payment closed exactly once, in the winner's own terms (doc 08 §4.1/§4.3: Cancelled for
            // one, Expired for the other).
            var payment = Assert.Single(row.Payments);
            Assert.Equal(cancelled ? PaymentStatus.Cancelled : PaymentStatus.Expired, payment.Status);
        }
    }

    /// <summary>
    /// Two staff pressing Cancel at once, which is the same guarantee in the shape a shop actually meets.
    /// </summary>
    [Fact]
    public async Task Two_cancels_at_once_close_the_order_once()
    {
        const int Callers = 4;

        var (order, variantId) = await PlaceOrderAsync(quantity: 2, onHand: 5);
        var before = await StockAsync(variantId);

        var outcomes = await Task.WhenAll(
            Enumerable.Range(0, Callers)
                .Select(index => Task.Run(
                    () => CancelAsync(order.PublicId, OrderCancelledBy.Staff, $"Caller {index}."),
                    Token)));

        Assert.Single(outcomes, outcome => outcome is CancelOrderOutcome.Applied);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Cancelled, row.Status);
        Assert.Single(row.StatusHistory, entry => entry.ToStatus == OrderStatus.Cancelled);
        Assert.Equal(before.Reserved - 2, (await StockAsync(variantId)).Reserved);

        // One audit row, not four: a claim that affected no rows must enlist nothing.
        Assert.Single(await AuditRowsAsync(order.PublicId));
    }

    /// <summary>
    /// The cancel transaction gives back everything this order was holding (doc 08 §5.6 step 3).
    /// </summary>
    /// <remarks>
    /// <b>This says nothing about doc 08 §5.3's duplicate-variant merge, and its first version claimed it
    /// did.</b> That version described "two lines of the same variant merged before the release" on a
    /// fixture that has one line of quantity three — <c>PlaceOrderAsync(quantity: 3)</c> puts one bag item
    /// in, so there is no duplicate to merge — and said "the quantity is what proves the merge happened at
    /// all", which it cannot.
    /// <para>
    /// <b>And no functional test can prove that merge, which is worth stating rather than leaving as a
    /// claim somebody re-reads as coverage.</b> Measured: deleting the <c>GroupBy</c> from
    /// <c>OrderStockRelease</c> entirely leaves all of <c>OrderCancellationTests</c> and
    /// <c>OrderExpiryTests</c> green. It has to, because two statements of 2 and 1 reach the same
    /// <c>Reserved</c> as one statement of 3 and each still affects exactly one row. The merge exists to
    /// stop one release taking the same stock row twice with a third writer able to get between — a
    /// deadlock, not a wrong figure — and a deadlock is produced by an interleaving a test cannot compel.
    /// The thing that protects it is that there is now <i>one</i> copy of the loop rather than two, which
    /// is the extraction's real argument.
    /// </para>
    /// <para>
    /// What this does assert is doc 08 §5.9's half-invariant that a cancel owns: after the cancel this
    /// order holds nothing, and the variant's <c>Reserved</c> is back to where it started.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_cancel_leaves_this_order_holding_nothing()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 3, onHand: 9);
        var orderId = await OrderIdAsync(order.PublicId);

        Assert.Equal(3, (await StockAsync(variantId)).Reserved);

        Assert.IsType<CancelOrderOutcome.Applied>(
            await CancelAsync(order.PublicId, OrderCancelledBy.Staff, "Out of scope for them."));

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Doc 08 §5.9 compares `StockLevels.Reserved` with the lines of orders in `StockState = 0`, and
        // this order is no longer in that set — which is the half of the invariant a cancel owns. The
        // *global* invariant is deliberately not asserted here, for the reason `OrderExpiryTests` records
        // in full: `SetStockAsync` zeroes `Reserved`, so it is false across a call to it.
        var stillHeldByThisOrder = await db.OrderLines
            .AsNoTracking()
            .Where(line =>
                line.OrderId == orderId
                && line.Order!.StockState == OrderStockState.Reserved)
            .SumAsync(line => (int)line.Quantity, Token);

        Assert.Equal(0, stillHeldByThisOrder);
        Assert.Equal(0, (await StockAsync(variantId)).Reserved);
    }

    /// <summary>
    /// <b>The payment statement closes the <i>open</i> payment and only that one.</b>
    /// </summary>
    /// <remarks>
    /// Doc 08 §4.3 gives cancel exactly two payment edges — <c>Pending → Cancelled</c> and
    /// <c>Initiated → Cancelled</c> — and the statement's <c>Status IN (0, 1)</c> is what keeps it to
    /// them. <b>Nothing pinned that clause</b>: widening the <c>WHERE</c> to every status left all fifteen
    /// of these tests green, which is how this test came to exist. The mutation is not harmless. A
    /// rejected bank transfer is <c>Failed</c> on an order that is still <c>PendingPayment</c> — reachable
    /// today, through the bank queue — and rewriting it to <c>Cancelled</c> would destroy the record that
    /// staff looked at the money and said no. The same clause is what will keep a
    /// <c>CapturedRequiresRefund</c> row intact when PayPal lands (§4.2 step 3.4, W4-01): captured money
    /// relabelled as a cancelled payment is money nobody would know to refund.
    /// </remarks>
    [Fact]
    public async Task A_cancel_closes_the_open_payment_and_leaves_a_rejected_one_alone()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 1, onHand: 3);
        var orderId = await OrderIdAsync(order.PublicId);

        // Doc 11 P7: staff found no credit, so the payment is Failed and the order has not moved.
        Assert.IsType<BankTransferDecisionResult.Applied>(
            await RejectAsync(orderId, "No credit found against that reference."));

        var rejected = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.PendingPayment, rejected.Status);
        Assert.Equal(PaymentStatus.Failed, Assert.Single(rejected.Payments).Status);

        var before = await StockAsync(variantId);

        Assert.IsType<CancelOrderOutcome.Applied>(
            await CancelAsync(order.PublicId, OrderCancelledBy.Staff, "They gave up on the transfer."));

        var row = await ReadOrderAsync(order.PublicId);

        Assert.Equal(OrderStatus.Cancelled, row.Status);
        Assert.Equal(OrderStockState.Released, row.StockState);

        // **Still Failed.** Doc 08 §4.3 has no `Failed → Cancelled` edge, and the rejection is the record
        // of a decision a person made about money.
        Assert.Equal(PaymentStatus.Failed, Assert.Single(row.Payments).Status);

        // And the stock still went back exactly once: the cancel did its own work, it simply left that
        // row alone.
        Assert.Equal(before.Reserved - 1, (await StockAsync(variantId)).Reserved);
    }

    /// <summary>
    /// <b>A customer pressing Cancel while a staff member approves the transfer.</b> The order row's own
    /// claim is the only thing standing between "the shop has the money" and "the items are back on
    /// sale".
    /// </summary>
    /// <remarks>
    /// Both transactions claim <c>Id = @o AND Status = 0 AND StockState = 0</c>, so doc 08 §5.2's
    /// argument says SQL Server takes an update lock, the loser re-evaluates the predicate against the row
    /// as the winner left it and affects 0 rows. What that argument has to produce is one of exactly two
    /// outcomes and never a mixture: <b>Paid and Committed with the stock deducted</b>, or <b>Cancelled
    /// and Released with the stock given back</b>. The combination worth fearing is a paid order whose
    /// items somebody else can now buy.
    /// <para>
    /// Which one wins is deliberately not asserted — that would be an assertion about scheduling. Six
    /// rounds, the same number the cancel-versus-expiry race uses.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Cancel_racing_a_staff_approval_leaves_the_order_wholly_one_or_wholly_the_other()
    {
        const int Rounds = 6;

        for (var round = 1; round <= Rounds; round++)
        {
            var (order, variantId) = await PlaceOrderAsync(quantity: 1, onHand: 4);
            var orderId = await OrderIdAsync(order.PublicId);
            var before = await StockAsync(variantId);

            var cancel = Task.Run(
                () => CancelAsync(order.PublicId, OrderCancelledBy.Customer, reason: null),
                Token);
            var approve = Task.Run(() => ApproveAsync(orderId, "Money is in."), Token);

            var cancelled = await cancel is CancelOrderOutcome.Applied;
            var paid = await approve is BankTransferDecisionResult.Applied;

            Assert.True(
                cancelled ^ paid,
                $"Round {round}: cancelled={cancelled}, paid={paid}. Exactly one must win.");

            var row = await ReadOrderAsync(order.PublicId);
            var after = await StockAsync(variantId);
            var payment = Assert.Single(row.Payments);

            if (cancelled)
            {
                Assert.Equal(OrderStatus.Cancelled, row.Status);
                Assert.Equal(OrderStockState.Released, row.StockState);
                Assert.Equal(PaymentStatus.Cancelled, payment.Status);
                Assert.False(payment.IsOrderSettlement);

                // Given back, not taken: `Reserved` falls and `OnHand` does not.
                Assert.Equal(before.Reserved - 1, after.Reserved);
                Assert.Equal(before.OnHand, after.OnHand);
                Assert.Null(row.InvoiceNumber);
            }
            else
            {
                Assert.Equal(OrderStatus.Paid, row.Status);
                Assert.Equal(OrderStockState.Committed, row.StockState);
                Assert.Equal(PaymentStatus.Succeeded, payment.Status);
                Assert.True(payment.IsOrderSettlement);

                // Deducted, not given back (doc 08 §5.5): both figures fall by the quantity.
                Assert.Equal(before.Reserved - 1, after.Reserved);
                Assert.Equal(before.OnHand - 1, after.OnHand);
                Assert.NotNull(row.InvoiceNumber);

                // And the loser left nothing of itself behind on an order the shop has been paid for.
                Assert.Null(row.CancelReason);
            }

            Assert.Single(
                row.StatusHistory,
                entry => entry.ToStatus is OrderStatus.Cancelled or OrderStatus.Paid);
        }
    }

    /// <summary>
    /// Doc 08 §4.1's one edge out of <c>Cancelled</c>, which W3-03 already built: a cancelled order whose
    /// money arrives can still be accepted.
    /// </summary>
    /// <remarks>
    /// <b>This is the test that proves cancel did not make the order unreachable.</b> The payment has to
    /// leave <c>UX_Payments_OneOpen</c>'s filter (doc 08 §4.3 N7) for <c>AcceptLateAsync</c> to settle it,
    /// and the admin detail's <c>CanAcceptLatePayment</c> flag requires a bank payment in <c>Expired</c> or
    /// <c>Pending</c> — not <c>Cancelled</c>. So this asserts what the <i>transaction</i> can do, and the
    /// gap in the screen is written up in the journal rather than quietly fixed from here.
    /// </remarks>
    [Fact]
    public async Task A_cancelled_order_can_still_take_a_late_payment()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 1, onHand: 4);
        var orderId = await OrderIdAsync(order.PublicId);

        Assert.IsType<CancelOrderOutcome.Applied>(
            await CancelAsync(order.PublicId, OrderCancelledBy.Customer, reason: null));

        var released = await StockAsync(variantId);

        var accepted = await AcceptLateAsync(orderId, "The transfer came in after they cancelled.");

        Assert.IsType<BankTransferDecisionResult.Applied>(accepted);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Paid, row.Status);
        Assert.Equal(OrderStockState.Committed, row.StockState);

        // Doc 08 §5.7: the stock is re-acquired, so OnHand falls by the quantity.
        Assert.Equal(released.OnHand - 1, (await StockAsync(variantId)).OnHand);

        // Doc 08 §4.1: `ClosedAtUtc` goes back to null, because the order is open again.
        Assert.Null(row.ClosedAtUtc);
    }

    /// <summary>
    /// <c>OrderConfirmationDto.CanCancel</c> on the wire, for every state that decides it.
    /// </summary>
    /// <remarks>
    /// <b>This is issue #126's pairing, and it is the only test that can see this field.</b> Playwright
    /// never starts the .NET API — it runs against Node stubs — so the storefront's Cancel panel could be
    /// drawn from a flag the real reader never sends and all of the browser tests would still be green. A
    /// dropped payload field once left 860 of them green.
    /// <para>
    /// All six states in one test, through the real endpoint and the real reader, because the question is
    /// one question: <i>does the flag agree with what the transaction will do?</i> Splitting it into six
    /// would hide the thing worth seeing, which is that the <c>true</c> case and the five <c>false</c>
    /// cases are answered by the same code path. Each state is arranged on a <b>separate order</b>, so one
    /// fixture cannot satisfy the next assertion.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_order_payload_says_whether_the_shopper_may_cancel()
    {
        var api = NewCheckoutVisitor();

        // 1. Awaiting payment, nothing in flight: the one state in which it is true.
        var (open, _) = await PlaceOrderAsync();
        Assert.True(await CanCancelAsync(api, open.PublicId), "a fresh unpaid order");

        // 2. Paid. Doc 11 P10's table: refunded, never cancelled.
        var (paid, _) = await PlaceOrderAsync();
        Assert.IsType<BankTransferDecisionResult.Applied>(
            await ApproveAsync(await OrderIdAsync(paid.PublicId)));
        Assert.False(await CanCancelAsync(api, paid.PublicId), "a paid order");

        // 3. Already cancelled. A closing state.
        var (closed, _) = await PlaceOrderAsync();
        Assert.IsType<CancelOrderOutcome.Applied>(
            await CancelAsync(closed.PublicId, OrderCancelledBy.Customer, reason: null));
        Assert.False(await CanCancelAsync(api, closed.PublicId), "a cancelled order");

        // 4. Expired. The sweep got there first, and the stock is already released.
        var (lapsed, _) = await PlaceOrderAsync();
        Assert.True(await ExpireAsync(await OrderIdAsync(lapsed.PublicId)));
        Assert.False(await CanCancelAsync(api, lapsed.PublicId), "an expired order");

        // 5. A capture in flight. Doc 11 P10 step 2's third guard, and a state the page cannot see any
        // other way — which is the whole argument for sending the flag rather than inferring it.
        var (locked, _) = await PlaceOrderAsync();
        await SetCaptureLockAsync(await OrderIdAsync(locked.PublicId), DateTime.UtcNow.AddHours(2));
        Assert.False(await CanCancelAsync(api, locked.PublicId), "an order with a capture lock");

        // 6. A payment that has already settled it, beside an order still reading PendingPayment. P10 step
        // 2's fourth guard, and the second state invisible to the page.
        var (settling, _) = await PlaceOrderAsync();
        await MarkPaymentSettlingAsync(await OrderIdAsync(settling.PublicId));
        Assert.False(await CanCancelAsync(api, settling.PublicId), "an order with a settling payment");
    }

    /// <summary>
    /// The shopper's own route: it cancels once, and every refusal afterwards is a <b>bare 404</b>.
    /// </summary>
    /// <remarks>
    /// <b>The 404 is the contract, not an accident.</b> The token never expires (doc 11 §7 G-41) and is a
    /// guest's only credential, so "no such order" and "that order can no longer be cancelled" must be
    /// indistinguishable — the same rule <c>GET /api/orders/{token}</c> and the payment-reference route
    /// already follow. The body is asserted empty, because a problem document would leak the distinction
    /// the status code is hiding.
    /// </remarks>
    [Fact]
    public async Task The_customer_route_cancels_once_and_then_answers_a_bare_404()
    {
        var api = NewCheckoutVisitor();
        var (order, variantId) = await PlaceOrderAsync(quantity: 2, onHand: 5);

        var before = await StockAsync(variantId);

        using var first = await api.CancelOrderAsync(order.PublicId.ToString("D"), Token);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var body = await first.Content.ReadFromJsonAsync<CancelOrderDto>(
            CheckoutApiClient.JsonOptions,
            Token);

        Assert.Equal(order.OrderNumber, body!.OrderNumber);

        // The rows, not the response: the route is a thin shell and the transaction is the thing.
        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Cancelled, row.Status);
        Assert.Equal(OrderStatusChangeSource.Customer, Assert.Single(
            row.StatusHistory,
            entry => entry.ToStatus == OrderStatus.Cancelled).Source);
        Assert.Equal(before.Reserved - 2, (await StockAsync(variantId)).Reserved);

        // And the order page now reads as cancelled, with no instructions to pay into. `statusLabel` is
        // the server's wording and is what the page prints; before W3-04b nothing could reach a status
        // that exercised it.
        var (reloaded, confirmation) = await api.GetOrderAsync(order.PublicId.ToString("D"), Token);
        Assert.Equal(HttpStatusCode.OK, reloaded.StatusCode);
        Assert.Equal(nameof(OrderStatus.Cancelled), confirmation!.Status);
        Assert.Equal("Cancelled", confirmation.StatusLabel);
        Assert.False(confirmation.CanCancel);
        Assert.Null(confirmation.Payment);

        using var second = await api.CancelOrderAsync(order.PublicId.ToString("D"), Token);
        using var unknown = await api.CancelOrderAsync(Guid.NewGuid().ToString("D"), Token);

        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // **The same answer, field for field, with the trace id taken out** — which is the property, not
        // "the body is empty". Two earlier versions of this assertion were wrong about the API and each
        // was worth the correction: a bare 404 is filled in by the host's problem-details middleware with
        // RFC 9110's generic document (so it is not empty), and that document carries a per-request
        // `traceId` (so it is not byte-identical either). Neither says anything about the order, so the
        // guarantee holds — and what has to be asserted is that a stranger holding a guessed token cannot
        // tell their guess apart from somebody else's real order, which only a field-by-field comparison
        // of the two responses says.
        Assert.Equal(
            await ProblemWithoutTraceAsync(unknown),
            await ProblemWithoutTraceAsync(second));

        // Nothing changed on the second or third attempt.
        Assert.Single(
            (await ReadOrderAsync(order.PublicId)).StatusHistory,
            entry => entry.ToStatus == OrderStatus.Cancelled);
    }

    /// <summary>
    /// A paid order answers 404 on the customer route, like every other refusal.
    /// </summary>
    /// <remarks>
    /// Its own test rather than a case of the one above, because this is the one a shopper could actually
    /// reach — they paid an hour ago and press the button they still have on screen — and because the
    /// thing that must not happen is not a wrong status code: it is stock released from an order whose
    /// money the shop has.
    /// </remarks>
    [Fact]
    public async Task A_paid_order_cannot_be_cancelled_through_the_customer_route()
    {
        var api = NewCheckoutVisitor();
        var (order, variantId) = await PlaceOrderAsync(quantity: 1, onHand: 3);

        Assert.IsType<BankTransferDecisionResult.Applied>(
            await ApproveAsync(await OrderIdAsync(order.PublicId)));

        var paid = await StockAsync(variantId);

        using var response = await api.CancelOrderAsync(order.PublicId.ToString("D"), Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Paid, row.Status);
        Assert.Equal(OrderStockState.Committed, row.StockState);
        Assert.Equal(paid, await StockAsync(variantId));
    }

    /// <summary>
    /// One refusal's problem document with its per-request <c>traceId</c> removed, so two of them can be
    /// compared for the thing that matters: whether either discloses which order the token named.
    /// </summary>
    private static async Task<string> ProblemWithoutTraceAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Token);

        using var document = JsonDocument.Parse(body);

        var fields = document.RootElement.EnumerateObject()
            .Where(property => property.Name is not "traceId")
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => $"{property.Name}={property.Value}");

        return string.Join('|', fields);
    }

    /// <summary>Whether <c>GET /api/orders/{token}</c> offers the shopper a Cancel control.</summary>
    private static async Task<bool> CanCancelAsync(CheckoutApiClient api, Guid publicId)
    {
        var (response, order) = await api.GetOrderAsync(publicId.ToString("D"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return order!.CanCancel;
    }

    // ----------------------------------------------------------------------------------------------
    // Fixtures. Each one rewrites a column on **this test's own** row, never on shared reference data.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// One instant, rounded to what a <c>datetime2(3)</c> column can hold.
    /// </summary>
    private static DateTime Millisecond(DateTime value) =>
        new(
            value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond),
            DateTimeKind.Utc);

    private Task<CancelOrderOutcome> CancelAsync(
        Guid orderPublicId,
        OrderCancelledBy cancelledBy,
        string? reason,
        DateTime? nowUtc = null) =>
        InScopeAsync(services => services
            .GetRequiredService<IOrderCancellationStore>()
            .CancelAsync(
                new OrderCancellation(
                    orderPublicId,
                    cancelledBy,
                    cancelledBy == OrderCancelledBy.Staff ? Fulfiller() : null,
                    reason,
                    nowUtc ?? DateTime.UtcNow),
                Token));

    /// <summary>Writes <c>CaptureLockedUntilUtc</c>, which nothing in this build does (it is PayPal's).</summary>
    private async Task SetCaptureLockAsync(int orderId, DateTime until)
    {
        using var scope = CreateScope();

        var moved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
            .Where(candidate => candidate.Id == orderId)
            .ExecuteUpdateAsync(
                candidate => candidate.SetProperty(row => row.CaptureLockedUntilUtc, until),
                Token);

        Assert.Equal(1, moved);
    }

    /// <summary>
    /// Flags this order's payment as the one that settles it, leaving the order <c>PendingPayment</c>.
    /// </summary>
    /// <remarks>
    /// <b>The payment has to be moved to <c>Succeeded</c> as well, and that is the schema talking rather
    /// than tidiness.</b> <c>CK_Payments_IsOrderSettlement</c> allows the flag only on a payment in
    /// Succeeded, PartiallyRefunded or Refunded — the first attempt at this fixture set the flag on the
    /// <c>Pending</c> row and the database refused it, which is how the guard's reachability came to be
    /// checked properly rather than assumed. So the state this produces is a <i>Succeeded settlement
    /// beside a PendingPayment order</i>: legal in the schema, written by no path in this build, and the
    /// only shape in which doc 11 P10 step 2's "no settling payment" can be the condition that refuses.
    /// </remarks>
    private async Task MarkPaymentSettlingAsync(int orderId)
    {
        using var scope = CreateScope();

        var moved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Payments
            .Where(payment => payment.OrderId == orderId)
            .ExecuteUpdateAsync(
                payment => payment
                    .SetProperty(row => row.Status, PaymentStatus.Succeeded)
                    .SetProperty(row => row.IsOrderSettlement, true),
                Token);

        Assert.Equal(1, moved);
    }

    /// <summary>
    /// Turns this order's open bank payment into a PayPal payment in <c>PENDING</c>.
    /// </summary>
    /// <remarks>
    /// The <i>payment's</i> provider code, not the order's: doc 08 §4.1 R1 and doc 11 P10's table both key
    /// the rule on the payment, and rewriting the order's would also need its <c>PaymentProvider</c>
    /// foreign key to point at a provider row the seeder deliberately leaves disabled.
    /// </remarks>
    private async Task MakePaymentPayPalPendingAsync(int orderId)
    {
        using var scope = CreateScope();

        var moved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Payments
            .Where(payment => payment.OrderId == orderId)
            .ExecuteUpdateAsync(
                payment => payment
                    .SetProperty(row => row.ProviderCode, PaymentProviderCodes.PayPal)
                    .SetProperty(row => row.Status, PaymentStatus.Pending),
                Token);

        Assert.Equal(1, moved);
    }

    private async Task<IReadOnlyList<(string? AfterJson, int? UserId, string? IpAddress)>>
        AuditRowsAsync(Guid publicId)
    {
        using var scope = CreateScope();

        var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .AsNoTracking()
            .Where(log =>
                log.EntityType == nameof(Order)
                && log.EntityId == publicId.ToString()
                && log.Action == AuditActions.StatusChange)
            .Select(log => new { log.AfterJson, log.UserId, log.IpAddress })
            .ToListAsync(Token);

        return rows.Select(row => (row.AfterJson, row.UserId, row.IpAddress)).ToList();
    }

    private async Task AssertAuditedAsync(Guid publicId, string what)
    {
        var rows = await AuditRowsAsync(publicId);

        Assert.Contains(
            rows,
            row => row.AfterJson is not null
                && row.AfterJson.Contains(what, StringComparison.Ordinal));

        // The actor, not merely "a row exists": an audit trail whose user column is null is not one.
        Assert.Contains(rows, row => row.UserId == 7001 && row.IpAddress == "203.0.113.9");

        // The reason is in the audit row too. It is a staff member's statement about why an order was
        // closed and is already readable by Orders.View — unlike the internal note, which
        // OrderFulfilmentStore deliberately keeps out because it can carry a telephone call's content.
        Assert.Contains(
            rows,
            row => row.AfterJson is not null
                && row.AfterJson.Contains("\"reason\"", StringComparison.Ordinal));
    }
}
