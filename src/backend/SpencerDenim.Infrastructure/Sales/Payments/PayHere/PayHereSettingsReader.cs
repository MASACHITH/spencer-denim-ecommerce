using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Sales.Payments.PayHere;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Payments.PayHere;

/// <summary>
/// The five PayHere rows of <c>ops.Settings</c> in one query, with the merchant secret decrypted (doc 11 P8b,
/// doc 08 §3.8).
/// <para>
/// <b>Five keys by name</b>, in the shape <c>BankTransferSettingsReader</c> established. Unlike that reader
/// this one <i>does</i> select a secret row — it has to, the secret is what signs every form and every
/// notification — and that is why the type is internal, is not a port a storefront endpoint can reach, and
/// returns a <see cref="PayHereSettings"/> whose <c>ToString</c> never prints it.
/// </para>
/// <para>
/// <b>Not cached</b> (see <see cref="IPayHereSettingsReader"/>): a secret that was just rotated must stop
/// working at the next request. <b>A value that cannot be read is the default, not an error</b>: a stored
/// number outside its bounds, a mode that is neither sandbox nor live, a secret that no key in the ring can
/// decrypt — each is treated as the state a fresh deployment is in, which is "not configured" for the ones
/// that matter, so a corrupt row degrades to PayHere being hidden rather than to a 500 at checkout.
/// </para>
/// </summary>
internal sealed class PayHereSettingsReader(
    AppDbContext dbContext,
    ISettingSecretProtector protector) : IPayHereSettingsReader
{
    private static readonly string[] Keys =
    [
        SettingKeys.PayHere.Mode,
        SettingKeys.PayHere.MerchantId,
        SettingKeys.PayHere.MerchantSecret,
        SettingKeys.PayHere.PendingMaxDays,
        SettingKeys.PayHere.ExpiryMinutes,
    ];

    public async Task<PayHereSettings> ReadAsync(CancellationToken cancellationToken)
    {
        var rows = await dbContext.Settings
            .AsNoTracking()
            .Where(setting => Keys.Contains(setting.Key))
            .Select(setting => new { setting.Key, setting.Value })
            .ToListAsync(cancellationToken);

        // Case-insensitively, to match Latin1_General_100_CI_AS_SC (see BankTransferSettingsReader).
        var values = rows.ToDictionary(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase);

        string? secret = null;

        if (values.GetValueOrDefault(SettingKeys.PayHere.MerchantSecret) is { Length: > 0 } stored
            && protector.TryUnprotect(stored, out var plaintext)
            && plaintext.Length > 0)
        {
            secret = plaintext;
        }

        return new PayHereSettings(
            PayHereSettings.NormaliseMode(values.GetValueOrDefault(SettingKeys.PayHere.Mode)),
            SettingsValue(values, SettingKeys.PayHere.MerchantId),
            secret,
            WholeNumber(
                values,
                SettingKeys.PayHere.PendingMaxDays,
                SettingKeys.PayHere.PendingMaxDaysDefault,
                SettingKeys.PayHere.PendingMaxDaysMin,
                SettingKeys.PayHere.PendingMaxDaysMax),
            WholeNumber(
                values,
                SettingKeys.PayHere.ExpiryMinutes,
                SettingKeys.PayHere.ExpiryMinutesDefault,
                SettingKeys.PayHere.ExpiryMinutesMin,
                SettingKeys.PayHere.ExpiryMinutesMax));
    }

    private static string? SettingsValue(Dictionary<string, string?> values, string key) =>
        values.GetValueOrDefault(key) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static int WholeNumber(Dictionary<string, string?> values, string key, int fallback, int min, int max) =>
        int.TryParse(
            values.GetValueOrDefault(key),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var parsed)
        && parsed >= min
        && parsed <= max
            ? parsed
            : fallback;
}

/// <summary>Whether the <c>PayHere</c> row of <c>sales.PaymentProviders</c> is switched on.</summary>
internal sealed class PayHereProviderSwitch(AppDbContext dbContext) : IPayHereProviderSwitch
{
    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken) =>
        await dbContext.PaymentProviders
            .AsNoTracking()
            .AnyAsync(
                provider => provider.Code == PaymentProviderCodes.PayHere && provider.IsEnabled,
                cancellationToken);
}
