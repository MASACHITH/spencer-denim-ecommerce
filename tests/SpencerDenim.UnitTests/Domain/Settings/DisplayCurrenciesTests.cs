using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.UnitTests.Domain.Settings;

/// <summary>
/// The five display currencies and the settings rows derived from them (issue #248).
/// </summary>
public sealed class DisplayCurrenciesTests
{
    [Fact]
    public void The_five_currencies_are_offered_in_this_order_with_these_symbols()
    {
        Assert.Equal(
            [("USD", "US$"), ("GBP", "£"), ("EUR", "€"), ("AUD", "A$"), ("AED", "AED")],
            DisplayCurrencies.All.Select(currency => (currency.Code, currency.Symbol)));
    }

    [Fact]
    public void The_symbols_are_the_real_characters_and_not_a_damaged_copy_of_them()
    {
        // U+00A3 and U+20AC. A symbol that went through the wrong encoding once is not a symbol.
        Assert.Equal((char)0x00A3, DisplayCurrencies.All.Single(currency => currency.Code == "GBP").Symbol[0]);
        Assert.Equal((char)0x20AC, DisplayCurrencies.All.Single(currency => currency.Code == "EUR").Symbol[0]);
        Assert.All(DisplayCurrencies.All, currency => Assert.DoesNotContain((char)0xFFFD, currency.Symbol));
    }

    [Fact]
    public void Each_body_field_is_the_lower_case_code_and_is_unique()
    {
        Assert.All(DisplayCurrencies.All, currency => Assert.Equal(currency.Code.ToLowerInvariant(), currency.Field));
        Assert.Equal(DisplayCurrencies.All.Count, DisplayCurrencies.All.Select(currency => currency.Field).Distinct().Count());
        Assert.Equal(DisplayCurrencies.All.Count, DisplayCurrencies.All.Select(currency => currency.Code).Distinct().Count());
        Assert.Equal(DisplayCurrencies.All.Count, DisplayCurrencies.All.Select(currency => currency.SettingKey).Distinct().Count());
    }

    /// <summary>
    /// The catalogue's rows are the table's rows, in its order, and nothing else is in the group, so a sixth key
    /// added to one list and not the other fails here.
    /// </summary>
    [Fact]
    public void The_catalogue_group_is_exactly_the_tables_keys_in_the_tables_order()
    {
        Assert.Equal(
            DisplayCurrencies.All.Select(currency => currency.SettingKey),
            SettingCatalog.KeysIn(SettingKeys.CurrencyDisplay.Group));
    }

    [Fact]
    public void Every_rate_is_an_unseeded_non_secret_decimal_under_settings_edit()
    {
        foreach (var currency in DisplayCurrencies.All)
        {
            var definition = SettingCatalog.Find(currency.SettingKey);

            Assert.NotNull(definition);
            Assert.Equal(SettingKeys.CurrencyDisplay.Group, definition.GroupKey);
            Assert.Equal(SettingValueType.Decimal, definition.ValueType);
            Assert.False(definition.IsSecret);

            // Settings.Edit and never Settings.Payments: doc 08 reserves that for Bank.* and Payments.* keys.
            Assert.Equal(Permissions.Settings.Edit, definition.RequiredPermission);

            // A rate is a fact only the owner has. A seeded default would be a wrong price on a product page.
            Assert.Null(definition.SeedValue);
        }
    }

    [Fact]
    public void The_keys_are_not_under_the_payments_prefix_that_the_sensitive_change_alert_and_permission_follow()
    {
        Assert.StartsWith("Currency.", SettingKeys.CurrencyDisplay.Group, StringComparison.Ordinal);
        Assert.All(
            DisplayCurrencies.All,
            currency =>
            {
                Assert.DoesNotContain("Payments", currency.SettingKey, StringComparison.Ordinal);
                Assert.StartsWith(SettingKeys.CurrencyDisplay.Group + ".", currency.SettingKey, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void The_labels_are_names_and_the_descriptions_fit_the_column()
    {
        foreach (var definition in SettingCatalog.All.Where(definition => definition.GroupKey == SettingKeys.CurrencyDisplay.Group))
        {
            Assert.InRange(definition.Label.Length, 1, SettingDefinition.LabelMaxLength);
            Assert.NotNull(definition.Description);

            // doc 08 §3.8: ops.Settings.Description is nvarchar(300).
            Assert.InRange(definition.Description.Length, 1, 300);
        }
    }

    /// <summary>
    /// The five rows are a save unit of their own, so the screen's token covers all five and only them.
    /// </summary>
    [Fact]
    public void The_five_rates_are_one_save_unit_of_their_own()
    {
        Assert.Contains(
            SettingCatalog.SaveUnits,
            unit => unit.Count == 1 && unit[0] == SettingKeys.CurrencyDisplay.Group);

        Assert.Equal(
            DisplayCurrencies.All.Select(currency => currency.SettingKey),
            SettingCatalog.KeysInSaveUnitFor(SettingKeys.CurrencyDisplay.Group));
    }

    /// <summary>PayPal's rate is a different number with a different job, and stays where it is.</summary>
    [Fact]
    public void PayPals_rate_is_not_one_of_the_display_rates()
    {
        Assert.DoesNotContain(SettingKeys.PayPal.LkrPerUsd, DisplayCurrencies.All.Select(currency => currency.SettingKey));
        Assert.NotNull(SettingCatalog.Find(SettingKeys.PayPal.LkrPerUsd));
        Assert.Equal(Permissions.Settings.Payments, SettingCatalog.Find(SettingKeys.PayPal.LkrPerUsd)!.RequiredPermission);
    }
}
