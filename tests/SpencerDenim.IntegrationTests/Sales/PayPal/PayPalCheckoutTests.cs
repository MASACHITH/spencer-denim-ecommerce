using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Application.Sales.Payments.PayPal;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales.PayPal;

/// <summary>
/// PayPal at checkout and on the order page, through the real API and a real database (PAY-02, PAY-03; doc 11
/// P6 and P8): when it is offered at all, what an order paid by PayPal looks like once placed, and what the
/// order page tells the customer to do. The PayPal behind it is the in-memory fake.
/// </summary>
public sealed class PayPalCheckoutTests(SqlServerDatabaseFixture sqlServer) : PayPalTestBase(sqlServer)
{
    // ---------------------------------------------------------------- offered only when ready

    /// <summary>
    /// Doc 11 P8's preconditions, as checkout enforces them: enabled by the shop AND credentials AND a webhook
    /// id AND a rate that is not old. Any one missing and PayPal is not on the list.
    /// </summary>
    [Fact]
    public async Task PayPal_is_offered_only_when_enabled_configured_and_the_rate_is_fresh()
    {
        // Seeded state: switched off, nothing filled in.
        Assert.DoesNotContain(PaymentProviderCodes.PayPal, await OfferedMethodsAsync());

        // Switched on but not configured: still not offered.
        await SwitchProviderAsync(true);
        Assert.DoesNotContain(PaymentProviderCodes.PayPal, await OfferedMethodsAsync());

        // Everything in place: offered, beside bank transfer.
        await ConfigurePayPalAsync();
        var offered = await OfferedMethodsAsync();
        Assert.Contains(PaymentProviderCodes.PayPal, offered);
        Assert.Contains(PaymentProviderCodes.BankTransfer, offered);

        // Configured but switched off by the shop: not offered.
        await SwitchProviderAsync(false);
        Assert.DoesNotContain(PaymentProviderCodes.PayPal, await OfferedMethodsAsync());
    }

    [Theory]
    [InlineData("client")]
    [InlineData("secret")]
    [InlineData("webhook")]
    public async Task A_missing_credential_hides_PayPal(string missing)
    {
        await ConfigurePayPalAsync();

        await SetSettingAsync(
            missing switch
            {
                "client" => SettingKeys.PayPal.ClientId,
                "secret" => SettingKeys.PayPal.ClientSecret,
                _ => SettingKeys.PayPal.WebhookId,
            },
            null);

        Assert.DoesNotContain(PaymentProviderCodes.PayPal, await OfferedMethodsAsync());
    }

    /// <summary>A secret that cannot be decrypted (a lost key, a plaintext pasted into the column) is "not configured", not an error.</summary>
    [Fact]
    public async Task A_stored_secret_that_cannot_be_read_hides_PayPal_and_does_not_throw()
    {
        await ConfigurePayPalAsync();
        await SetSettingAsync(SettingKeys.PayPal.ClientSecret, "plaintext-not-ciphertext");

        Assert.DoesNotContain(PaymentProviderCodes.PayPal, await OfferedMethodsAsync());
    }

    [Fact]
    public async Task A_rate_older_than_the_limit_hides_PayPal_and_a_rate_inside_it_does_not()
    {
        await ConfigurePayPalAsync(rateUpdatedAtUtc: Clock.Now.AddDays(-6));
        Assert.Contains(PaymentProviderCodes.PayPal, await OfferedMethodsAsync());

        // Seven days and a second: doc 11 P8, "PayPal is hidden at checkout when its rate is older than 7 days".
        await ConfigurePayPalAsync(rateUpdatedAtUtc: Clock.Now.AddDays(-7).AddSeconds(-1));
        Assert.DoesNotContain(PaymentProviderCodes.PayPal, await OfferedMethodsAsync());

        // The clock moves and the same rate ages out.
        await ConfigurePayPalAsync(rateUpdatedAtUtc: Clock.Now);
        Assert.Contains(PaymentProviderCodes.PayPal, await OfferedMethodsAsync());

        Clock.Advance(TimeSpan.FromDays(8));
        Assert.DoesNotContain(PaymentProviderCodes.PayPal, await OfferedMethodsAsync());
    }

    [Fact]
    public async Task No_rate_at_all_hides_PayPal()
    {
        await ConfigurePayPalAsync(rate: null);

        Assert.DoesNotContain(PaymentProviderCodes.PayPal, await OfferedMethodsAsync());
    }

    /// <summary>
    /// Hidden is also refused: an order for a method that is not on the list is turned away with the same
    /// answer a stale tab would get, so a customer cannot place a PayPal order the shop cannot take.
    /// </summary>
    [Fact]
    public async Task An_order_for_PayPal_is_refused_while_PayPal_is_unavailable()
    {
        await ConfigurePayPalAsync(rateUpdatedAtUtc: Clock.Now.AddDays(-30));

        var before = await OrderCountAsync();
        var (response, order, _) = await TryPlacePayPalOrderAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(order);
        Assert.Equal(before, await OrderCountAsync());

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Contains("paymentMethodCode", problem.GetProperty("errors").EnumerateObject().Select(error => error.Name));
    }

    // ---------------------------------------------------------------- the order PayPal opens

    [Fact]
    public async Task A_PayPal_order_opens_an_Initiated_payment_with_the_dollar_amount_at_a_frozen_rate()
    {
        await ConfigurePayPalAsync(rate: 301.25m, buffer: 3m);
        var placed = Clock.Now;

        var order = await PlacePayPalOrderAsync(quantity: 2);

        var payment = await PaymentAsync(order.PaymentId);
        var row = await OrderRowAsync(order.OrderId);

        Assert.Equal(PaymentProviderCodes.PayPal, payment.ProviderCode);
        Assert.Equal(PaymentStatus.Initiated, payment.Status);
        Assert.False(payment.IsOrderSettlement);

        // The LKR amount is the order's; the dollar amount is the formula's, and stored so a refund can be
        // reconciled later (doc 08 D1).
        Assert.Equal(row.GrandTotal, payment.Amount);
        Assert.Equal("LKR", payment.Currency);
        Assert.Equal("USD", payment.ProviderCurrency);
        Assert.Equal(PayPalUsdAmount.Compute(row.GrandTotal, 301.25m, 3m), payment.ProviderAmount);
        Assert.Equal(301.25m, payment.ExchangeRate);
        Assert.Equal(3m, payment.FxBufferPercent);

        // No PayPal order exists yet: it is made when the buyer presses "Pay now".
        Assert.Null(payment.ProviderReference);
        Assert.Empty(PayPal.Creates);

        // Due placed + Orders.PayPalExpiryMinutes (60), and the order holds its stock.
        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Equal(OrderStockState.Reserved, row.StockState);
        Assert.Equal(row.PlacedAtUtc.AddMinutes(60), row.PaymentDueAtUtc);
        Assert.InRange(row.PlacedAtUtc, placed.AddMinutes(-1), placed.AddMinutes(1));
        Assert.Null(row.CaptureLockedUntilUtc);
    }

    /// <summary>
    /// The PayPal window is the setting's, not a constant: a shop that sets 30 minutes gets orders due in 30.
    /// </summary>
    [Fact]
    public async Task The_payment_window_is_the_PayPalExpiryMinutes_setting()
    {
        await ConfigurePayPalAsync(expiryMinutes: 30);

        var order = await PlacePayPalOrderAsync();
        var row = await OrderRowAsync(order.OrderId);

        Assert.Equal(row.PlacedAtUtc.AddMinutes(30), row.PaymentDueAtUtc);
    }

    /// <summary>
    /// The instructions email is the bank transfer's, and a PayPal order has none to send: the customer pays on
    /// PayPal's page, so no email carrying bank details may be queued.
    /// </summary>
    [Fact]
    public async Task A_PayPal_order_queues_the_order_placed_email_and_no_bank_transfer_instructions()
    {
        await ConfigurePayPalAsync();

        var order = await PlacePayPalOrderAsync();

        Assert.Equal(1, await OutboxCountAsync(order.PublicId, EmailTemplateCodes.OrderPlaced));
        Assert.Equal(0, await OutboxCountAsync(order.PublicId, EmailTemplateCodes.BankTransferInstructions));
    }

    // ---------------------------------------------------------------- what the order page says

    private async Task<OrderConfirmationDto> ReadOrderPageAsync(PayPalOrder order)
    {
        using var response = await GetAsync($"/api/orders/{order.PublicId:D}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<OrderConfirmationDto>(CheckoutApiClient.JsonOptions, Token);
        Assert.NotNull(dto);

        return dto;
    }

    [Fact]
    public async Task The_order_page_shows_the_dollar_amount_before_the_redirect_and_a_link_to_the_start_endpoint()
    {
        await ConfigurePayPalAsync(rate: 300m, buffer: 3m);
        var order = await PlacePayPalOrderAsync();

        var page = await ReadOrderPageAsync(order);
        var how = page.Payment;

        Assert.NotNull(how);
        Assert.Equal(PaymentProviderCodes.PayPal, how.ProviderCode);
        Assert.Equal($"{Origin}/api/payments/paypal/start/{order.PublicId:D}", how.RedirectUrl);
        Assert.Null(how.RedirectForm);

        // A gateway that confirms itself takes no bank slip and has no reference to quote.
        Assert.False(how.AcceptsProof);
        Assert.Null(how.Reference);
        Assert.Null(page.Proof);

        // The dollar amount, the rate and the buffer, so nothing is a surprise at PayPal.
        var usd = PayPalUsdAmount.Format(order.UsdAmount);
        Assert.Contains(how.Fields, field => field.Label == "Amount charged by PayPal" && field.Value == $"USD {usd}");
        Assert.Contains(how.Fields, field => field.Label == "Exchange rate" && field.Value.Contains("300.00", StringComparison.Ordinal));
        Assert.Contains(how.Fields, field => field.Label == "Currency buffer" && field.Value.Contains("3%", StringComparison.Ordinal));
        Assert.Contains("PayPal cannot charge in rupees", how.Notes, StringComparison.Ordinal);
    }

    /// <summary>The panel answers for whatever stage the payment is at (doc 11 P8's customer messages).</summary>
    [Fact]
    public async Task The_order_page_says_what_is_happening_at_each_stage_and_offers_Pay_now_only_where_it_can()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();

        // Capturing: no redirect, "being processed".
        await SetPaymentStatusAsync(order.PaymentId, PaymentStatus.Capturing);
        var processing = (await ReadOrderPageAsync(order)).Payment!;
        Assert.Null(processing.RedirectUrl);
        Assert.False(processing.AcceptsProof);
        Assert.Contains("being processed", processing.Notes, StringComparison.Ordinal);

        // Pending: no redirect, "pending with PayPal", and the date the hold runs out.
        await SetPaymentStatusAsync(order.PaymentId, PaymentStatus.Pending);
        var pending = (await ReadOrderPageAsync(order)).Payment!;
        Assert.Null(pending.RedirectUrl);
        Assert.Contains("pending with PayPal", pending.Notes, StringComparison.Ordinal);

        // Failed (declined): a way to pay again, said so, at the CURRENT rate.
        await SetPaymentStatusAsync(order.PaymentId, PaymentStatus.Failed);
        await SetSettingAsync(
            SettingKeys.PayPal.LkrPerUsd,
            "320.00");
        await SetSettingAsync(
            SettingKeys.PayPal.RateUpdatedAtUtc,
            Clock.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        var declined = (await ReadOrderPageAsync(order)).Payment!;
        Assert.Equal($"{Origin}/api/payments/paypal/start/{order.PublicId:D}", declined.RedirectUrl);
        Assert.Contains("declined by PayPal", declined.Notes, StringComparison.Ordinal);

        var row = await OrderRowAsync(order.OrderId);
        Assert.Contains(
            declined.Fields,
            field => field.Label == "Amount charged by PayPal"
                && field.Value == "USD " + PayPalUsdAmount.Format(PayPalUsdAmount.Compute(row.GrandTotal, 320m, 3m)));

        // CapturedRequiresRefund: "we received your payment, our team will contact you", and no second Pay now.
        await SetPaymentStatusAsync(order.PaymentId, PaymentStatus.CapturedRequiresRefund);
        var exception = (await ReadOrderPageAsync(order)).Payment!;
        Assert.Null(exception.RedirectUrl);
        Assert.Contains("received your payment", exception.Notes, StringComparison.Ordinal);
    }

    /// <summary>
    /// A customer who is told to try again must not be offered a button that cannot work: with PayPal out of
    /// order, the panel says so instead of linking to a refusal.
    /// </summary>
    [Fact]
    public async Task A_declined_order_is_not_offered_a_retry_while_PayPal_is_unavailable()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();
        await SetPaymentStatusAsync(order.PaymentId, PaymentStatus.Failed);

        Clock.Advance(TimeSpan.FromDays(8));
        await SetOrderDueAsync(order.OrderId, Clock.Now.AddMinutes(30));

        var how = (await ReadOrderPageAsync(order)).Payment!;

        Assert.Null(how.RedirectUrl);
        Assert.Contains("not available right now", how.Notes, StringComparison.Ordinal);
    }

    /// <summary>A paid order has nothing for the customer to do, so the provider is not even asked.</summary>
    [Fact]
    public async Task A_paid_order_has_no_payment_panel()
    {
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();

        await StartAsync(order);
        PayPal.Approve(PayPal.OrderIdFor(PayPalRequestIds.Create(order.PaymentId))!);
        using var done = await GetAsync(ReturnPath(order.PublicId));
        Assert.Equal(PayPalPageHints.Paid, HintOf(done));

        var page = await ReadOrderPageAsync(order);

        Assert.Equal(nameof(OrderStatus.Paid), page.Status);
        Assert.Null(page.Payment);
    }

    private async Task SetPaymentStatusAsync(int paymentId, PaymentStatus status)
    {
        using var scope = CreateScope();

        var moved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Payments
            .Where(payment => payment.Id == paymentId)
            .ExecuteUpdateAsync(payment => payment.SetProperty(row => row.Status, status), Token);

        Assert.Equal(1, moved);
    }
}
