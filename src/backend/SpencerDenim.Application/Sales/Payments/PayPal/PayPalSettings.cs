using System.Globalization;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Application.Sales.Payments.PayPal;

/// <summary>Which PayPal the credentials belong to (<c>Payments.PayPal.Mode</c>).</summary>
public enum PayPalMode
{
    /// <summary>PayPal's test system: <c>api-m.sandbox.paypal.com</c>. No real money moves.</summary>
    Sandbox = 0,

    /// <summary>The real thing: <c>api-m.paypal.com</c>.</summary>
    Live = 1,
}

/// <summary>Whether, and if not why not, PayPal may be offered to a customer right now.</summary>
public enum PayPalAvailability
{
    /// <summary>Credentials, webhook id and a fresh rate are all in place.</summary>
    Available = 0,

    /// <summary>The client id, the secret or the webhook id is missing (or the secret cannot be read).</summary>
    NotConfigured = 1,

    /// <summary>No rate has been set, or it is not a usable number.</summary>
    RateMissing = 2,

    /// <summary>The rate was last set or confirmed longer ago than <c>RateMaxAgeDays</c> (doc 11 P8, D1).</summary>
    RateStale = 3,
}

/// <summary>
/// PayPal's settings, read from <c>ops.Settings</c> on every use (doc 08 §3.8 <c>Payments.PayPal</c>,
/// <c>Orders.PayPalExpiryMinutes</c>), with doc 08's defaults filled in for a number that is missing or does
/// not parse.
/// <para>
/// <b>A class with a hand-written <see cref="ToString"/>, and not a record, because it holds the client
/// secret.</b> A record prints every member from its generated <c>ToString</c>, and a logged exception, a
/// debugger watch or a test failure message would then carry the credential. This prints the facts that
/// help (mode, whether each credential is set, the rate) and never the secret.
/// </para>
/// </summary>
public sealed class PayPalSettings
{
    /// <summary>Which PayPal host to talk to.</summary>
    public PayPalMode Mode { get; init; } = PayPalMode.Sandbox;

    /// <summary>The REST app's client id, or null.</summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// The REST app's client secret, decrypted — or null when it is not set or cannot be decrypted.
    /// <b>Never log, serialise or store this.</b>
    /// </summary>
    public string? ClientSecret { get; init; }

    /// <summary>The webhook's id, or null.</summary>
    public string? WebhookId { get; init; }

    /// <summary>LKR per 1 USD, or null when no usable rate is set.</summary>
    public decimal? LkrPerUsd { get; init; }

    /// <summary>The conversion buffer, in percent.</summary>
    public decimal FxBufferPercent { get; init; } = SettingKeys.PayPal.FxBufferPercentDefault;

    /// <summary>How old the rate may be before PayPal is hidden.</summary>
    public int RateMaxAgeDays { get; init; } = SettingKeys.PayPal.RateMaxAgeDaysDefault;

    /// <summary>When the rate was last set or confirmed, or null.</summary>
    public DateTime? RateUpdatedAtUtc { get; init; }

    /// <summary>How long a claimed capture keeps its order from expiring.</summary>
    public int CaptureLockHours { get; init; } = SettingKeys.PayPal.CaptureLockHoursDefault;

    /// <summary>How long recovery keeps asking PayPal about a stuck capture.</summary>
    public int CaptureRecoveryMaxHours { get; init; } = SettingKeys.PayPal.CaptureRecoveryMaxHoursDefault;

    /// <summary>How long, from placement, an order is held for a PENDING capture.</summary>
    public int PendingCaptureMaxDays { get; init; } = SettingKeys.PayPal.PendingCaptureMaxDaysDefault;

    /// <summary>How long a customer has to pay by PayPal.</summary>
    public int ExpiryMinutes { get; init; } = SettingKeys.PayPal.ExpiryMinutesDefault;

    /// <summary>
    /// Whether the three things PayPal needs in order to take and confirm a payment are all present: the
    /// client id and secret (to call PayPal) and the webhook id (to trust what PayPal calls us with).
    /// </summary>
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(WebhookId);

    /// <summary>The base address of the PayPal REST API for <see cref="Mode"/>, without a trailing slash.</summary>
    public string ApiBaseUrl => Mode == PayPalMode.Live
        ? "https://api-m.paypal.com"
        : "https://api-m.sandbox.paypal.com";

    /// <summary>The address a buyer approves a payment at, for <see cref="Mode"/>, without a trailing slash.</summary>
    public string CheckoutBaseUrl => Mode == PayPalMode.Live
        ? "https://www.paypal.com"
        : "https://www.sandbox.paypal.com";

    /// <summary>
    /// Whether the rate is a usable number that was set or confirmed within <see cref="RateMaxAgeDays"/> of
    /// <paramref name="nowUtc"/>. A rate stamped in the future (a clock that moved) counts as fresh: the
    /// failure it guards against is an <i>old</i> rate.
    /// </summary>
    public bool RateIsFresh(DateTime nowUtc) =>
        LkrPerUsd is > 0m
        && RateUpdatedAtUtc is { } updated
        && nowUtc - updated <= TimeSpan.FromDays(RateMaxAgeDays);

    /// <summary>
    /// Whether PayPal may be offered to a customer at <paramref name="nowUtc"/>: configured <b>and</b> with a
    /// fresh rate (doc 11 P8 preconditions, "PayPal is hidden at checkout when its rate is older than 7
    /// days"). The shop's own on/off switch, <c>sales.PaymentProviders.IsEnabled</c>, is a separate question
    /// that checkout asks first.
    /// </summary>
    public PayPalAvailability AvailabilityAt(DateTime nowUtc)
    {
        if (!HasCredentials)
        {
            return PayPalAvailability.NotConfigured;
        }

        if (LkrPerUsd is not > 0m || RateUpdatedAtUtc is null)
        {
            return PayPalAvailability.RateMissing;
        }

        return RateIsFresh(nowUtc) ? PayPalAvailability.Available : PayPalAvailability.RateStale;
    }

    /// <summary>Everything except the secret, for a log line that has to say what was configured.</summary>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"PayPalSettings(Mode={Mode}, ClientId={(string.IsNullOrWhiteSpace(ClientId) ? "unset" : "set")}, "
            + $"ClientSecret={(string.IsNullOrWhiteSpace(ClientSecret) ? "unset" : "set")}, "
            + $"WebhookId={(string.IsNullOrWhiteSpace(WebhookId) ? "unset" : "set")}, "
            + $"LkrPerUsd={LkrPerUsd}, FxBufferPercent={FxBufferPercent}, RateUpdatedAtUtc={RateUpdatedAtUtc:O})");
}

/// <summary>
/// Reads <see cref="PayPalSettings"/> (doc 08 §3.8). Not cached: a Super Admin who switches to live or
/// corrects a credential must not have the old one used for another minute, and the read is rare — a
/// checkout, an order page, a capture.
/// </summary>
public interface IPayPalSettingsReader
{
    /// <summary>
    /// The settings now. Always an object: a missing or unparseable number takes doc 08's default, and a
    /// missing credential is simply null (see <see cref="PayPalSettings.HasCredentials"/>).
    /// </summary>
    Task<PayPalSettings> ReadAsync(CancellationToken cancellationToken);
}
