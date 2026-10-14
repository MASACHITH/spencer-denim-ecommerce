using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Application.Sales.Payments;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.PayPal;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales.PayPal;

/// <summary>
/// The webhook path, which the tester will attack: replay, out-of-order delivery, a forged signature, a
/// duplicate capture and a capture that arrives after the order expired (issue 54's definition of done; doc 08
/// §3.5 DB13 and §4.2). Each test posts real HTTP at <c>POST /api/payments/paypal/webhook</c> and reads the
/// rows.
/// <para>
/// The signature check is PayPal's, so here it is the fake's: a header of <c>valid-signature</c> verifies,
/// anything else does not, and the fake can be made to fail verification altogether. What these tests hold
/// the shop to is everything around it - nothing is stored before a signature passes, a repeat is a no-op,
/// and an event that arrives in the wrong order cannot undo money that has arrived.
/// </para>
/// </summary>
public sealed class PayPalWebhookTests(SqlServerDatabaseFixture sqlServer) : PayPalTestBase(sqlServer)
{
    /// <summary>An order placed, started (so PayPal has an order for it) and approved, but not yet returned.</summary>
    private async Task<(PayPalOrder Order, string PayPalOrderId)> StartedAsync(int quantity = 1)
    {
        await QuietLeftoversAsync();
        await ConfigurePayPalAsync();

        var order = await PlacePayPalOrderAsync(quantity);
        var paypalOrderId = await StartAsync(order);
        PayPal.Approve(paypalOrderId);

        return (order, paypalOrderId);
    }

    private static string Completed(PayPalOrder order, string paypalOrderId, string eventId, string captureId, decimal? amount = null) =>
        CaptureEvent("PAYMENT.CAPTURE.COMPLETED", eventId, captureId, paypalOrderId, order.PaymentId, amount ?? order.UsdAmount);

    private static string Event(string type, PayPalOrder order, string paypalOrderId, string eventId, string captureId) =>
        CaptureEvent(type, eventId, captureId, paypalOrderId, order.PaymentId, order.UsdAmount);

    // ------------------------------------------------------------------------------ before anything is stored

    /// <summary>Doc 08 DB13 step 3: an invalid signature is a 400, logged only, and consumes nothing.</summary>
    [Fact]
    public async Task A_forged_signature_is_refused_with_400_and_nothing_is_stored_or_changed()
    {
        var (order, paypalOrderId) = await StartedAsync();
        var events = await EventCountAsync();
        var seriesBefore = await InvoiceSeriesNextValueAsync();

        using var response = await PostWebhookAsync(
            Completed(order, paypalOrderId, "WH-FORGED-1", Cap("CAP-FORGED-1")),
            signature: "forged");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(PayPal.Verifications);

        // Nothing stored, nothing moved.
        Assert.Equal(events, await EventCountAsync());
        Assert.Equal(PaymentStatus.Initiated, (await PaymentAsync(order.PaymentId)).Status);
        Assert.Equal(OrderStatus.PendingPayment, (await OrderRowAsync(order.OrderId)).Status);
        Assert.Equal(seriesBefore, await InvoiceSeriesNextValueAsync());
    }

    /// <summary>
    /// The reason verification comes first: a forged request for a real event id must not be able to use up
    /// the key, or the genuine delivery would later be ignored as a duplicate.
    /// </summary>
    [Fact]
    public async Task A_forged_request_cannot_use_up_an_event_id_the_genuine_one_then_needs()
    {
        var (order, paypalOrderId) = await StartedAsync();
        var body = Completed(order, paypalOrderId, "WH-SAME-ID", Cap("CAP-REAL-1"));

        using (var forged = await PostWebhookAsync(body, signature: "forged"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        }

        using (var genuine = await PostWebhookAsync(body))
        {
            Assert.Equal(HttpStatusCode.OK, genuine.StatusCode);
        }

        // The genuine one was applied, not ignored as a duplicate of the forgery.
        Assert.Equal(PaymentStatus.Succeeded, (await PaymentAsync(order.PaymentId)).Status);
        Assert.Equal(OrderStatus.Paid, (await OrderRowAsync(order.OrderId)).Status);
    }

    /// <summary>Doc 08 DB13 step 2: PayPal could not be asked - 503, nothing stored, and PayPal's resend then works.</summary>
    [Fact]
    public async Task When_the_signature_cannot_be_checked_the_answer_is_503_and_a_resend_is_applied()
    {
        var (order, paypalOrderId) = await StartedAsync();
        var events = await EventCountAsync();
        var body = Completed(order, paypalOrderId, "WH-RETRY-1", Cap("CAP-RETRY-1"));

        PayPal.VerificationFails = true;

        using (var response = await PostWebhookAsync(body))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        Assert.Equal(events, await EventCountAsync());
        Assert.Equal(PaymentStatus.Initiated, (await PaymentAsync(order.PaymentId)).Status);

        // PayPal sends it again; verification works this time.
        PayPal.VerificationFails = false;

        using (var response = await PostWebhookAsync(body))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(PaymentStatus.Succeeded, (await PaymentAsync(order.PaymentId)).Status);
        Assert.Equal(events + 1, await EventCountAsync());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"id\":\"WH-1\"}")]
    [InlineData("")]
    public async Task A_body_that_is_not_an_event_is_400_and_PayPal_is_not_even_asked(string body)
    {
        await ConfigurePayPalAsync();
        var events = await EventCountAsync();

        using var response = await PostWebhookAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(PayPal.Verifications);
        Assert.Equal(events, await EventCountAsync());
    }

    [Fact]
    public async Task A_webhook_needs_no_session_and_no_antiforgery_token()
    {
        var (order, paypalOrderId) = await StartedAsync();

        // A bare client: no cookies, no headers but PayPal's.
        using var bare = PayPalHost.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = BaseAddress,
            HandleCookies = false,
        });

        using var response = await PostWebhookAsync(
            Completed(order, paypalOrderId, "WH-BARE-1", Cap("CAP-BARE-1")),
            client: bare);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ------------------------------------------------------------------------------ verified: applied once

    [Fact]
    public async Task A_verified_COMPLETED_pays_the_order_and_is_logged_verbatim_and_linked()
    {
        var (order, paypalOrderId) = await StartedAsync(quantity: 2);
        var before = await StockAsync(order.VariantId);
        var body = Completed(order, paypalOrderId, "WH-OK-1", Cap("CAP-OK-1"));

        using var response = await PostWebhookAsync(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = await OrderRowAsync(order.OrderId);
        Assert.Equal(OrderStatus.Paid, row.Status);
        Assert.Equal(OrderStockState.Committed, row.StockState);
        Assert.NotNull(row.InvoiceNumber);
        Assert.Equal(before.OnHand - 2, (await StockAsync(order.VariantId)).OnHand);

        var payment = await PaymentAsync(order.PaymentId);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.True(payment.IsOrderSettlement);
        Assert.Equal(Cap("CAP-OK-1"), payment.ProviderTransactionId);

        var stored = Assert.Single(await EventsOfAsync(order.PaymentId), e => e.ProviderEventId == "WH-OK-1");
        Assert.Equal(PaymentEventSource.VerifiedWebhook, stored.EventSource);
        Assert.Equal("PAYMENT.CAPTURE.COMPLETED", stored.EventType);
        Assert.Equal("PayPal:WH-OK-1", stored.IdempotencyKey);
        Assert.Equal(PaymentEventProcessingStatus.Processed, stored.ProcessingStatus);
        Assert.Equal(order.OrderId, stored.OrderId);
        Assert.Null(stored.NextAttemptAtUtc);
        Assert.Contains(Cap("CAP-OK-1"), stored.PayloadJson, StringComparison.Ordinal);

        // The verification was asked of PayPal with the body exactly as it arrived.
        Assert.Equal(body, PayPal.Verifications.Single().Body);
    }

    /// <summary>The headline replay case: the same event, delivered again and again, is one settlement.</summary>
    [Fact]
    public async Task A_replayed_event_is_answered_200_and_stored_and_applied_once()
    {
        var (order, paypalOrderId) = await StartedAsync();
        var seriesBefore = await InvoiceSeriesNextValueAsync();
        var body = Completed(order, paypalOrderId, "WH-REPLAY-1", Cap("CAP-REPLAY-1"));

        for (var delivery = 0; delivery < 3; delivery++)
        {
            using var response = await PostWebhookAsync(body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Single(await EventsOfAsync(order.PaymentId), e => e.ProviderEventId == "WH-REPLAY-1");

        var row = await OrderRowAsync(order.OrderId);
        Assert.Single(row.StatusHistory, entry => entry.ToStatus == OrderStatus.Paid);
        Assert.Equal((seriesBefore ?? 1) + 1, await InvoiceSeriesNextValueAsync());
        Assert.Equal(1, await OutboxCountAsync(order.PublicId, EmailTemplateCodes.PaymentConfirmed));
    }

    [Fact]
    public async Task Simultaneous_deliveries_of_one_event_are_one_stored_row_and_one_settlement()
    {
        var (order, paypalOrderId) = await StartedAsync(quantity: 2);
        var before = await StockAsync(order.VariantId);
        var body = Completed(order, paypalOrderId, "WH-RACE-1", Cap("CAP-RACE-1"));

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => Task.Run(() => PostWebhookAsync(body), Token)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));

        Assert.Single(await EventsOfAsync(order.PaymentId), e => e.ProviderEventId == "WH-RACE-1");
        Assert.Equal(OrderStatus.Paid, (await OrderRowAsync(order.OrderId)).Status);
        Assert.Equal(before.OnHand - 2, (await StockAsync(order.VariantId)).OnHand);
        Assert.Equal(1, await OutboxCountAsync(order.PublicId, EmailTemplateCodes.PaymentConfirmed));

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    /// <summary>PayPal sometimes sends the same fact under a new event id: the payment is already paid, so it is ignored.</summary>
    [Fact]
    public async Task The_same_capture_under_a_new_event_id_is_a_duplicate_and_is_ignored()
    {
        var (order, paypalOrderId) = await StartedAsync();

        using (var first = await PostWebhookAsync(Completed(order, paypalOrderId, "WH-DUP-A", Cap("CAP-DUP"))))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (var second = await PostWebhookAsync(Completed(order, paypalOrderId, "WH-DUP-B", Cap("CAP-DUP"))))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        var events = await EventsOfAsync(order.PaymentId);
        Assert.Equal(PaymentEventProcessingStatus.Processed, events.Single(e => e.ProviderEventId == "WH-DUP-A").ProcessingStatus);
        Assert.Equal(PaymentEventProcessingStatus.Ignored, events.Single(e => e.ProviderEventId == "WH-DUP-B").ProcessingStatus);
        Assert.Single((await OrderRowAsync(order.OrderId)).StatusHistory, entry => entry.ToStatus == OrderStatus.Paid);
    }

    // ------------------------------------------------------------------------------ out of order

    /// <summary>"DENIED after COMPLETED ignored": money that has arrived does not un-arrive.</summary>
    [Fact]
    public async Task A_DENIED_that_arrives_after_a_COMPLETED_is_ignored()
    {
        var (order, paypalOrderId) = await StartedAsync();

        using (var completed = await PostWebhookAsync(Completed(order, paypalOrderId, "WH-C-1", Cap("CAP-1"))))
        {
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        }

        using (var denied = await PostWebhookAsync(Event("PAYMENT.CAPTURE.DENIED", order, paypalOrderId, "WH-D-1", Cap("CAP-1"))))
        {
            Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
        }

        var payment = await PaymentAsync(order.PaymentId);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.True(payment.IsOrderSettlement);
        Assert.Null(payment.FailureCode);
        Assert.Equal(OrderStatus.Paid, (await OrderRowAsync(order.OrderId)).Status);

        var denial = Assert.Single(await EventsOfAsync(order.PaymentId), e => e.ProviderEventId == "WH-D-1");
        Assert.Equal(PaymentEventProcessingStatus.Ignored, denial.ProcessingStatus);
    }

    [Fact]
    public async Task A_PENDING_that_arrives_after_a_COMPLETED_changes_nothing()
    {
        var (order, paypalOrderId) = await StartedAsync();

        using (await PostWebhookAsync(Completed(order, paypalOrderId, "WH-C-2", Cap("CAP-2"))))
        {
        }

        var due = (await OrderRowAsync(order.OrderId)).PaymentDueAtUtc;

        using (var pending = await PostWebhookAsync(Event("PAYMENT.CAPTURE.PENDING", order, paypalOrderId, "WH-P-2", Cap("CAP-2"))))
        {
            Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        }

        Assert.Equal(PaymentStatus.Succeeded, (await PaymentAsync(order.PaymentId)).Status);
        Assert.Equal(due, (await OrderRowAsync(order.OrderId)).PaymentDueAtUtc);
    }

    /// <summary>PENDING then DENIED arriving the other way round: the refusal stands and the stale PENDING cannot revive the payment.</summary>
    [Fact]
    public async Task A_PENDING_that_arrives_after_a_DENIED_does_not_revive_the_payment()
    {
        var (order, paypalOrderId) = await StartedAsync();

        using (await PostWebhookAsync(Event("PAYMENT.CAPTURE.DENIED", order, paypalOrderId, "WH-D-3", Cap("CAP-3"))))
        {
        }

        Assert.Equal(PaymentStatus.Failed, (await PaymentAsync(order.PaymentId)).Status);

        using (var pending = await PostWebhookAsync(Event("PAYMENT.CAPTURE.PENDING", order, paypalOrderId, "WH-P-3", Cap("CAP-3"))))
        {
            Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        }

        Assert.Equal(PaymentStatus.Failed, (await PaymentAsync(order.PaymentId)).Status);
        Assert.Equal(
            PaymentEventProcessingStatus.Ignored,
            Assert.Single(await EventsOfAsync(order.PaymentId), e => e.ProviderEventId == "WH-P-3").ProcessingStatus);
    }

    /// <summary>
    /// "COMPLETED before the API response finished": the webhook lands while the capture call is still in
    /// flight. Both run the same apply; one pays the order and the other finds it paid.
    /// </summary>
    [Fact]
    public async Task A_webhook_that_beats_the_capture_response_leaves_exactly_one_settlement()
    {
        var (order, paypalOrderId) = await StartedAsync(quantity: 2);
        var before = await StockAsync(order.VariantId);
        var seriesBefore = await InvoiceSeriesNextValueAsync();

        PayPal.DuringCapture = async (_, _, _) =>
        {
            using var webhook = await PostWebhookAsync(Completed(order, paypalOrderId, "WH-EARLY-1", Cap("CAP-EARLY-1")));
            Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);
        };

        using var response = await GetAsync(ReturnPath(order.PublicId));

        // The buyer is told they are paid: the response found the payment already settled by the webhook.
        Assert.Equal(PayPalPageHints.Paid, HintOf(response));

        var payment = await PaymentAsync(order.PaymentId);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(Cap("CAP-EARLY-1"), payment.ProviderTransactionId);

        Assert.Equal(before.OnHand - 2, (await StockAsync(order.VariantId)).OnHand);
        Assert.Equal((seriesBefore ?? 1) + 1, await InvoiceSeriesNextValueAsync());
        Assert.Single((await OrderRowAsync(order.OrderId)).StatusHistory, entry => entry.ToStatus == OrderStatus.Paid);
        Assert.Equal(1, await OutboxCountAsync(order.PublicId, EmailTemplateCodes.PaymentConfirmed));
    }

    // ------------------------------------------------------------------------------ R1: the late COMPLETED

    /// <summary>
    /// Doc 08 §4.2 R1: a COMPLETED on a Failed payment is not a duplicate. The buyer's card was declined,
    /// they tried another way inside PayPal, and it went through - the money is real and the order is payable.
    /// </summary>
    [Fact]
    public async Task A_late_COMPLETED_on_a_Failed_payment_pays_the_order()
    {
        var (order, paypalOrderId) = await StartedAsync();

        PayPal.Script(FakePayPalGateway.CaptureScript.Declined);
        using (var declined = await GetAsync(ReturnPath(order.PublicId)))
        {
            Assert.Equal(PayPalPageHints.Declined, HintOf(declined));
        }

        Assert.Equal(PaymentStatus.Failed, (await PaymentAsync(order.PaymentId)).Status);

        using var late = await PostWebhookAsync(Completed(order, paypalOrderId, "WH-LATE-F", Cap("CAP-LATE-F")));

        Assert.Equal(HttpStatusCode.OK, late.StatusCode);

        var payment = await PaymentAsync(order.PaymentId);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.True(payment.IsOrderSettlement);

        // The decline's failure columns do not outlive the settlement.
        Assert.Null(payment.FailureCode);
        Assert.Equal(OrderStatus.Paid, (await OrderRowAsync(order.OrderId)).Status);

        Assert.Equal(
            PaymentEventProcessingStatus.Processed,
            Assert.Single(await EventsOfAsync(order.PaymentId), e => e.ProviderEventId == "WH-LATE-F").ProcessingStatus);
    }

    /// <summary>
    /// A buyer cancelled, started again, and the first attempt's payment completes after all: it pays the
    /// order and the newer open attempt is closed in the same transaction (doc 08 Appendix B1).
    /// </summary>
    [Fact]
    public async Task A_late_COMPLETED_on_a_Cancelled_payment_pays_the_order_and_closes_the_newer_attempt()
    {
        var (order, firstPayPalOrderId) = await StartedAsync();

        using (var cancelled = await GetAsync(CancelPath(order.PublicId)))
        {
            Assert.Equal(PayPalPageHints.Cancelled, HintOf(cancelled));
        }

        var secondPayPalOrderId = await StartAsync(order);
        Assert.NotEqual(firstPayPalOrderId, secondPayPalOrderId);
        Assert.Equal(2, (await PaymentsOfAsync(order.OrderId)).Count);

        using var late = await PostWebhookAsync(Completed(order, firstPayPalOrderId, "WH-LATE-C", Cap("CAP-LATE-C")));

        Assert.Equal(HttpStatusCode.OK, late.StatusCode);

        var payments = await PaymentsOfAsync(order.OrderId);
        Assert.Equal(PaymentStatus.Succeeded, payments[0].Status);
        Assert.True(payments[0].IsOrderSettlement);
        Assert.Equal(PaymentStatus.Cancelled, payments[1].Status);
        Assert.False(payments[1].IsOrderSettlement);
        Assert.Equal(OrderStatus.Paid, (await OrderRowAsync(order.OrderId)).Status);
    }

    // ------------------------------------------------------------------------------ not payable

    /// <summary>A capture arriving after the order expired: kept, flagged and refunded by a person, never lost.</summary>
    [Fact]
    public async Task A_COMPLETED_for_an_order_that_has_expired_is_a_payment_exception_with_an_alert()
    {
        var (order, paypalOrderId) = await StartedAsync();
        Assert.True(await ExpireAtAsync(order.OrderId, Clock.Now.AddHours(2)));
        var stock = await StockAsync(order.VariantId);

        try
        {
            using var response = await PostWebhookAsync(Completed(order, paypalOrderId, "WH-EXP-1", Cap("CAP-EXP-1")));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var payment = await PaymentAsync(order.PaymentId);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, payment.Status);
            Assert.False(payment.IsOrderSettlement);
            Assert.Equal(Cap("CAP-EXP-1"), payment.ProviderTransactionId);

            var row = await OrderRowAsync(order.OrderId);
            Assert.Equal(OrderStatus.Expired, row.Status);
            Assert.Null(row.InvoiceNumber);
            Assert.Equal(stock.OnHand, (await StockAsync(order.VariantId)).OnHand);

            var alert = Assert.Single(await AlertsOfTypeAsync(AdminAlertTypes.PaymentException, order.PublicId));
            Assert.Contains("order_expired", alert.DetailsJson, StringComparison.Ordinal);

            // It is Processed: the money is on record, which is the point.
            Assert.Equal(
                PaymentEventProcessingStatus.Processed,
                Assert.Single(await EventsOfAsync(order.PaymentId), e => e.ProviderEventId == "WH-EXP-1").ProcessingStatus);

            // And a replay raises no second banner.
            using (await PostWebhookAsync(Completed(order, paypalOrderId, "WH-EXP-1", Cap("CAP-EXP-1"))))
            {
            }

            Assert.Single(await AlertsOfTypeAsync(AdminAlertTypes.PaymentException, order.PublicId));
        }
        finally
        {
            await DeleteAlertsAsync(order.PublicId);
        }
    }

    /// <summary>"Customer pays twice in two tabs": a second PayPal order captured on an already-paid order is an exception to refund.</summary>
    [Fact]
    public async Task A_second_capture_on_an_order_that_is_already_paid_is_a_duplicate_payment_exception()
    {
        var (order, paypalOrderId) = await StartedAsync();

        using (var paid = await PostWebhookAsync(Completed(order, paypalOrderId, "WH-FIRST", Cap("CAP-FIRST"))))
        {
            Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        }

        // The second tab's payment: its own PayPal order, captured too.
        var secondPaymentId = await InsertPayPalPaymentAsync(order.OrderId, "FAKE-SECOND-TAB", PaymentStatus.Capturing);

        try
        {
            using var response = await PostWebhookAsync(CaptureEvent(
                "PAYMENT.CAPTURE.COMPLETED",
                "WH-SECOND",
                Cap("CAP-SECOND"),
                "FAKE-SECOND-TAB",
                secondPaymentId,
                SecondTabUsd));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // The order keeps its status and its one settling payment; the extra payment is to be refunded.
            var payments = await PaymentsOfAsync(order.OrderId);
            Assert.Equal(PaymentStatus.Succeeded, payments[0].Status);
            Assert.True(payments[0].IsOrderSettlement);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, payments[1].Status);
            Assert.False(payments[1].IsOrderSettlement);

            var row = await OrderRowAsync(order.OrderId);
            Assert.Equal(OrderStatus.Paid, row.Status);
            Assert.Equal(0m, row.RefundedAmount);

            var alert = Assert.Single(await AlertsOfTypeAsync(AdminAlertTypes.PaymentException, order.PublicId));
            Assert.Contains("order_already_paid", alert.DetailsJson, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteAlertsAsync(order.PublicId);
        }
    }

    [Fact]
    public async Task A_COMPLETED_for_the_wrong_amount_is_kept_as_an_exception_that_can_only_be_refunded()
    {
        var (order, paypalOrderId) = await StartedAsync();

        try
        {
            using var response = await PostWebhookAsync(
                Completed(order, paypalOrderId, "WH-AMT-1", Cap("CAP-AMT-1"), amount: order.UsdAmount - 1m));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var payment = await PaymentAsync(order.PaymentId);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, payment.Status);
            Assert.Equal(PaymentFailureCodes.AmountMismatch, payment.FailureCode);
            Assert.Equal(OrderStatus.PendingPayment, (await OrderRowAsync(order.OrderId)).Status);
        }
        finally
        {
            await DeleteAlertsAsync(order.PublicId);
        }
    }

    // ------------------------------------------------------------------------------ what the customer is told

    /// <summary>
    /// The three ways a verified success lands without settling the order must each leave the customer's order
    /// page saying "we received your payment" - read from the database, so it is there on any later visit.
    /// Nothing about the money (provider, amount, ids) travels with it, and no way to pay is offered beside it.
    /// </summary>
    private static void AssertReceivedAndNothingElse(OrderConfirmationDto page, string json, PayPalOrder order, string captureId)
    {
        Assert.Equal(OrderPaymentNotices.Received, page.PaymentNotice);
        Assert.Null(page.Payment);
        Assert.Null(page.Proof);

        // The order link is the whole credential: nothing of the payment itself may ride along with the notice.
        Assert.DoesNotContain(PayPalUsdAmount.Format(order.UsdAmount), json, StringComparison.Ordinal);
        Assert.DoesNotContain(captureId, json, StringComparison.Ordinal);
        Assert.DoesNotContain("FAKE-", json, StringComparison.Ordinal);
        Assert.DoesNotContain("USD", json, StringComparison.Ordinal);
        Assert.DoesNotContain("providerCode", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_success_for_an_expired_order_is_shown_to_the_customer_on_every_later_visit()
    {
        var (order, paypalOrderId) = await StartedAsync();

        // Before anything lands: no notice, and the way to pay is there.
        var before = await ReadOrderPageAsync(order);
        Assert.Null(before.PaymentNotice);
        Assert.NotNull(before.Payment);

        Assert.True(await ExpireAtAsync(order.OrderId, Clock.Now.AddHours(2)));

        try
        {
            // Expired first, and the payment is read as the page's own bank-less "closed" order: no notice yet.
            var expired = await ReadOrderPageAsync(order);
            Assert.Equal("Expired", expired.Status);
            Assert.Null(expired.PaymentNotice);

            // The notification lands while no browser is open.
            using (var response = await PostWebhookAsync(Completed(order, paypalOrderId, "WH-NOTICE-EXP", Cap("CAP-NOTICE-EXP"))))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            var (page, json) = await ReadOrderPageWithJsonAsync(order);

            Assert.Equal("Expired", page.Status);
            AssertReceivedAndNothingElse(page, json, order, Cap("CAP-NOTICE-EXP"));
            Assert.False(page.CanCancel);

            // And it is still there on the next visit, and after a replay of the same notification.
            using (await PostWebhookAsync(Completed(order, paypalOrderId, "WH-NOTICE-EXP", Cap("CAP-NOTICE-EXP"))))
            {
            }

            Assert.Equal(OrderPaymentNotices.Received, (await ReadOrderPageAsync(order)).PaymentNotice);
        }
        finally
        {
            await DeleteAlertsAsync(order.PublicId);
        }
    }

    [Fact]
    public async Task A_success_for_the_wrong_amount_leaves_the_order_waiting_but_stops_inviting_a_payment()
    {
        var (order, paypalOrderId) = await StartedAsync();

        try
        {
            using (var response = await PostWebhookAsync(
                Completed(order, paypalOrderId, "WH-NOTICE-AMT", Cap("CAP-NOTICE-AMT"), amount: order.UsdAmount - 1m)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            var (page, json) = await ReadOrderPageWithJsonAsync(order);

            // Still PendingPayment, as doc 11 P8 step 6 says - and yet no bank details, no Pay now, no
            // "we are still confirming the payment details": the page says the money arrived.
            Assert.Equal("PendingPayment", page.Status);
            AssertReceivedAndNothingElse(page, json, order, Cap("CAP-NOTICE-AMT"));

            // The start endpoint refuses to open a second attempt beside it, whatever the page does.
            var attemptsBefore = (await PaymentsOfAsync(order.OrderId)).Count;
            var createdBefore = PayPal.Creates.Count;

            using (var start = await GetAsync(StartPath(order.PublicId)))
            {
                Assert.Equal(PayPalPageHints.Received, HintOf(start));
            }

            Assert.Equal(attemptsBefore, (await PaymentsOfAsync(order.OrderId)).Count);
            Assert.Equal(createdBefore, PayPal.Creates.Count);

            // Nor does coming back through the return path invite one.
            using (var returned = await GetAsync(ReturnPath(order.PublicId)))
            {
                Assert.NotEqual(PayPalPageHints.Paid, HintOf(returned));
            }

            Assert.Equal(attemptsBefore, (await PaymentsOfAsync(order.OrderId)).Count);
        }
        finally
        {
            await DeleteAlertsAsync(order.PublicId);
        }
    }

    [Fact]
    public async Task A_second_payment_for_an_order_that_is_already_paid_is_shown_on_its_order_page()
    {
        var (order, paypalOrderId) = await StartedAsync();

        using (var paid = await PostWebhookAsync(Completed(order, paypalOrderId, "WH-NOTICE-FIRST", Cap("CAP-NOTICE-FIRST"))))
        {
            Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        }

        var paidPage = await ReadOrderPageAsync(order);
        Assert.Equal("Paid", paidPage.Status);
        Assert.Null(paidPage.PaymentNotice);

        var secondPaymentId = await InsertPayPalPaymentAsync(order.OrderId, "FAKE-NOTICE-SECOND", PaymentStatus.Capturing);

        try
        {
            using (var response = await PostWebhookAsync(CaptureEvent(
                "PAYMENT.CAPTURE.COMPLETED",
                "WH-NOTICE-SECOND",
                Cap("CAP-NOTICE-SECOND"),
                "FAKE-NOTICE-SECOND",
                secondPaymentId,
                SecondTabUsd)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            var (page, json) = await ReadOrderPageWithJsonAsync(order);

            Assert.Equal("Paid", page.Status);
            Assert.Equal(OrderPaymentNotices.Received, page.PaymentNotice);
            Assert.Null(page.Payment);
            Assert.DoesNotContain(Cap("CAP-NOTICE-SECOND"), json, StringComparison.Ordinal);
            Assert.DoesNotContain("FAKE-", json, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteAlertsAsync(order.PublicId);
        }
    }

    [Theory]
    [InlineData(PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Cancelled)]
    [InlineData(PaymentStatus.Expired)]
    public async Task A_payment_that_took_no_money_adds_no_notice(PaymentStatus status)
    {
        var (order, _) = await StartedAsync();

        await SetPaymentStatusAsync(order.PaymentId, status);

        Assert.Null((await ReadOrderPageAsync(order)).PaymentNotice);
    }

    // ------------------------------------------------------------------------------ recorded, not applied

    [Theory]
    [InlineData("PAYMENT.CAPTURE.REFUNDED")]
    [InlineData("PAYMENT.CAPTURE.REVERSED")]
    public async Task A_refund_or_reversal_is_recorded_and_changes_nothing(string eventType)
    {
        var (order, paypalOrderId) = await StartedAsync();

        // Ids carry the event type: the database is shared by both cases of this theory, and a repeated event
        // id is (correctly) a duplicate.
        using (await PostWebhookAsync(Completed(order, paypalOrderId, "WH-BEFORE-" + eventType, Cap("CAP-R-" + eventType))))
        {
        }

        var payment = await PaymentAsync(order.PaymentId);
        var row = await OrderRowAsync(order.OrderId);

        using var response = await PostWebhookAsync(Event(eventType, order, paypalOrderId, "WH-REFUND-" + eventType, Cap("CAP-R-" + eventType)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Doc 08 §4.3's dashboard-refund handling belongs to the refund task; until then it is on record.
        var after = await PaymentAsync(order.PaymentId);
        Assert.Equal(payment.Status, after.Status);
        Assert.Equal(payment.RefundedAmount, after.RefundedAmount);
        Assert.Equal(row.Status, (await OrderRowAsync(order.OrderId)).Status);

        var stored = Assert.Single(await EventsOfAsync(order.PaymentId), e => e.ProviderEventId == "WH-REFUND-" + eventType)
            ;
        Assert.Equal(PaymentEventProcessingStatus.Ignored, stored.ProcessingStatus);
    }

    [Theory]
    [InlineData("CHECKOUT.ORDER.APPROVED")]
    [InlineData("CHECKOUT.ORDER.COMPLETED")]
    [InlineData("PAYMENT.CAPTURE.A.NEW.THING")]
    public async Task An_event_type_the_shop_does_not_act_on_is_stored_ignored_and_answered_200(string eventType)
    {
        var (order, paypalOrderId) = await StartedAsync();
        var events = await EventCountAsync();

        using var response = await PostWebhookAsync(Event(eventType, order, paypalOrderId, "WH-OTHER-" + eventType, Cap("CAP-X")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(events + 1, await EventCountAsync());
        Assert.Equal(PaymentStatus.Initiated, (await PaymentAsync(order.PaymentId)).Status);
    }

    // ------------------------------------------------------------------------------ failures and the retry job

    /// <summary>
    /// An event for a payment the shop cannot find yet is stored and kept for the retry job, not dropped
    /// (doc 08 DB13), and the answer is still 200: PayPal's redelivery would be a duplicate.
    /// </summary>
    [Fact]
    public async Task An_event_for_a_payment_the_shop_cannot_find_yet_is_kept_and_applied_by_the_retry_job()
    {
        var (order, _) = await StartedAsync();
        const string LatePayPalOrder = "FAKE-NOT-STORED-YET";

        using (var response = await PostWebhookAsync(CaptureEvent(
            "PAYMENT.CAPTURE.COMPLETED",
            "WH-NOMATCH-1",
            Cap("CAP-NOMATCH-1"),
            LatePayPalOrder,
            paymentId: 99_999_999,
            order.UsdAmount)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var stored = await EventByProviderIdAsync("WH-NOMATCH-1");
        Assert.Equal(PaymentEventProcessingStatus.Failed, stored.ProcessingStatus);
        Assert.Equal(1, stored.AttemptCount);
        Assert.NotNull(stored.LastError);
        Assert.Equal(Clock.Now.AddMinutes(1), stored.NextAttemptAtUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Null(stored.PaymentId);

        // The shop's own write lands; the next pass attributes and applies the event.
        await SetProviderReferenceAsync(order.PaymentId, LatePayPalOrder);
        Clock.Advance(TimeSpan.FromMinutes(2));

        var report = await RunRetryAsync();

        Assert.Equal(1, report.Processed);

        stored = await EventByProviderIdAsync("WH-NOMATCH-1");
        Assert.Equal(PaymentEventProcessingStatus.Processed, stored.ProcessingStatus);
        Assert.Null(stored.NextAttemptAtUtc);
        Assert.Null(stored.LastError);
        Assert.Equal(order.PaymentId, stored.PaymentId);
        Assert.Equal(PaymentStatus.Succeeded, (await PaymentAsync(order.PaymentId)).Status);
    }

    /// <summary>A worker that died between storing and applying leaves a Received row; the job finds it.</summary>
    [Fact]
    public async Task An_event_stored_by_a_worker_that_died_is_applied_by_the_retry_job()
    {
        var (order, paypalOrderId) = await StartedAsync();

        // Exactly what the endpoint does, up to the moment the process died.
        var receipt = await RecordWebhookAsync(
            Completed(order, paypalOrderId, "WH-DIED-1", Cap("CAP-DIED-1")),
            "WH-DIED-1",
            received: Clock.Now);

        Assert.True(receipt.IsNew);
        Assert.Equal(PaymentStatus.Initiated, (await PaymentAsync(order.PaymentId)).Status);

        // Not yet due: the request that stored it may still be applying it.
        Assert.Equal(0, (await RunRetryAsync()).Claimed);

        Clock.Advance(PaymentEventRetry.GraceBeforeFirstRetry + TimeSpan.FromSeconds(1));
        var report = await RunRetryAsync();

        Assert.Equal(1, report.Claimed);
        Assert.Equal(1, report.Processed);
        Assert.Equal(PaymentStatus.Succeeded, (await PaymentAsync(order.PaymentId)).Status);
        Assert.Equal(PaymentEventProcessingStatus.Processed, (await EventByProviderIdAsync("WH-DIED-1")).ProcessingStatus);
    }

    [Fact]
    public async Task The_retry_job_backs_off_and_after_ten_attempts_stops_for_a_person_to_look()
    {
        var (order, _) = await StartedAsync();

        using (await PostWebhookAsync(CaptureEvent(
            "PAYMENT.CAPTURE.COMPLETED",
            "WH-NEVER-1",
            Cap("CAP-NEVER-1"),
            "FAKE-NEVER-EXISTS",
            paymentId: 99_999_998,
            order.UsdAmount)))
        {
        }

        var stored = await EventByProviderIdAsync("WH-NEVER-1");
        Assert.Equal(1, stored.AttemptCount);

        // Not due before its time: nothing is claimed.
        Assert.Equal(0, (await RunRetryAsync()).Claimed);

        for (var attempt = 2; attempt <= PaymentEventRetryPolicy.MaxAttempts; attempt++)
        {
            Clock.Advance(TimeSpan.FromDays(2));
            var report = await RunRetryAsync();

            Assert.Equal(1, report.Claimed);
            Assert.Equal(1, report.Failed);
        }

        stored = await EventByProviderIdAsync("WH-NEVER-1");

        Assert.Equal(PaymentEventProcessingStatus.Failed, stored.ProcessingStatus);
        Assert.Equal(PaymentEventRetryPolicy.MaxAttempts, stored.AttemptCount);
        Assert.Null(stored.NextAttemptAtUtc);

        // Out of attempts: no pass will ever pick it up again.
        Clock.Advance(TimeSpan.FromDays(30));
        Assert.Equal(0, (await RunRetryAsync()).Claimed);
    }

    /// <summary>No ops.JobLeases table exists, so the claim must be what makes two overlapping passes harmless.</summary>
    [Fact]
    public async Task Overlapping_retry_passes_apply_each_event_once()
    {
        await QuietLeftoversAsync();
        await ConfigurePayPalAsync();
        var orders = new List<(PayPalOrder Order, string PayPalOrderId)>();

        for (var i = 0; i < 4; i++)
        {
            var order = await PlacePayPalOrderAsync();
            orders.Add((order, await StartAsync(order)));
        }

        foreach (var (order, paypalOrderId) in orders)
        {
            await RecordWebhookAsync(
                Completed(order, paypalOrderId, "WH-OVERLAP-" + order.PaymentId, Cap("CAP-OVERLAP-" + order.PaymentId)),
                "WH-OVERLAP-" + order.PaymentId,
                received: Clock.Now);
        }

        var seriesBefore = await InvoiceSeriesNextValueAsync();
        Clock.Advance(TimeSpan.FromMinutes(3));

        var reports = await Task.WhenAll(
            Enumerable.Range(0, 3).Select(_ => Task.Run(() => RunRetryAsync(), Token)));

        // Between them, every event was processed - once.
        Assert.Equal(4, reports.Sum(report => report.Processed));
        Assert.Equal(0, reports.Sum(report => report.Failed));

        foreach (var (order, _) in orders)
        {
            Assert.Equal(PaymentStatus.Succeeded, (await PaymentAsync(order.PaymentId)).Status);
            Assert.Single((await OrderRowAsync(order.OrderId)).StatusHistory, entry => entry.ToStatus == OrderStatus.Paid);
        }

        Assert.Equal((seriesBefore ?? 1) + 4, await InvoiceSeriesNextValueAsync());
    }

    /// <summary>
    /// The log also holds our own API responses and the buyer's return, which are records and not work: the
    /// job only ever claims verified-webhook events, and only for providers that have a processor.
    /// </summary>
    [Fact]
    public async Task The_retry_job_leaves_records_and_other_providers_events_alone()
    {
        var (order, _) = await StartedAsync();

        // A return-redirect and an API-response row, both "Received" with a due retry time.
        await RecordAsync(PaymentEventSource.ReturnRedirect, "return.redirect", "PayPal:return:manual", order.PaymentId, order.OrderId);
        await RecordAsync(PaymentEventSource.ApiResponse, "api.capture", "PayPal:capture-response:manual", order.PaymentId, order.OrderId);

        Clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0, (await RunRetryAsync()).Claimed);
    }

    // ------------------------------------------------------------------------------ helpers

    /// <summary>The dollar amount <see cref="InsertPayPalPaymentAsync"/> gives a payment.</summary>
    private const decimal SecondTabUsd = 31.25m;

    private async Task<int> InsertPayPalPaymentAsync(int orderId, string providerReference, PaymentStatus status)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var grandTotal = await db.Orders.AsNoTracking()
            .Where(candidate => candidate.Id == orderId)
            .Select(candidate => candidate.GrandTotal)
            .SingleAsync(Token);

        var payment = new Payment
        {
            OrderId = orderId,
            ProviderCode = PaymentProviderCodes.PayPal,
            Status = status,
            Amount = grandTotal,
            Currency = SalesCurrency.Lkr,
            ProviderCurrency = "USD",
            ProviderAmount = SecondTabUsd,
            ExchangeRate = 300m,
            FxBufferPercent = 3m,
            ProviderReference = providerReference,
            CaptureStartedAtUtc = status == PaymentStatus.Capturing ? DateTime.UtcNow : null,
            CreatedAtUtc = DateTime.UtcNow,
        };

        db.Payments.Add(payment);
        await db.SaveChangesAsync(Token);

        return payment.Id;
    }

    private async Task SetProviderReferenceAsync(int paymentId, string reference)
    {
        using var scope = CreateScope();

        var moved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Payments
            .Where(payment => payment.Id == paymentId)
            .ExecuteUpdateAsync(payment => payment.SetProperty(row => row.ProviderReference, reference), Token);

        Assert.Equal(1, moved);
    }

    private async Task<PaymentEvent> EventByProviderIdAsync(string providerEventId)
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().PaymentEvents
            .AsNoTracking()
            .SingleAsync(candidate => candidate.ProviderEventId == providerEventId, Token);
    }

    private Task<PaymentEventReceipt> RecordWebhookAsync(string body, string eventId, DateTime received) =>
        InPayPalScopeAsync(services => services.GetRequiredService<IPaymentEventStore>().RecordAsync(
            new PaymentEventRecord(
                PaymentProviderCodes.PayPal,
                PaymentEventSource.VerifiedWebhook,
                "PAYMENT.CAPTURE.COMPLETED",
                eventId,
                PaymentEventKey.ForEvent(PaymentProviderCodes.PayPal, eventId),
                body,
                received),
            Token));

    private Task<PaymentEventReceipt> RecordAsync(
        PaymentEventSource source,
        string type,
        string key,
        int paymentId,
        int orderId) =>
        InPayPalScopeAsync(services => services.GetRequiredService<IPaymentEventStore>().RecordAsync(
            new PaymentEventRecord(
                PaymentProviderCodes.PayPal,
                source,
                type,
                null,
                key,
                """{"note":"a record"}""",
                Clock.Now,
                paymentId,
                orderId),
            Token));

    private Task<SpencerDenim.Infrastructure.Sales.Payments.PaymentEventRetryReport> RunRetryAsync() =>
        InPayPalScopeAsync(services => services
            .GetRequiredService<SpencerDenim.Infrastructure.Sales.Payments.PaymentEventRetryRunner>()
            .RunOnceAsync(Token));
}
