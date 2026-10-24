using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Domain.Settings;

/// <summary>
/// One currency a shopper may choose to see prices in (issue #248).
/// </summary>
/// <param name="Code">The ISO 4217 code, which is also what the public feed calls it.</param>
/// <param name="Symbol">
/// What goes in front of the figure: <c>US$</c>, the pound sign, the euro sign, <c>A$</c> and <c>AED</c>. The
/// dollars carry a letter or two so a shopper cannot mistake an Australian price for an American one.
/// </param>
/// <param name="SettingKey">The <c>ops.Settings</c> row holding the rate (rupees for one unit).</param>
/// <param name="Field">The field of the admin save's body that carries the rate: the lower-case code.</param>
/// <param name="Name">
/// The currency's name as it reads in the middle of a sentence ("Enter the pound sterling rate", "one UAE dirham"):
/// written out in full here, because lower-casing a capitalised name at the point of use turns "US dollar" into
/// "us dollar" and "UAE dirham" into "uae dirham".
/// </param>
/// <param name="Label">The same name as the settings screen's label, which starts a line, so it is capitalised.</param>
public sealed record DisplayCurrency(string Code, string Symbol, string SettingKey, string Field, string Name, string Label);

/// <summary>
/// The five display currencies, in the order the storefront offers them. <b>The one list</b> the save's
/// validator, the public feed, the admin screen's field order and the tests all read, so none of them can
/// disagree about which currencies exist.
/// </summary>
public static class DisplayCurrencies
{
    /// <summary>USD, GBP, EUR, AUD, AED.</summary>
    public static IReadOnlyList<DisplayCurrency> All { get; } =
    [
        new("USD", "US$", SettingKeys.CurrencyDisplay.UsdLkrPerUnit, "usd", "US dollar", "US dollar rate"),
        new("GBP", "£", SettingKeys.CurrencyDisplay.GbpLkrPerUnit, "gbp", "pound sterling", "Pound sterling rate"),
        new("EUR", "€", SettingKeys.CurrencyDisplay.EurLkrPerUnit, "eur", "euro", "Euro rate"),
        new("AUD", "A$", SettingKeys.CurrencyDisplay.AudLkrPerUnit, "aud", "Australian dollar", "Australian dollar rate"),
        new("AED", "AED", SettingKeys.CurrencyDisplay.AedLkrPerUnit, "aed", "UAE dirham", "UAE dirham rate"),
    ];
}

/// <summary>
/// Group <c>Currency.Display</c> (doc 08 §3.8; issue #248): the five display rates, derived from
/// <see cref="DisplayCurrencies"/> so a sixth currency is one line there and not a second list to keep in step.
/// <para>
/// <b>Seeded null, and an unset rate is not an error.</b> A rate is a fact only the owner has: a plausible
/// default would show a wrong price on every product page for as long as nobody noticed, which is the defect this
/// project has shipped before with a bank and a courier. An unset currency is not offered to shoppers at all.
/// </para>
/// </summary>
public static class CurrencySettingDefinitions
{
    /// <summary>The five definitions, in <see cref="DisplayCurrencies.All"/>'s order.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        .. DisplayCurrencies.All.Select(currency => new SettingDefinition(
            currency.SettingKey,
            SettingKeys.CurrencyDisplay.Group,
            SettingValueType.Decimal,
            Permissions.Settings.Edit,
            currency.Label,
            DescriptionFor(currency))),
    ];

    /// <summary>
    /// What staff are told the row is. <b>Only what is true of the system as it stands</b>: the rate is how many
    /// rupees one unit is worth, it is typed by hand, and an empty one leaves the currency out of the list the shop
    /// publishes. What a shopper then sees is the storefront's business and is not promised here. A test pins the
    /// exact text and that it fits the column (300 characters, doc 08 §3.8).
    /// </summary>
    public static string DescriptionFor(DisplayCurrency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);

        return $"How many rupees one {currency.Name} ({currency.Code}) is worth, typed in by hand. Digits with an "
            + "optional decimal point, up to four decimal places. Leave it empty to leave this currency out.";
    }
}
