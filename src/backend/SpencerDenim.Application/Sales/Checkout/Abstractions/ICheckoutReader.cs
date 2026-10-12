using SpencerDenim.Application.Sales.Cart.Contracts;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Checkout.Abstractions;

/// <summary>
/// A district as checkout needs it: the id and name that get snapshotted on to the order, and the zone
/// that chooses the rate row.
/// </summary>
public readonly record struct CheckoutDistrict(int Id, string Name, DeliveryZone DeliveryZone);

/// <summary>
/// A delivery method with the zone rate that applies, or without one.
/// </summary>
/// <param name="Rate">
/// <see langword="null"/> when no <c>sales.DeliveryMethodZoneRates</c> row exists for this (method, zone)
/// pair, which doc 08 §3.5 defines as "unavailable" — distinct from a row that is present with
/// <c>IsAvailable = 0</c>, which means "deliberately not offered here". Both end up unselectable; only the
/// second is something an admin configured on purpose.
/// </param>
public sealed record CheckoutDeliveryMethod(
    int Id,
    string Code,
    string Name,
    string? Description,
    bool RequiresAddress,
    CheckoutZoneRate? Rate)
{
    /// <summary>
    /// Whether a shopper may choose it: the method is enabled (the reader only returns enabled ones), a
    /// rate row exists, and that row says it is available here.
    /// </summary>
    public bool IsAvailable => Rate is { IsAvailable: true };
}

/// <summary>One <c>sales.DeliveryMethodZoneRates</c> row.</summary>
public readonly record struct CheckoutZoneRate(
    bool IsAvailable,
    decimal Fee,
    decimal? FreeAboveAmount,
    byte EstimatedDaysMin,
    byte EstimatedDaysMax);

/// <summary>One active <c>sales.PickupLocations</c> row, with the zone its district currently carries.</summary>
public sealed record CheckoutPickupLocation(
    int Id,
    string Name,
    string AddressLine,
    string City,
    int DistrictId,
    string DistrictName,
    DeliveryZone DeliveryZone,
    string? Phone,
    string? OpeningHours);

/// <summary>One enabled <c>sales.PaymentProviders</c> row.</summary>
public sealed record CheckoutPaymentMethod(string Code, string DisplayName, string? CheckoutDescription);

/// <summary>
/// One bag line with everything <c>sales.OrderLines</c> snapshots, plus whether it can be bought at all.
/// <para>
/// <b>This is the hand-off the brief asks to be made explicit.</b> The bag reprices on every read (doc 08
/// §6, and <c>CartReader</c> says so at length); an order line is a record of what was agreed and must
/// never be repriced. The join between the two is this type: it is produced by <b>one</b> read, in
/// <see cref="ICheckoutReader.ReadCartAsync"/>, and the three prices on it are the figures that go
/// verbatim into the order row. Nothing downstream reads the catalogue again, and nothing reads
/// <c>CartItems.UnitPriceAtAdd</c> at all.
/// </para>
/// </summary>
/// <param name="UnitPrice">Doc 08 §6 step 3's sale price — what one unit is charged at.</param>
public sealed record CheckoutCartLine(
    int VariantId,
    int ProductId,
    int DepartmentId,
    int CategoryId,
    string ProductName,
    string ProductCode,
    string Slug,
    string Sku,
    string ColourName,
    string SizeLabel,
    string? ThumbnailStorageKey,
    string? ImageUrl,
    string? ImageAltText,
    short Quantity,
    int AvailableQuantity,
    decimal UnitOriginalPrice,
    decimal UnitListPrice,
    decimal UnitPrice,
    CartLineStatus Status);

/// <summary>
/// Reads for checkout (CHK-01..04). Separate from <c>ICartReader</c> because the shapes differ: a bag line
/// carries what a bag page draws, a checkout line carries what an order row stores.
/// </summary>
public interface ICheckoutReader
{
    /// <summary>
    /// The active districts a delivery address may name, ordered by name — a shopper looking for Matara
    /// scans an alphabetical list, not a provincial one.
    /// </summary>
    Task<IReadOnlyList<CheckoutDistrict>> ActiveDistrictsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// One district, or <see langword="null"/> when the id is unknown <b>or the district is deactivated</b>.
    /// The two are not told apart, for the same reason <c>ICartReader.FindSellableVariantAsync</c> does not
    /// tell "no such variant" from "not for sale": a public endpoint must not let a caller map the shop's
    /// reference data by probing ids.
    /// </summary>
    Task<CheckoutDistrict?> FindActiveDistrictAsync(int districtId, CancellationToken cancellationToken);

    /// <summary>
    /// Every <b>enabled</b> delivery method with the rate that applies to this zone, in
    /// <c>SortOrder</c>. A method with no rate row for the zone comes back with a null rate rather than
    /// being dropped — see <see cref="CheckoutDeliveryMethod.Rate"/>.
    /// </summary>
    Task<IReadOnlyList<CheckoutDeliveryMethod>> DeliveryMethodsForZoneAsync(
        DeliveryZone zone,
        CancellationToken cancellationToken);

    /// <summary>
    /// One <b>enabled</b> delivery method by id, with no rate attached — the zone is not known yet.
    /// <para>
    /// Placement needs this before it can validate anything else, because
    /// <c>DeliveryMethods.RequiresAddress</c> decides whether an address or a pickup location is the
    /// required half of the request, and <c>CK_Orders_ShipAddress</c> keys on the method's code. A
    /// disabled method answers <see langword="null"/>: doc 11 P6 says an option switched off after the
    /// shopper chose it is refused with "choose another", not honoured because it used to be on.
    /// </para>
    /// </summary>
    Task<CheckoutDeliveryMethod?> FindEnabledDeliveryMethodAsync(
        int deliveryMethodId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The rate for one (method, zone) pair, or <see langword="null"/> when there is no row — which doc 08
    /// §3.5 defines as unavailable.
    /// </summary>
    Task<CheckoutZoneRate?> FindZoneRateAsync(
        int deliveryMethodId,
        DeliveryZone zone,
        CancellationToken cancellationToken);

    /// <summary>The active pickup locations, in <c>SortOrder</c>. Empty until the client supplies one (D8).</summary>
    Task<IReadOnlyList<CheckoutPickupLocation>> ActivePickupLocationsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// One active pickup location by id, with the zone its district currently carries (R7).
    /// <see langword="null"/> when the id is unknown or the location has been deactivated.
    /// </summary>
    Task<CheckoutPickupLocation?> FindActivePickupLocationAsync(
        int pickupLocationId,
        CancellationToken cancellationToken);

    /// <summary>The enabled payment providers, in <c>DisplayOrder</c>.</summary>
    Task<IReadOnlyList<CheckoutPaymentMethod>> EnabledPaymentMethodsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// One <c>marketing.PromoCodes</c> row by code (ADM-13, doc 08 §6 step 7), or <see langword="null"/>
    /// when no undeleted row holds it.
    /// <para>
    /// <b>Soft-deleted rows never reach the evaluator</b>, so a removed code is "not recognised" rather
    /// than found-and-refused and <see cref="PromoCodeTerms"/> does not carry a flag the evaluator would
    /// have to remember to read. Two things exclude them: the model's global soft-delete filter
    /// (<c>ApplySoftDelete</c>, doc 08 §1.5) and the implementation's own predicate, which is what would
    /// survive an <c>IgnoreQueryFilters()</c>.
    /// </para>
    /// <para>
    /// <b>Inactive and out-of-window rows <i>are</i> returned</b>, unlike every other lookup on this
    /// interface. The reason is the opposite of the district one: a shopper has typed a string and is
    /// owed an answer about <i>it</i>, and <see cref="PromoEvaluator"/> is the single place that decides
    /// which of the five unusable states collapse into one sentence. A reader that filtered on
    /// <c>IsActive</c> would make that decision twice, in two languages.
    /// </para>
    /// </summary>
    /// <param name="normalisedCode">
    /// The code already trimmed and upper-cased by <c>PromoCodeText.Normalise</c>. The match is still
    /// case-insensitive in SQL — the collation is <c>Latin1_General_100_CI_AS_SC</c> (doc 08 §1.1) — so
    /// normalising is about what gets <i>stored</i> on the order, not about finding the row.
    /// </param>
    Task<PromoCodeTerms?> FindPromoCodeAsync(string normalisedCode, CancellationToken cancellationToken);

    /// <summary>
    /// The bag, repriced from today's catalogue, with the snapshot columns an order line needs. This is the
    /// moment the price is frozen — see <see cref="CheckoutCartLine"/>.
    /// </summary>
    Task<IReadOnlyList<CheckoutCartLine>> ReadCartAsync(int cartId, CancellationToken cancellationToken);

    /// <summary>
    /// The current available stock per variant, for reporting a failed reservation line by line (doc 08
    /// §5.4 step 3). Read <b>after</b> the rollback, so the figures are what a shopper would now see
    /// rather than what they were when the attempt began.
    /// </summary>
    Task<IReadOnlyDictionary<int, int>> AvailableStockAsync(
        IReadOnlyCollection<int> variantIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// A placed order by its public token (CHK-04). <see langword="null"/> when there is no such order,
    /// which is the only answer a wrong token ever gets.
    /// </summary>
    Task<OrderConfirmationDto?> FindOrderAsync(Guid publicId, CancellationToken cancellationToken);

    /// <summary>
    /// A placed order by the idempotency key that created it, for replaying a double submission (doc 08
    /// §5.4: "<c>UX_Orders_CheckoutIdempotencyKey</c> turns a double submit into a no-op").
    /// </summary>
    Task<OrderConfirmationDto?> FindOrderByCheckoutKeyAsync(
        Guid checkoutIdempotencyKey,
        CancellationToken cancellationToken);
}
