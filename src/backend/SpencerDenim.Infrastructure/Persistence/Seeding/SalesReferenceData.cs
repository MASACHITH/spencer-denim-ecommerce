using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>Doc 08 §9's nine provinces, with the ids it fixes (1–9).</summary>
/// <param name="Id">The seeded primary key. Doc 08 §3.1: "seeded 1–9, no identity".</param>
public sealed record ProvinceSeed(int Id, string Code, string Name);

/// <summary>Doc 08 §9's twenty-five districts, with the ids it fixes (1–25).</summary>
/// <param name="DeliveryZone">
/// The <b>initial</b> zone only: §9 says "client to confirm (D8)" and §3.1 makes the column
/// admin-editable. The seeder therefore inserts it and never updates it.
/// </param>
public sealed record DistrictSeed(
    int Id,
    int ProvinceId,
    string Code,
    string Name,
    DeliveryZone DeliveryZone,
    short SortOrder);

/// <summary>Doc 08 §9's three delivery methods.</summary>
public sealed record DeliveryMethodSeed(
    string Code,
    string Name,
    string Description,
    bool IsEnabled,
    bool RequiresAddress,
    short SortOrder);

/// <summary>
/// One row of doc 08 §9's nine zone rates. <b>Every figure here is a placeholder the client has still to
/// confirm (D8).</b>
/// </summary>
public sealed record DeliveryMethodZoneRateSeed(
    string DeliveryMethodCode,
    DeliveryZone DeliveryZone,
    bool IsAvailable,
    decimal Fee,
    decimal? FreeAboveAmount,
    byte EstimatedDaysMin,
    byte EstimatedDaysMax);

/// <summary>Doc 08 §9's two payment providers.</summary>
/// <summary>One courier the seeder offers (issue #33).</summary>
/// <param name="Name">As the courier writes it; unique in <c>sales.Couriers</c>.</param>
/// <param name="Phone">Customer line, printable ASCII, at most 20 characters, or null when not verified.</param>
/// <param name="TrackingUrlTemplate">An https URL containing <c>{trackingNumber}</c>, or null for form-only tracking.</param>
/// <param name="IsActive">Whether staff can pick it on dispatch from the start.</param>
/// <param name="SortOrder">Where it sits in the picker.</param>
/// <param name="IntroducedIn">The <see cref="SalesReferenceData.CourierSeedVersion"/> that first offered it.</param>
public sealed record CourierSeed(
    string Name,
    string? Phone,
    string? TrackingUrlTemplate,
    bool IsActive,
    short SortOrder,
    int IntroducedIn = 1);

public sealed record PaymentProviderSeed(
    string Code,
    string DisplayName,
    bool IsEnabled,
    short DisplayOrder,
    string CheckoutDescription);

/// <summary>
/// The seed rows doc 08 §9 specifies for the reference data checkout reads: provinces, districts,
/// delivery methods, zone rates and payment providers.
/// <para>
/// <b>Every value in this file is copied from doc 08 §9 rather than chosen here.</b> That is the whole
/// point of the file existing: a delivery fee that a developer invented would be a price the shop has not
/// agreed to, and the one honest alternative to "calculated at checkout" is a figure that comes from the
/// design document the client's proposal was costed against. §9 marks the nine rate rows <b>placeholders,
/// client to confirm (D8)</b>, and so does every comment below. Changing them is an admin action once
/// ADM-22 has a screen; until then it is a SQL update, and that limitation is in the task report rather
/// than hidden here.
/// </para>
/// </summary>
internal static class SalesReferenceData
{
    /// <summary>Doc 08 §9: nine provinces, LK-1 … LK-9.</summary>
    public static readonly IReadOnlyList<ProvinceSeed> Provinces =
    [
        new(1, "LK-1", "Western"),
        new(2, "LK-2", "Central"),
        new(3, "LK-3", "Southern"),
        new(4, "LK-4", "Northern"),
        new(5, "LK-5", "Eastern"),
        new(6, "LK-6", "North Western"),
        new(7, "LK-7", "North Central"),
        new(8, "LK-8", "Uva"),
        new(9, "LK-9", "Sabaragamuwa"),
    ];

    /// <summary>
    /// Doc 08 §9: twenty-five districts. <b>Initial</b> delivery zones, exactly as §9 assigns them —
    /// Colombo is its own zone, Gampaha and Kalutara are Western, and everything else is the most
    /// conservative IslandWide. The client has not confirmed the map (D8), so a reassignment is expected
    /// and the seeder never overwrites one.
    /// <para>
    /// <see cref="DistrictSeed.SortOrder"/> follows the id, which follows §9's province-by-province
    /// listing. It is a presentation field; the checkout page sorts the picker by name instead, because a
    /// shopper looking for Matara scans an alphabetical list and not a provincial one.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<DistrictSeed> Districts =
    [
        // Western
        new(1, 1, "LK-11", "Colombo", DeliveryZone.Colombo, 1),
        new(2, 1, "LK-12", "Gampaha", DeliveryZone.Western, 2),
        new(3, 1, "LK-13", "Kalutara", DeliveryZone.Western, 3),

        // Central
        new(4, 2, "LK-21", "Kandy", DeliveryZone.IslandWide, 4),
        new(5, 2, "LK-22", "Matale", DeliveryZone.IslandWide, 5),
        new(6, 2, "LK-23", "Nuwara Eliya", DeliveryZone.IslandWide, 6),

        // Southern
        new(7, 3, "LK-31", "Galle", DeliveryZone.IslandWide, 7),
        new(8, 3, "LK-32", "Matara", DeliveryZone.IslandWide, 8),
        new(9, 3, "LK-33", "Hambantota", DeliveryZone.IslandWide, 9),

        // Northern
        new(10, 4, "LK-41", "Jaffna", DeliveryZone.IslandWide, 10),
        new(11, 4, "LK-42", "Kilinochchi", DeliveryZone.IslandWide, 11),
        new(12, 4, "LK-43", "Mannar", DeliveryZone.IslandWide, 12),
        new(13, 4, "LK-44", "Vavuniya", DeliveryZone.IslandWide, 13),
        new(14, 4, "LK-45", "Mullaitivu", DeliveryZone.IslandWide, 14),

        // Eastern
        new(15, 5, "LK-51", "Batticaloa", DeliveryZone.IslandWide, 15),
        new(16, 5, "LK-52", "Ampara", DeliveryZone.IslandWide, 16),
        new(17, 5, "LK-53", "Trincomalee", DeliveryZone.IslandWide, 17),

        // North Western
        new(18, 6, "LK-61", "Kurunegala", DeliveryZone.IslandWide, 18),
        new(19, 6, "LK-62", "Puttalam", DeliveryZone.IslandWide, 19),

        // North Central
        new(20, 7, "LK-71", "Anuradhapura", DeliveryZone.IslandWide, 20),
        new(21, 7, "LK-72", "Polonnaruwa", DeliveryZone.IslandWide, 21),

        // Uva
        new(22, 8, "LK-81", "Badulla", DeliveryZone.IslandWide, 22),
        new(23, 8, "LK-82", "Monaragala", DeliveryZone.IslandWide, 23),

        // Sabaragamuwa
        new(24, 9, "LK-91", "Ratnapura", DeliveryZone.IslandWide, 24),
        new(25, 9, "LK-92", "Kegalle", DeliveryZone.IslandWide, 25),
    ];

    /// <summary>
    /// Doc 08 §9: three methods, with "PICKUP disabled until a PickupLocation exists".
    /// <para>
    /// <b>PICKUP ships disabled and stays disabled</b>, because §9 also seeds <b>zero</b> pickup
    /// locations ("client input, D8") and doc 11 P6's preconditions require at least one active location.
    /// Seeding an invented shop address would be the same mistake as inventing a fee: it would put a
    /// street a shopper could drive to on a live checkout page. Checkout therefore offers Standard and
    /// Express today; CHK-02's third option switches itself on the moment the client's address is in the
    /// database and the method is enabled.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<DeliveryMethodSeed> DeliveryMethods =
    [
        new(
            DeliveryMethod.StandardCode,
            "Standard delivery",
            "Delivered by courier to your address.",
            IsEnabled: true,
            RequiresAddress: true,
            SortOrder: 1),
        new(
            DeliveryMethod.ExpressCode,
            "Express delivery",
            "Our fastest courier service, where it is available.",
            IsEnabled: true,
            RequiresAddress: true,
            SortOrder: 2),
        new(
            DeliveryMethod.PickupCode,
            "Store pickup",
            "Collect your order from one of our stores.",

            // Doc 08 §9. There is no pickup location to collect from, and an enabled method with no
            // location is an option that fails after the shopper has chosen it.
            IsEnabled: false,
            RequiresAddress: false,
            SortOrder: 3),
    ];

    /// <summary>
    /// Doc 08 §9's nine zone rates. <b>Placeholders, client to confirm (D8)</b> — every fee, threshold
    /// and estimate below is quoted from §9's table and none of it is this task's invention.
    /// <list type="bullet">
    /// <item><description>
    /// <b>Express is unavailable island-wide</b>, which §9 states and §3.5 explains: a row that is present
    /// with <c>IsAvailable = 0</c> says "deliberately not offered here", where a missing row would say
    /// "nobody has configured this yet". The two are different facts and the admin gap check reads the
    /// difference, so the row is written.
    /// </description></item>
    /// <item><description>
    /// <b>Pickup's rates are seeded although the method is disabled.</b> They cost nothing to hold and
    /// they mean enabling the method later is one switch rather than a switch plus three rate rows
    /// somebody has to remember.
    /// </description></item>
    /// </list>
    /// </summary>
    public static readonly IReadOnlyList<DeliveryMethodZoneRateSeed> DeliveryMethodZoneRates =
    [
        // Colombo
        new(DeliveryMethod.StandardCode, DeliveryZone.Colombo, true, 400m, 10_000m, 1, 2),
        new(DeliveryMethod.ExpressCode, DeliveryZone.Colombo, true, 950m, null, 1, 1),
        new(DeliveryMethod.PickupCode, DeliveryZone.Colombo, true, 0m, null, 1, 2),

        // Western
        new(DeliveryMethod.StandardCode, DeliveryZone.Western, true, 450m, 10_000m, 2, 3),
        new(DeliveryMethod.ExpressCode, DeliveryZone.Western, true, 1_200m, null, 1, 2),
        new(DeliveryMethod.PickupCode, DeliveryZone.Western, true, 0m, null, 1, 2),

        // IslandWide
        new(DeliveryMethod.StandardCode, DeliveryZone.IslandWide, true, 600m, 15_000m, 3, 5),
        new(DeliveryMethod.ExpressCode, DeliveryZone.IslandWide, false, 0m, null, 1, 2),
        new(DeliveryMethod.PickupCode, DeliveryZone.IslandWide, true, 0m, null, 1, 2),
    ];

    /// <summary>
    /// Doc 08 §9: <c>BankTransfer</c> enabled, <c>PayPal</c> disabled until credentials and a fresh
    /// exchange rate are set.
    /// <para>
    /// <b>The descriptions say what the order will do and nothing about money that has not been built.</b>
    /// Bank details, the reference to quote and the slip upload are doc 11 P7 — W3-03's step 3 — and the
    /// 48-hour window is a setting that will live in <c>ops.Settings</c> (ADM-22), so the copy here states
    /// the fact that holds regardless: the order waits for payment. A seeded sentence promising bank
    /// details on a page that has none would be the shop lying on its own checkout.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<PaymentProviderSeed> PaymentProviders =
    [
        new(
            PaymentProviderCodes.BankTransfer,
            "Bank or online transfer",
            IsEnabled: true,
            DisplayOrder: 1,
            "Place your order now and transfer the total to our bank account. We hold your items while we "
            + "wait for the payment."),
        new(
            PaymentProviderCodes.PayPal,
            "PayPal",

            // Doc 08 §9 and §3.5: "enabling a provider requires its settings to be valid (e.g. PayPal
            // credentials and a fresh FX rate)". Neither exists (ops.Settings is ADM-22, PayPal is W4-01),
            // so the row is seeded disabled and checkout does not list it.
            IsEnabled: false,
            DisplayOrder: 2,
            "Pay by card or PayPal balance."),
    ];

    /// <summary>
    /// The courier seed version this build ships, recorded in <c>Seed.CouriersVersion</c> (issue #33). A courier
    /// whose <see cref="CourierSeed.IntroducedIn"/> is at or below a database's recorded version has already been
    /// offered to it, so one an admin has deleted stays deleted (the #152 rule).
    /// </summary>
    public const int CourierSeedVersion = 1;

    /// <summary>
    /// <c>sales.Couriers</c> (ADM-22): the carriers the shop starts with (owner decision, 2026-10-21, issue #33,
    /// which reversed the earlier "seed no courier" rule; and the same day's decision to deliver worldwide).
    /// <para>
    /// <b>Every name, telephone number and tracking pattern was checked on the courier's own site on
    /// 2026-10-21</b> — <c>docs/research/2026-10-21-sri-lanka-delivery-returns-couriers.md</c> §3 records how. A
    /// template is set only where the site was seen reading the number from the address; a courier whose tracking
    /// is a form (AJAX, a postback or a CAPTCHA) has none, so its number is shown as text rather than as a link to
    /// a blank form. Nothing is guessed: DHL Express's site refused automated checks, so its template and
    /// telephone number are left for the owner to fill in.
    /// </para>
    /// <para>
    /// <b>Only the three with verified links are switched on.</b> The rest are real and widely used and are
    /// switched on from Settings → Couriers when the shop books with them. The two international carriers are off
    /// until overseas delivery is live.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<CourierSeed> Couriers =
    [
        new("Domex", "011 7 759 759", "https://www.domex.lk/Order-Details.php?wbno={trackingNumber}", IsActive: true, SortOrder: 1),
        new("Koombiyo Delivery", "011 7886786", "https://koombiyodelivery.lk/Track/track_id?id={trackingNumber}&phone=", IsActive: true, SortOrder: 2),
        new("Citypak", "+94 11 7697000", "https://track.citypak.lk/track?tracking_number={trackingNumber}", IsActive: true, SortOrder: 3),
        new("Pronto Lanka", "+94 112 102 700", null, IsActive: false, SortOrder: 4),
        new("Trans Express", "011 2999 888", null, IsActive: false, SortOrder: 5),
        new("Sri Lanka Post (Speed Post)", "011 2328112", null, IsActive: false, SortOrder: 6),

        // International (owner decision 2026-10-21: the shop also delivers worldwide). Off until that is live.
        new("Sri Lanka Post EMS", null, null, IsActive: false, SortOrder: 7),
        new("DHL Express", null, null, IsActive: false, SortOrder: 8),
    ];
}
