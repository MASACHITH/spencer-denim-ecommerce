using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Accounts.Contracts;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Sales.Cart.Contracts;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Application.Sales.Orders.Contracts;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Media;
using SpencerDenim.Domain.Reference;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Catalog;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales;

/// <summary>
/// Reads for checkout (CHK-01..04).
/// <para>
/// <b><see cref="ReadCartAsync"/> is where a price stops being a quote and becomes a record.</b> The bag
/// reprices on every read, deliberately (doc 08 §6, and <c>CartReader</c>'s remarks say why at length);
/// an order line must never be repriced, because it is the record of what was agreed. This method is the
/// join: it runs the same arithmetic as the bag, over the same catalogue columns, and its three prices are
/// the figures written into <c>sales.OrderLines</c> without being touched again. <c>UnitPriceAtAdd</c> is
/// not read at all — not here and not downstream.
/// </para>
/// <para>
/// <b>Why the pricing is not shared with <c>CartReader</c> as code.</b> Both call the same
/// <c>CatalogPricing</c> functions (<c>ListPrice</c>, <c>OriginalPrice</c>, <c>SalePrice</c>) and the same
/// <c>CatalogVisibility.VisibleProduct</c> expression; what differs is the projection, because an order
/// line keeps a product code, a department, a category and a thumbnail key that a bag line has no use for.
/// Sharing the projection would mean the bag page carrying columns it never draws. Sharing the *rules* is
/// what matters, and those live in one place already.
/// </para>
/// </summary>
internal sealed class CheckoutReader(
    AppDbContext dbContext,
    IOptions<CatalogOptions> options,
    IClock clock,
    IPaymentProviderRegistry paymentProviders,
    ICampaignOfferReader campaigns) : ICheckoutReader
{
    private readonly CatalogOptions _options = options.Value;

    public async Task<IReadOnlyList<CheckoutDistrict>> ActiveDistrictsAsync(CancellationToken cancellationToken) =>
        await dbContext.Districts
            .AsNoTracking()
            .Where(district => district.IsActive)

            // By name, not by SortOrder: a shopper looking for Matara in a list of twenty-five scans
            // alphabetically. SortOrder exists for an admin screen that groups them by province.
            .OrderBy(district => district.Name)
            .Select(district => new CheckoutDistrict(district.Id, district.Name, district.DeliveryZone))
            .ToListAsync(cancellationToken);

    public async Task<CheckoutDistrict?> FindActiveDistrictAsync(
        int districtId,
        CancellationToken cancellationToken) =>
        await dbContext.Districts
            .AsNoTracking()
            .Where(district => district.Id == districtId && district.IsActive)
            .Select(district => (CheckoutDistrict?)new CheckoutDistrict(
                district.Id,
                district.Name,
                district.DeliveryZone))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<CheckoutDeliveryMethod>> DeliveryMethodsForZoneAsync(
        DeliveryZone zone,
        CancellationToken cancellationToken)
    {
        // A left join, so a method with no rate row for this zone still comes back. Doc 08 §3.5 gives "no
        // row" the meaning "unavailable", and the page has to be able to say that rather than silently
        // showing one option fewer than the shopper saw last week.
        var rows = await dbContext.DeliveryMethods
            .AsNoTracking()

            // International delivery is the overseas method and has no zone rates (owner decision
            // 2026-10-21). Left in, it would be listed as unavailable on every Sri Lankan checkout.
            .Where(method => method.IsEnabled && method.Code != DeliveryMethod.InternationalCode)
            .OrderBy(method => method.SortOrder)
            .ThenBy(method => method.Id)
            .Select(method => new
            {
                method.Id,
                method.Code,
                method.Name,
                method.Description,
                method.RequiresAddress,
                Rate = method.ZoneRates
                    .Where(rate => rate.DeliveryZone == zone)
                    .Select(rate => new RateRow(
                        rate.IsAvailable,
                        rate.Fee,
                        rate.FreeAboveAmount,
                        rate.EstimatedDaysMin,
                        rate.EstimatedDaysMax))
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new CheckoutDeliveryMethod(
                row.Id,
                row.Code,
                row.Name,
                row.Description,
                row.RequiresAddress,
                ToRate(row.Rate)))
            .ToList();
    }

    public async Task<CheckoutDeliveryMethod?> FindEnabledDeliveryMethodAsync(
        int deliveryMethodId,
        CancellationToken cancellationToken) =>
        await dbContext.DeliveryMethods
            .AsNoTracking()
            .Where(method => method.Id == deliveryMethodId && method.IsEnabled)
            .Select(method => new CheckoutDeliveryMethod(
                method.Id,
                method.Code,
                method.Name,
                method.Description,
                method.RequiresAddress,

                // No zone is known at this point, so no rate is attached. The caller asks for the rate
                // separately once it has resolved the district (doc 08 §3.5 R7).
                null))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<CheckoutZoneRate?> FindZoneRateAsync(
        int deliveryMethodId,
        DeliveryZone zone,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.DeliveryMethodZoneRates
            .AsNoTracking()
            .Where(rate => rate.DeliveryMethodId == deliveryMethodId && rate.DeliveryZone == zone)
            .Select(rate => new RateRow(
                rate.IsAvailable,
                rate.Fee,
                rate.FreeAboveAmount,
                rate.EstimatedDaysMin,
                rate.EstimatedDaysMax))
            .FirstOrDefaultAsync(cancellationToken);

        return ToRate(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CheckoutCountry>> CountriesAsync(CancellationToken cancellationToken)
    {
        var methodEnabled = await InternationalEnabledAsync(cancellationToken);

        // Ordered on the entity, before the projection: EF cannot translate an OrderBy applied after a
        // positional-record Select (the #126 promo-code 500).
        var rows = await CountryRows(dbContext.Countries
                .AsNoTracking()
                .OrderBy(country => country.SortOrder)
                .ThenBy(country => country.Name))
            .ToListAsync(cancellationToken);

        return rows.Select(row => ToCountry(row, methodEnabled)).ToList();
    }

    /// <inheritdoc />
    public async Task<CheckoutCountry?> FindCountryAsync(string countryCode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(countryCode);

        // Case-insensitive by the database collation; the caller has already upper-cased it.
        var row = await CountryRows(dbContext.Countries
                .AsNoTracking()
                .Where(candidate => candidate.Code == countryCode))
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : ToCountry(row, await InternationalEnabledAsync(cancellationToken));
    }

    /// <inheritdoc />
    public async Task<CheckoutDeliveryMethod?> InternationalMethodAsync(CancellationToken cancellationToken) =>
        await dbContext.DeliveryMethods
            .AsNoTracking()
            .Where(method => method.Code == DeliveryMethod.InternationalCode && method.IsEnabled)
            .Select(method => new CheckoutDeliveryMethod(
                method.Id,
                method.Code,
                method.Name,
                method.Description,
                method.RequiresAddress,
                null))
            .FirstOrDefaultAsync(cancellationToken);

    private Task<bool> InternationalEnabledAsync(CancellationToken cancellationToken) =>
        dbContext.DeliveryMethods
            .AsNoTracking()
            .AnyAsync(
                method => method.Code == DeliveryMethod.InternationalCode && method.IsEnabled,
                cancellationToken);

    private static IQueryable<CountryRow> CountryRows(IQueryable<Country> countries) =>
        countries
            .Select(country => new CountryRow(
                country.Code,
                country.Name,
                country.SortOrder,
                country.PostalCodeRequired,
                country.RegionRequired,
                country.IsShippingEnabled,
                country.DeliveryFeeOverride,
                country.EstimatedDaysMinOverride,
                country.EstimatedDaysMaxOverride,
                country.ExpressFeeOverride,
                country.ExpressDaysMinOverride,
                country.ExpressDaysMaxOverride,
                country.Region != null && country.Region.IsEnabled,
                country.Region != null ? country.Region.Fee : null,
                country.Region != null ? country.Region.EstimatedDaysMin : (byte)0,
                country.Region != null ? country.Region.EstimatedDaysMax : (byte)0,
                country.Region != null ? country.Region.ExpressFee : null,
                country.Region != null ? country.Region.ExpressDaysMin : (byte)3,
                country.Region != null ? country.Region.ExpressDaysMax : (byte)7));

    private static CheckoutCountry ToCountry(CountryRow row, bool internationalEnabled)
    {
        var domestic = string.Equals(row.Code, Country.SriLankaCode, StringComparison.OrdinalIgnoreCase);

        var standardQuote = domestic
            ? new OverseasQuote(OverseasRefusal.None, 0m, 0, 0)
            : OverseasDeliveryRate.Resolve(new OverseasRateInputs(
                internationalEnabled,
                row.IsShippingEnabled,
                row.FeeOverride,
                row.DaysMinOverride,
                row.DaysMaxOverride,
                row.RegionEnabled,
                row.RegionFee,
                row.RegionDaysMin,
                row.RegionDaysMax));

        var expressQuote = domestic
            ? new OverseasQuote(OverseasRefusal.None, 0m, 0, 0)
            : OverseasDeliveryRate.ResolveExpress(new OverseasExpressInputs(
                internationalEnabled,
                row.IsShippingEnabled,
                row.ExpressFeeOverride,
                row.ExpressDaysMinOverride,
                row.ExpressDaysMaxOverride,
                row.RegionEnabled,
                row.RegionExpressFee,
                row.RegionExpressDaysMin,
                row.RegionExpressDaysMax));

        return new CheckoutCountry(row.Code, row.Name, domestic, row.PostalCodeRequired, row.RegionRequired, standardQuote, expressQuote);
    }

    private sealed record CountryRow(
        string Code,
        string Name,
        short SortOrder,
        bool PostalCodeRequired,
        bool RegionRequired,
        bool IsShippingEnabled,
        decimal? FeeOverride,
        byte? DaysMinOverride,
        byte? DaysMaxOverride,
        decimal? ExpressFeeOverride,
        byte? ExpressDaysMinOverride,
        byte? ExpressDaysMaxOverride,
        bool RegionEnabled,
        decimal? RegionFee,
        byte RegionDaysMin,
        byte RegionDaysMax,
        decimal? RegionExpressFee,
        byte RegionExpressDaysMin,
        byte RegionExpressDaysMax);

    public async Task<IReadOnlyList<CheckoutPickupLocation>> ActivePickupLocationsAsync(
        CancellationToken cancellationToken)
    {
        var rows = await (
            from location in dbContext.PickupLocations.AsNoTracking()
            join district in dbContext.Districts.AsNoTracking()
                on location.DistrictId equals district.Id
            where location.IsActive && district.IsActive
            orderby location.Name
            select new { location, district })
            .ToListAsync(cancellationToken);

        return rows.Select(row => ToPickupLocation(row.location, row.district)).ToList();
    }

    public async Task<CheckoutPickupLocation?> FindActivePickupLocationAsync(
        int pickupLocationId,
        CancellationToken cancellationToken)
    {
        var row = await (
            from location in dbContext.PickupLocations.AsNoTracking()
            join district in dbContext.Districts.AsNoTracking()
                on location.DistrictId equals district.Id
            where location.Id == pickupLocationId && location.IsActive && district.IsActive
            select new { location, district })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : ToPickupLocation(row.location, row.district);
    }

    /// <summary>
    /// The payment methods a shopper may choose: the providers the shop has switched on
    /// (<c>sales.PaymentProviders.IsEnabled</c>) <b>that can take a payment right now</b>.
    /// </summary>
    /// <remarks>
    /// <b>Two questions, asked of two different owners (W4-01).</b> Whether a provider is <i>enabled</i> is
    /// the shop's decision and lives in the table. Whether it is <i>available</i> is the provider's own fact —
    /// PayPal needs credentials and an exchange rate updated within its age limit (doc 11 P8: "PayPal is hidden
    /// at checkout when its rate is older than 7 days") — and is asked through
    /// <see cref="IPaymentProvider.IsAvailableAsync"/>, which answers true for a provider with no such
    /// conditions. This reader names no provider; it only declines to list one that says it cannot work.
    /// <para>
    /// A provider that is enabled but has <b>no code in this build</b> is still listed, as it always was, so
    /// that <c>PlaceOrderCommandHandler</c>'s own refusal ("cannot be used yet") is the one a shopper reads.
    /// Because the placement handler resolves the shopper's choice from this same list, a method that is
    /// hidden here is also refused there.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<CheckoutPaymentMethod>> EnabledPaymentMethodsAsync(
        CancellationToken cancellationToken)
    {
        var enabled = await dbContext.PaymentProviders
            .AsNoTracking()
            .Where(provider => provider.IsEnabled)
            .OrderBy(provider => provider.DisplayOrder)
            .ThenBy(provider => provider.Code)
            .Select(provider => new CheckoutPaymentMethod(
                provider.Code,
                provider.DisplayName,
                provider.CheckoutDescription))
            .ToListAsync(cancellationToken);

        var offered = new List<CheckoutPaymentMethod>(enabled.Count);

        foreach (var method in enabled)
        {
            if (paymentProviders.Find(method.Code) is { } provider
                && !await provider.IsAvailableAsync(cancellationToken))
            {
                continue;
            }

            offered.Add(method);
        }

        return offered;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>IsDeleted</c> is excluded twice over: <c>PromoCode</c> carries the global soft-delete filter
    /// (<c>ApplySoftDelete</c>, doc 08 §1.5), and the predicate is written out here as well. The second is
    /// what would survive an <c>IgnoreQueryFilters()</c> — which this very file uses in
    /// <c>ReadCartAsync</c> for a documented reason, so the next person to need one here does not
    /// silently start matching removed codes.
    /// <para>
    /// <b>Inactive and out-of-window rows <i>are</i> returned</b>, unlike the district and pickup-location
    /// lookups above. Those collapse "no such row" and "deactivated" so a caller cannot map the shop's
    /// reference data by probing ids. A promo code is not reference data a caller could enumerate — it is
    /// a string the shopper was given — and the five unusable states have to be told apart
    /// <i>somewhere</i> so that "that code is fully used" and "spend Rs. 5,000 to use that code" can be
    /// said at all. <c>PromoEvaluator</c> is that somewhere, and it collapses four of them back into one
    /// sentence. A reader that filtered on <c>IsActive</c> would make that decision twice, in two
    /// languages, and the second copy would drift.
    /// </para>
    /// <para>
    /// <b>No explicit <c>ToUpper</c>.</b> The match is case-insensitive because the database collation is
    /// <c>Latin1_General_100_CI_AS_SC</c> (doc 08 §1.1) — asserted by an integration test that applies a
    /// lower-cased code and reads the stored upper-case one back, rather than trusted — so
    /// <c>UX_PromoCodes_Code</c> is used as an index seek and a <c>ToUpper</c> in the predicate would have
    /// made it a scan.
    /// </para>
    /// </remarks>
    public async Task<PromoCodeTerms?> FindPromoCodeAsync(
        string normalisedCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalisedCode);

        var rows = await dbContext.PromoCodes
            .AsNoTracking()
            .Where(code => !code.IsDeleted && code.Code == normalisedCode)
            .Select(code => new PromoCodeTerms(
                code.Id,
                code.Code,
                code.DiscountType,
                code.DiscountValue,
                code.MaxDiscountAmount,
                code.MinSpendAmount,
                code.ExcludeSaleItems,
                code.StartsAtUtc,
                code.EndsAtUtc,
                code.TotalUsageLimit,
                code.PerCustomerLimit,
                code.UsageCount,
                code.IsActive))
            .Take(1)
            .ToListAsync(cancellationToken);

        return rows.Count == 1 ? rows[0] : null;
    }

    public async Task<IReadOnlyList<CheckoutCartLine>> ReadCartAsync(
        int cartId,
        CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters for the reason CartReader gives: Product, Category and MediaAsset carry the
        // soft-delete filter, and with it on, a line whose product was deleted would be dropped from the
        // join and vanish. At checkout that would be worse than on the bag page — an order placed for
        // everything *except* the thing that was withdrawn, with no message. IsDeleted is read as data and
        // the line comes back Unavailable, which blocks the placement (G-08).
        var rows = await dbContext.CartItems
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(item => item.CartId == cartId)

            // Oldest first, the same order the bag page lists them in, so the order lines are numbered the
            // way the shopper read their bag.
            .OrderBy(item => item.AddedAtUtc)
            .ThenBy(item => item.Id)
            .Select(item => new CheckoutLineRow(
                item.VariantId,
                item.Quantity,
                item.Variant!.Sku,
                item.Variant.IsActive,
                item.Variant.PriceOverride,
                item.Variant.CompareAtPriceOverride,
                item.Variant.ProductColour!.Name,
                item.Variant.Size!.Label,
                item.Variant.ProductId,
                item.Variant.Product!.ProductCode,
                item.Variant.Product.Name,
                item.Variant.Product.Slug,
                item.Variant.Product.BasePrice,
                item.Variant.Product.CompareAtPrice,
                item.Variant.Product.IsPublished,
                item.Variant.Product.IsDeleted,
                item.Variant.Product.DepartmentId,
                item.Variant.Product.Department!.IsActive,
                item.Variant.Product.PrimaryCategoryId,
                item.Variant.Product.PrimaryCategory!.IsActive,
                item.Variant.Product.PrimaryCategory.IsDeleted,
                item.Variant.StockLevel == null ? 0 : item.Variant.StockLevel.Available,
                item.Variant.Product.Media
                    .Where(media =>
                        media.ProductColourId == item.Variant.ProductColourId
                        && media.MediaRole == ProductMediaRole.Gallery
                        && media.MediaAsset!.Status == MediaAssetStatus.Ready
                        && !media.MediaAsset.IsDeleted)
                    .OrderBy(media => media.SortOrder)
                    .ThenBy(media => media.Id)
                    .Select(media => new MediaRef(
                        media.MediaAsset!.StorageKey,
                        media.MediaAsset.PublicId,
                        media.MediaAsset.RenditionSetVersion,
                        media.MediaAsset.Width))
                    .FirstOrDefault()
                    ?? (item.Variant.Product.CardImageAsset != null
                        && item.Variant.Product.CardImageAsset.Status == MediaAssetStatus.Ready
                        && !item.Variant.Product.CardImageAsset.IsDeleted
                            ? new MediaRef(
                                item.Variant.Product.CardImageAsset.StorageKey,
                                item.Variant.Product.CardImageAsset.PublicId,
                                item.Variant.Product.CardImageAsset.RenditionSetVersion,
                                item.Variant.Product.CardImageAsset.Width)
                            : null),
                item.Variant.Product.Media
                    .Where(media =>
                        media.ProductColourId == item.Variant.ProductColourId
                        && media.MediaRole == ProductMediaRole.Gallery
                        && media.MediaAsset!.Status == MediaAssetStatus.Ready
                        && !media.MediaAsset.IsDeleted)
                    .OrderBy(media => media.SortOrder)
                    .ThenBy(media => media.Id)
                    .Select(media => media.AltTextOverride ?? media.MediaAsset!.AltText)
                    .FirstOrDefault()
                    ?? (item.Variant.Product.CardImageAsset != null
                        && item.Variant.Product.CardImageAsset.Status == MediaAssetStatus.Ready
                        && !item.Variant.Product.CardImageAsset.IsDeleted
                            ? item.Variant.Product.CardImageAsset.AltText
                            : null)))
            .ToListAsync(cancellationToken);

        // Doc 08 §6 step 3, live — doc 08 §7.4's "checkout always prices live" in as many words. This is
        // the read whose figures become the order's stored totals, so it must never see the five-minute
        // stored summary: a campaign that ended four minutes ago would otherwise be charged.
        var offers = await campaigns.ReadAsync(
            rows.Select(row => row.ProductId).Distinct().ToList(),
            cancellationToken);

        return rows.Select(row => ToLine(row, offers, _options.MediaBaseUrl)).ToList();
    }

    public async Task<IReadOnlyDictionary<int, int>> AvailableStockAsync(
        IReadOnlyCollection<int> variantIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variantIds);

        if (variantIds.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var rows = await dbContext.StockLevels
            .AsNoTracking()
            .Where(level => variantIds.Contains(level.VariantId))
            .Select(level => new { level.VariantId, level.Available })
            .ToListAsync(cancellationToken);

        var available = rows.ToDictionary(row => row.VariantId, row => Math.Max(row.Available, 0));

        // A variant with no StockLevels row has nothing in stock, which is a different statement from
        // "we have no figure for it". The caller is reporting a shortfall to a shopper and needs an entry
        // for every line it asked about.
        foreach (var variantId in variantIds)
        {
            available.TryAdd(variantId, 0);
        }

        return available;
    }

    public async Task<OrderConfirmationDto?> FindOrderAsync(Guid publicId, CancellationToken cancellationToken) =>
        await FindOrderWhereAsync(order => order.PublicId == publicId, cancellationToken);

    public async Task<OrderConfirmationDto?> FindOrderByCheckoutKeyAsync(
        Guid checkoutIdempotencyKey,
        CancellationToken cancellationToken) =>
        await FindOrderWhereAsync(
            order => order.CheckoutIdempotencyKey == checkoutIdempotencyKey,
            cancellationToken);

    /// <summary>
    /// One projection for both ways of finding an order, so the success page and the replay of a double
    /// submission cannot answer differently about the same row.
    /// </summary>
    private async Task<OrderConfirmationDto?> FindOrderWhereAsync(
        System.Linq.Expressions.Expression<Func<Order, bool>> predicate,
        CancellationToken cancellationToken)
    {
        // One projection for both ways of finding an order, so the success page and the replay of a double
        // submission cannot answer differently about the same row.
        var row = await dbContext.Orders
            .AsNoTracking()
            .Where(predicate)
            .Select(order => new OrderRow(
                order.OrderNumber,
                order.PublicId,
                order.Status,
                order.Email,
                order.FirstName,
                order.LastName,
                order.Phone,
                order.Lines
                    .OrderBy(line => line.LineNumber)
                    .Select(line => new OrderLineRow(
                        line.LineNumber,
                        line.ProductName,
                        line.ProductCode,
                        line.Sku,
                        line.ColourName,
                        line.SizeLabel,
                        line.ThumbnailStorageKey,
                        line.Quantity,
                        line.UnitOriginalPrice,
                        line.UnitPrice,
                        line.LineSubtotal,
                        line.PromoDiscountAllocated,
                        line.LineTotal))
                    .ToList(),
                order.Delivery.DeliveryMethodCode,
                order.Delivery.DeliveryMethodName,
                order.Delivery.PickupLocationName,
                order.Delivery.EstimatedDaysMin,
                order.Delivery.EstimatedDaysMax,
                order.ShipAddress.RecipientName,
                order.ShipAddress.Phone,
                order.ShipAddress.Line1,
                order.ShipAddress.Line2,
                order.ShipAddress.City,
                order.ShipAddress.PostalCode,
                order.ShipAddress.Landmark,
                order.DeliveryDistrictName,
                order.ItemsOriginalTotal,
                order.ItemsSubtotal,
                order.MarkdownDiscountTotal,
                order.CampaignDiscountTotal,
                order.PromoDiscountAmount,
                order.DeliveryFeeAmount,
                order.DeliveryDiscountAmount,
                order.GrandTotal,
                order.Currency,
                order.PaymentProviderCode,
                order.PaymentProvider!.DisplayName,
                order.CustomerNote,
                order.PlacedAtUtc,
                order.PaymentDueAtUtc,

                // Doc 11 P10 step 2's four guards, read so the page can offer Cancel order only when it
                // will work. Every one is re-decided inside the transaction's conditional UPDATE (doc 08
                // §5.2); these are what the *screen* needs, and two of them — a capture in flight and a
                // payment that has already settled — are facts the page has no other way to see.
                order.StockState,
                order.CaptureLockedUntilUtc,
                order.Payments.Any(payment => payment.IsOrderSettlement),
                order.Payments.Any(payment =>
                    payment.ProviderCode == PaymentProviderCodes.PayPal
                    && payment.Status == PaymentStatus.Pending),

                // Money that arrived and could not settle the order, from any provider: the notice below.
                // Read from the payment *status*, which is the one provider-neutral fact (doc 08 §4.2 step 4),
                // so a provider added later is covered without this reader hearing its name.
                order.Payments.Any(payment => payment.Status == PaymentStatus.CapturedRequiresRefund),

                // W3-01: doc 11 P11 step 14 says a guest following their emailed link sees "status,
                // tracking", and this payload carried neither — the only place in the system that showed
                // a tracking number was the admin panel. Joined by the id the order kept, so a courier
                // that has since been deleted leaves the number as text the shopper can quote.
                order.CourierName,
                order.TrackingNumber,
                order.ShippedAtUtc,
                dbContext.Couriers
                    .Where(courier => courier.Id == order.CourierId)
                    .Select(courier => courier.TrackingUrlTemplate)
                    .FirstOrDefault(),

                // Owner decision 2026-10-21: where in the world it is going.
                order.ShipAddress.Region,
                order.DeliveryCountryCode,
                order.DeliveryCountryName))
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : ToConfirmation(row, _options.MediaBaseUrl, clock.UtcNow.UtcDateTime);
    }

    /// <summary>
    /// Whether doc 11 P10 step 2 would let the shopper cancel this order.
    /// </summary>
    /// <remarks>
    /// <b>The same four conditions as <c>OrderCancellationStore</c>'s claim, in the same order</b>:
    /// <c>PendingPayment</c>, stock <c>Reserved</c>, no active capture lock, no settling payment — plus
    /// doc 08 §4.1 R1's PayPal <c>PENDING</c> rule, which hands that decision to <c>Orders.Approve</c>
    /// alone and so is never the customer's.
    /// <para>
    /// <b>It is a read model and the claim is the boundary.</b> Two copies of a predicate is the shape this
    /// project warns about, and the mitigation is the one #126 prescribes rather than a shared expression:
    /// <c>CheckoutEndpointTests</c> asserts this flag against a real row for each of the states, through
    /// the real reader, beside the integration tests that assert the <i>transaction</i> refuses the same
    /// ones. A browser test could not see either.
    /// </para>
    /// </remarks>
    private static bool CanCancel(OrderRow row, DateTime nowUtc) =>
        row.Status == OrderStatus.PendingPayment
        && row.StockState == OrderStockState.Reserved
        && (row.CaptureLockedUntilUtc is null || row.CaptureLockedUntilUtc < nowUtc)
        && !row.HasSettlementPayment
        && !row.HasPayPalPendingPayment;

    private static OrderConfirmationDto ToConfirmation(
        OrderRow row,
        string? mediaBaseUrl,
        DateTime nowUtc) =>
        new(
            row.OrderNumber,
            row.PublicId,
            row.Status.ToString(),

            // The shopper's own wording, from the one function the admin screens use. Doc 11 P9's pickup
            // variant: `Shipped` reads as "Ready for collection" and `Delivered` as "Collected" for an
            // order the customer collects. The page printed `status.toLowerCase()` before this, so a
            // pickup customer read "This order is shipped."
            OrderStatusLabels.For(
                row.Status,
                string.Equals(row.DeliveryMethodCode, DeliveryMethod.PickupCode, StringComparison.Ordinal)),
            row.Email,
            row.FirstName,
            row.LastName,
            row.Phone,
            row.Lines
                .Select(line => new OrderLineDto(
                    line.LineNumber,
                    line.ProductName,
                    line.ProductCode,
                    line.Sku,
                    line.ColourName,
                    line.SizeLabel,

                    // The stored key turned into a URL, and deliberately **not** a rendition: choosing one
                    // needs the live asset's RenditionSetVersion and width, and an order must not read the
                    // catalogue again — which is the whole reason doc 08 §3.5 snapshots a storage key here.
                    // So an order keeps showing the file it was placed against, and a withdrawn photograph
                    // leaves a gap rather than quietly becoming a different picture.
                    MediaUrlBuilder.ToPublicUrl(mediaBaseUrl, line.ThumbnailStorageKey),
                    line.Quantity,
                    line.UnitOriginalPrice,
                    line.UnitPrice,
                    line.LineSubtotal,
                    line.PromoDiscountAllocated,
                    line.LineTotal))
                .ToList()
                .AsReadOnly(),
            new OrderDeliveryDto(
                row.DeliveryMethodCode,
                row.DeliveryMethodName,
                row.PickupLocationName,
                row.RecipientName,
                row.ShipPhone,
                row.Line1,
                row.Line2,
                row.City,
                row.DeliveryDistrictName,
                row.PostalCode,
                row.Landmark,
                row.EstimatedDaysMin,
                row.EstimatedDaysMax,
                row.ShipRegion,
                row.DeliveryCountryCode,
                row.DeliveryCountryName),
            row.ItemsOriginalTotal,
            row.ItemsSubtotal,

            // The same "Savings" figure the bag and the checkout summary print: markdown plus campaign.
            // Derived rather than stored, because CK_Orders_Totals already proves the two components are
            // exactly this difference.
            row.MarkdownDiscountTotal + row.CampaignDiscountTotal,
            row.PromoDiscountAmount,
            row.DeliveryFeeAmount,
            row.DeliveryDiscountAmount,
            row.GrandTotal,
            row.Currency,
            row.PaymentProviderCode,
            row.PaymentProviderName,
            row.CustomerNote,
            row.PlacedAtUtc,
            row.PaymentDueAtUtc,

            // Named arguments from here on, because the four that follow are optional and only two are
            // being set. Positional would mean restating a default — and `IsDuplicateSubmission` is a
            // fact about a *submission*, which a read of an order by its token can never be.
            CanCancel: CanCancel(row, nowUtc),

            // Doc 11 P8 step 6: "We received your payment, our team will contact you". A code and nothing
            // else - no provider, no amount, no payment id - because the order link is the whole credential.
            PaymentNotice: row.HasUnsettledPayment ? OrderPaymentNotices.Received : null,

            // W3-01: present only when all three parts of the dispatch snapshot are. Doc 08 §3.5 makes
            // them nullable and doc 11 P9's dispatch writes them together, but a null block and a block
            // of nulls are different things to a client, and the second one makes every consumer write
            // the same three-way check. A pickup order has no courier and gets no block, which is right:
            // there is nothing to follow, and `StatusLabel` already reads "Ready for collection".
            Tracking: row is { CourierName: { } courierName, TrackingNumber: { } trackingNumber, ShippedAtUtc: { } shippedAtUtc }
                ? new CustomerOrderTrackingDto(
                    courierName,
                    trackingNumber,
                    OrderTrackingLink.Build(row.TrackingUrlTemplate, trackingNumber),
                    shippedAtUtc)
                : null);

    /// <summary>
    /// Builds a <see cref="CheckoutPickupLocation"/> from a location and the district it is in (R7).
    /// <para>
    /// <b>The join is written out in both callers rather than shared</b>, and that is a constraint rather
    /// than duplication for its own sake: EF cannot translate a <c>Where</c> or an <c>OrderBy</c> over a
    /// property of an object a projection constructed, so a shared helper returning
    /// <c>IQueryable&lt;CheckoutPickupLocation&gt;</c> — or even one returning a named record wrapping the
    /// two entities — threw <i>"could not be translated"</i> as soon as either caller added its own clause.
    /// That was found as a <b>500 on every checkout read</b>, which is why it is written down here.
    /// </para>
    /// <para>
    /// Both callers join to the <b>active</b> districts, which is load-bearing rather than incidental: a
    /// store in a district the client has deactivated must not be offered, because the order would snapshot
    /// a district the shop has withdrawn and <c>FindActiveDistrictAsync</c> would refuse the same id on the
    /// delivery path. One rule, both paths.
    /// </para>
    /// <para>
    /// And neither joins through a navigation, because <c>PickupLocation</c> has none to <c>District</c>:
    /// the foreign key is configured without one so that <c>ref</c> reference data does not gain a
    /// collection of sales rows — the same rule <c>Orders.CustomerUserId</c> follows for <c>AppUser</c>.
    /// </para>
    /// The district's name and zone are read <b>live</b> here, which is right for an option being offered
    /// now. The order snapshots whichever one the shopper chose, so a later reassignment cannot change a
    /// placed order (doc 08 §3.5 R7, N4).
    /// </summary>
    private static CheckoutPickupLocation ToPickupLocation(PickupLocation location, District district) =>
        new(
            location.Id,
            location.Name,
            location.AddressLine,
            location.City,
            district.Id,
            district.Name,
            district.DeliveryZone,
            location.Phone,
            location.OpeningHours);

    private static CheckoutZoneRate? ToRate(RateRow? row) =>
        row is null
            ? null
            : new CheckoutZoneRate(
                row.IsAvailable,
                row.Fee,
                row.FreeAboveAmount,
                row.EstimatedDaysMin,
                row.EstimatedDaysMax);

    private static CheckoutCartLine ToLine(
        CheckoutLineRow row,
        CampaignOfferSet offers,
        string? mediaBaseUrl)
    {
        // The same five conditions CartReader applies, and they are the storefront's definition of "a
        // shopper may reach this": published, not deleted, active variant, active department, active and
        // undeleted primary category.
        var sellable = row is { IsPublished: true, IsProductDeleted: false, IsVariantActive: true }
            && row.IsDepartmentActive
            && row.IsCategoryActive
            && !row.IsCategoryDeleted;

        var list = CatalogPricing.ListPrice(row.BasePrice, row.PriceOverride);
        var original = CatalogPricing.OriginalPrice(list, row.CompareAtPrice, row.CompareAtPriceOverride);

        // Step 3: the lowest resulting price among the campaigns that reach this product, resolved
        // against *this variant's* list price (doc 08 §6 step 1), and null when none of them beats it.
        var campaign = offers.Best(row.ProductId, list);
        var sale = CatalogPricing.SalePrice(list, campaign?.Price);

        var available = Math.Max(row.Available, 0);

        var status = !sellable
            ? CartLineStatus.Unavailable
            : available <= 0
                ? CartLineStatus.OutOfStock
                : available < row.Quantity
                    ? CartLineStatus.NotEnoughStock
                    : CartLineStatus.Buyable;

        return new CheckoutCartLine(
            row.VariantId,
            row.ProductId,
            row.DepartmentId,
            row.CategoryId,
            row.ProductName,
            row.ProductCode,
            row.Slug,
            row.Sku,
            row.ColourName,
            row.SizeLabel,

            // The storage key, not a URL: doc 08 §3.5 stores `ThumbnailStorageKey` on the order line so a
            // withdrawn photograph leaves a gap in an old order rather than changing it, and a URL would
            // bake today's rendition scheme into a row that outlives it.
            row.Image?.StorageKey,
            MediaUrlBuilder.ToRenditionUrl(mediaBaseUrl, row.Image, MediaRenditions.Thumb),
            row.Image is null ? null : row.ImageAltText,
            row.Quantity,
            available,
            original,
            list,
            sale,

            // Which campaign priced the line, for `sales.OrderLines.SaleCampaignId`. Null when no
            // campaign won, which is also the case when one targeted the product but could not beat its
            // list price — a line that was not discounted must not record a campaign.
            campaign?.CampaignId,
            status);
    }

    /// <summary>One <c>sales.Orders</c> row as the confirmation needs it, before the image URL is built.</summary>
    private sealed record OrderRow(
        string OrderNumber,
        Guid PublicId,
        OrderStatus Status,
        string Email,
        string FirstName,
        string? LastName,
        string? Phone,
        List<OrderLineRow> Lines,
        string DeliveryMethodCode,
        string DeliveryMethodName,
        string? PickupLocationName,
        byte EstimatedDaysMin,
        byte EstimatedDaysMax,
        string? RecipientName,
        string? ShipPhone,
        string? Line1,
        string? Line2,
        string? City,
        string? PostalCode,
        string? Landmark,
        string? DeliveryDistrictName,
        decimal ItemsOriginalTotal,
        decimal ItemsSubtotal,
        decimal MarkdownDiscountTotal,
        decimal CampaignDiscountTotal,
        decimal PromoDiscountAmount,
        decimal DeliveryFeeAmount,
        decimal DeliveryDiscountAmount,
        decimal GrandTotal,
        string Currency,
        string PaymentProviderCode,
        string PaymentProviderName,
        string? CustomerNote,
        DateTime PlacedAtUtc,
        DateTime PaymentDueAtUtc,
        OrderStockState StockState,
        DateTime? CaptureLockedUntilUtc,
        bool HasSettlementPayment,
        bool HasPayPalPendingPayment,
        bool HasUnsettledPayment,

        // W3-01 (ACC-02, doc 11 P11 steps 12 and 14): the dispatch snapshot and the courier's *current*
        // template. Doc 08 §3.5 keeps the name and the number on the order and never a built URL, so
        // editing a template changes the link on older orders too.
        string? CourierName,
        string? TrackingNumber,
        DateTime? ShippedAtUtc,
        string? TrackingUrlTemplate,
        string? ShipRegion,
        string DeliveryCountryCode,
        string DeliveryCountryName);

    private sealed record OrderLineRow(
        short LineNumber,
        string ProductName,
        string ProductCode,
        string Sku,
        string ColourName,
        string SizeLabel,
        string? ThumbnailStorageKey,
        short Quantity,
        decimal UnitOriginalPrice,
        decimal UnitPrice,
        decimal LineSubtotal,
        decimal PromoDiscountAllocated,
        decimal LineTotal);

    private sealed record RateRow(
        bool IsAvailable,
        decimal Fee,
        decimal? FreeAboveAmount,
        byte EstimatedDaysMin,
        byte EstimatedDaysMax);

    /// <summary>One <c>sales.CartItems</c> row joined to everything an order line has to snapshot.</summary>
    private sealed record CheckoutLineRow(
        int VariantId,
        short Quantity,
        string Sku,
        bool IsVariantActive,
        decimal? PriceOverride,
        decimal? CompareAtPriceOverride,
        string ColourName,
        string SizeLabel,
        int ProductId,
        string ProductCode,
        string ProductName,
        string Slug,
        decimal BasePrice,
        decimal? CompareAtPrice,
        bool IsPublished,
        bool IsProductDeleted,
        int DepartmentId,
        bool IsDepartmentActive,
        int CategoryId,
        bool IsCategoryActive,
        bool IsCategoryDeleted,
        int Available,
        MediaRef? Image,
        string? ImageAltText);
}
