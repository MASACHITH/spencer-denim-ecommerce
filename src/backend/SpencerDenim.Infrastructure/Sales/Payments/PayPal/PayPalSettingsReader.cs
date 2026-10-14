using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Sales.Payments.PayPal;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Payments.PayPal;

/// <summary>
/// <see cref="IPayPalSettingsReader"/> over <c>ops.Settings</c> (doc 08 §3.8): the eleven
/// <c>Payments.PayPal.*</c> keys and <c>Orders.PayPalExpiryMinutes</c>, in one query by name.
/// <para>
/// <b>The only place the client secret is decrypted</b>, and the result is held only in the returned object —
/// <see cref="PayPalSettings"/> redacts it from <c>ToString</c> — for the length of one call. If the stored
/// ciphertext cannot be read (a Data Protection key that was lost, or a value somebody put plaintext into)
/// the secret is simply null, which makes PayPal "not configured" rather than throwing something that could
/// carry the value into a log.
/// </para>
/// <para>
/// <b>Not cached</b>, for the reason <c>BankTransferSettingsReader</c> gives: the read is rare, and the
/// failure a cache would add is the expensive direction — a Super Admin who switches from sandbox to live, or
/// pastes a corrected secret, must not have PayPal keep using the old one for another minute.
/// </para>
/// <para>
/// <b>A number that is missing or not valid takes doc 08's default</b>, so a deleted row or a hand-edited
/// value cannot turn a three-hour lock into zero. The exceptions are the credentials and the rate, which
/// have no default: they are null, and PayPal is not offered.
/// </para>
/// </summary>
internal sealed class PayPalSettingsReader(AppDbContext dbContext, ISettingSecretProtector secrets)
    : IPayPalSettingsReader
{
    private static readonly string[] Keys =
    [
        SettingKeys.PayPal.Mode,
        SettingKeys.PayPal.ClientId,
        SettingKeys.PayPal.ClientSecret,
        SettingKeys.PayPal.WebhookId,
        SettingKeys.PayPal.LkrPerUsd,
        SettingKeys.PayPal.FxBufferPercent,
        SettingKeys.PayPal.RateMaxAgeDays,
        SettingKeys.PayPal.RateUpdatedAtUtc,
        SettingKeys.PayPal.CaptureLockHours,
        SettingKeys.PayPal.CaptureRecoveryMaxHours,
        SettingKeys.PayPal.PendingCaptureMaxDays,
        SettingKeys.PayPal.ExpiryMinutes,
    ];

    public async Task<PayPalSettings> ReadAsync(CancellationToken cancellationToken)
    {
        var rows = await dbContext.Settings
            .AsNoTracking()
            .Where(setting => Keys.Contains(setting.Key))
            .Select(setting => new { setting.Key, setting.Value })
            .ToListAsync(cancellationToken);

        // Case-insensitively, to match Latin1_General_100_CI_AS_SC (see SettingCatalog.ByKey).
        var values = rows.ToDictionary(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase);

        string? Text(string key) =>
            values.GetValueOrDefault(key) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        string? secret = null;

        if (Text(SettingKeys.PayPal.ClientSecret) is { } protectedSecret
            && secrets.TryUnprotect(protectedSecret, out var plaintext)
            && !string.IsNullOrWhiteSpace(plaintext))
        {
            secret = plaintext;
        }

        var rate = Decimal(Text(SettingKeys.PayPal.LkrPerUsd));
        var buffer = Decimal(Text(SettingKeys.PayPal.FxBufferPercent));

        return new PayPalSettings
        {
            Mode = string.Equals(Text(SettingKeys.PayPal.Mode), SettingKeys.PayPal.ModeLive, StringComparison.OrdinalIgnoreCase)
                ? PayPalMode.Live
                : PayPalMode.Sandbox,
            ClientId = Text(SettingKeys.PayPal.ClientId),
            ClientSecret = secret,
            WebhookId = Text(SettingKeys.PayPal.WebhookId),
            LkrPerUsd = rate is > 0m ? rate : null,
            FxBufferPercent = buffer is >= 0m ? buffer.Value : SettingKeys.PayPal.FxBufferPercentDefault,
            RateMaxAgeDays = Whole(
                Text(SettingKeys.PayPal.RateMaxAgeDays),
                SettingKeys.PayPal.RateMaxAgeDaysMin,
                SettingKeys.PayPal.RateMaxAgeDaysMax,
                SettingKeys.PayPal.RateMaxAgeDaysDefault),
            RateUpdatedAtUtc = Instant(Text(SettingKeys.PayPal.RateUpdatedAtUtc)),
            CaptureLockHours = Whole(
                Text(SettingKeys.PayPal.CaptureLockHours),
                SettingKeys.PayPal.CaptureLockHoursMin,
                SettingKeys.PayPal.CaptureLockHoursMax,
                SettingKeys.PayPal.CaptureLockHoursDefault),
            CaptureRecoveryMaxHours = Whole(
                Text(SettingKeys.PayPal.CaptureRecoveryMaxHours),
                SettingKeys.PayPal.CaptureRecoveryMaxHoursMin,
                SettingKeys.PayPal.CaptureRecoveryMaxHoursMax,
                SettingKeys.PayPal.CaptureRecoveryMaxHoursDefault),
            PendingCaptureMaxDays = Whole(
                Text(SettingKeys.PayPal.PendingCaptureMaxDays),
                SettingKeys.PayPal.PendingCaptureMaxDaysMin,
                SettingKeys.PayPal.PendingCaptureMaxDaysMax,
                SettingKeys.PayPal.PendingCaptureMaxDaysDefault),
            ExpiryMinutes = Whole(
                Text(SettingKeys.PayPal.ExpiryMinutes),
                SettingKeys.PayPal.ExpiryMinutesMin,
                SettingKeys.PayPal.ExpiryMinutesMax,
                SettingKeys.PayPal.ExpiryMinutesDefault),
        };
    }

    private static decimal? Decimal(string? text) =>
        text is not null
        && decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static int Whole(string? text, int min, int max, int fallback) =>
        text is not null
        && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value >= min
        && value <= max
            ? value
            : fallback;

    private static DateTime? Instant(string? text) =>
        text is not null
        && DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var value)
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : null;
}
