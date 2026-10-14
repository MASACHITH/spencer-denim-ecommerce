using SpencerDenim.Application.Sales.Payments.PayPal;

namespace SpencerDenim.UnitTests.Application.Sales.PayPal;

/// <summary>
/// When PayPal may be offered to a customer (doc 11 P8 preconditions): credentials, the webhook id and an
/// exchange rate that is not older than <c>RateMaxAgeDays</c>.
/// </summary>
public sealed class PayPalSettingsTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);

    private static PayPalSettings Ready(
        string? clientId = "client",
        string? secret = "secret",
        string? webhook = "webhook",
        decimal? rate = 300m,
        DateTime? updated = null,
        int maxAgeDays = 7) =>
        new()
        {
            ClientId = clientId,
            ClientSecret = secret,
            WebhookId = webhook,
            LkrPerUsd = rate,
            RateUpdatedAtUtc = updated ?? Now.AddDays(-1),
            RateMaxAgeDays = maxAgeDays,
        };

    [Fact]
    public void Credentials_a_webhook_id_and_a_fresh_rate_make_PayPal_available()
    {
        Assert.Equal(PayPalAvailability.Available, Ready().AvailabilityAt(Now));
    }

    [Theory]
    [InlineData(null, "secret", "webhook")]
    [InlineData("client", null, "webhook")]
    [InlineData("client", "secret", null)]
    [InlineData("", "secret", "webhook")]
    [InlineData("client", "   ", "webhook")]
    [InlineData("client", "secret", "")]
    public void Any_missing_credential_means_PayPal_is_not_configured(string? clientId, string? secret, string? webhook)
    {
        var settings = Ready(clientId, secret, webhook);

        Assert.False(settings.HasCredentials);
        Assert.Equal(PayPalAvailability.NotConfigured, settings.AvailabilityAt(Now));
    }

    [Fact]
    public void Without_a_rate_PayPal_is_not_offered()
    {
        Assert.Equal(PayPalAvailability.RateMissing, Ready(rate: null).AvailabilityAt(Now));
        Assert.Equal(PayPalAvailability.RateMissing, Ready(rate: 0m).AvailabilityAt(Now));
        Assert.Equal(
            PayPalAvailability.RateMissing,
            new PayPalSettings { ClientId = "c", ClientSecret = "s", WebhookId = "w", LkrPerUsd = 300m }
                .AvailabilityAt(Now));
    }

    [Fact]
    public void A_rate_older_than_the_limit_hides_PayPal_and_one_exactly_at_the_limit_does_not()
    {
        Assert.Equal(
            PayPalAvailability.Available,
            Ready(updated: Now.AddDays(-7)).AvailabilityAt(Now));

        Assert.Equal(
            PayPalAvailability.RateStale,
            Ready(updated: Now.AddDays(-7).AddSeconds(-1)).AvailabilityAt(Now));

        // Doc 11 P8: "PayPal is hidden at checkout when its rate is older than 7 days."
        Assert.Equal(PayPalAvailability.RateStale, Ready(updated: Now.AddDays(-30)).AvailabilityAt(Now));
    }

    [Fact]
    public void The_age_limit_is_the_settings_own()
    {
        Assert.Equal(PayPalAvailability.RateStale, Ready(updated: Now.AddDays(-3), maxAgeDays: 2).AvailabilityAt(Now));
        Assert.Equal(PayPalAvailability.Available, Ready(updated: Now.AddDays(-3), maxAgeDays: 4).AvailabilityAt(Now));
    }

    [Fact]
    public void A_rate_stamped_in_the_future_counts_as_fresh_because_the_danger_is_an_old_one()
    {
        Assert.Equal(PayPalAvailability.Available, Ready(updated: Now.AddMinutes(5)).AvailabilityAt(Now));
    }

    [Fact]
    public void Credentials_are_checked_before_the_rate()
    {
        Assert.Equal(
            PayPalAvailability.NotConfigured,
            Ready(secret: null, rate: null).AvailabilityAt(Now));
    }

    [Theory]
    [InlineData(PayPalMode.Sandbox, "https://api-m.sandbox.paypal.com", "https://www.sandbox.paypal.com")]
    [InlineData(PayPalMode.Live, "https://api-m.paypal.com", "https://www.paypal.com")]
    public void The_mode_chooses_the_PayPal_host(PayPalMode mode, string api, string checkout)
    {
        var settings = new PayPalSettings { Mode = mode };

        Assert.Equal(api, settings.ApiBaseUrl);
        Assert.Equal(checkout, settings.CheckoutBaseUrl);
    }

    [Fact]
    public void The_default_mode_is_sandbox()
    {
        Assert.Equal(PayPalMode.Sandbox, new PayPalSettings().Mode);
    }

    [Fact]
    public void The_defaults_are_the_figures_doc_08_states()
    {
        var settings = new PayPalSettings();

        Assert.Equal(3.00m, settings.FxBufferPercent);
        Assert.Equal(7, settings.RateMaxAgeDays);
        Assert.Equal(3, settings.CaptureLockHours);
        Assert.Equal(24, settings.CaptureRecoveryMaxHours);
        Assert.Equal(5, settings.PendingCaptureMaxDays);
        Assert.Equal(60, settings.ExpiryMinutes);
    }

    /// <summary>
    /// The object holds the client secret, so printing it - in a log, a failed assertion, a debugger watch -
    /// must never print it.
    /// </summary>
    [Fact]
    public void Printing_the_settings_never_shows_the_secret()
    {
        var text = Ready(secret: "s3cr3t-value-123", clientId: "AbC-client-id").ToString();

        Assert.DoesNotContain("s3cr3t-value-123", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AbC-client-id", text, StringComparison.Ordinal);
        Assert.Contains("ClientSecret=set", text, StringComparison.Ordinal);
    }
}
