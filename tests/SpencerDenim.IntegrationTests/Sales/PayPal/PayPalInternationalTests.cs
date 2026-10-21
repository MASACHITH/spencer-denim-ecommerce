using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Payments.PayPal;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales.PayPal;

/// <summary>
/// Owner decision 2026-10-21: PayPal is the only payment method, for Sri Lankan and overseas addresses alike, and
/// it charges the USD equivalent of the LKR total — delivery fee included — at the admin's rate. Over HTTP with
/// the fake gateway recording exactly what would be sent to PayPal: the create-order call carries the order's own
/// ship-to address in PayPal's fields with the ISO country code, and the USD amount the payment recorded.
/// </summary>
public sealed class PayPalInternationalTests(SqlServerDatabaseFixture sqlServer) : PayPalTestBase(sqlServer)
{
    [Fact]
    public async Task An_overseas_PayPal_order_sends_its_address_and_the_USD_amount_of_the_total_with_delivery()
    {
        await ConfigurePayPalAsync(rate: 300m, buffer: 3m);

        var order = await PlaceOverseasPayPalOrderAsync("US", postalCode: "94043", region: "CA");

        await StartAsync(order);

        var create = PayPal.Creates[^1];
        Assert.Equal(PayPalRequestIds.Create(order.PaymentId), create.RequestId);
        Assert.Equal("USD", create.Currency);

        // The total includes North America's delivery fee, and the USD figure is the one the payment froze.
        var row = await OrderRowAsync(order.OrderId);
        Assert.True(row.DeliveryFeeAmount > 0m);
        Assert.Equal(row.ItemsSubtotal + row.DeliveryFeeAmount, row.GrandTotal);
        Assert.Equal(order.UsdAmount, create.Amount);
        Assert.Equal(PayPalUsdAmount.Compute(row.GrandTotal, 300m, 3m), create.Amount);

        var shipping = Assert.IsType<PayPalShipping>(create.Shipping);
        Assert.Equal("Amelia Hart", shipping.FullName);
        Assert.Equal("10 Example Street", shipping.Line1);
        Assert.Equal("Apartment 4", shipping.Line2);
        Assert.Equal("Capital City", shipping.City);
        Assert.Equal("CA", shipping.Region);
        Assert.Equal("94043", shipping.PostalCode);
        Assert.Equal("US", shipping.CountryCode);
    }

    [Fact]
    public async Task A_Sri_Lankan_PayPal_order_sends_its_address_with_LK()
    {
        await ConfigurePayPalAsync();

        var order = await PlacePayPalOrderAsync();
        await StartAsync(order);

        var shipping = Assert.IsType<PayPalShipping>(PayPal.Creates[^1].Shipping);
        Assert.Equal("LK", shipping.CountryCode);
        Assert.Equal("14 Galle Road", shipping.Line1);
        Assert.Null(shipping.Region);
        Assert.Equal("00300", shipping.PostalCode);
    }

    [Fact]
    public async Task When_PayPal_refuses_the_address_the_buyer_is_still_taken_to_PayPal_once_without_it()
    {
        await ConfigurePayPalAsync();
        PayPal.RejectCreate = request => request.Shipping is null ? null : "POSTAL_CODE_REQUIRED";

        var order = await PlaceOverseasPayPalOrderAsync("GB", postalCode: "SW1A 2AA", region: null);
        await StartAsync(order);

        var creates = PayPal.Creates.Where(create => create.CustomId == PayPalRequestIds.CustomId(order.PaymentId)).ToList();
        Assert.Equal(2, creates.Count);
        Assert.NotNull(creates[0].Shipping);
        Assert.Null(creates[1].Shipping);
        Assert.Equal(PayPalRequestIds.CreateWithoutShipping(order.PaymentId), creates[1].RequestId);
        Assert.Equal(creates[0].Amount, creates[1].Amount);
    }

    [Fact]
    public async Task A_refusal_that_is_not_about_the_address_is_not_retried()
    {
        await ConfigurePayPalAsync();
        PayPal.RejectCreate = _ => "PAYEE_ACCOUNT_RESTRICTED";

        var order = await PlaceOverseasPayPalOrderAsync("GB", postalCode: "SW1A 2AA", region: null);

        using var response = await GetAsync(StartPath(order.PublicId));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(PayPalPageHints.Unavailable, HintOf(response));
        Assert.Single(PayPal.Creates, create => create.CustomId == PayPalRequestIds.CustomId(order.PaymentId));
    }

    [Fact]
    public async Task With_bank_transfer_off_and_PayPal_on_a_bank_transfer_order_is_refused_and_PayPal_is_offered()
    {
        await ConfigurePayPalAsync();
        await SetBankTransferAsync(false);

        try
        {
            // Other suites may leave providers of their own; what matters is PayPal in, bank transfer out.
            var offered = await OfferedMethodsAsync();
            Assert.Contains(PaymentProviderCodes.PayPal, offered);
            Assert.DoesNotContain(PaymentProviderCodes.BankTransfer, offered);

            var (response, _, _) = await TryPlaceAsync(PaymentProviderCodes.BankTransfer);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("paymentMethodCode", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);

            var (placed, order, _) = await TryPlaceAsync(PaymentProviderCodes.PayPal);
            Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
            Assert.Equal(PaymentProviderCodes.PayPal, order!.PaymentMethodCode);

            // No bank-transfer instructions email is queued for a PayPal order.
            Assert.Equal(0, await OutboxCountAsync(order.PublicId, "BankTransferInstructions"));
        }
        finally
        {
            await SetBankTransferAsync(true);
        }
    }

    // ------------------------------------------------------------------------------------------- helpers

    private async Task<PayPalOrder> PlaceOverseasPayPalOrderAsync(string countryCode, string? postalCode, string? region)
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);

        var client = NewPayPalClient();
        var (added, _) = await new CartApiClient(client).AddAsync(variantId, 1, Token);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        var api = new CheckoutApiClient(client);
        var international = await DeliveryMethodIdAsync(DeliveryMethod.InternationalCode);
        var (_, session) = await api.GetAsync(null, international, Token, countryCode: countryCode);
        Assert.Contains(session!.PaymentMethods, method => method.Code == PaymentProviderCodes.PayPal);

        var body = InternationalCheckoutTests.OverseasBody(session, international, countryCode, postalCode, region);
        body["paymentMethodCode"] = PaymentProviderCodes.PayPal;

        var (response, order) = await api.PlaceAsync(body, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = CreateScope();
        var payment = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Payments
            .AsNoTracking()
            .Where(candidate => candidate.Order!.PublicId == order!.PublicId)
            .Select(candidate => new { candidate.Id, candidate.OrderId, candidate.Order!.GrandTotal, candidate.ProviderAmount })
            .SingleAsync(Token);

        return new PayPalOrder(order!.PublicId, order.OrderNumber, payment.OrderId, payment.Id, variantId, payment.GrandTotal, payment.ProviderAmount ?? 0m);
    }

    private async Task<(HttpResponseMessage Response, SpencerDenim.Application.Sales.Checkout.Contracts.OrderConfirmationDto? Order, int VariantId)>
        TryPlaceAsync(string paymentMethodCode)
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);

        var client = NewPayPalClient();
        await new CartApiClient(client).AddAsync(variantId, 1, Token);

        var api = new CheckoutApiClient(client);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);
        var (_, session) = await api.GetAsync(colombo, standard, Token);

        var body = ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session));
        body["paymentMethodCode"] = paymentMethodCode;

        var (response, order) = await api.PlaceAsync(body, Token);
        return (response, order, variantId);
    }

    private async Task SetBankTransferAsync(bool enabled)
    {
        using var scope = CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().PaymentProviders
            .Where(provider => provider.Code == PaymentProviderCodes.BankTransfer)
            .ExecuteUpdateAsync(update => update.SetProperty(provider => provider.IsEnabled, enabled), CancellationToken.None);
    }
}
