using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Sales.Payments.PayPal.Admin;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.UnitTests.Application.Sales.PayPal;

/// <summary>
/// The field rules for the PayPal settings panel (PAY-02): each value is something that reaches PayPal or
/// decides what a customer is charged, so each is strict about what it can be and returns the normalised text
/// that is stored.
/// </summary>
public sealed class PayPalSettingRulesTests
{
    private static (string? Value, List<ValidationError> Errors) Clean(string key, string? raw)
    {
        var errors = new List<ValidationError>();
        var value = PayPalSettingRules.Clean(errors, key, "field", raw);

        return (value, errors);
    }

    [Theory]
    [InlineData("sandbox", "sandbox")]
    [InlineData("live", "live")]
    [InlineData("LIVE", "live")]
    [InlineData("  Sandbox ", "sandbox")]
    public void The_environment_is_sandbox_or_live(string raw, string expected)
    {
        var (value, errors) = Clean(SettingKeys.PayPal.Mode, raw);

        Assert.Equal(expected, value);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("production")]
    [InlineData("test")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_refused_and_the_environment_cannot_be_cleared(string? raw)
    {
        var (_, errors) = Clean(SettingKeys.PayPal.Mode, raw);

        Assert.Single(errors);
    }

    [Theory]
    [InlineData("300", "300.00")]
    [InlineData("300.5", "300.50")]
    [InlineData("301.25", "301.25")]
    [InlineData("299.123456", "299.123456")]
    [InlineData("0.5", "0.50")]
    public void A_rate_is_stored_with_at_least_two_places(string raw, string expected)
    {
        var (value, errors) = Clean(SettingKeys.PayPal.LkrPerUsd, raw);

        Assert.Equal(expected, value);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-300")]
    [InlineData("abc")]
    [InlineData("300,50")]
    [InlineData("1e3")]
    [InlineData("10000.01")]
    [InlineData("300.1234567")]
    [InlineData("30 0")]
    public void A_rate_that_cannot_be_right_is_refused(string raw)
    {
        var (_, errors) = Clean(SettingKeys.PayPal.LkrPerUsd, raw);

        Assert.Single(errors);
    }

    [Fact]
    public void A_blank_rate_clears_it_which_hides_PayPal()
    {
        var (value, errors) = Clean(SettingKeys.PayPal.LkrPerUsd, "  ");

        Assert.Null(value);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("3", "3.00")]
    [InlineData("3.5", "3.50")]
    [InlineData("0", "0.00")]
    [InlineData("20", "20.00")]
    public void A_buffer_is_a_percentage_with_two_places(string raw, string expected)
    {
        var (value, errors) = Clean(SettingKeys.PayPal.FxBufferPercent, raw);

        Assert.Equal(expected, value);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("20.01")]
    [InlineData("3.001")]
    [InlineData("three")]
    public void A_buffer_outside_zero_to_twenty_is_refused(string raw)
    {
        Assert.Single(Clean(SettingKeys.PayPal.FxBufferPercent, raw).Errors);
    }

    [Theory]
    [InlineData("Payments.PayPal.RateMaxAgeDays", "1", "30")]
    [InlineData("Payments.PayPal.CaptureLockHours", "1", "24")]
    [InlineData("Payments.PayPal.CaptureRecoveryMaxHours", "1", "168")]
    [InlineData("Payments.PayPal.PendingCaptureMaxDays", "1", "14")]
    [InlineData("Orders.PayPalExpiryMinutes", "10", "180")]
    public void A_whole_number_setting_accepts_its_bounds_and_nothing_beyond(string key, string min, string max)
    {
        Assert.Equal(min, Clean(key, min).Value);
        Assert.Equal(max, Clean(key, max).Value);

        Assert.Single(Clean(key, (int.Parse(min, System.Globalization.CultureInfo.InvariantCulture) - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).Errors);
        Assert.Single(Clean(key, (int.Parse(max, System.Globalization.CultureInfo.InvariantCulture) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).Errors);
        Assert.Single(Clean(key, "2.5").Errors);
        Assert.Single(Clean(key, "-3").Errors);
        Assert.Single(Clean(key, "soon").Errors);
    }

    [Theory]
    [InlineData("AbC-123_def.GHI")]
    [InlineData("AZcl-8_8Kfoo")]
    public void A_client_id_is_letters_digits_and_a_few_separators(string raw)
    {
        var (value, errors) = Clean(SettingKeys.PayPal.ClientId, raw);

        Assert.Equal(raw, value);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("quote\"d")]
    [InlineData("new\nline")]
    public void A_client_id_with_anything_else_in_it_is_refused(string raw)
    {
        Assert.Single(Clean(SettingKeys.PayPal.ClientId, raw).Errors);
    }

    [Fact]
    public void A_client_id_too_long_to_be_one_is_refused()
    {
        Assert.Single(Clean(SettingKeys.PayPal.ClientId, new string('a', PayPalSettingRules.ClientIdMaxLength + 1)).Errors);
    }

    [Theory]
    [InlineData("8PT597110X687430LKGECATA", true)]
    [InlineData("WH-with.dot", false)]
    public void A_webhook_id_is_letters_digits_hyphens_and_underscores(string raw, bool accepted)
    {
        var (value, errors) = Clean(SettingKeys.PayPal.WebhookId, raw);

        Assert.Equal(accepted, errors.Count == 0);

        if (accepted)
        {
            Assert.Equal(raw, value);
        }
    }

    [Fact]
    public void A_blank_text_setting_clears_it()
    {
        Assert.Null(Clean(SettingKeys.PayPal.ClientId, " ").Value);
        Assert.Null(Clean(SettingKeys.PayPal.WebhookId, null).Value);
    }

    /// <summary>Blank is "keep", not "clear": a secret is write-only, so the screen cannot send back what it was never told.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_secret_means_keep_the_stored_one(string? raw)
    {
        var errors = new List<ValidationError>();

        Assert.Null(PayPalSettingRules.CleanSecret(errors, "clientSecret", raw));
        Assert.Empty(errors);
    }

    [Fact]
    public void A_pasted_secret_loses_only_the_whitespace_around_it()
    {
        var errors = new List<ValidationError>();

        Assert.Equal("EBWKjlELKMYqRNQ6sYvFo64FtaRLRR5BdHEESmha49TM", PayPalSettingRules.CleanSecret(errors, "clientSecret", "  EBWKjlELKMYqRNQ6sYvFo64FtaRLRR5BdHEESmha49TM\n"));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("has a space")]
    [InlineData("tab\tinside")]
    [InlineData("line\nbreak")]
    public void A_secret_with_whitespace_inside_is_refused_not_repaired(string raw)
    {
        var errors = new List<ValidationError>();

        Assert.Null(PayPalSettingRules.CleanSecret(errors, "clientSecret", raw));
        Assert.Single(errors);
    }

    [Fact]
    public void The_validation_message_for_a_secret_does_not_repeat_it()
    {
        var errors = new List<ValidationError>();

        PayPalSettingRules.CleanSecret(errors, "clientSecret", "super secret value with spaces");

        Assert.DoesNotContain("super", errors.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_this_command_does_not_write_is_a_programming_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Clean(SettingKeys.PayPal.RateUpdatedAtUtc, "2026-01-01"));
    }
}
