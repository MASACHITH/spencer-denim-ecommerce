using System.Globalization;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.UnitTests.Domain.Settings;

/// <summary>
/// What counts as a display rate (issue #248). The save and the public feed both apply
/// <see cref="CurrencyRateRules"/>, so these are the rules for both.
/// </summary>
public sealed class CurrencyRateRulesTests
{
    [Theory]
    [InlineData("399.5", "399.5")]
    [InlineData("399", "399")]
    [InlineData("0.0001", "0.0001")]
    [InlineData("1000000", "1000000")]
    [InlineData("1000000.0000", "1000000")]
    [InlineData("82.2750", "82.275")]
    [InlineData("007.50", "7.5")]
    public void A_plain_positive_figure_is_a_rate_and_has_one_canonical_text(string text, string canonical)
    {
        var problem = CurrencyRateRules.Check(text, out var rate);

        Assert.Equal(CurrencyRateProblem.None, problem);
        Assert.Equal(canonical, CurrencyRateRules.Canonical(rate));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" 399.5")]
    [InlineData("399.5 ")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1e3")]
    [InlineData("1,5")]
    [InlineData("1,000.5")]
    [InlineData(".5")]
    [InlineData("5.")]
    [InlineData(".")]
    [InlineData("1.2.3")]
    [InlineData("٣٩٩")]
    public void Anything_but_plain_invariant_digits_is_not_a_number(string? text)
    {
        Assert.Equal(CurrencyRateProblem.NotANumber, CurrencyRateRules.Check(text, out var rate));
        Assert.Equal(0m, rate);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.0000")]
    [InlineData("1000000.0001")]
    [InlineData("1000001")]
    [InlineData("99999999999999999999999999999999")]
    public void Zero_and_anything_above_the_ceiling_is_out_of_range(string text)
    {
        Assert.Equal(CurrencyRateProblem.OutOfRange, CurrencyRateRules.Check(text, out _));
    }

    [Theory]
    [InlineData("1.00001")]
    [InlineData("399.123456")]
    public void More_than_four_decimal_places_is_too_many(string text)
    {
        Assert.Equal(CurrencyRateProblem.TooManyDecimals, CurrencyRateRules.Check(text, out _));
    }

    /// <summary>The feed trims the ends of a stored value, as every settings save does, and never throws.</summary>
    [Theory]
    [InlineData("399.5", "399.5")]
    [InlineData("  399.5  ", "399.5")]
    [InlineData("0.0001", "0.0001")]
    public void A_stored_value_that_is_a_rate_is_read(string stored, string expected)
    {
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), CurrencyRateRules.TryReadStored(stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("1e3")]
    [InlineData("1000001")]
    [InlineData("399.12345")]
    [InlineData("399,5")]
    public void A_stored_value_that_is_not_a_rate_is_null_and_never_throws(string? stored)
    {
        Assert.Null(CurrencyRateRules.TryReadStored(stored));
    }

    /// <summary>
    /// A figure means the same on every machine: the rules never consult the thread's culture, which is how a server
    /// set to a German locale would otherwise read "399.5" as 3995 or refuse it.
    /// </summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("si-LK")]
    [InlineData("ar-SA")]
    public void The_thread_culture_changes_nothing(string cultureName)
    {
        var before = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);

            Assert.Equal(CurrencyRateProblem.None, CurrencyRateRules.Check("399.5", out var rate));
            Assert.Equal(399.5m, rate);
            Assert.Equal("399.5", CurrencyRateRules.Canonical(rate));
            Assert.Equal(CurrencyRateProblem.NotANumber, CurrencyRateRules.Check("399,5", out _));
            Assert.Equal(399.5m, CurrencyRateRules.TryReadStored("399.5"));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void The_bounds_in_the_rules_are_the_ones_the_key_class_declares()
    {
        Assert.Equal(CurrencyRateProblem.None, CurrencyRateRules.Check("1000000", out _));
        Assert.Equal(1_000_000m, SettingKeys.CurrencyDisplay.MaxRate);
        Assert.Equal(0m, SettingKeys.CurrencyDisplay.MinRateExclusive);
        Assert.Equal(4, SettingKeys.CurrencyDisplay.MaxDecimalPlaces);
    }
}
