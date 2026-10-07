using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Doc 11 P7 steps 6a, 6b and 8, against a real SQL Server (PAY-01).
/// <para>
/// <b>Every assertion is on the rows, never on the return value alone.</b> The result says what the store
/// believes it did; <c>sales.Orders</c>, <c>sales.Payments</c>, <c>inventory.StockLevels</c>,
/// <c>sales.OrderStatusHistory</c> and <c>ops.AuditLogs</c> are what it did. That distinction is the
/// whole point of an integration test here: the thing that can go wrong is a statement that affected the
/// wrong number of rows, and no in-memory double can show it.
/// </para>
/// </summary>
public sealed class BankTransferReviewTests(SqlServerDatabaseFixture sqlServer) : BankTransferTestBase(sqlServer)
{
    /// <summary><c>INV-2026-000045</c>: doc 08 §3.5's shape, with a four-digit year.</summary>
    private static readonly Regex InvoiceNumberShape = new(@"^INV-\d{4}-\d{6,}$", RegexOptions.None);

    [Fact]
    public async Task Approving_pays_the_order_sells_the_stock_and_issues_an_invoice_number()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 2, onHand: 5);
        var orderId = await OrderIdAsync(order.PublicId);

        var before = await StockAsync(variantId);
        Assert.Equal(2, before.Reserved);

        var result = await ApproveAsync(orderId, "Credit on the statement at 14:02.");

        var applied = Assert.IsType<BankTransferDecisionResult.Applied>(result);
        Assert.Equal(order.OrderNumber, applied.OrderNumber);
        Assert.NotNull(applied.InvoiceNumber);
        Assert.Matches(InvoiceNumberShape, applied.InvoiceNumber);

        var row = await ReadOrderAsync(order.PublicId);

        // Doc 08 §4.1's whole Paid-effects list, read back.
        Assert.Equal(OrderStatus.Paid, row.Status);
        Assert.Equal(OrderStockState.Committed, row.StockState);
        Assert.NotNull(row.PaidAtUtc);
        Assert.Null(row.CaptureLockedUntilUtc);
        Assert.Null(row.ClosedAtUtc);
        Assert.Equal(applied.InvoiceNumber, row.InvoiceNumber);
        Assert.NotNull(row.InvoiceIssuedAtUtc);

        // Every timestamp the decision wrote is the one instant it was given, so an audit trail cannot
        // read as several events a few milliseconds apart.
        var payment = Assert.Single(row.Payments);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.True(payment.IsOrderSettlement);
        Assert.Equal(4242, payment.ApprovedByUserId);
        Assert.Equal("Credit on the statement at 14:02.", payment.ApprovalNote);
        Assert.Equal(row.PaidAtUtc, payment.ApprovedAtUtc);
        Assert.Equal(row.PaidAtUtc, payment.CompletedAtUtc);
        Assert.Equal(row.PaidAtUtc, row.InvoiceIssuedAtUtc);

        // Doc 08 §5.5: reserved becomes sold. OnHand down by the quantity, Reserved down by it too, so
        // Available is unchanged — the goods were already promised away.
        var after = await StockAsync(variantId);
        Assert.Equal(before.OnHand - 2, after.OnHand);
        Assert.Equal(before.Reserved - 2, after.Reserved);
        Assert.Equal(before.Available, after.Available);

        var history = Assert.Single(row.StatusHistory, entry => entry.ToStatus == OrderStatus.Paid);
        Assert.Equal(OrderStatus.PendingPayment, history.FromStatus);
        Assert.Equal(OrderStatusChangeSource.Staff, history.Source);
        Assert.Equal(4242, history.ChangedByUserId);
        Assert.Contains("approved", history.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("14:02", history.Note, StringComparison.Ordinal);

        // Doc 11 P7: "every approve and reject is audited". sales.Payments is not [A], so the SaveChanges
        // interceptor never sees any of this and the row has to be written deliberately.
        var audit = await AuditRowAsync(order.PublicId);
        Assert.Equal(AuditActions.Approve, audit.Action);
        Assert.Equal(4242, audit.UserId);
        Assert.Contains("\"decision\":\"approve\"", audit.AfterJson, StringComparison.Ordinal);
        Assert.Contains(applied.InvoiceNumber, audit.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejecting_fails_the_payment_and_leaves_the_order_awaiting_payment()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 1, onHand: 4);
        var orderId = await OrderIdAsync(order.PublicId);

        var before = await StockAsync(variantId);

        var result = await RejectAsync(orderId, "The amount is Rs. 500 short.");

        var applied = Assert.IsType<BankTransferDecisionResult.Applied>(result);

        // A rejection issues no invoice number, because nothing was invoiced.
        Assert.Null(applied.InvoiceNumber);

        var row = await ReadOrderAsync(order.PublicId);

        // Doc 11 P7 step 6b: "the order stays PendingPayment until its due time". Everything about the
        // order is asserted unchanged, not just its status — a rejection that closed the order or released
        // its stock would take the shopper's second chance away.
        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Equal(OrderStockState.Reserved, row.StockState);
        Assert.Null(row.PaidAtUtc);
        Assert.Null(row.ClosedAtUtc);
        Assert.Null(row.InvoiceNumber);

        var payment = Assert.Single(row.Payments);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.False(payment.IsOrderSettlement);
        Assert.Equal("REJECTED", payment.FailureCode);
        Assert.Equal("The amount is Rs. 500 short.", payment.FailureMessage);
        Assert.Equal("The amount is Rs. 500 short.", payment.ApprovalNote);
        Assert.Equal(4242, payment.ApprovedByUserId);
        Assert.NotNull(payment.ApprovedAtUtc);

        var stock = await StockAsync(variantId);
        Assert.Equal(before, stock);

        // The order did not move, so FromStatus and ToStatus are both PendingPayment — the honest encoding
        // of "stayed where it was", with the reason in the note. Doc 11 P7 requires the rejection to be in
        // the history whether or not the status changed.
        var history = Assert.Single(
            row.StatusHistory,
            entry => entry.Note is not null
                && entry.Note.Contains("rejected", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(OrderStatus.PendingPayment, history.FromStatus);
        Assert.Equal(OrderStatus.PendingPayment, history.ToStatus);
        Assert.Equal(OrderStatusChangeSource.Staff, history.Source);
    }

    [Fact]
    public async Task A_rejected_transfer_can_be_resubmitted_as_a_new_pending_payment()
    {
        var (order, _) = await PlaceOrderAsync();
        var orderId = await OrderIdAsync(order.PublicId);

        Assert.IsType<BankTransferDecisionResult.Applied>(await RejectAsync(orderId));

        // Doc 08 §3.5 N7: after a rejection a resubmission creates a *new* Pending row. This is the one
        // place UX_Payments_OneOpen could be made to fight, and the first row is Failed so it does not.
        var reopened = await InScopeAsync(services => services
            .GetRequiredService<IBankTransferProofStore>()
            .OpenPaymentAsync(order.PublicId, DateTime.UtcNow, Token));

        Assert.NotNull(reopened);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(2, row.Payments.Count);
        Assert.Single(row.Payments, payment => payment.Status == PaymentStatus.Failed);

        var open = Assert.Single(row.Payments, payment => payment.Status == PaymentStatus.Pending);
        Assert.Equal(reopened, open.Id);

        // The new row asks for the same sum the shopper agreed to (G-10), read from the order and never
        // from a caller.
        Assert.Equal(row.GrandTotal, open.Amount);
        Assert.Equal("LKR", open.Currency);
        Assert.False(open.IsOrderSettlement);

        // And the new row is the one an approval then settles.
        var approved = Assert.IsType<BankTransferDecisionResult.Applied>(await ApproveAsync(orderId));
        Assert.NotNull(approved.InvoiceNumber);

        var settled = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Paid, settled.Status);
        Assert.Equal(
            open.Id,
            Assert.Single(settled.Payments, payment => payment.IsOrderSettlement).Id);
    }

    [Fact]
    public async Task Approving_twice_pays_once_and_tells_the_second_approver_why()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 3, onHand: 6);
        var orderId = await OrderIdAsync(order.PublicId);

        var before = await StockAsync(variantId);

        Assert.IsType<BankTransferDecisionResult.Applied>(await ApproveAsync(orderId));

        var second = await ApproveAsync(orderId);

        var refused = Assert.IsType<BankTransferDecisionResult.Refused>(second);
        Assert.Equal(BankTransferRefusalCodes.AlreadyPaid, refused.Code);
        Assert.Equal(OrderStatus.Paid, refused.OrderStatus);
        Assert.Equal(PaymentStatus.Succeeded, refused.PaymentStatus);

        // The second attempt changed nothing: one settlement, one invoice number, one deduction.
        var row = await ReadOrderAsync(order.PublicId);
        Assert.Single(row.Payments, payment => payment.IsOrderSettlement);

        var after = await StockAsync(variantId);
        Assert.Equal(before.OnHand - 3, after.OnHand);
        Assert.Equal(before.Reserved - 3, after.Reserved);
    }

    [Fact]
    public async Task Rejecting_after_an_approval_is_refused_and_the_money_stays_settled()
    {
        var (order, _) = await PlaceOrderAsync();
        var orderId = await OrderIdAsync(order.PublicId);

        Assert.IsType<BankTransferDecisionResult.Applied>(await ApproveAsync(orderId));

        // A staff member whose screen is a minute old. There is no open Pending row any more, so the
        // rejection has nothing to act on — and crucially it does not reach the order.
        var refused = Assert.IsType<BankTransferDecisionResult.Refused>(
            await RejectAsync(orderId, "Changed my mind."));

        Assert.Equal(BankTransferRefusalCodes.AlreadyPaid, refused.Code);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Paid, row.Status);

        var payment = Assert.Single(row.Payments);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Null(payment.FailureCode);
    }

    [Fact]
    public async Task Approving_an_expired_order_is_refused_and_points_at_accept_late_payment()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 2, onHand: 5);
        var orderId = await OrderIdAsync(order.PublicId);

        Assert.True(await ExpireAsync(orderId));

        var released = await StockAsync(variantId);
        Assert.Equal(0, released.Reserved);

        var refused = Assert.IsType<BankTransferDecisionResult.Refused>(await ApproveAsync(orderId));

        // The code is what matters: it is what lets the screen offer the one action that *will* work,
        // which is doc 11 P7's business rule ("the approver is told and the screen reloads").
        Assert.Equal(BankTransferRefusalCodes.OrderExpired, refused.Code);
        Assert.Equal(OrderStatus.Expired, refused.OrderStatus);
        Assert.Equal(PaymentStatus.Expired, refused.PaymentStatus);
        Assert.Contains("Accept late payment", refused.Message, StringComparison.Ordinal);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Expired, row.Status);
        Assert.Null(row.InvoiceNumber);
    }

    [Fact]
    public async Task Accepting_a_late_payment_pays_the_order_and_takes_the_stock_again()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 2, onHand: 5);
        var orderId = await OrderIdAsync(order.PublicId);

        Assert.True(await ExpireAsync(orderId));

        var released = await StockAsync(variantId);
        Assert.Equal(0, released.Reserved);
        Assert.Equal(5, released.OnHand);

        var result = await AcceptLateAsync(orderId, "Credit landed on day 3.");

        var applied = Assert.IsType<BankTransferDecisionResult.Applied>(result);
        Assert.Matches(InvoiceNumberShape, applied.InvoiceNumber);

        var row = await ReadOrderAsync(order.PublicId);

        Assert.Equal(OrderStatus.Paid, row.Status);
        Assert.Equal(OrderStockState.Committed, row.StockState);

        // Doc 08 §4.1: ClosedAtUtc is cleared, and PaidAtUtc is *today* — revenue lands on the approval
        // date, not the placed date (doc 08 §8.1).
        Assert.Null(row.ClosedAtUtc);
        Assert.NotNull(row.PaidAtUtc);
        Assert.True(row.PaidAtUtc > row.PlacedAtUtc);

        // Doc 08 §4.3 N7: the Expired row itself settles.
        var payment = Assert.Single(row.Payments);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.True(payment.IsOrderSettlement);

        // Doc 08 §5.7: OnHand falls and Reserved is untouched, because the reservation was given back at
        // expiry. So Available falls too — the goods are gone from the shelf now, not re-promised.
        var after = await StockAsync(variantId);
        Assert.Equal(released.OnHand - 2, after.OnHand);
        Assert.Equal(0, after.Reserved);
        Assert.Equal(released.Available - 2, after.Available);

        var history = Assert.Single(row.StatusHistory, entry => entry.ToStatus == OrderStatus.Paid);
        Assert.Equal(OrderStatus.Expired, history.FromStatus);
        Assert.Equal(OrderStatusChangeSource.Staff, history.Source);
        Assert.Contains("Late payment accepted", history.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_late_payment_is_refused_with_every_short_line_when_the_stock_has_gone()
    {
        var variantIds = await CheckoutVariantIdsAsync(2);
        await SetStockAsync(variantIds[0], 2);
        await SetStockAsync(variantIds[1], 2);

        var cart = NewVisitor();
        foreach (var variantId in variantIds)
        {
            var (added, _) = await cart.AddAsync(variantId, 2, Token);
            Assert.Equal(System.Net.HttpStatusCode.OK, added.StatusCode);
        }

        var api = new CheckoutApiClient(cart.Http);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);
        var (_, session) = await api.GetAsync(colombo, standard, Token);

        var (placed, order) = await api.PlaceAsync(
            ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session)),
            Token);

        Assert.Equal(System.Net.HttpStatusCode.Created, placed.StatusCode);
        var orderId = await OrderIdAsync(order!.PublicId);

        Assert.True(await ExpireAsync(orderId));

        // Both lines sold to somebody else while the order sat expired. Both have to be reported: a staff
        // member told about one of two will press the button again and be told about the other
        // (doc 11 P7 step 8, "refused with the list of short lines").
        await SetStockAsync(variantIds[0], 0);
        await SetStockAsync(variantIds[1], 1);

        var result = await AcceptLateAsync(orderId);

        var shortStock = Assert.IsType<BankTransferDecisionResult.ShortStock>(result);
        Assert.Equal(2, shortStock.Lines.Count);

        var first = shortStock.Lines.Single(line => line.VariantId == variantIds[0]);
        var second = shortStock.Lines.Single(line => line.VariantId == variantIds[1]);

        Assert.Equal(0, first.Available);
        Assert.Equal(1, second.Available);

        // **Named, so a staff member can act on it.** The refusal used to carry variant ids alone, which
        // on a screen with no order-detail page is a dead end. Every field is a placement-time snapshot
        // off sales.OrderLines, so this is the name the customer bought under.
        Assert.False(string.IsNullOrWhiteSpace(first.Sku));
        Assert.False(string.IsNullOrWhiteSpace(first.ProductName));
        Assert.True(first.Needed > 0);
        Assert.True(second.Needed > 0);

        // **Nothing was written.** The order is still Expired, no invoice number was issued, the payment
        // is still Expired, and — the one a partial deduction would break — the stock is exactly as the
        // arrangement left it.
        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Expired, row.Status);
        Assert.Equal(OrderStockState.Released, row.StockState);
        Assert.Null(row.InvoiceNumber);
        Assert.Equal(PaymentStatus.Expired, Assert.Single(row.Payments).Status);

        Assert.Equal(0, (await StockAsync(variantIds[0])).OnHand);
        Assert.Equal(1, (await StockAsync(variantIds[1])).OnHand);
    }

    [Fact]
    public async Task A_late_payment_on_an_order_that_never_expired_is_refused()
    {
        var (order, _) = await PlaceOrderAsync();
        var orderId = await OrderIdAsync(order.PublicId);

        var refused = Assert.IsType<BankTransferDecisionResult.Refused>(await AcceptLateAsync(orderId));

        Assert.Equal(BankTransferRefusalCodes.OrderNotClosed, refused.Code);
        Assert.Equal(OrderStatus.PendingPayment, refused.OrderStatus);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Null(row.InvoiceNumber);
    }

    [Fact]
    public async Task Invoice_numbers_are_gapless_and_a_refused_decision_consumes_none()
    {
        var first = await PlaceOrderAsync();
        var second = await PlaceOrderAsync();
        var third = await PlaceOrderAsync();

        var firstId = await OrderIdAsync(first.Order.PublicId);
        var secondId = await OrderIdAsync(second.Order.PublicId);
        var thirdId = await OrderIdAsync(third.Order.PublicId);

        var before = await InvoiceSeriesNextValueAsync();

        var one = Assert.IsType<BankTransferDecisionResult.Applied>(await ApproveAsync(firstId));

        // A decision that cannot be applied must not consume a number. This is the half of "gapless" that
        // an in-transaction allocation buys and a separate transaction would not: the order claim fails,
        // the transaction rolls back, and NextValue goes back with it.
        Assert.IsType<BankTransferDecisionResult.Refused>(await ApproveAsync(firstId));
        Assert.IsType<BankTransferDecisionResult.Refused>(await AcceptLateAsync(secondId));
        Assert.IsType<BankTransferDecisionResult.Applied>(await RejectAsync(thirdId));

        var two = Assert.IsType<BankTransferDecisionResult.Applied>(await ApproveAsync(secondId));

        var after = await InvoiceSeriesNextValueAsync();

        // Exactly two numbers were handed out, and they are consecutive.
        Assert.NotNull(after);
        Assert.Equal((before ?? 1) + 2, after);

        var firstSequence = SequenceOf(one.InvoiceNumber!);
        var secondSequence = SequenceOf(two.InvoiceNumber!);
        Assert.Equal(firstSequence + 1, secondSequence);
    }

    [Fact]
    public async Task Approving_and_expiring_at_once_leaves_one_winner_and_consistent_stock()
    {
        const int Rounds = 8;

        for (var round = 1; round <= Rounds; round++)
        {
            var (order, variantId) = await PlaceOrderAsync(quantity: 1, onHand: 3);
            var orderId = await OrderIdAsync(order.PublicId);
            var before = await StockAsync(variantId);

            // Both paths claim the order row first (doc 08 §5.3), so one of them waits for the other's
            // lock and then re-evaluates its own predicate. Either outcome is correct; what must not
            // happen is both.
            var approve = Task.Run(() => ApproveAsync(orderId), Token);
            var expire = Task.Run(() => ExpireAsync(orderId, DateTime.UtcNow.AddHours(49)), Token);

            var approveOutcome = await approve;
            var expired = await expire;

            var approved = approveOutcome is BankTransferDecisionResult.Applied;

            Assert.True(
                approved ^ expired,
                $"Round {round}: approve={approveOutcome.GetType().Name}, expire={expired}. "
                + "Exactly one of the two must win.");

            var row = await ReadOrderAsync(order.PublicId);
            var after = await StockAsync(variantId);

            if (approved)
            {
                Assert.Equal(OrderStatus.Paid, row.Status);
                Assert.Equal(OrderStockState.Committed, row.StockState);
                Assert.NotNull(row.InvoiceNumber);
                Assert.Equal(PaymentStatus.Succeeded, Assert.Single(row.Payments).Status);

                // Sold: OnHand down, Reserved down, Available unchanged.
                Assert.Equal(before.OnHand - 1, after.OnHand);
                Assert.Equal(before.Reserved - 1, after.Reserved);
            }
            else
            {
                Assert.Equal(OrderStatus.Expired, row.Status);
                Assert.Equal(OrderStockState.Released, row.StockState);
                Assert.Null(row.InvoiceNumber);
                Assert.Equal(PaymentStatus.Expired, Assert.Single(row.Payments).Status);

                // Released: OnHand unchanged, Reserved down, Available up.
                Assert.Equal(before.OnHand, after.OnHand);
                Assert.Equal(before.Reserved - 1, after.Reserved);
            }
        }
    }

    [Fact]
    public async Task Two_staff_approving_the_same_transfer_at_once_pay_it_once()
    {
        const int Rounds = 6;
        const int Contenders = 4;

        for (var round = 1; round <= Rounds; round++)
        {
            var (order, variantId) = await PlaceOrderAsync(quantity: 2, onHand: 6);
            var orderId = await OrderIdAsync(order.PublicId);
            var before = await StockAsync(variantId);

            var attempts = await Task.WhenAll(
                Enumerable.Range(0, Contenders)
                    .Select(_ => Task.Run(() => ApproveAsync(orderId), Token)));

            var applied = attempts.OfType<BankTransferDecisionResult.Applied>().ToList();

            Assert.Single(applied);

            // Every loser is told something a staff member can act on, not a 0-row mystery.
            foreach (var loser in attempts.Where(attempt => attempt is not BankTransferDecisionResult.Applied))
            {
                var refused = Assert.IsType<BankTransferDecisionResult.Refused>(loser);
                Assert.Contains(
                    refused.Code,
                    new[]
                    {
                        BankTransferRefusalCodes.AlreadyPaid,
                        BankTransferRefusalCodes.AlreadySettled,
                        BankTransferRefusalCodes.PaymentNotReviewable,
                    });
            }

            var row = await ReadOrderAsync(order.PublicId);

            // UX_Payments_OneSettlement would have refused a second settlement anyway; the point is that
            // it never had to, because the order-row claim decided it first (doc 08 §4.2).
            Assert.Single(row.Payments, payment => payment.IsOrderSettlement);
            Assert.Equal(applied[0].InvoiceNumber, row.InvoiceNumber);

            // And the stock moved exactly once.
            var after = await StockAsync(variantId);
            Assert.Equal(before.OnHand - 2, after.OnHand);
            Assert.Equal(before.Reserved - 2, after.Reserved);
        }
    }

    [Fact]
    public async Task Approving_and_rejecting_at_once_applies_exactly_one_of_them()
    {
        const int Rounds = 8;

        for (var round = 1; round <= Rounds; round++)
        {
            var (order, variantId) = await PlaceOrderAsync(quantity: 1, onHand: 4);
            var orderId = await OrderIdAsync(order.PublicId);
            var before = await StockAsync(variantId);

            // **This race is why the payment update carries its own status guard.** In the window between
            // reading the open payment and updating it, an approval can commit; without
            // `AND Status = Pending` on the statement itself the rejection would set the row that now
            // settles the order to Failed, leaving a paid order with no money against it.
            //
            // **Two things this comment used to claim are wrong, and both are corrected here.** It said
            // "reject deliberately does not claim the order row" — the shipped code claims it, and the
            // comment was left behind by the fix. And it is not true that no sequential test can show the
            // guard: a tester reverted the claim and ran this test 48 rounds without a single red, because
            // approve does six statements before committing and so reject almost always reaches its UPDATE
            // first. What this test does establish is that **exactly one decision applies** under real
            // contention, whichever wins, and that the rows agree with the winner afterwards; the claim's
            // own contribution is lock ordering and a refusal diagnosed from settled state, which the
            // store's comment now states without overstating.
            var approve = Task.Run(() => ApproveAsync(orderId), Token);
            var reject = Task.Run(() => RejectAsync(orderId, "No credit found."), Token);

            var approveOutcome = await approve;
            var rejectOutcome = await reject;

            var approved = approveOutcome is BankTransferDecisionResult.Applied;
            var rejected = rejectOutcome is BankTransferDecisionResult.Applied;

            Assert.True(
                approved ^ rejected,
                $"Round {round}: approve={approveOutcome.GetType().Name}, "
                + $"reject={rejectOutcome.GetType().Name}. Exactly one of the two must apply.");

            var row = await ReadOrderAsync(order.PublicId);
            var payment = Assert.Single(row.Payments);
            var after = await StockAsync(variantId);

            if (approved)
            {
                Assert.Equal(OrderStatus.Paid, row.Status);
                Assert.Equal(PaymentStatus.Succeeded, payment.Status);
                Assert.True(payment.IsOrderSettlement);
                Assert.Null(payment.FailureCode);
                Assert.NotNull(row.InvoiceNumber);
                Assert.Equal(before.OnHand - 1, after.OnHand);
            }
            else
            {
                Assert.Equal(OrderStatus.PendingPayment, row.Status);
                Assert.Equal(PaymentStatus.Failed, payment.Status);
                Assert.False(payment.IsOrderSettlement);
                Assert.Equal("REJECTED", payment.FailureCode);
                Assert.Null(row.InvoiceNumber);

                // The reservation is untouched, so the shopper can still transfer again.
                Assert.Equal(before, after);
            }
        }
    }

    /// <summary>
    /// Doc 08 §4.1's <c>Cancelled → Paid</c> (doc 11 P10): a customer cancels, their transfer arrives
    /// anyway, and staff choose to honour it.
    /// </summary>
    /// <remarks>
    /// <b>The red-before for a specified path that was unreachable.</b> The late acceptance looked for
    /// <c>[Pending, Expired]</c> payments, and a cancelled order's payment is <c>Cancelled (5)</c>, so the
    /// order claim succeeded, no payment was found, and the whole transaction rolled back reporting
    /// <c>payment_not_reviewable</c> — "there is nothing to review" about an order with a payment row
    /// sitting right there.
    /// <para>
    /// Nothing writes a cancelled order or a cancelled payment yet (customer cancel is W3-04), so the state
    /// is arranged by hand — which is the only way to reach it and exactly why it is worth a test now
    /// rather than when the cancel path lands on top of it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_late_payment_is_accepted_on_a_cancelled_order()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 1, onHand: 3);
        var orderId = await OrderIdAsync(order.PublicId);

        // Cancelled with its stock given back, which is what doc 08 §4.1 says a cancellation leaves
        // behind, and the payment cancelled with it.
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Assert.Equal(1, await db.Orders
                .Where(candidate => candidate.Id == orderId)
                .ExecuteUpdateAsync(
                    candidate => candidate
                        .SetProperty(row => row.Status, OrderStatus.Cancelled)
                        .SetProperty(row => row.StockState, OrderStockState.Released)
                        .SetProperty(row => row.ClosedAtUtc, DateTime.UtcNow),
                    Token));

            Assert.Equal(1, await db.Payments
                .Where(candidate => candidate.OrderId == orderId)
                .ExecuteUpdateAsync(
                    candidate => candidate.SetProperty(row => row.Status, PaymentStatus.Cancelled),
                    Token));

            // The reservation goes back too, so the re-acquire below is taking real stock rather than
            // stock this order is still holding.
            Assert.Equal(1, await db.StockLevels
                .Where(level => level.VariantId == variantId)
                .ExecuteUpdateAsync(
                    level => level.SetProperty(row => row.Reserved, 0),
                    Token));
        }

        var before = await StockAsync(variantId);

        var applied = Assert.IsType<BankTransferDecisionResult.Applied>(await AcceptLateAsync(orderId));
        Assert.NotNull(applied.InvoiceNumber);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Paid, row.Status);
        Assert.Equal(OrderStockState.Committed, row.StockState);
        Assert.Equal(applied.InvoiceNumber, row.InvoiceNumber);

        // The cancelled payment is the one that settles: it is the money that arrived.
        var payment = row.Payments.Single();
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.True(payment.IsOrderSettlement);

        // And the goods are taken from OnHand again (doc 08 §5.7), because the cancellation gave them back.
        Assert.Equal(before.OnHand - 1, (await StockAsync(variantId)).OnHand);
    }

    /// <summary>
    /// The other half of the provider guard: an order that <i>is</i> a bank transfer whose payment row is
    /// not.
    /// </summary>
    /// <remarks>
    /// <b>This shape is where the payment-row filter is the only thing standing in the way.</b> The order
    /// claim passes (the order really is a bank-transfer order), so what has to refuse is
    /// <c>FindReviewablePaymentAsync</c>'s <c>ProviderCode = BankTransfer</c> — and if it did not, the
    /// settle statement's own provider guard would refuse behind it, which is the belt the order-level
    /// guard has.
    /// <para>
    /// Not reachable today (nothing creates a PayPal payment row) and written for the day W4-01 does:
    /// a captured PayPal payment sitting on an order whose provider column says otherwise is precisely the
    /// mixed row an approval must not settle.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_payment_row_from_another_provider_is_not_settled_by_the_bank_flow()
    {
        var (order, variantId) = await PlaceOrderAsync();
        var orderId = await OrderIdAsync(order.PublicId);

        var before = await StockAsync(variantId);
        var seriesBefore = await InvoiceSeriesNextValueAsync();

        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var moved = await db.Payments
                .Where(candidate => candidate.OrderId == orderId)
                .ExecuteUpdateAsync(
                    candidate => candidate.SetProperty(
                        row => row.ProviderCode,
                        PaymentProviderCodes.PayPal),
                    Token);

            // The arrangement, asserted: a no-op update would leave a bank payment behind and this test
            // would be checking the ordinary approval path while claiming to check the guard.
            Assert.Equal(1, moved);
        }

        var outcome = await ApproveAsync(orderId);

        // Refused rather than NotFound: the order exists and is a bank-transfer order, so the honest
        // answer names what is wrong with it rather than pretending the token is unknown.
        var refused = Assert.IsType<BankTransferDecisionResult.Refused>(outcome);
        Assert.Equal(BankTransferRefusalCodes.PaymentNotReviewable, refused.Code);

        // And nothing moved — rows, stock and the invoice series.
        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Equal(OrderStockState.Reserved, row.StockState);
        Assert.Null(row.InvoiceNumber);
        Assert.Equal(before, await StockAsync(variantId));
        Assert.Equal(seriesBefore, await InvoiceSeriesNextValueAsync());
    }

    [Fact]
    public async Task A_PayPal_order_cannot_be_approved_through_the_bank_flow()
    {
        var (order, _) = await PlaceOrderAsync();
        var orderId = await OrderIdAsync(order.PublicId);

        // There is no way to place a PayPal order (the provider is seeded disabled and has no code), so
        // the row is moved by hand. That is the point: the guard has to hold for a row that exists
        // however it got there, and this is the only way to make one.
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var moved = await db.Orders
                .Where(candidate => candidate.Id == orderId)
                .ExecuteUpdateAsync(
                    candidate => candidate.SetProperty(
                        row => row.PaymentProviderCode,
                        PaymentProviderCodes.PayPal),
                    Token);

            // The arrangement, asserted. An ExecuteUpdate that matched nothing would leave a bank order
            // behind and the three assertions below would be testing the bank path against a bank order —
            // a test that passes for the wrong reason, which is worse than one that fails.
            Assert.Equal(1, moved);

            Assert.Equal(
                PaymentProviderCodes.PayPal,
                await db.Orders
                    .AsNoTracking()
                    .Where(candidate => candidate.Id == orderId)
                    .Select(candidate => candidate.PaymentProviderCode)
                    .SingleAsync(Token));
        }

        var seriesBefore = await InvoiceSeriesNextValueAsync();
        var paymentBefore = (await ReadOrderAsync(order.PublicId)).Payments.Single().Status;

        try
        {
            // NotFound, not Refused: the same answer a token naming nothing gets, so the admin API cannot
            // be used to ask which orders exist.
            Assert.IsType<BankTransferDecisionResult.NotFound>(await ApproveAsync(orderId));
            Assert.IsType<BankTransferDecisionResult.NotFound>(await RejectAsync(orderId));
            Assert.IsType<BankTransferDecisionResult.NotFound>(await AcceptLateAsync(orderId));

            // **Asserted on the rows and the series, not only on the result type.** A tester's mutation
            // made exactly this point: the defect this test was written for did not return a wrong result
            // type, it paid an order — so what must be checked is that nothing moved.
            var row = await ReadOrderAsync(order.PublicId);
            Assert.Equal(OrderStatus.PendingPayment, row.Status);
            Assert.Equal(OrderStockState.Reserved, row.StockState);
            Assert.Null(row.InvoiceNumber);
            Assert.Null(row.PaidAtUtc);
            Assert.Equal(paymentBefore, row.Payments.Single().Status);
            Assert.Equal(seriesBefore, await InvoiceSeriesNextValueAsync());
        }
        finally
        {
            using var scope = CreateScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
                .Where(candidate => candidate.Id == orderId)
                .ExecuteUpdateAsync(
                    candidate => candidate.SetProperty(
                        row => row.PaymentProviderCode,
                        PaymentProviderCodes.BankTransfer),
                    CancellationToken.None);
        }
    }

    /// <summary>The <c>ops.AuditLogs</c> row this order's decision wrote.</summary>
    private async Task<AuditLog> AuditRowAsync(Guid orderPublicId)
    {
        var entityId = orderPublicId.ToString("D", System.Globalization.CultureInfo.InvariantCulture);

        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .AsNoTracking()
            .Where(row => row.EntityType == nameof(Order) && row.EntityId == entityId)
            .OrderByDescending(row => row.Id)
            .FirstAsync(Token);
    }

    /// <summary>The numeric part of <c>INV-2026-000045</c>.</summary>
    private static int SequenceOf(string invoiceNumber) =>
        int.Parse(invoiceNumber.Split('-')[2], System.Globalization.CultureInfo.InvariantCulture);
}
