using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.IntegrationTests.Sales;

namespace SpencerDenim.IntegrationTests.Ops;

/// <summary>
/// The transactional outbox, against real orders and a real SQL Server (doc 08 §3.8: "Rows are inserted
/// in the business transaction"; doc 11 P6 step 9: "the order, … and the queued emails").
/// <para>
/// <b>Every order here is placed over HTTP and settled through the real transactions</b>, never inserted
/// by hand — <c>AdminOrderTestBase</c>'s own rule. The point of this suite is that a message and the
/// change it is about are one commit, and an order whose status a fixture had set would prove nothing
/// about that.
/// </para>
/// <para>
/// <b>Nothing in this suite sends anything.</b> <c>ApiFactory</c> blanks the SMTP keys and replaces the
/// transport with one that throws, so the host's <c>SendEmails</c> job leaves the queue alone exactly as
/// it does in production today — which is also what makes the <c>Status</c> assertions below stable.
/// </para>
/// </summary>
public sealed class OrderEmailTransactionTests(SqlServerDatabaseFixture sqlServer)
    : AdminOrderTestBase(sqlServer)
{
    /// <summary>
    /// Doc 11 P6 step 10, bank transfer: "Sends <b>Order placed</b> and <b>Bank transfer
    /// instructions</b>".
    /// </summary>
    [Fact]
    public async Task Placing_an_order_queues_the_confirmation_and_the_instructions()
    {
        await SetBankDetailsAsync();

        try
        {
            var (order, _) = await PlaceOrderAsync();

            var queued = await OutboxAsync(order.PublicId);

            Assert.Equal(
                [EmailTemplateCodes.OrderPlaced, EmailTemplateCodes.BankTransferInstructions],
                queued.Select(row => row.TemplateCode));

            Assert.All(queued, row =>
            {
                Assert.Equal(EmailOutboxStatus.Pending, row.Status);
                Assert.Equal(0, row.AttemptCount);
                Assert.Equal("nimali@example.com", row.ToEmail);
                Assert.Equal("Nimali Perera", row.ToName);
                Assert.Equal("Order", row.RelatedEntityType);
                Assert.Null(row.SentAtUtc);

                // Due the moment it was created, so the next sweep takes it: see
                // `OrderEmailOutbox.Enlist`.
                Assert.Equal(row.CreatedAtUtc, row.NextAttemptAtUtc);
            });

            // Doc 11 P7 step 1's account details reached the body. The account number is the one value a
            // customer retypes into their banking app, so it is the one asserted here.
            var instructions = queued.Single(
                row => row.TemplateCode == EmailTemplateCodes.BankTransferInstructions);

            Assert.Contains("0000 1111 2222", instructions.BodyText!, StringComparison.Ordinal);
            Assert.Contains(order.OrderNumber, instructions.BodyText!, StringComparison.Ordinal);
        }
        finally
        {
            await ClearBankDetailsAsync();
        }
    }

    /// <summary>
    /// <b>With the shop's bank details unset, the instructions email is not queued at all.</b>
    /// </summary>
    /// <remarks>
    /// This is the decision the task turns on, and the shop's real state: we do not have the client's bank
    /// details. An email headed "How to pay" with a blank account number is not a degraded email — a
    /// customer could transfer money nowhere — so there is none, and the order confirmation goes out
    /// alone. It is the rule W3-04's invoice already follows for the store's unset legal details.
    /// <para>
    /// <b>The second assertion is the one that makes this test hard to pass by accident:</b> the
    /// confirmation that <i>is</i> queued must not mention paying at all, because it cannot say how.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task With_the_bank_details_unset_the_instructions_email_is_not_queued()
    {
        var (order, _) = await PlaceOrderAsync();

        var queued = await OutboxAsync(order.PublicId);

        var row = Assert.Single(queued);
        Assert.Equal(EmailTemplateCodes.OrderPlaced, row.TemplateCode);

        Assert.DoesNotContain("Account number", row.BodyText!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Reference to quote", row.BodyText!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A placement that rolled back queues nothing.
    /// </summary>
    /// <remarks>
    /// <b>This is the defect the outbox pattern exists to prevent, from the side that is easy to get
    /// wrong.</b> An email about an order that does not exist would reach a customer who then had nothing
    /// to open. The reservation is refused before the order row is inserted (doc 08 §5.4 step 3), so the
    /// enlisted messages have to go with the rollback — and
    /// <c>OrderPlacementStore.DetachAll</c> is what stops a retried attempt re-adding them.
    /// </remarks>
    [Fact]
    public async Task An_order_that_could_not_be_placed_queues_nothing()
    {
        await SetBankDetailsAsync();

        try
        {
            var before = await OutboxCountAsync();

            var variantId = (await CheckoutVariantIdsAsync(12))[11];
            await SetStockAsync(variantId, onHand: 1);

            var (_, _, api) = await NewBagAsync(variantId, quantity: 1);
            var colombo = await DistrictIdAsync("Colombo");
            var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

            var (_, session) = await api.GetAsync(colombo, standard, Token);

            // The stock goes away between the quote and the press, so the reserving UPDATE matches no row
            // and the whole transaction rolls back.
            await SetStockAsync(variantId, onHand: 0);

            var (response, _) = await api.PlaceAsync(
                ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session)),
                Token);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(before, await OutboxCountAsync());
        }
        finally
        {
            await ClearBankDetailsAsync();
        }
    }

    /// <summary>Doc 11 P7 step 6a: "<b>Payment confirmed</b> email queued".</summary>
    [Fact]
    public async Task Approving_a_transfer_queues_the_payment_confirmation_with_its_invoice_number()
    {
        var (order, _) = await PlaceOrderAsync();
        var orderId = await OrderIdAsync(order.PublicId);

        var applied = Assert.IsType<BankTransferDecisionResult.Applied>(
            await ApproveAsync(orderId, "Credit found."));

        var confirmation = Assert.Single(
            await OutboxAsync(order.PublicId),
            row => row.TemplateCode == EmailTemplateCodes.PaymentConfirmed);

        // The invoice number was issued inside the same transaction. If the message carried a figure the
        // database had rolled back, this is where it would show.
        Assert.Contains(applied.InvoiceNumber!, confirmation.BodyText!, StringComparison.Ordinal);
        Assert.Contains(order.OrderNumber, confirmation.BodyText!, StringComparison.Ordinal);
        Assert.DoesNotContain("reopened", confirmation.BodyText!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Doc 11 P7's notification list: "<b>No email on rejection</b>".
    /// </summary>
    /// <remarks>
    /// The order stays <c>PendingPayment</c> with time left on it, so there is nothing to tell the
    /// customer that would not also invite them to stop trying. §7 G-14 leaves the question with the
    /// client; until they answer it, this is the specified behaviour and this test is what keeps it.
    /// </remarks>
    [Fact]
    public async Task Rejecting_a_transfer_queues_nothing()
    {
        var (order, _) = await PlaceOrderAsync();
        var orderId = await OrderIdAsync(order.PublicId);

        var before = await OutboxCountAsync();

        Assert.IsType<BankTransferDecisionResult.Applied>(await RejectAsync(orderId, "No credit found."));

        Assert.Equal(before, await OutboxCountAsync());
    }

    /// <summary>
    /// An approval that lost the race queues nothing, because its whole transaction rolled back.
    /// </summary>
    /// <remarks>
    /// The second approval is refused by the claim's own <c>WHERE</c> (the order is already <c>Paid</c>),
    /// and <c>BankTransferReviewStore.InTransactionAsync</c> rolls back everything a non-applied answer
    /// touched. A second confirmation email would be a customer told twice that their money had arrived.
    /// </remarks>
    [Fact]
    public async Task A_refused_approval_queues_nothing()
    {
        var (order, _) = await PlaceOrderAsync();
        var orderId = await OrderIdAsync(order.PublicId);

        Assert.IsType<BankTransferDecisionResult.Applied>(await ApproveAsync(orderId));

        var after = await OutboxCountAsync();

        Assert.IsType<BankTransferDecisionResult.Refused>(await ApproveAsync(orderId));

        Assert.Equal(after, await OutboxCountAsync());
    }

    /// <summary>
    /// Doc 11 P7 step 8 / P10 step 11: a late acceptance sends <c>PaymentConfirmed</c> too, and says
    /// something different because the customer's last message said the order had closed.
    /// </summary>
    [Fact]
    public async Task Accepting_a_late_payment_queues_a_confirmation_that_says_the_order_was_reopened()
    {
        var (order, _) = await PlaceOrderAsync();
        var orderId = await OrderIdAsync(order.PublicId);

        Assert.True(await ExpireAsync(orderId));
        Assert.IsType<BankTransferDecisionResult.Applied>(await AcceptLateAsync(orderId, "Credit arrived."));

        var queued = await OutboxAsync(order.PublicId);

        Assert.Equal(
            [EmailTemplateCodes.OrderPlaced, EmailTemplateCodes.OrderExpired, EmailTemplateCodes.PaymentConfirmed],
            queued.Select(row => row.TemplateCode));

        var confirmation = queued.Last();
        Assert.Contains("reopened your order", confirmation.BodyText!, StringComparison.Ordinal);
    }

    /// <summary>Doc 11 P7 step 7 / P10 flow B: "<b>Order expired</b> email".</summary>
    [Fact]
    public async Task Expiring_an_order_queues_the_expiry_email()
    {
        var (order, _) = await PlaceOrderAsync();
        var orderId = await OrderIdAsync(order.PublicId);

        Assert.True(await ExpireAsync(orderId));

        var expiry = Assert.Single(
            await OutboxAsync(order.PublicId),
            row => row.TemplateCode == EmailTemplateCodes.OrderExpired);

        Assert.Contains(order.OrderNumber, expiry.BodyText!, StringComparison.Ordinal);
        Assert.Contains("back on sale", expiry.BodyText!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An expiry that found nothing to do queues nothing.
    /// </summary>
    /// <remarks>
    /// The sweep hands the same order to every instance and the claim decides which one wins (see
    /// <c>OrderExpiryStore</c>), so a losing claim must leave no trace. A second "your order has closed"
    /// is a message about something that happened once.
    /// </remarks>
    [Fact]
    public async Task A_second_expiry_of_the_same_order_queues_nothing()
    {
        var (order, _) = await PlaceOrderAsync();
        var orderId = await OrderIdAsync(order.PublicId);

        Assert.True(await ExpireAsync(orderId));
        var after = await OutboxCountAsync();

        Assert.False(await ExpireAsync(orderId));
        Assert.Equal(after, await OutboxCountAsync());
    }

    /// <summary>
    /// An order that is not yet due queues nothing, which is the same guard from the other side.
    /// </summary>
    [Fact]
    public async Task An_order_that_is_not_due_queues_nothing()
    {
        var (order, _) = await PlaceOrderAsync();
        var orderId = await OrderIdAsync(order.PublicId);

        var before = await OutboxCountAsync();

        Assert.False(await ExpireAsync(orderId, order.PaymentDueAtUtc.AddSeconds(-1)));

        Assert.Equal(before, await OutboxCountAsync());
    }

    /// <summary>
    /// A cancellation queues the message, and it says which of the two things happened (doc 11 P10 step
    /// 3's source).
    /// </summary>
    /// <remarks>
    /// <b>This template is a scope increase and not doc 11 §4's.</b> §4.4 lists "order cancelled" among
    /// the events with no notification in v1, and §7 G-14 recommends adding one with the decision left to
    /// the client. It is built on the owner's instruction for W3-06; the note is on
    /// <c>EmailTemplateCodes.OrderCancelled</c> and in the task report.
    /// </remarks>
    [Theory]
    [InlineData(OrderCancelledBy.Customer, "cancelled as you asked")]
    [InlineData(OrderCancelledBy.Staff, "We have had to cancel")]
    public async Task Cancelling_an_unpaid_order_queues_a_message_that_says_who_cancelled_it(
        OrderCancelledBy by,
        string expected)
    {
        var (order, _) = await PlaceOrderAsync();

        Assert.IsType<CancelOrderOutcome.Applied>(await CancelAsync(order.PublicId, by));

        var cancellation = Assert.Single(
            await OutboxAsync(order.PublicId),
            row => row.TemplateCode == EmailTemplateCodes.OrderCancelled);

        Assert.Contains(expected, cancellation.BodyText!, StringComparison.Ordinal);
        Assert.Contains(order.OrderNumber, cancellation.BodyText!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refused cancellation queues nothing: the order was already paid, so nothing happened.
    /// </summary>
    [Fact]
    public async Task A_refused_cancellation_queues_nothing()
    {
        var paid = await PaidOrderAsync();

        var before = await OutboxCountAsync();

        Assert.IsType<CancelOrderOutcome.Refused>(
            await CancelAsync(paid.PublicId, OrderCancelledBy.Customer));

        Assert.Equal(before, await OutboxCountAsync());
    }

    /// <summary>
    /// Doc 11 P9 step 5: "queues <b>Order shipped</b> email with the tracking link built from the
    /// courier's template".
    /// </summary>
    [Fact]
    public async Task Dispatching_an_order_queues_the_shipped_email_with_a_real_tracking_link()
    {
        var paid = await PaidOrderAsync();
        var courierId = await NewCourierAsync(name: $"xunit-mail-{Guid.NewGuid():N}"[..30]);

        Assert.IsType<OrderTransitionResult.Applied>(await StartProcessingAsync(paid.OrderId));

        var beforeDispatch = await OutboxCountAsync();

        Assert.IsType<OrderTransitionResult.Applied>(
            await MarkShippedAsync(paid.OrderId, courierId, "TRACK-77881"));

        var shipped = Assert.Single(
            await OutboxAsync(paid.PublicId),
            row => row.TemplateCode == EmailTemplateCodes.OrderShipped);

        Assert.Contains("TRACK-77881", shipped.BodyText!, StringComparison.Ordinal);

        // The link is built from the courier's own template, which NewCourierAsync sets. A composer that
        // printed the template verbatim would leave "{trackingNumber}" in a customer's inbox.
        Assert.Contains(
            "https://track.example.test/TRACK-77881",
            shipped.BodyText!,
            StringComparison.Ordinal);

        Assert.DoesNotContain("{trackingNumber}", shipped.BodyText!, StringComparison.Ordinal);

        // Start processing queued nothing (doc 11 P9: "No email on Processing"), so the dispatch added
        // exactly one message.
        Assert.Equal(beforeDispatch + 1, await OutboxCountAsync());
    }

    /// <summary>
    /// Doc 11 §7 G-35: a pickup order's dispatch is a different message, with the location and no courier.
    /// </summary>
    [Fact]
    public async Task A_pickup_dispatch_queues_the_collection_wording_with_the_location()
    {
        var paid = await PaidOrderAsync();
        await MakePickupAsync(paid.OrderId);

        Assert.IsType<OrderTransitionResult.Applied>(await StartProcessingAsync(paid.OrderId));
        Assert.IsType<OrderTransitionResult.Applied>(
            await MarkShippedAsync(paid.OrderId, courierId: null, trackingNumber: null));

        var shipped = Assert.Single(
            await OutboxAsync(paid.PublicId),
            row => row.TemplateCode == EmailTemplateCodes.OrderShipped);

        Assert.Contains("ready to collect", shipped.Subject, StringComparison.Ordinal);

        // The name is the order's own snapshot — what the shopper chose — and the address is read live
        // from the location (see `OrderFulfilmentStore.ReadPickupAsync`).
        Assert.Contains("A test pickup point", shipped.BodyText!, StringComparison.Ordinal);
        Assert.Contains("A test address, A test city", shipped.BodyText!, StringComparison.Ordinal);
        Assert.DoesNotContain("courier", shipped.BodyText!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Doc 11 P9: no email on Processing or Delivered, and no second message when a tracking number is
    /// corrected.
    /// </summary>
    /// <remarks>
    /// <b>The correction case is the one worth pinning.</b> Doc 11 P9's exception says the customer is
    /// still holding a link that does not work and is telephoned about it; a second <c>OrderShipped</c>
    /// would be a plausible thing for somebody to add, and it is not what the document asks for (§7 G-14).
    /// </remarks>
    [Fact]
    public async Task Processing_delivery_and_a_corrected_tracking_number_queue_nothing()
    {
        var paid = await PaidOrderAsync();
        var courierId = await ShipAsync(paid.OrderId);

        var after = await OutboxCountAsync();

        Assert.IsType<OrderTransitionResult.Applied>(
            await UpdateDispatchAsync(paid.OrderId, courierId, "TRACK-CORRECTED"));

        Assert.IsType<OrderTransitionResult.Applied>(await MarkDeliveredAsync(paid.OrderId));

        Assert.Equal(after, await OutboxCountAsync());
    }

    /// <summary>
    /// The whole life of one order, in the order the messages were queued.
    /// </summary>
    /// <remarks>
    /// <b>A sequence assertion rather than five separate ones, because the sequence is what a customer
    /// experiences.</b> It also catches the one thing per-transition tests cannot: a message queued twice
    /// by two different transitions, or one queued by a transition that should not send anything.
    /// </remarks>
    [Fact]
    public async Task One_order_from_placement_to_delivery_queues_exactly_four_messages()
    {
        await SetBankDetailsAsync();

        try
        {
            var (order, _) = await PlaceOrderAsync();
            var orderId = await OrderIdAsync(order.PublicId);

            Assert.IsType<BankTransferDecisionResult.Applied>(await ApproveAsync(orderId));

            // ShipAsync starts processing itself, so calling StartProcessingAsync first would leave its
            // own call with nothing to do and refuse the dispatch.
            await ShipAsync(orderId);
            Assert.IsType<OrderTransitionResult.Applied>(await MarkDeliveredAsync(orderId));

            Assert.Equal(
                [
                    EmailTemplateCodes.OrderPlaced,
                    EmailTemplateCodes.BankTransferInstructions,
                    EmailTemplateCodes.PaymentConfirmed,
                    EmailTemplateCodes.OrderShipped,
                ],
                (await OutboxAsync(order.PublicId)).Select(row => row.TemplateCode));
        }
        finally
        {
            await ClearBankDetailsAsync();
        }
    }

    /// <summary>
    /// The queue the shop is actually in: messages waiting, and the job sending none of them.
    /// </summary>
    /// <remarks>
    /// <b>This is the end-to-end form of the decision the task turns on.</b> With no SMTP configured the
    /// host's <c>SendEmails</c> job runs, finds work, and deliberately does not touch it — so the rows are
    /// still <c>Pending</c> with <c>AttemptCount = 0</c> and no <c>LastError</c>. The three clauses are
    /// the three ways this could have gone wrong: marked Sent, marked Failed, or burned through the ladder.
    /// </remarks>
    [Fact]
    public async Task With_no_smtp_configured_the_queue_is_left_exactly_as_it_was_written()
    {
        var (order, _) = await PlaceOrderAsync();

        var row = Assert.Single(await OutboxAsync(order.PublicId));

        Assert.Equal(EmailOutboxStatus.Pending, row.Status);
        Assert.Equal(0, row.AttemptCount);
        Assert.Null(row.LastError);
        Assert.Null(row.SentAtUtc);
        Assert.Null(row.LockedUntilUtc);
    }

    private Task<CancelOrderOutcome> CancelAsync(Guid publicId, OrderCancelledBy by) =>
        InScopeAsync(services => services
            .GetRequiredService<IOrderCancellationStore>()
            .CancelAsync(
                new OrderCancellation(
                    publicId,
                    by,
                    by == OrderCancelledBy.Staff ? Fulfiller() : null,
                    by == OrderCancelledBy.Staff ? "Customer rang to cancel." : null,
                    DateTime.UtcNow),
                Token));

    /// <summary>Every message queued about one order, oldest first.</summary>
    private async Task<List<EmailOutboxMessage>> OutboxAsync(Guid publicId)
    {
        using var scope = CreateScope();
        var id = publicId.ToString("D");

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().EmailOutbox
            .AsNoTracking()
            .Where(message => message.RelatedEntityId == id)
            .OrderBy(message => message.Id)
            .ToListAsync(Token);
    }

    /// <summary>
    /// How many messages the whole table holds, for the "nothing was queued" assertions.
    /// </summary>
    /// <remarks>
    /// A whole-table count is safe here and would not be in a parallel suite: the assembly sets
    /// <c>DisableTestParallelization</c>, nothing else inserts into this table, and the <c>SendEmails</c>
    /// job cannot — it only ever updates. A count scoped to one order would not work for the rollback
    /// test, which is about an order that was never created and therefore has no token to scope by.
    /// </remarks>
    private async Task<int> OutboxCountAsync()
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().EmailOutbox
            .AsNoTracking()
            .CountAsync(Token);
    }
}
