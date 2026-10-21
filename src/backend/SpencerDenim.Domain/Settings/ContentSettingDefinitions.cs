using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Domain.Settings;

/// <summary>
/// Group <c>Content</c> (doc 08 §3.8; PDP-05, issue #33): the product page's two short delivery and returns
/// texts. Their own file, beside <see cref="PayPalSettingDefinitions"/>, so the catalogue lists them in one
/// line and the wording lives where it can be read as wording.
/// <para>
/// <b>The seed values are the shop's starting terms, in our own words</b> (owner instruction, 2026-10-21): the
/// middle of what Sri Lankan apparel shops publish, adjusted for this shop's own terms — no cash on delivery
/// (#55), delivery worldwide as well as island-wide, and payment online (owner decision of the same day, which
/// made PayPal the only way to pay; the refund wording is "your original payment method" so it stays true if
/// bank transfer is switched back on). The research and the sources are in
/// <c>docs/research/2026-10-21-sri-lanka-delivery-returns-couriers.md</c>; the owner's defaults (14 days,
/// unworn with tags, 7 working days, 48 hours, sale items final, return postage) are recorded in the #33 journal entry.
/// </para>
/// <para>
/// <b>No fee and no delivery day appears in either text, on purpose.</b> Those live in
/// <c>sales.DeliveryMethodZoneRates</c> and the product page draws them from there, beside these texts; a figure
/// typed here would be a second source of truth that drifts the first time the owner changes a rate. The
/// numbers that <i>are</i> here — the return window, the refund time and the damage window — are policy, which
/// nothing else in the system holds, and the owner edits them on the settings screen.
/// </para>
/// <para>
/// <b>Applied on insert only</b> (<see cref="SettingDefinition.SeedValue"/>), so an owner who rewrites or empties
/// a text keeps it through every later deployment. The markup is already what the sanitiser produces — a unit
/// test sanitises each seed and asserts it comes back unchanged — so what is seeded is exactly what a save of
/// the same words would store.
/// </para>
/// </summary>
public static class ContentSettingDefinitions
{
    /// <summary>The delivery text seeded into <see cref="SettingKeys.Content.PdpDeliveryInfoHtml"/>.</summary>
    public const string DeliveryInfoSeed =
        "<ul>"
        + "<li>Within Sri Lanka we deliver to every district by courier. The fee for your district is shown at "
        + "checkout, before you pay.</li>"
        + "<li>We also deliver worldwide with a tracked international service, usually in 5 to 15 working days. "
        + "The fee for your country is shown at checkout.</li>"
        + "<li>Delivery times are in working days, counted from dispatch once your payment is confirmed. Customs "
        + "duties and taxes on overseas orders are paid by the customer.</li>"
        + "<li>Pay securely online at checkout. Cash on delivery is not available.</li>"
        + "</ul>";

    /// <summary>The returns text seeded into <see cref="SettingKeys.Content.PdpReturnsInfoHtml"/>.</summary>
    public const string ReturnsInfoSeed =
        "<ul>"
        + "<li>Changed your mind? Return or exchange within 14 days of delivery.</li>"
        + "<li>Items must be unworn and unwashed, with all their tags still attached.</li>"
        + "<li>Innerwear, swimwear, socks and sale items are final sale and cannot be returned.</li>"
        + "<li>Refunds go back to your original payment method within 7 working days of your return reaching "
        + "us and passing our check.</li>"
        + "<li>Return postage is yours to pay, from Sri Lanka or overseas, unless the item is faulty.</li>"
        + "<li>Damaged, faulty or wrong item? Tell us within 48 hours of delivery, with photos, and we will "
        + "put it right at our cost.</li>"
        + "</ul>";

    /// <summary>Both definitions, in the order the settings screen shows them: delivery, then returns.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        new(
            SettingKeys.Content.PdpDeliveryInfoHtml,
            SettingKeys.Content.Group,
            SettingValueType.TextHtml,
            Permissions.Settings.Edit,
            "Product page delivery text",
            "A few lines shown under the delivery fees on every product page, with a link to the Shipping page. "
            + "Fees and delivery days are shown from the delivery rates, so do not type them here. Paragraphs, "
            + "lists, bold and links are kept; anything else is removed.",
            SeedValue: DeliveryInfoSeed),

        new(
            SettingKeys.Content.PdpReturnsInfoHtml,
            SettingKeys.Content.Group,
            SettingValueType.TextHtml,
            Permissions.Settings.Edit,
            "Product page returns text",
            "A few lines about returns and exchanges shown on every product page, with a link to the Returns "
            + "page. Keep it short and in line with the Returns page. Paragraphs, lists, bold and links are "
            + "kept; anything else is removed.",
            SeedValue: ReturnsInfoSeed),
    ];
}
