using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Application.Sales.Delivery;
using SpencerDenim.Domain.Reference;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Owner decision 2026-10-21: Sri Lanka plus worldwide delivery, over HTTP against the real database (#126).
/// The Sri Lankan path must be exactly what it was; an overseas country gets one option — International
/// delivery at its region's fee or its own — no district, no store pickup, and its own address rules; and a
/// country the shop does not deliver to is refused with nothing written.
/// </summary>
/// <remarks>
/// <b>Shared database.</b> Every change this class makes to a country, a region or the International method is
/// recorded first and put back in <see cref="DisposeAsync"/>, because every suite in the assembly reads the same
/// rows.
/// </remarks>
public sealed class InternationalCheckoutTests(SqlServerDatabaseFixture sqlServer) : CheckoutTestBase(sqlServer)
{
    private readonly List<Func<AppDbContext, Task>> _restore = [];

    [Fact]
    public async Task The_country_list_is_every_ISO_country_with_Sri_Lanka_first_and_overseas_fees()
    {
        var response = await NewCheckoutVisitor().Http.GetAsync(new Uri("/api/delivery/countries", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("public", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);

        var countries = await response.Content.ReadFromJsonAsync<List<DeliveryCountryDto>>(CheckoutApiClient.JsonOptions, Token);
        Assert.NotNull(countries);
        Assert.Equal(249, countries.Count);

        var first = countries[0];
        Assert.Equal(("LK", true, true), (first.Code, first.IsDomestic, first.IsAvailable));
        Assert.Null(first.Fee);

        var gb = countries.Single(country => country.Code == "GB");
        Assert.Equal(await RegionFeeAsync(DeliveryRegion.Europe), gb.Fee);
        Assert.True(gb.PostalCodeRequired);
        Assert.False(gb.RegionRequired);

        var us = countries.Single(country => country.Code == "US");
        Assert.True(us.RegionRequired);

        // Antarctica has no street addresses: seeded switched off, so listed but not available, with no fee.
        var aq = countries.Single(country => country.Code == "AQ");
        Assert.False(aq.IsAvailable);
        Assert.Null(aq.Fee);
    }

    [Fact]
    public async Task Sri_Lanka_named_explicitly_quotes_exactly_what_no_country_quotes()
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);
        var (_, _, api) = await NewBagAsync(variantId);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (_, implicitly) = await api.GetAsync(colombo, standard, Token);
        var (_, explicitly) = await api.GetAsync(colombo, standard, Token, countryCode: "lk");

        Assert.NotNull(implicitly);
        Assert.NotNull(explicitly);
        Assert.Equal("LK", implicitly.SelectedCountryCode);
        Assert.Equal("LK", explicitly.SelectedCountryCode);
        Assert.Equal(implicitly.Totals, explicitly.Totals);
        Assert.Equal(
            implicitly.DeliveryOptions.Select(option => option.Code),
            explicitly.DeliveryOptions.Select(option => option.Code));

        // International delivery is never one of Sri Lanka's options.
        Assert.DoesNotContain(implicitly.DeliveryOptions, option => option.Code == DeliveryMethod.InternationalCode);
    }

    [Fact]
    public async Task A_Sri_Lankan_order_is_stored_as_Sri_Lanka_with_its_district()
    {
        var (order, _) = await PlaceSriLankanAsync();

        var row = await OrderByNumberAsync(order.OrderNumber);
        Assert.Equal(("LK", "Sri Lanka"), (row.DeliveryCountryCode, row.DeliveryCountryName));
        Assert.Equal("Colombo", row.DeliveryDistrictName);
        Assert.Equal(DeliveryZone.Colombo, row.DeliveryZone);
        Assert.Null(row.ShipAddress.Region);
        Assert.Equal("LK", order.Delivery.CountryCode);
        Assert.Equal("Colombo", order.Delivery.DistrictName);
    }

    [Theory]
    [InlineData("GB", DeliveryRegion.Europe, "SW1A 2AA", null)]
    [InlineData("US", DeliveryRegion.NorthAmerica, "94043", "CA")]
    [InlineData("AE", DeliveryRegion.MiddleEast, null, null)]
    [InlineData("AU", DeliveryRegion.Oceania, "2000", "NSW")]
    [InlineData("JP", DeliveryRegion.Asia, "100-0001", null)]
    public async Task An_overseas_order_is_charged_its_regions_fee_and_stored_with_no_district(
        string countryCode,
        string regionCode,
        string? postalCode,
        string? region)
    {
        var fee = await RegionFeeAsync(regionCode);
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);
        var (_, _, api) = await NewBagAsync(variantId);
        var international = await DeliveryMethodIdAsync(DeliveryMethod.InternationalCode);

        var (_, session) = await api.GetAsync(null, international, Token, countryCode: countryCode);

        Assert.NotNull(session);
        Assert.Equal(countryCode, session.SelectedCountryCode);
        var option = Assert.Single(session.DeliveryOptions);
        Assert.Equal((DeliveryMethod.InternationalCode, true, fee), (option.Code, option.IsAvailable, option.Fee));
        Assert.Equal(fee, session.Totals.DeliveryFeeAmount);
        Assert.Empty(session.Blockers);

        var before = await OrderCountAsync();
        var (response, order) = await api.PlaceAsync(
            OverseasBody(session, international, countryCode, postalCode, region),
            Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(order);
        Assert.Equal(before + 1, await OrderCountAsync());

        var row = await OrderByNumberAsync(order.OrderNumber);
        Assert.Equal(countryCode, row.DeliveryCountryCode);
        Assert.Null(row.DeliveryDistrictId);
        Assert.Null(row.DeliveryDistrictName);
        Assert.Null(row.DeliveryZone);
        Assert.Equal(DeliveryMethod.InternationalCode, row.Delivery.DeliveryMethodCode);
        Assert.Equal(fee, row.DeliveryFeeAmount);
        Assert.Equal(row.ItemsSubtotal + fee, row.GrandTotal);
        Assert.Equal(region, row.ShipAddress.Region);
        Assert.Equal(postalCode, row.ShipAddress.PostalCode);

        // The order page carries the country and the region, and no district.
        Assert.Equal(countryCode, order.Delivery.CountryCode);
        Assert.Null(order.Delivery.DistrictName);
        Assert.Equal(region, order.Delivery.Region);
    }

    [Fact]
    public async Task A_country_fee_override_replaces_the_regions_fee_at_checkout_and_on_the_order()
    {
        await OverrideCountryAsync("FR", fee: 4_321m, daysMin: 2, daysMax: 4);

        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);
        var (_, _, api) = await NewBagAsync(variantId);
        var international = await DeliveryMethodIdAsync(DeliveryMethod.InternationalCode);

        var (_, session) = await api.GetAsync(null, international, Token, countryCode: "FR");

        Assert.NotNull(session);
        var option = Assert.Single(session.DeliveryOptions);
        Assert.Equal((4_321m, (byte?)2, (byte?)4), (option.Fee!.Value, option.EstimatedDaysMin, option.EstimatedDaysMax));

        var (response, order) = await api.PlaceAsync(OverseasBody(session, international, "FR", "75008", null), Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var row = await OrderByNumberAsync(order!.OrderNumber);
        Assert.Equal(4_321m, row.DeliveryFeeAmount);
        Assert.Equal((2, 4), (row.Delivery.EstimatedDaysMin, row.Delivery.EstimatedDaysMax));
    }

    [Fact]
    public async Task A_switched_off_country_is_blocked_by_name_and_refused_with_nothing_written()
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);
        var (_, cartId, api) = await NewBagAsync(variantId);
        var international = await DeliveryMethodIdAsync(DeliveryMethod.InternationalCode);

        // Quoted while open, then switched off before the press: the realistic race.
        var (_, open) = await api.GetAsync(null, international, Token, countryCode: "DE");
        await OverrideCountryAsync("DE", enabled: false);

        var (_, closed) = await api.GetAsync(null, international, Token, countryCode: "DE");
        Assert.NotNull(closed);
        Assert.Contains(CheckoutBlocker.CountryNotServed, closed.Blockers);
        Assert.Null(closed.Totals.GrandTotal);

        var before = await OrderCountAsync();
        var reserved = await ReservedAsync(variantId);
        var (response, _) = await api.PlaceAsync(OverseasBody(open!, international, "DE", "10115", null), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("We cannot deliver to Germany", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.Equal(before, await OrderCountAsync());
        Assert.Equal(reserved, await ReservedAsync(variantId));
        Assert.Equal(1, await CartItemCountAsync(cartId));
    }

    [Fact]
    public async Task A_switched_off_region_and_a_switched_off_International_method_refuse_overseas_orders()
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);
        var (_, _, api) = await NewBagAsync(variantId);
        var international = await DeliveryMethodIdAsync(DeliveryMethod.InternationalCode);

        await SetRegionEnabledAsync(DeliveryRegion.Oceania, false);
        var (_, oceania) = await api.GetAsync(null, international, Token, countryCode: "NZ");
        Assert.Contains(CheckoutBlocker.CountryNotServed, oceania!.Blockers);

        await SetInternationalEnabledAsync(false);
        var (_, asia) = await api.GetAsync(null, null, Token, countryCode: "SG");
        Assert.Contains(CheckoutBlocker.CountryNotServed, asia!.Blockers);
        Assert.Empty(asia.DeliveryOptions);

        // Sri Lanka is untouched by either switch.
        var (_, colombo) = await api.GetAsync(
            await DistrictIdAsync("Colombo"),
            await DeliveryMethodIdAsync(DeliveryMethod.StandardCode),
            Token);
        Assert.Empty(colombo!.Blockers);
    }

    [Theory]
    [InlineData("US", null, "CA", "+1 650 253 0000", "address.postalCode")]
    [InlineData("US", "94043", null, "+1 650 253 0000", "address.region")]
    [InlineData("GB", null, null, "+44 20 7946 0958", "address.postalCode")]
    [InlineData("GB", "SW1A#2AA", null, "+44 20 7946 0958", "address.postalCode")]
    [InlineData("AE", null, null, "04 123 4567", "address.phone")]
    public async Task An_overseas_address_is_held_to_its_countrys_shape(
        string countryCode,
        string? postalCode,
        string? region,
        string phone,
        string field)
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);
        var (_, _, api) = await NewBagAsync(variantId);
        var international = await DeliveryMethodIdAsync(DeliveryMethod.InternationalCode);
        var (_, session) = await api.GetAsync(null, international, Token, countryCode: countryCode);

        var body = OverseasBody(session!, international, countryCode, postalCode, region);
        ((Dictionary<string, object?>)body["address"]!)["phone"] = phone;

        var before = await OrderCountAsync();
        var (response, _) = await api.PlaceAsync(body, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await ErrorFieldsAsync(response));
        Assert.Equal(before, await OrderCountAsync());
    }

    [Fact]
    public async Task An_overseas_order_needs_no_district_but_a_Sri_Lankan_one_still_does()
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);
        var (_, _, api) = await NewBagAsync(variantId);
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);
        var (_, session) = await api.GetAsync(await DistrictIdAsync("Colombo"), standard, Token);

        var body = ValidBody(session!.CheckoutKey, standard, await DistrictIdAsync("Colombo"), QuotedTotal(session));
        body.Remove("districtId");

        var (response, _) = await api.PlaceAsync(body, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("districtId", await ErrorFieldsAsync(response));
    }

    [Theory]
    [InlineData(DeliveryMethod.StandardCode, "GB", "International delivery")]
    [InlineData(DeliveryMethod.PickupCode, "GB", "Store pickup is only available in Sri Lanka.")]
    [InlineData(DeliveryMethod.InternationalCode, "LK", "International delivery is for addresses outside Sri Lanka")]
    public async Task The_method_must_fit_the_country(string methodCode, string countryCode, string message)
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);
        var (_, _, api) = await NewBagAsync(variantId);
        var methodId = await DeliveryMethodIdAsync(methodCode);

        if (methodCode == DeliveryMethod.PickupCode)
        {
            // Pickup is seeded switched off (no store yet); the rule must hold the day it is switched on.
            await SetMethodEnabledAsync(DeliveryMethod.PickupCode, true);
        }

        var (_, session) = await api.GetAsync(null, null, Token, countryCode: countryCode);
        var body = countryCode == "LK"
            ? ValidBody(session!.CheckoutKey, methodId, await DistrictIdAsync("Colombo"), 1m)
            : OverseasBody(session!, methodId, countryCode, "SW1A 2AA", null, expectedGrandTotal: 1m);

        if (methodCode == DeliveryMethod.PickupCode)
        {
            body["pickupLocationId"] = 1;
        }

        var before = await OrderCountAsync();
        var (response, _) = await api.PlaceAsync(body, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.Equal(before, await OrderCountAsync());
    }

    [Theory]
    [InlineData("ZZ")]
    [InlineData("GBR")]
    [InlineData("1")]
    public async Task An_unknown_or_malformed_country_is_refused(string countryCode)
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);
        var (_, _, api) = await NewBagAsync(variantId);
        var international = await DeliveryMethodIdAsync(DeliveryMethod.InternationalCode);

        var (quote, session) = await api.GetAsync(null, international, Token, countryCode: countryCode);
        Assert.Equal(HttpStatusCode.OK, quote.StatusCode);
        Assert.Null(session!.SelectedCountryCode);
        Assert.Empty(session.DeliveryOptions);

        var (response, _) = await api.PlaceAsync(OverseasBody(session, international, countryCode, "1", null, 1m), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("countryCode", await ErrorFieldsAsync(response));
    }

    [Fact]
    public async Task An_overseas_quote_ignores_a_district_sent_with_it()
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);
        var (_, _, api) = await NewBagAsync(variantId);
        var international = await DeliveryMethodIdAsync(DeliveryMethod.InternationalCode);

        var (_, session) = await api.GetAsync(await DistrictIdAsync("Colombo"), international, Token, countryCode: "IN");

        Assert.Null(session!.SelectedDistrictId);
        Assert.Equal(DeliveryMethod.InternationalCode, Assert.Single(session.DeliveryOptions).Code);
    }

    // ------------------------------------------------------------------------------------------- helpers

    private async Task<(OrderConfirmationDto Order, CheckoutApiClient Api)> PlaceSriLankanAsync()
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);
        var (_, _, api) = await NewBagAsync(variantId);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);
        var (_, session) = await api.GetAsync(colombo, standard, Token);

        var (response, order) = await api.PlaceAsync(ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session)), Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (order!, api);
    }

    internal static Dictionary<string, object?> OverseasBody(
        CheckoutSessionDto session,
        int deliveryMethodId,
        string countryCode,
        string? postalCode,
        string? region,
        decimal? expectedGrandTotal = null) =>
        new(StringComparer.Ordinal)
        {
            ["checkoutKey"] = session.CheckoutKey,
            ["firstName"] = "Amelia",
            ["lastName"] = "Hart",
            ["email"] = "amelia@example.com",
            ["phone"] = "+44 20 7946 0958",
            ["countryCode"] = countryCode,
            ["deliveryMethodId"] = deliveryMethodId,
            ["paymentMethodCode"] = PaymentProviderCodes.BankTransfer,
            ["expectedGrandTotal"] = expectedGrandTotal ?? session.Totals.GrandTotal ?? 1m,
            ["address"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["recipientName"] = "Amelia Hart",
                ["phone"] = "+44 20 7946 0958",
                ["line1"] = "10 Example Street",
                ["line2"] = "Apartment 4",
                ["city"] = "Capital City",
                ["region"] = region,
                ["postalCode"] = postalCode,
            },
        };

    private static async Task<List<string>> ErrorFieldsAsync(HttpResponseMessage response)
    {
        using var problem = await CheckoutApiClient.ProblemAsync(response, Token);

        return problem.RootElement.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().Select(property => property.Name).ToList()
            : [];
    }

    private async Task<decimal> RegionFeeAsync(string regionCode)
    {
        using var scope = CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>().DeliveryRegions
            .AsNoTracking()
            .Where(region => region.Code == regionCode)
            .Select(region => region.Fee)
            .SingleAsync(Token))!.Value;
    }

    private async Task OverrideCountryAsync(
        string code,
        bool enabled = true,
        decimal? fee = null,
        byte? daysMin = null,
        byte? daysMax = null)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var original = await db.Countries.AsNoTracking().SingleAsync(country => country.Code == code, Token);

        _restore.Add(context => context.Countries
            .Where(country => country.Code == code)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(country => country.IsShippingEnabled, original.IsShippingEnabled)
                    .SetProperty(country => country.DeliveryFeeOverride, original.DeliveryFeeOverride)
                    .SetProperty(country => country.EstimatedDaysMinOverride, original.EstimatedDaysMinOverride)
                    .SetProperty(country => country.EstimatedDaysMaxOverride, original.EstimatedDaysMaxOverride),
                CancellationToken.None));

        await db.Countries
            .Where(country => country.Code == code)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(country => country.IsShippingEnabled, enabled)
                    .SetProperty(country => country.DeliveryFeeOverride, fee)
                    .SetProperty(country => country.EstimatedDaysMinOverride, daysMin)
                    .SetProperty(country => country.EstimatedDaysMaxOverride, daysMax),
                Token);
    }

    private async Task SetRegionEnabledAsync(string code, bool enabled)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var original = await db.DeliveryRegions.AsNoTracking().Where(region => region.Code == code).Select(region => region.IsEnabled).SingleAsync(Token);

        _restore.Add(context => context.DeliveryRegions
            .Where(region => region.Code == code)
            .ExecuteUpdateAsync(update => update.SetProperty(region => region.IsEnabled, original), CancellationToken.None));

        await db.DeliveryRegions
            .Where(region => region.Code == code)
            .ExecuteUpdateAsync(update => update.SetProperty(region => region.IsEnabled, enabled), Token);
    }

    private Task SetInternationalEnabledAsync(bool enabled) => SetMethodEnabledAsync(DeliveryMethod.InternationalCode, enabled);

    private async Task SetMethodEnabledAsync(string code, bool enabled)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var original = await db.DeliveryMethods.AsNoTracking().Where(method => method.Code == code).Select(method => method.IsEnabled).SingleAsync(Token);

        _restore.Add(context => context.DeliveryMethods
            .Where(method => method.Code == code)
            .ExecuteUpdateAsync(update => update.SetProperty(method => method.IsEnabled, original), CancellationToken.None));

        await db.DeliveryMethods
            .Where(method => method.Code == code)
            .ExecuteUpdateAsync(update => update.SetProperty(method => method.IsEnabled, enabled), Token);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_restore.Count > 0 && SqlServer.IsAvailable)
        {
            using var scope = SqlServer.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Newest first, so a row changed twice ends at its original value.
            for (var index = _restore.Count - 1; index >= 0; index--)
            {
                await _restore[index](db);
            }

            _restore.Clear();
        }

        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
