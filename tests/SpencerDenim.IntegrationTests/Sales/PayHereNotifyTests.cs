using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Payments;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.PayHere;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// PayHere's server-to-server notification, posted to the real endpoint, against a real SQL Server (doc 11
/// P8b step 4). <b>This is the attack surface</b>, so the suite is written the way an attacker would work:
/// forge the signature, replay the request, bend the amount and the currency, name another order, pay late,
/// pay twice, send it twice at once.
/// <para>
/// Every assertion is on rows - the order, the payment, the stock, the invoice series, the event log, the
/// outbox, the banner - because the property being proved is that <i>nothing changed</i> or that <i>exactly
/// one thing changed</i>, and an HTTP status cannot say that.
/// </para>
/// </summary>
public sealed class PayHereNotifyTests(SqlServerDatabaseFixture sqlServer) : PayHereTestBase(sqlServer)
{
    // ---- the happy path ----------------------------------------------------------------------------

    [Fact]
    public async Task A_verified_success_pays_the_order_with_every_effect_of_an_approval()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync(quantity: 2);
        var before = await StockAsync(placed.VariantId);
        var payHereId = NewPayHerePaymentId();

        Assert.Equal(2, before.Reserved);

        using var response = await PostNotifyAsync(
            NotifyForm(placed.OrderReference, placed.GrandTotal, payHerePaymentId: payHereId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(Token));

        var order = await ReadOrderAsync(placed.PublicId);

        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal(OrderStockState.Committed, order.StockState);
        Assert.NotNull(order.PaidAtUtc);
        Assert.NotNull(order.InvoiceNumber);

        var payment = Assert.Single(order.Payments);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.True(payment.IsOrderSettlement);
        Assert.Equal(PaymentProviderCodes.PayHere, payment.ProviderCode);
        Assert.Equal(placed.GrandTotal, payment.Amount);
        Assert.Equal("LKR", payment.Currency);
        Assert.Equal(payHereId, payment.ProviderTransactionId);
        Assert.Equal(payHereId, payment.ProviderReference);
        Assert.Null(payment.FailureCode);

        // LKR: nothing was converted.
        Assert.Null(payment.ProviderCurrency);
        Assert.Null(payment.ProviderAmount);
        Assert.Null(payment.ExchangeRate);
        Assert.Null(payment.FxBufferPercent);

        // Nobody approved it.
        Assert.Null(payment.ApprovedByUserId);

        // Doc 08 §5.5: reserved becomes sold.
        var after = await StockAsync(placed.VariantId);
        Assert.Equal(before.OnHand - 2, after.OnHand);
        Assert.Equal(before.Reserved - 2, after.Reserved);

        var history = Assert.Single(order.StatusHistory, entry => entry.ToStatus == OrderStatus.Paid);
        Assert.Equal(OrderStatusChangeSource.PaymentProvider, history.Source);
        Assert.Null(history.ChangedByUserId);
        Assert.Equal("Payment confirmed by PayHere", history.Note);

        // Doc 11 P8 step 6, queued in the same transaction.
        var email = Assert.Single(await OutboxAsync(placed.PublicId, EmailTemplateCodes.PaymentConfirmed));
        Assert.Contains(order.InvoiceNumber, email.BodyText!, StringComparison.Ordinal);

        // No banner for an ordinary payment.
        Assert.Empty(await AlertsAsync(placed.PublicId));
    }

    [Fact]
    public async Task The_event_is_stored_once_linked_and_processed_and_holds_no_card_data_and_no_signature()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();
        var payHereId = NewPayHerePaymentId();
        var form = NotifyForm(placed.OrderReference, placed.GrandTotal, payHerePaymentId: payHereId);

        using var response = await PostNotifyAsync(form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = Assert.Single(await EventsAsync(providerPaymentId: payHereId));

        Assert.Equal(PaymentEventSource.VerifiedWebhook, stored.EventSource);
        Assert.Equal(PaymentEventProcessingStatus.Processed, stored.ProcessingStatus);
        Assert.Equal("payment.success", stored.EventType);
        Assert.Equal($"PayHere:{payHereId}:2", stored.IdempotencyKey);
        Assert.Equal(placed.PaymentId, stored.PaymentId);
        Assert.Equal(placed.OrderId, stored.OrderId);
        Assert.Null(stored.NextAttemptAtUtc);
        Assert.Equal(1, stored.AttemptCount);

        // What PayHere posted that this shop has no use for was never written down.
        foreach (var forbidden in new[] { "Kamal", "************1234", "12/30", "card_", "md5sig", form["md5sig"] })
        {
            Assert.DoesNotContain(forbidden, stored.PayloadJson, StringComparison.Ordinal);
        }

        using var payload = JsonDocument.Parse(stored.PayloadJson);
        Assert.Equal(placed.OrderReference, payload.RootElement.GetProperty("order_id").GetString());
        Assert.Equal(payHereId, payload.RootElement.GetProperty("payment_id").GetString());
    }

    // ---- forged and tampered requests: nothing is stored -------------------------------------------

    [Fact]
    public async Task A_forged_signature_is_400_and_stores_nothing_and_changes_nothing()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();
        var events = await PayHereEventCountAsync();

        using var response = await PostNotifyAsync(
            NotifyForm(placed.OrderReference, placed.GrandTotal, signature: new string('A', 32)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(Token));

        Assert.Equal(events, await PayHereEventCountAsync());
        await AssertStillUnpaidAsync(placed);
    }

    [Fact]
    public async Task A_signature_made_with_the_wrong_secret_is_refused()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();
        var events = await PayHereEventCountAsync();

        using var response = await PostNotifyAsync(
            NotifyForm(placed.OrderReference, placed.GrandTotal, secret: "a-guess-at-the-secret"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(events, await PayHereEventCountAsync());
        await AssertStillUnpaidAsync(placed);
    }

    [Theory]
    [InlineData("payhere_amount", "1.00")]
    [InlineData("payhere_currency", "USD")]
    [InlineData("status_code", "0")]
    [InlineData("order_id", "SD-2026-000001-1")]
    public async Task Changing_a_signed_field_of_a_genuine_notification_breaks_it(string field, string value)
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();
        var events = await PayHereEventCountAsync();

        var form = NotifyForm(placed.OrderReference, placed.GrandTotal);
        form[field] = value;

        using var response = await PostNotifyAsync(form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(events, await PayHereEventCountAsync());
        await AssertStillUnpaidAsync(placed);
    }

    [Fact]
    public async Task The_amount_is_signed_as_text_so_a_reformatted_amount_does_not_verify()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();

        // Signed over "1500.50" and posted as "1500.5": the same number and a different message.
        var form = NotifyForm(placed.OrderReference, placed.GrandTotal);
        form["payhere_amount"] = form["payhere_amount"].TrimEnd('0');

        if (form["payhere_amount"] == PayHereHash.FormatAmount(placed.GrandTotal))
        {
            // The total has no trailing zero to drop (a whole number of rupees ends in ".00"): drop the decimals.
            form["payhere_amount"] = form["payhere_amount"][..form["payhere_amount"].IndexOf('.', StringComparison.Ordinal)];
        }

        using var response = await PostNotifyAsync(form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertStillUnpaidAsync(placed);
    }

    [Fact]
    public async Task Another_merchants_id_is_refused_and_stores_nothing()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();
        var events = await PayHereEventCountAsync();

        using var response = await PostNotifyAsync(
            NotifyForm(placed.OrderReference, placed.GrandTotal, merchantId: "9999999"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(events, await PayHereEventCountAsync());
        await AssertStillUnpaidAsync(placed);
    }

    [Fact]
    public async Task A_forged_request_cannot_consume_the_key_of_a_real_one()
    {
        // An attacker who guesses a real PayHere payment ID must not get the genuine delivery ignored as a
        // duplicate: the forged request stores nothing, so the genuine one is still new.
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();
        var payHereId = NewPayHerePaymentId();

        using var forged = await PostNotifyAsync(
            NotifyForm(placed.OrderReference, placed.GrandTotal, payHerePaymentId: payHereId, signature: new string('B', 32)));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

        using var genuine = await PostNotifyAsync(
            NotifyForm(placed.OrderReference, placed.GrandTotal, payHerePaymentId: payHereId));
        Assert.Equal(HttpStatusCode.OK, genuine.StatusCode);

        Assert.Equal(OrderStatus.Paid, (await ReadOrderAsync(placed.PublicId)).Status);
        Assert.Single(await EventsAsync(providerPaymentId: payHereId));
    }

    [Fact]
    public async Task After_the_secret_is_rotated_the_old_one_stops_working_with_no_restart()
    {
        await ConfigurePayHereAsync(secret: "the-first-secret");
        var placed = await PlacePayHereOrderAsync();

        await ConfigurePayHereAsync(secret: "the-second-secret");

        using var old = await PostNotifyAsync(NotifyForm(placed.OrderReference, placed.GrandTotal, secret: "the-first-secret"));
        Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);
        await AssertStillUnpaidAsync(placed);

        using var current = await PostNotifyAsync(NotifyForm(placed.OrderReference, placed.GrandTotal, secret: "the-second-secret"));
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(OrderStatus.Paid, (await ReadOrderAsync(placed.PublicId)).Status);
    }

    [Fact]
    public async Task Without_a_configured_account_the_answer_is_503_and_nothing_is_stored()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();
        var events = await PayHereEventCountAsync();

        await ConfigurePayHereAsync(secret: null);

        using var response = await PostNotifyAsync(NotifyForm(placed.OrderReference, placed.GrandTotal));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Equal(events, await PayHereEventCountAsync());
        await AssertStillUnpaidAsync(placed);
    }

    // ---- the shape of the request ------------------------------------------------------------------

    [Fact]
    public async Task A_json_body_is_415_and_a_form_missing_its_fields_is_400()
    {
        await ConfigurePayHereAsync();
        var events = await PayHereEventCountAsync();

        using var client = NewNotifyClient();

        using var json = await client.PostAsync(
            new Uri(NotifyPath, UriKind.Relative),
            new StringContent("{\"status_code\":2}", Encoding.UTF8, "application/json"),
            Token);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, json.StatusCode);

        using var empty = await client.PostAsync(
            new Uri(NotifyPath, UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string>()),
            Token);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        using var partial = await client.PostAsync(
            new Uri(NotifyPath, UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["merchant_id"] = MerchantId, ["status_code"] = "2" }),
            Token);
        Assert.Equal(HttpStatusCode.BadRequest, partial.StatusCode);

        using var get = await client.GetAsync(new Uri(NotifyPath, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);

        Assert.Equal(events, await PayHereEventCountAsync());
    }

    [Fact]
    public async Task A_field_posted_twice_is_refused_because_it_is_ambiguous_what_was_signed()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();
        var form = NotifyForm(placed.OrderReference, placed.GrandTotal);

        // A genuine, correctly signed success with a second, contradictory status_code appended.
        var body = string.Join('&', form.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"))
            + "&status_code=-2";

        using var client = NewNotifyClient();
        using var response = await client.PostAsync(
            new Uri(NotifyPath, UriKind.Relative),
            new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"),
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertStillUnpaidAsync(placed);
    }

    [Fact]
    public async Task A_body_far_over_any_real_notification_is_not_accepted()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();
        var events = await PayHereEventCountAsync();
        var form = NotifyForm(placed.OrderReference, placed.GrandTotal);
        form["padding"] = new string('x', 40_000);

        using var response = await PostNotifyAsync(form);

        // 413 where the server enforces the ceiling; the in-memory test server may let it through, in which
        // case the extra field is simply never read and the genuine notification is applied. What must not
        // happen is a 5xx.
        Assert.True(
            response.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest or HttpStatusCode.OK,
            $"Unexpected {(int)response.StatusCode}.");

        if (response.StatusCode != HttpStatusCode.OK)
        {
            Assert.Equal(events, await PayHereEventCountAsync());
        }
    }

    // ---- replay and duplicates ---------------------------------------------------------------------

    [Fact]
    public async Task A_replayed_notification_is_answered_200_each_time_and_applied_once()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync(quantity: 2);
        var before = await StockAsync(placed.VariantId);
        var seriesBefore = await InvoiceSeriesNextValueAsync();
        var payHereId = NewPayHerePaymentId();
        var form = NotifyForm(placed.OrderReference, placed.GrandTotal, payHerePaymentId: payHereId);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var response = await PostNotifyAsync(form);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Single(await EventsAsync(providerPaymentId: payHereId));

        var order = await ReadOrderAsync(placed.PublicId);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Single(order.StatusHistory, entry => entry.ToStatus == OrderStatus.Paid);
        Assert.Single(await OutboxAsync(placed.PublicId, EmailTemplateCodes.PaymentConfirmed));
        Assert.Equal((seriesBefore ?? 1) + 1, await InvoiceSeriesNextValueAsync());

        var after = await StockAsync(placed.VariantId);
        Assert.Equal(before.OnHand - 2, after.OnHand);
        Assert.Equal(before.Reserved - 2, after.Reserved);
        Assert.Empty(await AlertsAsync(placed.PublicId));
    }

    [Fact]
    public async Task Parallel_copies_of_one_notification_settle_the_order_exactly_once()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync(quantity: 2);
        var before = await StockAsync(placed.VariantId);
        var seriesBefore = await InvoiceSeriesNextValueAsync();
        var payHereId = NewPayHerePaymentId();
        var form = NotifyForm(placed.OrderReference, placed.GrandTotal, payHerePaymentId: payHereId);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(async _ =>
            {
                using var response = await PostNotifyAsync(form);
                return response.StatusCode;
            }));

        Assert.All(responses, status => Assert.Equal(HttpStatusCode.OK, status));

        Assert.Single(await EventsAsync(providerPaymentId: payHereId));

        var order = await ReadOrderAsync(placed.PublicId);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Single(order.StatusHistory, entry => entry.ToStatus == OrderStatus.Paid);
        Assert.Single(await OutboxAsync(placed.PublicId, EmailTemplateCodes.PaymentConfirmed));
        Assert.Equal((seriesBefore ?? 1) + 1, await InvoiceSeriesNextValueAsync());

        var after = await StockAsync(placed.VariantId);
        Assert.Equal(before.OnHand - 2, after.OnHand);
        Assert.Equal(before.Reserved - 2, after.Reserved);
        Assert.Empty(await AlertsAsync(placed.PublicId));
    }

    /// <summary>
    /// Two different PayHere payments for one order arriving together - the customer paid in two tabs. The
    /// claim on the order is the lock: one settles it, the other is a second charge and is put on the
    /// dashboard, and the stock and the invoice move once.
    /// </summary>
    [Fact]
    public async Task Two_different_successes_arriving_together_settle_once_and_flag_the_other()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync(quantity: 2);
        var before = await StockAsync(placed.VariantId);
        var first = NewPayHerePaymentId();
        var second = NewPayHerePaymentId();

        try
        {
            var responses = await Task.WhenAll(
                new[] { first, second }.Select(async id =>
                {
                    using var response = await PostNotifyAsync(
                        NotifyForm(placed.OrderReference, placed.GrandTotal, payHerePaymentId: id));
                    return response.StatusCode;
                }));

            Assert.All(responses, status => Assert.Equal(HttpStatusCode.OK, status));

            var order = await ReadOrderAsync(placed.PublicId);
            Assert.Equal(OrderStatus.Paid, order.Status);
            Assert.Single(order.StatusHistory, entry => entry.ToStatus == OrderStatus.Paid);
            Assert.Single(await OutboxAsync(placed.PublicId, EmailTemplateCodes.PaymentConfirmed));

            var after = await StockAsync(placed.VariantId);
            Assert.Equal(before.OnHand - 2, after.OnHand);

            // The money that did not settle the order is on the dashboard.
            var alert = Assert.Single(await AlertsAsync(placed.PublicId));
            Assert.Equal(AdminAlertTypes.PaymentException, alert.AlertType);
            Assert.Contains("\"reason\":\"order_already_paid\"", alert.DetailsJson, StringComparison.Ordinal);

            // Both events are stored and finished.
            Assert.Equal(2, (await EventsAsync(placed.PaymentId)).Count);
            Assert.All(await EventsAsync(placed.PaymentId), e => Assert.Equal(PaymentEventProcessingStatus.Processed, e.ProcessingStatus));
        }
        finally
        {
            await DeleteAlertsAsync(placed.PublicId);
        }
    }

    [Fact]
    public async Task A_second_charge_after_the_order_is_paid_is_flagged_and_never_settles_again()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();
        var firstId = NewPayHerePaymentId();
        var secondId = NewPayHerePaymentId();

        try
        {
            using var paid = await PostNotifyAsync(NotifyForm(placed.OrderReference, placed.GrandTotal, payHerePaymentId: firstId));
            Assert.Equal(HttpStatusCode.OK, paid.StatusCode);

            var seriesAfterFirst = await InvoiceSeriesNextValueAsync();

            using var again = await PostNotifyAsync(NotifyForm(placed.OrderReference, placed.GrandTotal, payHerePaymentId: secondId));
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);

            var order = await ReadOrderAsync(placed.PublicId);
            var payment = Assert.Single(order.Payments);

            // The first charge is still the settling one, under its own ID.
            Assert.Equal(PaymentStatus.Succeeded, payment.Status);
            Assert.Equal(firstId, payment.ProviderTransactionId);
            Assert.Equal(firstId, payment.ProviderReference);
            Assert.Equal(seriesAfterFirst, await InvoiceSeriesNextValueAsync());
            Assert.Single(await OutboxAsync(placed.PublicId, EmailTemplateCodes.PaymentConfirmed));

            var alert = Assert.Single(await AlertsAsync(placed.PublicId));
            Assert.Equal(AdminAlertSeverity.Warning, alert.Severity);
            Assert.DoesNotContain(secondId, alert.DetailsJson!, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteAlertsAsync(placed.PublicId);
        }
    }

    // ---- amount and currency -----------------------------------------------------------------------

    [Fact]
    public async Task A_success_for_a_different_amount_is_recorded_and_flagged_not_accepted()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync(quantity: 2);
        var before = await StockAsync(placed.VariantId);
        var payHereId = NewPayHerePaymentId();

        try
        {
            // A genuine, correctly signed notification for a smaller sum than the order's total.
            using var response = await PostNotifyAsync(
                NotifyForm(placed.OrderReference, 1.00m, payHerePaymentId: payHereId));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var order = await ReadOrderAsync(placed.PublicId);

            Assert.Equal(OrderStatus.PendingPayment, order.Status);
            Assert.Equal(OrderStockState.Reserved, order.StockState);
            Assert.Null(order.InvoiceNumber);

            var payment = Assert.Single(order.Payments);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, payment.Status);
            Assert.False(payment.IsOrderSettlement);
            Assert.Equal(PaymentFailureCodes.AmountMismatch, payment.FailureCode);
            Assert.Equal(payHereId, payment.ProviderTransactionId);

            Assert.Equal(before.OnHand, (await StockAsync(placed.VariantId)).OnHand);
            Assert.Empty(await OutboxAsync(placed.PublicId, EmailTemplateCodes.PaymentConfirmed));

            var alert = Assert.Single(await AlertsAsync(placed.PublicId));
            Assert.Equal(AdminAlertTypes.PaymentException, alert.AlertType);
            Assert.Contains("amount_mismatch", alert.DetailsJson, StringComparison.Ordinal);

            Assert.Equal(PaymentEventProcessingStatus.Processed, Assert.Single(await EventsAsync(providerPaymentId: payHereId)).ProcessingStatus);
        }
        finally
        {
            await DeleteAlertsAsync(placed.PublicId);
        }
    }

    [Fact]
    public async Task A_success_in_another_currency_is_recorded_and_flagged_not_accepted()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();

        try
        {
            using var response = await PostNotifyAsync(
                NotifyForm(placed.OrderReference, placed.GrandTotal, currency: "USD"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var order = await ReadOrderAsync(placed.PublicId);
            Assert.Equal(OrderStatus.PendingPayment, order.Status);

            var payment = Assert.Single(order.Payments);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, payment.Status);
            Assert.Equal(PaymentFailureCodes.AmountMismatch, payment.FailureCode);
            Assert.Single(await AlertsAsync(placed.PublicId));
        }
        finally
        {
            await DeleteAlertsAsync(placed.PublicId);
        }
    }

    // ---- an order that can no longer take the money ------------------------------------------------

    [Fact]
    public async Task A_success_after_the_order_expired_is_kept_as_money_to_refund_and_alerts_staff()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();
        var payHereId = NewPayHerePaymentId();

        Assert.True(await ExpireAsync(placed.OrderId, DateTime.UtcNow.AddHours(3)));

        var stockAfterExpiry = await StockAsync(placed.VariantId);

        try
        {
            using var response = await PostNotifyAsync(
                NotifyForm(placed.OrderReference, placed.GrandTotal, payHerePaymentId: payHereId));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var order = await ReadOrderAsync(placed.PublicId);
            Assert.Equal(OrderStatus.Expired, order.Status);
            Assert.Null(order.PaidAtUtc);
            Assert.Null(order.InvoiceNumber);

            var payment = Assert.Single(order.Payments);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, payment.Status);
            Assert.False(payment.IsOrderSettlement);
            Assert.Equal(payHereId, payment.ProviderTransactionId);

            Assert.Equal(stockAfterExpiry, await StockAsync(placed.VariantId));
            Assert.Empty(await OutboxAsync(placed.PublicId, EmailTemplateCodes.PaymentConfirmed));

            var alert = Assert.Single(await AlertsAsync(placed.PublicId));
            Assert.Equal(AdminAlertTypes.PaymentException, alert.AlertType);
            Assert.Contains("order_expired", alert.DetailsJson, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteAlertsAsync(placed.PublicId);
        }
    }

    [Fact]
    public async Task A_success_after_the_customer_cancelled_is_kept_as_money_to_refund()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();

        using (var cancel = await NewCheckoutVisitorWithoutCookieJar().CancelOrderAsync(placed.PublicId.ToString("D"), Token))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        }

        try
        {
            using var response = await PostNotifyAsync(NotifyForm(placed.OrderReference, placed.GrandTotal));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var order = await ReadOrderAsync(placed.PublicId);
            Assert.Equal(OrderStatus.Cancelled, order.Status);

            Assert.Equal(PaymentStatus.CapturedRequiresRefund, Assert.Single(order.Payments).Status);

            var alert = Assert.Single(await AlertsAsync(placed.PublicId));
            Assert.Contains("order_cancelled", alert.DetailsJson, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteAlertsAsync(placed.PublicId);
        }
    }

    // ---- pending, failed, cancelled and the order they arrive in ----------------------------------

    [Fact]
    public async Task Pending_holds_the_order_for_the_configured_days_and_stops_the_page_offering_another_payment()
    {
        await ConfigurePayHereAsync(pendingMaxDays: "5");
        var placed = await PlacePayHereOrderAsync();

        using var response = await PostNotifyAsync(NotifyForm(placed.OrderReference, placed.GrandTotal, status: "0"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var order = await ReadOrderAsync(placed.PublicId);

        Assert.Equal(OrderStatus.PendingPayment, order.Status);
        Assert.Equal(PaymentStatus.Pending, Assert.Single(order.Payments).Status);
        Assert.Equal(order.PlacedAtUtc.AddDays(5), order.PaymentDueAtUtc, TimeSpan.FromSeconds(2));

        // The page says it is pending and offers no way to pay again.
        var json = await OrderJsonAsync(placed.PublicId);
        var instructions = json.GetProperty("payment");

        Assert.Equal(JsonValueKind.Null, instructions.GetProperty("redirectForm").ValueKind);
        Assert.Contains("pending with PayHere", instructions.GetProperty("notes").GetString(), StringComparison.Ordinal);
        Assert.False(instructions.GetProperty("acceptsProof").GetBoolean());
    }

    [Fact]
    public async Task Pending_then_success_pays_the_order()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();
        var payHereId = NewPayHerePaymentId();

        using var pending = await PostNotifyAsync(
            NotifyForm(placed.OrderReference, placed.GrandTotal, status: "0", payHerePaymentId: payHereId));
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);

        using var success = await PostNotifyAsync(
            NotifyForm(placed.OrderReference, placed.GrandTotal, status: "2", payHerePaymentId: payHereId));
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);

        var order = await ReadOrderAsync(placed.PublicId);

        Assert.Equal(OrderStatus.Paid, order.Status);

        var payment = Assert.Single(order.Payments);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.True(payment.IsOrderSettlement);
        Assert.Equal(payHereId, payment.ProviderTransactionId);

        // The same PayHere payment, two statuses, two events.
        Assert.Equal(2, (await EventsAsync(providerPaymentId: payHereId)).Count);
    }

    [Fact]
    public async Task Pending_then_failed_cuts_the_hold_back_to_the_normal_window_and_opens_a_new_attempt()
    {
        await ConfigurePayHereAsync(pendingMaxDays: "5", expiryMinutes: "60");
        var placed = await PlacePayHereOrderAsync();
        var payHereId = NewPayHerePaymentId();

        using (var pending = await PostNotifyAsync(
            NotifyForm(placed.OrderReference, placed.GrandTotal, status: "0", payHerePaymentId: payHereId)))
        {
            Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        }

        var extended = await ReadOrderAsync(placed.PublicId);
        Assert.True(extended.PaymentDueAtUtc > DateTime.UtcNow.AddDays(4));

        var failedAt = DateTime.UtcNow;

        using (var failed = await PostNotifyAsync(
            NotifyForm(placed.OrderReference, placed.GrandTotal, status: "-2", payHerePaymentId: payHereId)))
        {
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        }

        var order = await ReadOrderAsync(placed.PublicId);

        // Cut back to now + 60 minutes, not left at five days: stock and the promo use are released at expiry.
        Assert.Equal(failedAt.AddMinutes(60), order.PaymentDueAtUtc, TimeSpan.FromSeconds(30));
        Assert.Equal(OrderStatus.PendingPayment, order.Status);
        Assert.Equal(OrderStockState.Reserved, order.StockState);

        var payments = order.Payments.OrderBy(payment => payment.Id).ToList();
        Assert.Equal(2, payments.Count);

        Assert.Equal(PaymentStatus.Failed, payments[0].Status);
        Assert.Equal("FAILED", payments[0].FailureCode);
        Assert.Equal("Payment not completed", payments[0].FailureMessage);
        Assert.Equal(payHereId, payments[0].ProviderReference);

        // The customer can try again, and the new attempt has its own order_id.
        Assert.Equal(PaymentStatus.Initiated, payments[1].Status);
        Assert.Equal(placed.GrandTotal, payments[1].Amount);

        var form = (await OrderJsonAsync(placed.PublicId)).GetProperty("payment").GetProperty("redirectForm");
        var orderIdField = form.GetProperty("fields").EnumerateArray()
            .Single(field => field.GetProperty("name").GetString() == "order_id")
            .GetProperty("value").GetString();

        Assert.Equal(PayHereOrderReference.Format(placed.OrderNumber, payments[1].Id), orderIdField);
        Assert.NotEqual(placed.OrderReference, orderIdField);
    }

    [Fact]
    public async Task A_failure_on_an_unpaid_attempt_does_not_touch_the_deadline()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();
        var dueBefore = (await ReadOrderAsync(placed.PublicId)).PaymentDueAtUtc;

        using var response = await PostNotifyAsync(NotifyForm(placed.OrderReference, placed.GrandTotal, status: "-1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var order = await ReadOrderAsync(placed.PublicId);

        Assert.Equal(dueBefore, order.PaymentDueAtUtc);

        var first = order.Payments.OrderBy(payment => payment.Id).First();
        Assert.Equal(PaymentStatus.Failed, first.Status);
        Assert.Equal("CANCELLED", first.FailureCode);
        Assert.Equal(2, order.Payments.Count);
    }

    [Fact]
    public async Task A_cancelled_attempt_followed_by_a_late_success_on_it_is_applied()
    {
        // R1: a late success on a Failed or Cancelled payment is not a duplicate. It pays the order, and the
        // attempt that was opened for the customer's next go is closed in the same transaction.
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();
        var payHereId = NewPayHerePaymentId();

        using (var cancelled = await PostNotifyAsync(NotifyForm(placed.OrderReference, placed.GrandTotal, status: "-1")))
        {
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        }

        using (var success = await PostNotifyAsync(
            NotifyForm(placed.OrderReference, placed.GrandTotal, status: "2", payHerePaymentId: payHereId)))
        {
            Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        }

        var order = await ReadOrderAsync(placed.PublicId);

        Assert.Equal(OrderStatus.Paid, order.Status);

        var payments = order.Payments.OrderBy(payment => payment.Id).ToList();

        Assert.Equal(PaymentStatus.Succeeded, payments[0].Status);
        Assert.True(payments[0].IsOrderSettlement);
        Assert.Null(payments[0].FailureCode);
        Assert.Null(payments[0].FailureMessage);

        // The replacement is no longer open: only one payment may be open, and the order is paid.
        Assert.DoesNotContain(payments, payment => payment.Status is PaymentStatus.Initiated or PaymentStatus.Pending);
        Assert.Equal(PaymentStatus.Cancelled, payments[1].Status);
    }

    [Fact]
    public async Task A_failure_after_the_order_was_paid_changes_nothing()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();
        var payHereId = NewPayHerePaymentId();

        using (var success = await PostNotifyAsync(
            NotifyForm(placed.OrderReference, placed.GrandTotal, payHerePaymentId: payHereId)))
        {
            Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        }

        // Out of order: the failure of an earlier card attempt reaches us after the success.
        using (var failed = await PostNotifyAsync(
            NotifyForm(placed.OrderReference, placed.GrandTotal, status: "-2", payHerePaymentId: NewPayHerePaymentId())))
        {
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        }

        // And a pending notice for the paid payment.
        using (var pending = await PostNotifyAsync(
            NotifyForm(placed.OrderReference, placed.GrandTotal, status: "0", payHerePaymentId: payHereId)))
        {
            Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        }

        var order = await ReadOrderAsync(placed.PublicId);

        Assert.Equal(OrderStatus.Paid, order.Status);

        var payment = Assert.Single(order.Payments);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.True(payment.IsOrderSettlement);
        Assert.Null(payment.FailureCode);

        Assert.All(await EventsAsync(placed.PaymentId), e => Assert.NotEqual(PaymentEventProcessingStatus.Failed, e.ProcessingStatus));
    }

    [Fact]
    public async Task A_failure_after_the_order_expired_changes_nothing()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();

        Assert.True(await ExpireAsync(placed.OrderId, DateTime.UtcNow.AddHours(3)));

        using var response = await PostNotifyAsync(NotifyForm(placed.OrderReference, placed.GrandTotal, status: "-2"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var order = await ReadOrderAsync(placed.PublicId);

        Assert.Equal(OrderStatus.Expired, order.Status);

        // No new attempt is opened for an order that is gone.
        Assert.Equal(PaymentStatus.Expired, Assert.Single(order.Payments).Status);
    }

    // ---- chargeback --------------------------------------------------------------------------------

    [Fact]
    public async Task A_chargeback_raises_an_alert_and_changes_nothing_else()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();
        var payHereId = NewPayHerePaymentId();

        try
        {
            using (var success = await PostNotifyAsync(
                NotifyForm(placed.OrderReference, placed.GrandTotal, payHerePaymentId: payHereId)))
            {
                Assert.Equal(HttpStatusCode.OK, success.StatusCode);
            }

            var paid = await ReadOrderAsync(placed.PublicId);

            using (var chargeback = await PostNotifyAsync(
                NotifyForm(placed.OrderReference, placed.GrandTotal, status: "-3", payHerePaymentId: payHereId)))
            {
                Assert.Equal(HttpStatusCode.OK, chargeback.StatusCode);
            }

            var order = await ReadOrderAsync(placed.PublicId);

            Assert.Equal(OrderStatus.Paid, order.Status);
            Assert.Equal(paid.StatusHistory.Count, order.StatusHistory.Count);
            Assert.Equal(PaymentStatus.Succeeded, Assert.Single(order.Payments).Status);
            Assert.Equal(paid.RefundedAmount, order.RefundedAmount);

            var alert = Assert.Single(await AlertsAsync(placed.PublicId));
            Assert.Equal(AdminAlertTypes.PaymentException, alert.AlertType);
            Assert.Equal(AdminAlertSeverity.Warning, alert.Severity);
            Assert.Contains("Chargeback", alert.Title, StringComparison.Ordinal);
            Assert.Contains("\"reason\":\"chargeback\"", alert.DetailsJson, StringComparison.Ordinal);

            var stored = (await EventsAsync(providerPaymentId: payHereId)).Single(e => e.EventType == "payment.chargeback");
            Assert.Equal(PaymentEventProcessingStatus.Processed, stored.ProcessingStatus);
        }
        finally
        {
            await DeleteAlertsAsync(placed.PublicId);
        }
    }

    // ---- a notification that names nothing of ours -------------------------------------------------

    [Fact]
    public async Task A_verified_notification_for_an_order_this_shop_never_issued_is_stored_ignored_and_answered_200()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();
        var payHereId = NewPayHerePaymentId();

        // Properly signed, so it is a verified fact about PayHere's side - and still names no payment here.
        foreach (var reference in new[] { "SD-2099-000001-999999", "totally-not-ours", "SD-2026-000123" })
        {
            using var response = await PostNotifyAsync(
                NotifyForm(reference, 100m, payHerePaymentId: payHereId + reference.Length));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var stored = await EventsAsync(providerPaymentId: null);
        var unmatched = stored.Where(e => e.ProviderEventId!.StartsWith(payHereId, StringComparison.Ordinal)).ToList();

        Assert.Equal(3, unmatched.Count);
        Assert.All(unmatched, e =>
        {
            Assert.Equal(PaymentEventProcessingStatus.Ignored, e.ProcessingStatus);
            Assert.Null(e.PaymentId);
            Assert.Null(e.OrderId);
        });

        await AssertStillUnpaidAsync(placed);
    }

    [Fact]
    public async Task One_orders_payment_ID_under_another_orders_number_pays_neither()
    {
        await ConfigurePayHereAsync();
        var first = await PlacePayHereOrderAsync();
        var second = await PlacePayHereOrderAsync();

        // A correctly signed notification whose order_id pairs the second order's number with the first
        // order's payment: it names an attempt that does not exist.
        var crossed = PayHereOrderReference.Format(second.OrderNumber, first.PaymentId);

        using var response = await PostNotifyAsync(NotifyForm(crossed, first.GrandTotal));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await AssertStillUnpaidAsync(first);
        await AssertStillUnpaidAsync(second);

        var stored = Assert.Single(await EventsAsync(), e => e.PayloadJson.Contains(crossed, StringComparison.Ordinal));
        Assert.Equal(PaymentEventProcessingStatus.Ignored, stored.ProcessingStatus);
    }

    [Fact]
    public async Task A_notification_settles_only_the_order_it_names()
    {
        await ConfigurePayHereAsync();
        var first = await PlacePayHereOrderAsync();
        var second = await PlacePayHereOrderAsync();

        using var response = await PostNotifyAsync(NotifyForm(first.OrderReference, first.GrandTotal));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(OrderStatus.Paid, (await ReadOrderAsync(first.PublicId)).Status);
        await AssertStillUnpaidAsync(second);
    }

    [Fact]
    public async Task A_bank_transfer_order_cannot_be_paid_by_a_PayHere_notification()
    {
        await ConfigurePayHereAsync();
        var (bankOrder, _) = await PlaceOrderAsync();
        var bankOrderId = await OrderIdAsync(bankOrder.PublicId);

        using var scope = CreateScope();
        var bankPaymentId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Payments
            .AsNoTracking()
            .Where(payment => payment.OrderId == bankOrderId)
            .Select(payment => payment.Id)
            .SingleAsync(Token);

        var reference = PayHereOrderReference.Format(bankOrder.OrderNumber, bankPaymentId);

        using var response = await PostNotifyAsync(NotifyForm(reference, bankOrder.GrandTotal));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var order = await ReadOrderAsync(bankOrder.PublicId);
        Assert.Equal(OrderStatus.PendingPayment, order.Status);
        Assert.Equal(PaymentStatus.Pending, Assert.Single(order.Payments).Status);
        Assert.Equal(PaymentProviderCodes.BankTransfer, Assert.Single(order.Payments).ProviderCode);
    }

    // ---- a failure to apply is not a failure to answer ---------------------------------------------

    /// <summary>
    /// A real failure, not a mocked one: PayHere's payment ID is already recorded against another order's
    /// payment, so writing it again violates the unique index on (provider, reference). The notification is
    /// stored, PayHere is told 200, the event is Failed with a retry time - and the ladder runs out in the
    /// sweep with a banner a person has to read.
    /// </summary>
    [Fact]
    public async Task A_notification_that_cannot_be_applied_is_stored_retried_and_finally_flagged_for_a_person()
    {
        await ConfigurePayHereAsync();
        var first = await PlacePayHereOrderAsync();
        var second = await PlacePayHereOrderAsync();
        var sharedPayHereId = NewPayHerePaymentId();

        try
        {
            // The first order is paid under this PayHere payment ID, which records it as the reference.
            using (var paid = await PostNotifyAsync(
                NotifyForm(first.OrderReference, first.GrandTotal, payHerePaymentId: sharedPayHereId)))
            {
                Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
            }

            // A pending notice for the second order that reuses the ID: the write cannot succeed.
            using (var response = await PostNotifyAsync(
                NotifyForm(second.OrderReference, second.GrandTotal, status: "0", payHerePaymentId: sharedPayHereId)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            var failed = Assert.Single(
                await EventsAsync(second.PaymentId),
                e => e.EventType == "payment.pending");

            Assert.Equal(PaymentEventProcessingStatus.Failed, failed.ProcessingStatus);
            Assert.Equal(1, failed.AttemptCount);
            Assert.NotNull(failed.LastError);
            Assert.NotNull(failed.NextAttemptAtUtc);
            Assert.Equal(DateTime.UtcNow.AddMinutes(1), failed.NextAttemptAtUtc!.Value, TimeSpan.FromSeconds(30));

            // The order itself was not half-changed by the failed attempt.
            var order = await ReadOrderAsync(second.PublicId);
            Assert.Equal(PaymentStatus.Initiated, Assert.Single(order.Payments).Status);
            Assert.Equal(order.PlacedAtUtc.AddMinutes(60), order.PaymentDueAtUtc, TimeSpan.FromSeconds(2));

            // Not due yet: a sweep leaves it alone.
            var early = await RunSweepAsync();
            Assert.Equal(0, early.Failed);

            // Five more failures walk the ladder; the sixth is the last.
            for (var round = 2; round <= 6; round++)
            {
                await MakeEventDueAsync(failed.Id);

                var swept = await RunSweepAsync();
                Assert.True(swept.Failed >= 1, $"Round {round}: {swept}");

                var now = Assert.Single(await EventsAsync(second.PaymentId), e => e.Id == failed.Id);
                Assert.Equal(PaymentEventProcessingStatus.Failed, now.ProcessingStatus);
                Assert.Equal(round, now.AttemptCount);
            }

            var spent = Assert.Single(await EventsAsync(second.PaymentId), e => e.Id == failed.Id);

            Assert.Null(spent.NextAttemptAtUtc);

            // Nothing will try again, and a banner says so.
            await MakeEventDueAsync(failed.Id);
            Assert.Equal(0, (await RunSweepAsync()).Claimed);

            using var db = CreateScope();
            var banner = await db.ServiceProvider.GetRequiredService<AppDbContext>().AdminAlerts
                .AsNoTracking()
                .Where(alert => alert.AlertType == AdminAlertTypes.PaymentException
                    && alert.Title.Contains($"payment event {failed.Id}"))
                .SingleAsync(Token);

            Assert.Equal(AdminAlertSeverity.Warning, banner.Severity);
            Assert.Contains("\"reason\":\"notification_not_applied\"", banner.DetailsJson, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteAlertsAsync(first.PublicId);
            await DeleteAlertsAsync(second.PublicId);
        }
    }

    // ---- the retry sweep ---------------------------------------------------------------------------

    [Fact]
    public async Task The_sweep_finishes_a_notification_whose_first_delivery_died_after_it_was_stored()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync(quantity: 2);
        var before = await StockAsync(placed.VariantId);
        var payHereId = NewPayHerePaymentId();

        // What a crash between "stored" and "applied" leaves behind: a verified event, Received, whose retry
        // time (received + 2 minutes) has passed.
        var eventId = await StoreVerifiedEventAsync(
            placed.OrderReference,
            placed.GrandTotal,
            payHereId,
            receivedAt: DateTime.UtcNow.AddMinutes(-10));

        Assert.Equal(OrderStatus.PendingPayment, (await ReadOrderAsync(placed.PublicId)).Status);

        var result = await RunSweepAsync();

        Assert.True(result.Claimed >= 1);

        var order = await ReadOrderAsync(placed.PublicId);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal(PaymentStatus.Succeeded, Assert.Single(order.Payments).Status);
        Assert.Equal(before.Reserved - 2, (await StockAsync(placed.VariantId)).Reserved);

        var stored = Assert.Single(await EventsAsync(providerPaymentId: payHereId));
        Assert.Equal(eventId, stored.Id);
        Assert.Equal(PaymentEventProcessingStatus.Processed, stored.ProcessingStatus);
        Assert.Null(stored.NextAttemptAtUtc);

        // And PayHere sending it again, late, changes nothing.
        using var redelivery = await PostNotifyAsync(
            NotifyForm(placed.OrderReference, placed.GrandTotal, payHerePaymentId: payHereId));
        Assert.Equal(HttpStatusCode.OK, redelivery.StatusCode);
        Assert.Single(await EventsAsync(providerPaymentId: payHereId));
        Assert.Single(await OutboxAsync(placed.PublicId, EmailTemplateCodes.PaymentConfirmed));
    }

    [Fact]
    public async Task The_sweep_leaves_an_event_alone_until_its_retry_time()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();
        var payHereId = NewPayHerePaymentId();

        await StoreVerifiedEventAsync(placed.OrderReference, placed.GrandTotal, payHereId, receivedAt: DateTime.UtcNow);

        await RunSweepAsync();

        await AssertStillUnpaidAsync(placed);
        Assert.Equal(PaymentEventProcessingStatus.Received, Assert.Single(await EventsAsync(providerPaymentId: payHereId)).ProcessingStatus);
    }

    [Fact]
    public async Task Two_sweeps_at_once_apply_an_event_once()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync(quantity: 2);
        var before = await StockAsync(placed.VariantId);
        var payHereId = NewPayHerePaymentId();

        await StoreVerifiedEventAsync(placed.OrderReference, placed.GrandTotal, payHereId, DateTime.UtcNow.AddMinutes(-10));

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => RunSweepAsync(), Token)));

        Assert.Equal(1, results.Sum(result => result.Claimed));

        var order = await ReadOrderAsync(placed.PublicId);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Single(order.StatusHistory, entry => entry.ToStatus == OrderStatus.Paid);
        Assert.Equal(before.Reserved - 2, (await StockAsync(placed.VariantId)).Reserved);
        Assert.Single(await OutboxAsync(placed.PublicId, EmailTemplateCodes.PaymentConfirmed));
    }

    [Fact]
    public void The_retry_sweep_is_a_hosted_service_of_the_API()
    {
        var hosted = Factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>();

        Assert.Contains(
            hosted,
            service => service.GetType().Name == "RetryPayHereEventsService");
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private async Task AssertStillUnpaidAsync(PayHereOrder placed)
    {
        var order = await ReadOrderAsync(placed.PublicId);

        Assert.Equal(OrderStatus.PendingPayment, order.Status);
        Assert.Equal(OrderStockState.Reserved, order.StockState);
        Assert.Null(order.PaidAtUtc);
        Assert.Null(order.InvoiceNumber);
        Assert.DoesNotContain(order.StatusHistory, entry => entry.ToStatus == OrderStatus.Paid);

        var payment = order.Payments.OrderBy(candidate => candidate.Id).First();
        Assert.Equal(PaymentStatus.Initiated, payment.Status);
        Assert.False(payment.IsOrderSettlement);
        Assert.Null(payment.ProviderTransactionId);

        Assert.Empty(await OutboxAsync(placed.PublicId, EmailTemplateCodes.PaymentConfirmed));
        Assert.Empty(await AlertsAsync(placed.PublicId));
    }

    private async Task<long> StoreVerifiedEventAsync(
        string orderReference,
        decimal amount,
        string payHereId,
        DateTime receivedAt)
    {
        using var scope = CreateApplicationScope();

        var notification = new PayHereNotification(
            MerchantId,
            orderReference,
            payHereId,
            PayHereHash.FormatAmount(amount),
            "LKR",
            "2",
            "Successfully received the payment",
            "VISA");

        var receipt = await scope.ServiceProvider.GetRequiredService<IPaymentEventStore>().RecordAsync(
            new PaymentEventRecord(
                PaymentProviderCodes.PayHere,
                PaymentEventSource.VerifiedWebhook,
                notification.EventType,
                payHereId,
                PaymentEventKey.ForEvent(PaymentProviderCodes.PayHere, $"{payHereId}:2"),
                notification.ToStoredJson(),
                receivedAt),
            Token);

        Assert.True(receipt.IsNew);

        return receipt.EventId;
    }

    private async Task<PayHereSweepResult> RunSweepAsync()
    {
        using var scope = CreateApplicationScope();

        return await scope.ServiceProvider.GetRequiredService<PayHereRetrySweep>().RunAsync(Token);
    }

    private async Task MakeEventDueAsync(long eventId)
    {
        using var scope = CreateScope();

        // Failed with a retry time: bring the time forward. Failed with none (the ladder is spent): this is
        // deliberately a no-op, because IX_PaymentEvents_Retry's predicate needs a time and there is none.
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().PaymentEvents
            .Where(candidate => candidate.Id == eventId && candidate.NextAttemptAtUtc != null)
            .ExecuteUpdateAsync(
                candidate => candidate.SetProperty(row => row.NextAttemptAtUtc, DateTime.UtcNow.AddMinutes(-1)),
                Token);
    }
}
