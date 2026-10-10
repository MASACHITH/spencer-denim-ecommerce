using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Payments;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.PayPal;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Domain.Settings;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales.PayPal;

/// <summary>
/// Doc 08 §4.2 and doc 11 P8, end to end: the buyer is sent to PayPal, comes back, and the shop claims the
/// capture, captures and applies the answer - against a real SQL Server, through the real endpoints, with
/// the in-memory PayPal standing in for the one company this repository may not call.
/// <para>
/// <b>Every assertion is on the rows</b> - the order, the payment, the stock, the invoice series, the log and
/// the alerts - as the settlement tests it builds on do. The browser's redirect carries a hint only to pick a
/// sentence; the order's real status is what decides whether anybody is paid.
/// </para>
/// </summary>
public sealed class PayPalCaptureFlowTests(SqlServerDatabaseFixture sqlServer) : PayPalTestBase(sqlServer)
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(5);

    private string PayPalOrderOf(int paymentId) =>
        PayPal.OrderIdFor(PayPalRequestIds.Create(paymentId))
        ?? throw new InvalidOperationException($"PayPal made no order for payment {paymentId}.");

    /// <summary>The buyer presses Pay now, approves at PayPal and is sent back.</summary>
    private async Task<(string PayPalOrderId, HttpResponseMessage Return)> PayAsync(PayPalOrder order)
    {
        var paypalOrderId = await StartAsync(order);
        PayPal.Approve(paypalOrderId);

        return (paypalOrderId, await GetAsync(ReturnPath(order.PublicId)));
    }

    // ------------------------------------------------------------------------------ start

    [Fact]
    public async Task Starting_makes_the_PayPal_order_once_and_a_second_start_goes_back_to_the_same_one()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();

        var first = await StartAsync(order);
        var second = await StartAsync(order);

        Assert.Equal(first, second);

        // One order at PayPal, however many times the button was pressed.
        var created = Assert.Single(PayPal.Creates);
        Assert.Equal(PayPalRequestIds.Create(order.PaymentId), created.RequestId);
        Assert.Equal(order.UsdAmount, created.Amount);
        Assert.Equal("USD", created.Currency);
        Assert.Equal($"pay-{order.PaymentId}", created.CustomId);
        Assert.Contains(order.OrderNumber, created.Description, StringComparison.Ordinal);
        Assert.Equal($"{Origin}/api/payments/paypal/return/{order.PublicId:D}", created.ReturnUrl);
        Assert.Equal($"{Origin}/api/payments/paypal/cancel/{order.PublicId:D}", created.CancelUrl);

        var payment = await PaymentAsync(order.PaymentId);
        Assert.Equal(first, payment.ProviderReference);
        Assert.Equal(PaymentStatus.Initiated, payment.Status);

        // The order is exactly as placed: starting moves no money and holds nothing.
        var row = await OrderRowAsync(order.OrderId);
        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Null(row.CaptureLockedUntilUtc);
    }

    [Fact]
    public async Task Starting_an_order_that_does_not_exist_is_a_404()
    {
        await ConfigurePayPalAsync();

        using var response = await GetAsync(StartPath(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(PayPal.Creates);
    }

    [Fact]
    public async Task Starting_an_order_that_can_no_longer_be_paid_goes_back_and_creates_nothing()
    {
        await ConfigurePayPalAsync();

        // Expired.
        var expired = await PlacePayPalOrderAsync();
        Assert.True(await ExpireAtAsync(expired.OrderId, Clock.Now.AddHours(2)));

        using (var response = await GetAsync(StartPath(expired.PublicId)))
        {
            Assert.Equal(PayPalPageHints.Closed, HintOf(response));
        }

        // Paid.
        var paid = await PlacePayPalOrderAsync();
        var (_, done) = await PayAsync(paid);
        Assert.Equal(PayPalPageHints.Paid, HintOf(done));
        var creates = PayPal.Creates.Count;

        using (var response = await GetAsync(StartPath(paid.PublicId)))
        {
            Assert.Equal(PayPalPageHints.Closed, HintOf(response));
        }

        // Past its deadline but not yet swept by the job: still "no longer".
        var late = await PlacePayPalOrderAsync();
        await SetOrderDueAsync(late.OrderId, Clock.Now.AddMinutes(-1));

        using (var response = await GetAsync(StartPath(late.PublicId)))
        {
            Assert.Equal(PayPalPageHints.Closed, HintOf(response));
        }

        Assert.Equal(creates, PayPal.Creates.Count);
    }

    /// <summary>A bank-transfer order has no business being started on PayPal.</summary>
    [Fact]
    public async Task Starting_an_order_placed_with_the_bank_transfer_is_refused()
    {
        await ConfigurePayPalAsync();
        var (bankOrder, _) = await PlaceOrderAsync();

        using var response = await GetAsync(StartPath(bankOrder.PublicId));

        Assert.Equal(PayPalPageHints.Closed, HintOf(response));
        Assert.Empty(PayPal.Creates);
    }

    [Fact]
    public async Task Starting_while_PayPal_is_not_configured_goes_back_to_the_order_page()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();

        // A Super Admin removes the secret after the order was placed.
        await SetSettingAsync(SettingKeys.PayPal.ClientSecret, null);

        using var response = await GetAsync(StartPath(order.PublicId));

        Assert.Equal(PayPalPageHints.Unavailable, HintOf(response));
        Assert.Empty(PayPal.Creates);
    }

    [Fact]
    public async Task When_PayPal_cannot_be_reached_to_create_the_order_nothing_is_stored_and_the_next_start_works()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();

        PayPal.Down = true;

        using (var response = await GetAsync(StartPath(order.PublicId)))
        {
            Assert.Equal(PayPalPageHints.Unavailable, HintOf(response));
        }

        Assert.Null((await PaymentAsync(order.PaymentId)).ProviderReference);

        PayPal.Down = false;
        var paypalOrderId = await StartAsync(order);

        Assert.Equal(paypalOrderId, (await PaymentAsync(order.PaymentId)).ProviderReference);
    }

    /// <summary>
    /// A start must be able to finish what a crashed one began: PayPal made the order, the process died before
    /// the id was stored. The second try sends the same request id and gets the same order back.
    /// </summary>
    [Fact]
    public async Task A_start_that_died_after_PayPal_made_the_order_is_finished_by_the_next_one()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();

        // PayPal makes the order; the shop never learns of it.
        var lost = await PayPal.CreateOrderAsync(
            new PayPalCreateOrderRequest(
                PayPalRequestIds.Create(order.PaymentId),
                order.UsdAmount,
                "USD",
                PayPalRequestIds.CustomId(order.PaymentId),
                "x",
                "https://x.test/r",
                "https://x.test/c"),
            Token);

        var lostId = Assert.IsType<PayPalCreateOrderResult.Created>(lost).OrderId;
        Assert.Null((await PaymentAsync(order.PaymentId)).ProviderReference);

        var paypalOrderId = await StartAsync(order);

        Assert.Equal(lostId, paypalOrderId);
        Assert.Equal(lostId, (await PaymentAsync(order.PaymentId)).ProviderReference);
    }

    // ------------------------------------------------------------------------------ the happy path

    /// <summary>
    /// Start, approve, return: the order is Paid with every effect a staff approval has, and the browser got
    /// a sentence.
    /// </summary>
    [Fact]
    public async Task A_buyer_who_approves_at_PayPal_pays_the_order_with_every_effect_of_an_approval()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync(quantity: 2);
        var before = await StockAsync(order.VariantId);
        var seriesBefore = await InvoiceSeriesNextValueAsync();

        var (paypalOrderId, response) = await PayAsync(order);

        Assert.Equal(PayPalPageHints.Paid, HintOf(response));
        Assert.StartsWith($"/order/{order.PublicId:D}", response.Headers.Location!.OriginalString, StringComparison.Ordinal);

        // The capture went out once, under the stable request id, for the stored PayPal order.
        var call = Assert.Single(PayPal.Captures);
        Assert.Equal(paypalOrderId, call.OrderId);
        Assert.Equal($"capture-{order.PaymentId}", call.RequestId);

        var row = await OrderRowAsync(order.OrderId);
        Assert.Equal(OrderStatus.Paid, row.Status);
        Assert.Equal(OrderStockState.Committed, row.StockState);
        Assert.NotNull(row.PaidAtUtc);
        Assert.Null(row.CaptureLockedUntilUtc);
        Assert.NotNull(row.InvoiceNumber);

        var payment = await PaymentAsync(order.PaymentId);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.True(payment.IsOrderSettlement);
        Assert.Equal(PayPal.CaptureIdOf(paypalOrderId), payment.ProviderTransactionId);
        Assert.Equal("PAYER-" + paypalOrderId, payment.PayerReference);
        Assert.NotNull(payment.CaptureStartedAtUtc);
        Assert.Null(payment.FailureCode);

        // Doc 08 §5.5: reserved becomes sold, once; one gapless invoice number; one history row, from the provider.
        var after = await StockAsync(order.VariantId);
        Assert.Equal(before.OnHand - 2, after.OnHand);
        Assert.Equal(before.Reserved - 2, after.Reserved);
        Assert.Equal((seriesBefore ?? 1) + 1, await InvoiceSeriesNextValueAsync());

        var history = Assert.Single(row.StatusHistory, entry => entry.ToStatus == OrderStatus.Paid);
        Assert.Equal(OrderStatusChangeSource.PaymentProvider, history.Source);
        Assert.Null(history.ChangedByUserId);

        // Doc 11 P8 step 6: "Payment confirmed" email, in the same transaction.
        Assert.Equal(1, await OutboxCountAsync(order.PublicId, EmailTemplateCodes.PaymentConfirmed));

        // No banner: nothing went wrong.
        Assert.Empty(await AlertsOfTypeAsync(AdminAlertTypes.PaymentException, order.PublicId));
    }

    /// <summary>
    /// Every provider event is in the log for reconciliation: the order PayPal made, the buyer's return and
    /// PayPal's capture answer - with no payer name, email or address in any of them.
    /// </summary>
    [Fact]
    public async Task The_PayPal_order_the_return_and_the_capture_answer_are_all_recorded_without_the_payers_details()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();

        var (paypalOrderId, _) = await PayAsync(order);

        var events = await EventsOfAsync(order.PaymentId);

        Assert.Contains(events, e => e.EventType == "api.create-order" && e.EventSource == PaymentEventSource.ApiResponse);
        Assert.Contains(events, e => e.EventType == "return.redirect" && e.EventSource == PaymentEventSource.ReturnRedirect);
        Assert.Contains(events, e => e.EventType == "api.capture" && e.EventSource == PaymentEventSource.ApiResponse);

        // They are a log and not work: finished, with no retry time for the sweep to find.
        Assert.All(events, e =>
        {
            Assert.Equal(PaymentEventProcessingStatus.Processed, e.ProcessingStatus);
            Assert.Null(e.NextAttemptAtUtc);
            Assert.Equal(PaymentProviderCodes.PayPal, e.ProviderCode);
        });

        var capture = events.Single(e => e.EventType == "api.capture");
        using var json = JsonDocument.Parse(capture.PayloadJson);
        Assert.Equal("captured", json.RootElement.GetProperty("result").GetString());
        Assert.Equal(paypalOrderId, json.RootElement.GetProperty("paypalOrderId").GetString());
        Assert.DoesNotContain("@", capture.PayloadJson, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------ the claim

    /// <summary>
    /// Two returns at once (a double click, two tabs): the claim lets exactly one capture, and the other is
    /// told the payment is being processed - or finds it already paid.
    /// </summary>
    [Fact]
    public async Task Two_parallel_returns_capture_exactly_once()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();
        var before = await StockAsync(order.VariantId);

        var paypalOrderId = await StartAsync(order);
        PayPal.Approve(paypalOrderId);

        // Hold the first capture call open until the second return has arrived and been refused.
        var released = new TaskCompletionSource();
        PayPal.DuringCapture = async (call, _, _) =>
        {
            if (call == 1)
            {
                await released.Task;
            }
        };

        var first = Task.Run(() => GetAsync(ReturnPath(order.PublicId)), Token);

        // The second arrives while the first holds the claim.
        await WaitUntilAsync(() => PayPal.CaptureCalls == 1);
        using var second = await GetAsync(ReturnPath(order.PublicId));
        Assert.Equal(PayPalPageHints.Processing, HintOf(second));

        released.SetResult();
        using var firstResponse = await first;
        Assert.Equal(PayPalPageHints.Paid, HintOf(firstResponse));

        Assert.Equal(1, PayPal.CaptureCalls);
        Assert.Equal(1, PayPal.CapturesAtPayPal(paypalOrderId));

        var row = await OrderRowAsync(order.OrderId);
        Assert.Equal(OrderStatus.Paid, row.Status);
        Assert.Single(row.StatusHistory, entry => entry.ToStatus == OrderStatus.Paid);
        Assert.Equal(before.OnHand - 1, (await StockAsync(order.VariantId)).OnHand);
        Assert.Equal(1, await OutboxCountAsync(order.PublicId, EmailTemplateCodes.PaymentConfirmed));
    }

    /// <summary>Six returns racing: whatever the interleaving, one capture and one settlement.</summary>
    [Fact]
    public async Task A_burst_of_returns_never_captures_twice_or_settles_twice()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync(quantity: 2);
        var before = await StockAsync(order.VariantId);
        var seriesBefore = await InvoiceSeriesNextValueAsync();

        var paypalOrderId = await StartAsync(order);
        PayPal.Approve(paypalOrderId);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => Task.Run(() => GetAsync(ReturnPath(order.PublicId)), Token)));

        var hints = responses.Select(HintOf).ToList();

        // Every answer is one of the two honest ones.
        Assert.All(hints, hint => Assert.Contains(hint, new[] { PayPalPageHints.Paid, PayPalPageHints.Processing }));
        Assert.Contains(PayPalPageHints.Paid, hints);

        Assert.Equal(1, PayPal.CapturesAtPayPal(paypalOrderId));
        Assert.Equal(OrderStatus.Paid, (await OrderRowAsync(order.OrderId)).Status);
        Assert.Equal(before.OnHand - 2, (await StockAsync(order.VariantId)).OnHand);
        Assert.Equal((seriesBefore ?? 1) + 1, await InvoiceSeriesNextValueAsync());
        Assert.Equal(1, await OutboxCountAsync(order.PublicId, EmailTemplateCodes.PaymentConfirmed));

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// The browser's return is not proof of payment, and the PayPal order captured is the one the shop stored -
    /// never the one in the query string.
    /// </summary>
    [Fact]
    public async Task The_return_captures_the_stored_PayPal_order_and_ignores_the_token_in_the_address()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();
        var paypalOrderId = await StartAsync(order);
        PayPal.Approve(paypalOrderId);

        using var response = await GetAsync(ReturnPath(order.PublicId) + "?token=SOME-OTHER-ORDER&PayerID=EVIL");

        Assert.Equal(PayPalPageHints.Paid, HintOf(response));
        Assert.Equal(paypalOrderId, Assert.Single(PayPal.Captures).OrderId);
    }

    /// <summary>A return without an approval is not a payment: nothing is paid and the buyer can go back.</summary>
    [Fact]
    public async Task A_return_without_approval_pays_nothing_and_gives_the_claim_back()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();
        var paypalOrderId = await StartAsync(order);

        // The buyer is back, but never approved.
        using (var response = await GetAsync(ReturnPath(order.PublicId)))
        {
            Assert.Equal(PayPalPageHints.NotApproved, HintOf(response));
        }

        var row = await OrderRowAsync(order.OrderId);
        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Equal(OrderStockState.Reserved, row.StockState);
        Assert.Null(row.CaptureLockedUntilUtc);

        var payment = await PaymentAsync(order.PaymentId);
        Assert.Equal(PaymentStatus.Initiated, payment.Status);
        Assert.False(payment.IsOrderSettlement);

        // They go back, approve, and return again: now it pays.
        PayPal.Approve(paypalOrderId);

        using (var response = await GetAsync(ReturnPath(order.PublicId)))
        {
            Assert.Equal(PayPalPageHints.Paid, HintOf(response));
        }

        Assert.Equal(OrderStatus.Paid, (await OrderRowAsync(order.OrderId)).Status);
    }

    [Fact]
    public async Task A_return_before_any_PayPal_order_exists_captures_nothing()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();

        using var response = await GetAsync(ReturnPath(order.PublicId));

        // They came back, but there is no PayPal order they could have approved.
        Assert.Equal(PayPalPageHints.NotApproved, HintOf(response));
        Assert.Empty(PayPal.Captures);
        Assert.Equal(PaymentStatus.Initiated, (await PaymentAsync(order.PaymentId)).Status);
    }

    [Fact]
    public async Task A_return_for_an_order_that_does_not_exist_is_a_404()
    {
        using var response = await GetAsync(ReturnPath(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Doc 11 P8 "Exceptions": the buyer approves after the deadline and the order has expired - the claim
    /// fails, nothing is captured, and they are told the order can no longer be paid.
    /// </summary>
    [Fact]
    public async Task A_buyer_who_approves_after_the_order_expired_is_told_so_and_nothing_is_captured()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();
        var paypalOrderId = await StartAsync(order);
        PayPal.Approve(paypalOrderId);

        Clock.Advance(TimeSpan.FromMinutes(61));
        Assert.True(await ExpireAtAsync(order.OrderId, Clock.Now));

        using var response = await GetAsync(ReturnPath(order.PublicId));

        Assert.Equal(PayPalPageHints.Closed, HintOf(response));
        Assert.Empty(PayPal.Captures);
        Assert.Equal(0, PayPal.CapturesAtPayPal(paypalOrderId));

        // The expiry marked the open payment Expired with the order; no money moved.
        Assert.Equal(PaymentStatus.Expired, (await PaymentAsync(order.PaymentId)).Status);
        Assert.Equal(OrderStatus.Expired, (await OrderRowAsync(order.OrderId)).Status);
    }

    // ------------------------------------------------------------------------------ the awkward results

    /// <summary>
    /// The scenario the whole claim exists for: the capture is in flight, the order expires under it (the
    /// lock having run out), and COMPLETED arrives. The money is kept as an exception and staff are told.
    /// </summary>
    [Fact]
    public async Task A_capture_that_completes_after_the_order_expired_becomes_a_payment_exception_with_an_alert()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();
        var stockBefore = await StockAsync(order.VariantId);

        var paypalOrderId = await StartAsync(order);
        PayPal.Approve(paypalOrderId);

        var expired = false;
        PayPal.DuringCapture = async (_, _, _) =>
        {
            // Four hours pass inside the capture call: the three-hour lock has lapsed, so expiry may proceed.
            Clock.Advance(TimeSpan.FromHours(4));
            expired = await ExpireAtAsync(order.OrderId, Clock.Now);
        };

        using var response = await GetAsync(ReturnPath(order.PublicId));

        Assert.True(expired);
        Assert.Equal(PayPalPageHints.Received, HintOf(response));

        try
        {
            var row = await OrderRowAsync(order.OrderId);
            Assert.Equal(OrderStatus.Expired, row.Status);
            Assert.Equal(OrderStockState.Released, row.StockState);
            Assert.Null(row.PaidAtUtc);
            Assert.Null(row.InvoiceNumber);

            var payment = await PaymentAsync(order.PaymentId);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, payment.Status);
            Assert.False(payment.IsOrderSettlement);
            Assert.Equal(PayPal.CaptureIdOf(paypalOrderId), payment.ProviderTransactionId);

            var alert = Assert.Single(await AlertsOfTypeAsync(AdminAlertTypes.PaymentException, order.PublicId));
            Assert.Equal(AdminAlertSeverity.Warning, alert.Severity);
            Assert.Contains("order_expired", alert.DetailsJson, StringComparison.Ordinal);

            // No stock was taken for an order that no longer holds any.
            Assert.Equal(stockBefore.OnHand, (await StockAsync(order.VariantId)).OnHand);
            Assert.Equal(0, await OutboxCountAsync(order.PublicId, EmailTemplateCodes.PaymentConfirmed));
        }
        finally
        {
            await DeleteAlertsAsync(order.PublicId);
        }
    }

    /// <summary>Doc 08 §4.2 R9: a wrong amount is kept as an exception, can only be refunded, and frees the lock.</summary>
    [Fact]
    public async Task A_capture_for_the_wrong_amount_is_an_exception_that_clears_the_lock_and_leaves_the_order_payable()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();
        var stockBefore = await StockAsync(order.VariantId);

        PayPal.CaptureAmountOverride = 1.00m;
        var (_, response) = await PayAsync(order);

        try
        {
            Assert.Equal(PayPalPageHints.Received, HintOf(response));

            var payment = await PaymentAsync(order.PaymentId);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, payment.Status);
            Assert.Equal(PaymentFailureCodes.AmountMismatch, payment.FailureCode);
            Assert.False(payment.IsOrderSettlement);

            // The order is still payable: PendingPayment, nothing deducted, and the lock the claim set is gone.
            var row = await OrderRowAsync(order.OrderId);
            Assert.Equal(OrderStatus.PendingPayment, row.Status);
            Assert.Equal(OrderStockState.Reserved, row.StockState);
            Assert.Null(row.CaptureLockedUntilUtc);
            Assert.Equal(stockBefore.OnHand, (await StockAsync(order.VariantId)).OnHand);

            var alert = Assert.Single(await AlertsOfTypeAsync(AdminAlertTypes.PaymentException, order.PublicId));
            Assert.Contains("amount_mismatch", alert.DetailsJson, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteAlertsAsync(order.PublicId);
        }
    }

    [Fact]
    public async Task A_capture_in_the_wrong_currency_is_an_exception_too()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();

        PayPal.CaptureCurrency = "EUR";
        var (_, response) = await PayAsync(order);

        try
        {
            Assert.Equal(PayPalPageHints.Received, HintOf(response));
            Assert.Equal(PaymentFailureCodes.AmountMismatch, (await PaymentAsync(order.PaymentId)).FailureCode);
            Assert.Equal(OrderStatus.PendingPayment, (await OrderRowAsync(order.OrderId)).Status);
        }
        finally
        {
            await DeleteAlertsAsync(order.PublicId);
        }
    }

    /// <summary>
    /// A PENDING capture (an eCheck): the payment is Pending, the lock is cleared and the hold is extended to
    /// placed + 5 days, with the stock still reserved. Retry is unavailable until PayPal resolves it.
    /// </summary>
    [Fact]
    public async Task A_PENDING_capture_holds_the_order_for_five_days_and_blocks_a_retry()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();
        var stockBefore = await StockAsync(order.VariantId);

        PayPal.Script(FakePayPalGateway.CaptureScript.Pending);
        var (paypalOrderId, response) = await PayAsync(order);

        Assert.Equal(PayPalPageHints.Pending, HintOf(response));

        var payment = await PaymentAsync(order.PaymentId);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.False(payment.IsOrderSettlement);
        Assert.Equal(PayPal.CaptureIdOf(paypalOrderId), payment.ProviderTransactionId);

        var row = await OrderRowAsync(order.OrderId);
        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Equal(OrderStockState.Reserved, row.StockState);
        Assert.Null(row.CaptureLockedUntilUtc);

        // N16: max(PaymentDueAtUtc, PlacedAtUtc + PendingCaptureMaxDays).
        Assert.Equal(row.PlacedAtUtc.AddDays(5), row.PaymentDueAtUtc, Tolerance);
        Assert.Equal(stockBefore.Reserved, (await StockAsync(order.VariantId)).Reserved);

        // Doc 11 P8 step 9: a Pending PayPal payment blocks retry, and nothing new is created.
        var paymentsBefore = (await PaymentsOfAsync(order.OrderId)).Count;
        var createsBefore = PayPal.Creates.Count;

        using (var start = await GetAsync(StartPath(order.PublicId)))
        {
            Assert.Equal(PayPalPageHints.Pending, HintOf(start));
        }

        Assert.Equal(paymentsBefore, (await PaymentsOfAsync(order.OrderId)).Count);
        Assert.Equal(createsBefore, PayPal.Creates.Count);

        // And the order cannot be expired out from under it for five days.
        Assert.False(await ExpireAtAsync(order.OrderId, Clock.Now.AddDays(1)));
    }

    /// <summary>
    /// Doc 08 §4.2 R2: when a Pending capture is later DENIED the long hold is cut back to now + 60 minutes,
    /// the buyer may retry until then, and a retry opens a fresh payment at the CURRENT rate.
    /// </summary>
    [Fact]
    public async Task A_later_DENIED_cuts_the_hold_back_and_reopens_the_retry_at_the_current_rate()
    {
        await ConfigurePayPalAsync(rate: 300m);
        var order = await PlacePayPalOrderAsync();

        PayPal.Script(FakePayPalGateway.CaptureScript.Pending);
        var (paypalOrderId, _) = await PayAsync(order);
        var captureId = PayPal.CaptureIdOf(paypalOrderId)!;

        Clock.Advance(TimeSpan.FromDays(2));

        using var denied = await PostWebhookAsync(CaptureEvent(
            "PAYMENT.CAPTURE.DENIED",
            "WH-DENIED-1",
            captureId,
            paypalOrderId,
            order.PaymentId,
            order.UsdAmount));

        Assert.Equal(HttpStatusCode.OK, denied.StatusCode);

        var payment = await PaymentAsync(order.PaymentId);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(PayPalCaptureApplier.InstrumentDeclined, payment.FailureCode);
        Assert.False(payment.IsOrderSettlement);

        // min(PaymentDueAtUtc, now + PayPalExpiryMinutes): the five-day hold is over.
        var row = await OrderRowAsync(order.OrderId);
        Assert.Equal(Clock.Now.AddMinutes(60), row.PaymentDueAtUtc, Tolerance);
        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Null(row.CaptureLockedUntilUtc);

        // The buyer may try again, and it is a new payment at today's rate.
        await SetSettingAsync(SettingKeys.PayPal.LkrPerUsd, "310.00");
        await SetSettingAsync(
            SettingKeys.PayPal.RateUpdatedAtUtc,
            Clock.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        var retryPayPalOrderId = await StartAsync(order);
        Assert.NotEqual(paypalOrderId, retryPayPalOrderId);

        var payments = await PaymentsOfAsync(order.OrderId);
        Assert.Equal(2, payments.Count);

        var retry = payments[1];
        Assert.Equal(PaymentStatus.Initiated, retry.Status);
        Assert.Equal(310m, retry.ExchangeRate);
        Assert.Equal(PayPalUsdAmount.Compute(row.GrandTotal, 310m, 3m), retry.ProviderAmount);
        Assert.Equal(retryPayPalOrderId, retry.ProviderReference);

        // The failed attempt is untouched, and the retry pays the order.
        Assert.Equal(PaymentStatus.Failed, (await PaymentAsync(order.PaymentId)).Status);

        PayPal.Approve(retryPayPalOrderId);
        using var done = await GetAsync(ReturnPath(order.PublicId));

        Assert.Equal(PayPalPageHints.Paid, HintOf(done));
        Assert.Equal(OrderStatus.Paid, (await OrderRowAsync(order.OrderId)).Status);
        Assert.Equal(PaymentStatus.Failed, (await PaymentAsync(order.PaymentId)).Status);
    }

    [Fact]
    public async Task A_Pending_capture_that_later_completes_pays_the_order()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();

        PayPal.Script(FakePayPalGateway.CaptureScript.Pending);
        var (paypalOrderId, _) = await PayAsync(order);

        using var completed = await PostWebhookAsync(CaptureEvent(
            "PAYMENT.CAPTURE.COMPLETED",
            "WH-DONE-1",
            PayPal.CaptureIdOf(paypalOrderId)!,
            paypalOrderId,
            order.PaymentId,
            order.UsdAmount));

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

        var row = await OrderRowAsync(order.OrderId);
        Assert.Equal(OrderStatus.Paid, row.Status);
        Assert.Equal(PaymentStatus.Succeeded, (await PaymentAsync(order.PaymentId)).Status);
        Assert.Equal(1, await OutboxCountAsync(order.PublicId, EmailTemplateCodes.PaymentConfirmed));
    }

    /// <summary>Doc 11 P8 "Exceptions": a PENDING capture completes on day 6, after the order expired - an exception, then a refund.</summary>
    [Fact]
    public async Task A_Pending_capture_that_completes_after_the_hold_expired_is_an_exception()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();

        PayPal.Script(FakePayPalGateway.CaptureScript.Pending);
        var (paypalOrderId, _) = await PayAsync(order);

        Clock.Advance(TimeSpan.FromDays(6));
        Assert.True(await ExpireAtAsync(order.OrderId, Clock.Now));

        try
        {
            using var completed = await PostWebhookAsync(CaptureEvent(
                "PAYMENT.CAPTURE.COMPLETED",
                "WH-LATE-1",
                PayPal.CaptureIdOf(paypalOrderId)!,
                paypalOrderId,
                order.PaymentId,
                order.UsdAmount));

            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

            Assert.Equal(OrderStatus.Expired, (await OrderRowAsync(order.OrderId)).Status);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, (await PaymentAsync(order.PaymentId)).Status);
            Assert.Single(await AlertsOfTypeAsync(AdminAlertTypes.PaymentException, order.PublicId));
        }
        finally
        {
            await DeleteAlertsAsync(order.PublicId);
        }
    }

    /// <summary>
    /// DENIED at capture: Failed (INSTRUMENT_DECLINED), the lock cleared, the deadline untouched (the payment
    /// was not Pending, so there is no long hold to cut), and the buyer can try again until it is due.
    /// </summary>
    [Fact]
    public async Task A_declined_capture_fails_the_payment_and_lets_the_buyer_try_again()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();
        var dueBefore = (await OrderRowAsync(order.OrderId)).PaymentDueAtUtc;

        PayPal.Script(FakePayPalGateway.CaptureScript.Declined);
        var (paypalOrderId, response) = await PayAsync(order);

        Assert.Equal(PayPalPageHints.Declined, HintOf(response));

        var payment = await PaymentAsync(order.PaymentId);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal("INSTRUMENT_DECLINED", payment.FailureCode);
        Assert.Null(payment.ProviderTransactionId);

        var row = await OrderRowAsync(order.OrderId);
        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Equal(OrderStockState.Reserved, row.StockState);
        Assert.Null(row.CaptureLockedUntilUtc);
        Assert.Equal(dueBefore, row.PaymentDueAtUtc);

        // Try again: a new payment, a new PayPal order, and this one pays.
        var retryOrderId = await StartAsync(order);
        Assert.NotEqual(paypalOrderId, retryOrderId);
        Assert.Equal(2, (await PaymentsOfAsync(order.OrderId)).Count);

        PayPal.Approve(retryOrderId);
        using var done = await GetAsync(ReturnPath(order.PublicId));

        Assert.Equal(PayPalPageHints.Paid, HintOf(done));
        Assert.Equal(OrderStatus.Paid, (await OrderRowAsync(order.OrderId)).Status);
    }

    // ------------------------------------------------------------------------------ the lock and the timeout

    /// <summary>
    /// A timeout leaves the payment Capturing with the lock on: the order cannot expire under the money, the
    /// buyer is told it is being processed, and a second return captures nothing more.
    /// </summary>
    [Fact]
    public async Task A_timeout_leaves_the_payment_Capturing_and_the_order_locked_against_expiry()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();
        var claimedAt = Clock.Now;

        PayPal.Script(FakePayPalGateway.CaptureScript.AnswerLost);
        var (paypalOrderId, response) = await PayAsync(order);

        Assert.Equal(PayPalPageHints.Processing, HintOf(response));

        var payment = await PaymentAsync(order.PaymentId);
        Assert.Equal(PaymentStatus.Capturing, payment.Status);
        Assert.Equal(claimedAt, payment.CaptureStartedAtUtc!.Value, Tolerance);
        Assert.False(payment.IsOrderSettlement);

        var row = await OrderRowAsync(order.OrderId);
        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Equal(claimedAt.AddHours(3), row.CaptureLockedUntilUtc!.Value, Tolerance);

        // The order is past its 60 minutes and still cannot be expired: the money may be about to arrive.
        Clock.Advance(TimeSpan.FromHours(2));
        Assert.False(await ExpireAtAsync(order.OrderId, Clock.Now));
        Assert.Equal(OrderStatus.PendingPayment, (await OrderRowAsync(order.OrderId)).Status);

        // A second tab: told it is being processed, and nothing more is asked of PayPal.
        using (var again = await GetAsync(ReturnPath(order.PublicId)))
        {
            Assert.Equal(PayPalPageHints.Processing, HintOf(again));
        }

        using (var start = await GetAsync(StartPath(order.PublicId)))
        {
            Assert.Equal(PayPalPageHints.Processing, HintOf(start));
        }

        Assert.Equal(1, PayPal.CaptureCalls);
        Assert.Equal(1, PayPal.CapturesAtPayPal(paypalOrderId));
        Assert.Single(await PaymentsOfAsync(order.OrderId));
    }

    /// <summary>
    /// A refusal the shop has no specific answer for proves nothing about the money: the payment stays
    /// Capturing and the lock stays on, for the recovery job to find out.
    /// </summary>
    [Fact]
    public async Task A_refusal_with_no_specific_meaning_leaves_the_payment_for_recovery()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();

        PayPal.Script(FakePayPalGateway.CaptureScript.Rejected);
        var (_, response) = await PayAsync(order);

        Assert.Equal(PayPalPageHints.Processing, HintOf(response));
        Assert.Equal(PaymentStatus.Capturing, (await PaymentAsync(order.PaymentId)).Status);
        Assert.NotNull((await OrderRowAsync(order.OrderId)).CaptureLockedUntilUtc);
    }

    // ------------------------------------------------------------------------------ cancel

    [Fact]
    public async Task A_buyer_who_backs_out_cancels_the_attempt_and_the_next_one_starts_fresh()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();
        var first = await StartAsync(order);

        using (var response = await GetAsync(CancelPath(order.PublicId)))
        {
            Assert.Equal(PayPalPageHints.Cancelled, HintOf(response));
        }

        var payments = await PaymentsOfAsync(order.OrderId);
        Assert.Equal(PaymentStatus.Cancelled, Assert.Single(payments).Status);
        Assert.Equal(OrderStatus.PendingPayment, (await OrderRowAsync(order.OrderId)).Status);

        // Pay again: a new payment (the index allows it: the old one is closed) and a new PayPal order.
        var second = await StartAsync(order);

        Assert.NotEqual(first, second);

        payments = await PaymentsOfAsync(order.OrderId);
        Assert.Equal(2, payments.Count);
        Assert.Equal(PaymentStatus.Initiated, payments[1].Status);

        PayPal.Approve(second);
        using var done = await GetAsync(ReturnPath(order.PublicId));
        Assert.Equal(PayPalPageHints.Paid, HintOf(done));
    }

    /// <summary>Cancel touches an Initiated attempt only: a payment being collected, pending or paid is left alone.</summary>
    [Fact]
    public async Task Cancelling_cannot_touch_a_payment_that_is_being_captured_pending_or_paid()
    {
        await ConfigurePayPalAsync();

        // Capturing.
        var capturing = await PlacePayPalOrderAsync();
        PayPal.Script(FakePayPalGateway.CaptureScript.AnswerLost);
        await PayAsync(capturing);

        using (var response = await GetAsync(CancelPath(capturing.PublicId)))
        {
            Assert.Equal(PayPalPageHints.Processing, HintOf(response));
        }

        Assert.Equal(PaymentStatus.Capturing, (await PaymentAsync(capturing.PaymentId)).Status);

        // Pending.
        var pending = await PlacePayPalOrderAsync();
        PayPal.Script(FakePayPalGateway.CaptureScript.Pending);
        await PayAsync(pending);

        using (var response = await GetAsync(CancelPath(pending.PublicId)))
        {
            Assert.Equal(PayPalPageHints.Pending, HintOf(response));
        }

        Assert.Equal(PaymentStatus.Pending, (await PaymentAsync(pending.PaymentId)).Status);

        // Paid.
        var paid = await PlacePayPalOrderAsync();
        await PayAsync(paid);

        using (var response = await GetAsync(CancelPath(paid.PublicId)))
        {
            Assert.Equal(PayPalPageHints.Paid, HintOf(response));
        }

        Assert.Equal(PaymentStatus.Succeeded, (await PaymentAsync(paid.PaymentId)).Status);
    }

    [Fact]
    public async Task Cancelling_an_order_that_does_not_exist_is_a_404()
    {
        using var response = await GetAsync(CancelPath(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------------------------ the retry rule

    /// <summary>
    /// The UX_Payments_OneOpen hazard, as a property: whatever a buyer presses, an order never holds two open
    /// (Initiated or Pending) PayPal payments, and a payment being captured is never beside a new one.
    /// </summary>
    [Fact]
    public async Task Parallel_starts_never_leave_two_open_payments_on_one_order()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();

        // Close the first attempt so every start below is a retry that wants to INSERT.
        await StartAsync(order);
        using (var cancelled = await GetAsync(CancelPath(order.PublicId)))
        {
            Assert.Equal(PayPalPageHints.Cancelled, HintOf(cancelled));
        }

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(() => GetAsync(StartPath(order.PublicId)), Token)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Redirect, response.StatusCode));

        var payments = await PaymentsOfAsync(order.OrderId);
        var open = payments.Where(payment => payment.Status is PaymentStatus.Initiated or PaymentStatus.Pending).ToList();

        // One cancelled, one new open one: eight starts, one attempt.
        Assert.Equal(2, payments.Count);
        Assert.Single(open);

        // And they all went to the one PayPal order for it.
        Assert.Single(
            responses.Select(response => response.Headers.Location!.OriginalString).Distinct(StringComparer.Ordinal));

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    // ------------------------------------------------------------------------------ helpers

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 600; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50, Token);
        }

        Assert.Fail("The condition the test was waiting for never became true.");
    }
}
