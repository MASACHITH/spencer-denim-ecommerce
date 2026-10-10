using System.Net;
using System.Text.Json;
using SpencerDenim.Application.Sales.Payments.PayHere;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// PayHere at checkout and on the order page (doc 11 P8b step 1; PAY-03): when it is offered, what placing an
/// order with it writes, and the signed form the order page is handed - all through the real endpoints.
/// </summary>
public sealed class PayHereCheckoutTests(SqlServerDatabaseFixture sqlServer) : PayHereTestBase(sqlServer)
{
    // ---- when PayHere is offered -------------------------------------------------------------------

    [Fact]
    public async Task A_fresh_deployment_does_not_offer_PayHere()
    {
        // Seeded disabled, with no merchant account: the state every deployment starts in.
        var offered = await OfferedMethodCodesAsync();

        Assert.Contains(PaymentProviderCodes.BankTransfer, offered);
        Assert.DoesNotContain(PaymentProviderCodes.PayHere, offered);
    }

    [Fact]
    public async Task A_configured_account_is_not_offered_while_the_provider_row_is_switched_off()
    {
        await ConfigurePayHereAsync(enabled: false);

        Assert.DoesNotContain(PaymentProviderCodes.PayHere, await OfferedMethodCodesAsync());
    }

    [Theory]
    [InlineData("sandbox", null, "a-secret")]
    [InlineData("sandbox", "1211149", null)]
    [InlineData("production", "1211149", "a-secret")]
    public async Task A_switched_on_row_without_a_usable_account_is_hidden_and_cannot_be_chosen(
        string? mode,
        string? merchantId,
        string? secret)
    {
        await ConfigurePayHereAsync(enabled: true, mode: mode, merchantId: merchantId, secret: secret);

        Assert.DoesNotContain(PaymentProviderCodes.PayHere, await OfferedMethodCodesAsync());

        // And a request that names it anyway is refused as "not available", not accepted.
        var (response, order, _) = await TryPlaceOrderWithAsync(PaymentProviderCodes.PayHere);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(order);

        using var problem = await CheckoutApiClient.ProblemAsync(response, Token);
        Assert.Contains("not available", problem.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_enabled_and_configured_account_is_offered_beside_the_bank_transfer()
    {
        await ConfigurePayHereAsync();

        var offered = await OfferedMethodCodesAsync();

        Assert.Contains(PaymentProviderCodes.PayHere, offered);
        Assert.Contains(PaymentProviderCodes.BankTransfer, offered);
    }

    [Fact]
    public async Task Clearing_the_secret_hides_PayHere_at_the_very_next_request()
    {
        await ConfigurePayHereAsync();
        Assert.Contains(PaymentProviderCodes.PayHere, await OfferedMethodCodesAsync());

        await ConfigurePayHereAsync(secret: null);

        Assert.DoesNotContain(PaymentProviderCodes.PayHere, await OfferedMethodCodesAsync());
    }

    // ---- placing an order --------------------------------------------------------------------------

    [Fact]
    public async Task Placing_an_order_opens_an_initiated_lkr_payment_due_after_the_configured_window()
    {
        await ConfigurePayHereAsync(expiryMinutes: "45");

        var placed = await PlacePayHereOrderAsync(quantity: 2);

        var order = await ReadOrderAsync(placed.PublicId);

        Assert.Equal(OrderStatus.PendingPayment, order.Status);
        Assert.Equal(PaymentProviderCodes.PayHere, order.PaymentProviderCode);
        Assert.Equal(OrderStockState.Reserved, order.StockState);
        Assert.Equal(order.PlacedAtUtc.AddMinutes(45), order.PaymentDueAtUtc, TimeSpan.FromSeconds(2));

        var payment = Assert.Single(order.Payments);

        Assert.Equal(PaymentStatus.Initiated, payment.Status);
        Assert.Equal(PaymentProviderCodes.PayHere, payment.ProviderCode);
        Assert.Equal(order.GrandTotal, payment.Amount);
        Assert.Equal("LKR", payment.Currency);
        Assert.False(payment.IsOrderSettlement);
        Assert.Null(payment.ProviderReference);
        Assert.Null(payment.ProviderTransactionId);

        // LKR: nothing converted, so none of PayPal's conversion columns is written.
        Assert.Null(payment.ProviderCurrency);
        Assert.Null(payment.ProviderAmount);
        Assert.Null(payment.ExchangeRate);
        Assert.Null(payment.FxBufferPercent);
    }

    [Fact]
    public async Task The_default_window_is_an_hour()
    {
        await ConfigurePayHereAsync();

        var placed = await PlacePayHereOrderAsync();

        var order = await ReadOrderAsync(placed.PublicId);

        Assert.Equal(order.PlacedAtUtc.AddMinutes(60), order.PaymentDueAtUtc, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_PayHere_order_queues_the_order_placed_email_and_no_bank_instructions()
    {
        await ConfigurePayHereAsync();

        var placed = await PlacePayHereOrderAsync();

        Assert.Single(await OutboxAsync(placed.PublicId, EmailTemplateCodes.OrderPlaced));
        Assert.Empty(await OutboxAsync(placed.PublicId, EmailTemplateCodes.BankTransferInstructions));
    }

    // ---- the order page ----------------------------------------------------------------------------

    [Fact]
    public async Task The_order_page_is_handed_a_signed_form_with_every_value_from_the_servers_own_order()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync(quantity: 2);

        var json = await OrderJsonAsync(placed.PublicId);
        var instructions = json.GetProperty("payment");

        Assert.Equal("PayHere", instructions.GetProperty("providerCode").GetString());
        Assert.False(instructions.GetProperty("acceptsProof").GetBoolean());
        Assert.Equal(JsonValueKind.Null, instructions.GetProperty("redirectUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, instructions.GetProperty("reference").ValueKind);
        Assert.Empty(instructions.GetProperty("fields").EnumerateArray());

        var form = instructions.GetProperty("redirectForm");

        Assert.Equal("POST", form.GetProperty("method").GetString());
        Assert.Equal("https://sandbox.payhere.lk/pay/checkout", form.GetProperty("action").GetString());

        var fields = form.GetProperty("fields").EnumerateArray()
            .Select(field => (Name: field.GetProperty("name").GetString()!, Value: field.GetProperty("value").GetString()!))
            .ToList();

        Assert.Equal(
            [
                "merchant_id", "return_url", "cancel_url", "notify_url", "order_id", "items", "currency", "amount",
                "first_name", "last_name", "email", "phone", "address", "city", "country", "hash",
            ],
            fields.Select(field => field.Name));

        var values = fields.ToDictionary(field => field.Name, field => field.Value);
        var total = PayHereHash.FormatAmount(placed.GrandTotal);

        Assert.Equal(MerchantId, values["merchant_id"]);
        Assert.Equal($"{Origin}/order/{placed.PublicId:D}?payment=return", values["return_url"]);
        Assert.Equal($"{Origin}/order/{placed.PublicId:D}?payment=cancelled", values["cancel_url"]);
        Assert.Equal($"{Origin}/api/payments/payhere/notify", values["notify_url"]);
        Assert.Equal(placed.OrderReference, values["order_id"]);
        Assert.Equal($"Order {placed.OrderNumber}", values["items"]);
        Assert.Equal("LKR", values["currency"]);
        Assert.Equal(total, values["amount"]);
        Assert.Equal("Nimali", values["first_name"]);
        Assert.Equal("Perera", values["last_name"]);
        Assert.Equal("nimali@example.com", values["email"]);
        Assert.Equal("0771234567", values["phone"]);
        Assert.Equal("14 Galle Road, Flat 3", values["address"]);
        Assert.Equal("Kollupitiya", values["city"]);
        Assert.Equal("Sri Lanka", values["country"]);

        // The signature PayHere checks, recomputed here from the merchant account.
        Assert.Equal(
            PayHereHash.CheckoutHash(MerchantId, placed.OrderReference, total, "LKR", Secret),
            values["hash"]);

        // The amount on the form is the order's total, which the shopper was shown: never anything else.
        Assert.Equal(placed.GrandTotal, json.GetProperty("grandTotal").GetDecimal());
        Assert.Equal(placed.GrandTotal, instructions.GetProperty("amountLkr").GetDecimal());
    }

    [Fact]
    public async Task Nothing_secret_is_in_the_order_page_payload()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();

        var text = (await OrderJsonAsync(placed.PublicId)).GetRawText();

        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(PayHereHash.SecretDigest(Secret), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_mode_is_read_at_the_moment_of_use_so_live_needs_no_restart()
    {
        await ConfigurePayHereAsync(mode: "sandbox");
        var placed = await PlacePayHereOrderAsync();

        Assert.Equal("https://sandbox.payhere.lk/pay/checkout", await FormActionAsync(placed));

        await ConfigurePayHereAsync(mode: "live");

        Assert.Equal("https://www.payhere.lk/pay/checkout", await FormActionAsync(placed));
    }

    [Fact]
    public async Task The_form_is_withdrawn_when_the_account_stops_being_usable_and_the_order_stays_readable()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();

        await ConfigurePayHereAsync(secret: null);

        var json = await OrderJsonAsync(placed.PublicId);

        Assert.Equal(placed.OrderNumber, json.GetProperty("orderNumber").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("payment").ValueKind);
    }

    [Fact]
    public async Task A_paid_order_carries_no_payment_block()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();

        using var response = await PostNotifyAsync(NotifyForm(placed.OrderReference, placed.GrandTotal));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await OrderJsonAsync(placed.PublicId);

        Assert.Equal("Paid", json.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("payment").ValueKind);
    }

    private async Task<string?> FormActionAsync(PayHereOrder placed) =>
        (await OrderJsonAsync(placed.PublicId))
            .GetProperty("payment")
            .GetProperty("redirectForm")
            .GetProperty("action")
            .GetString();
}

/// <summary>
/// A host that was never told the shop's public address: PayHere cannot be given a return or a notify address,
/// so it must not be offered however completely the account is filled in.
/// </summary>
public sealed class PayHereWithoutPublicAddressTests(SqlServerDatabaseFixture sqlServer) : PayHereTestBase(sqlServer)
{
    protected override string? PublicOrigin => null;

    [Fact]
    public async Task A_complete_account_is_still_hidden_without_a_public_address()
    {
        await ConfigurePayHereAsync();

        Assert.DoesNotContain(PaymentProviderCodes.PayHere, await OfferedMethodCodesAsync());

        var (response, _, _) = await TryPlaceOrderWithAsync(PaymentProviderCodes.PayHere);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
