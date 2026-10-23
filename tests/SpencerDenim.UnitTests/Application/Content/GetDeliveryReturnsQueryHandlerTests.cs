using SpencerDenim.Application.Accounts.Contracts;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.DeliveryReturns;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Content;

namespace SpencerDenim.UnitTests.Application.Content;

/// <summary>
/// The panel's handler (issue #33): it asks checkout's reader once per zone that has a district, reads pickup
/// locations only when pickup is on offer, and sanitises the two texts on the way out.
/// </summary>
public sealed class GetDeliveryReturnsQueryHandlerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_texts_are_sanitised_on_the_way_out()
    {
        var reader = new FakeCheckoutReader();
        var texts = new FakeTexts(new ProductPageTexts(
            "<p>Fast<script>alert(1)</script></p><img src=x onerror=alert(2)>",
            "<a href=\"javascript:alert(3)\">Returns</a>"));

        var panel = await new GetDeliveryReturnsQueryHandler(reader, texts, new HtmlSanitiser())
            .HandleAsync(new GetDeliveryReturnsQuery(), Token);

        Assert.Contains("Fast", panel.DeliveryInfoHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("script", panel.DeliveryInfoHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", panel.DeliveryInfoHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript", panel.ReturnsInfoHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Returns", panel.ReturnsInfoHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unset_texts_and_markup_the_sanitiser_refuses_are_empty()
    {
        var nested = string.Concat(Enumerable.Repeat("<b>", HtmlSanitiser.MaxNestingDepth + 5)) + "x";
        var texts = new FakeTexts(new ProductPageTexts(null, nested));

        var panel = await new GetDeliveryReturnsQueryHandler(new FakeCheckoutReader(), texts, new HtmlSanitiser())
            .HandleAsync(new GetDeliveryReturnsQuery(), Token);

        Assert.Equal(string.Empty, panel.DeliveryInfoHtml);
        Assert.Equal(string.Empty, panel.ReturnsInfoHtml);
    }

    [Fact]
    public async Task Each_zone_with_a_district_is_priced_once_and_pickup_locations_are_not_read_without_pickup()
    {
        var reader = new FakeCheckoutReader();

        var panel = await new GetDeliveryReturnsQueryHandler(reader, new FakeTexts(new(null, null)), new HtmlSanitiser())
            .HandleAsync(new GetDeliveryReturnsQuery(), Token);

        Assert.Equal([DeliveryZone.Colombo, DeliveryZone.IslandWide], reader.ZonesAsked);
        Assert.Equal(0, reader.LocationReads);
        Assert.Empty(panel.PickupPoints);
        Assert.Equal([500m, 700m], panel.Methods.Single().Rates.Select(rate => rate.Fee));
    }

    [Fact]
    public async Task With_pickup_enabled_the_locations_are_read_and_offered()
    {
        var reader = new FakeCheckoutReader { PickupEnabled = true };

        var panel = await new GetDeliveryReturnsQueryHandler(reader, new FakeTexts(new(null, null)), new HtmlSanitiser())
            .HandleAsync(new GetDeliveryReturnsQuery(), Token);

        Assert.Equal(1, reader.LocationReads);
        Assert.Equal("Flagship", Assert.Single(panel.PickupPoints).Name);
    }

    private sealed class FakeTexts(ProductPageTexts texts) : IProductPageTextReader
    {
        public Task<ProductPageTexts> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(texts);
    }

    private sealed class FakeCheckoutReader : ICheckoutReader
    {
        public bool PickupEnabled { get; init; }

        public List<DeliveryZone> ZonesAsked { get; } = [];

        public int LocationReads { get; private set; }

        public Task<IReadOnlyList<CheckoutDistrict>> ActiveDistrictsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CheckoutDistrict>>(
            [
                new(4, "Kandy", DeliveryZone.IslandWide),
                new(1, "Colombo", DeliveryZone.Colombo),
                new(5, "Galle", DeliveryZone.IslandWide),
            ]);

        public Task<IReadOnlyList<CheckoutDeliveryMethod>> DeliveryMethodsForZoneAsync(DeliveryZone zone, CancellationToken cancellationToken)
        {
            ZonesAsked.Add(zone);
            var fee = zone == DeliveryZone.Colombo ? 500m : 700m;

            IReadOnlyList<CheckoutDeliveryMethod> methods = PickupEnabled
                ? [new(1, "STANDARD", "Standard", null, true, new(true, fee, null, 1, 3)), new(3, "PICKUP", "Pickup", null, false, new(true, 0m, null, 1, 2))]
                : [new(1, "STANDARD", "Standard", null, true, new(true, fee, null, 1, 3))];

            return Task.FromResult(methods);
        }

        public Task<IReadOnlyList<CheckoutPickupLocation>> ActivePickupLocationsAsync(CancellationToken cancellationToken)
        {
            LocationReads++;
            return Task.FromResult<IReadOnlyList<CheckoutPickupLocation>>(
                [new(1, "Flagship", "1 Galle Road", "Colombo", 1, "Colombo", DeliveryZone.Colombo, null, null)]);
        }

        public Task<CheckoutDistrict?> FindActiveDistrictAsync(int districtId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CheckoutDeliveryMethod?> FindEnabledDeliveryMethodAsync(int deliveryMethodId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CheckoutZoneRate?> FindZoneRateAsync(int deliveryMethodId, DeliveryZone zone, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CheckoutPickupLocation?> FindActivePickupLocationAsync(int pickupLocationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CheckoutPaymentMethod>> EnabledPaymentMethodsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PromoCodeTerms?> FindPromoCodeAsync(string normalisedCode, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CheckoutCartLine>> ReadCartAsync(int cartId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<int, int>> AvailableStockAsync(IReadOnlyCollection<int> variantIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OrderConfirmationDto?> FindOrderAsync(Guid publicId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OrderConfirmationDto?> FindOrderByCheckoutKeyAsync(Guid checkoutIdempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
