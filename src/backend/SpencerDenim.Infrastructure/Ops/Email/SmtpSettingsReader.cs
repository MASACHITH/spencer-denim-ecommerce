using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Ops.Email;

/// <summary>
/// <see cref="ISmtpSettingsReader"/> over <c>ops.Settings</c> and <c>Email__Smtp__*</c>: one query by key for the seven
/// <c>Email.Smtp.*</c> rows, the password decrypted, then <see cref="SmtpSettingsResolver"/> decides.
/// <para>
/// <b>The only place the stored mail password is decrypted for sending.</b> The plaintext lives in the returned
/// <see cref="SmtpSettings"/>, whose <c>ToString</c> redacts it, for the length of one sweep. A ciphertext that cannot
/// be read makes the stored account unusable (configuration is used instead) rather than throwing something that
/// could carry the value into a log.
/// </para>
/// <para>
/// <b>Not cached</b>, for <c>PayPalSettingsReader</c>'s reason: a Super Admin who corrects the account must not have the
/// old one used for another minute, and the read is seven rows by primary key every sweep.
/// </para>
/// </summary>
internal sealed class SmtpSettingsReader(
    AppDbContext dbContext,
    ISettingSecretProtector secrets,
    EmailOptions options) : ISmtpSettingsReader
{
    private static readonly string[] Keys =
    [
        SettingKeys.Smtp.Host,
        SettingKeys.Smtp.Port,
        SettingKeys.Smtp.Username,
        SettingKeys.Smtp.Password,
        SettingKeys.Smtp.FromAddress,
        SettingKeys.Smtp.FromName,
        SettingKeys.Smtp.UseStartTls,
    ];

    public async Task<EffectiveSmtpSettings> ReadEffectiveAsync(CancellationToken cancellationToken) =>
        SmtpSettingsResolver.Resolve(await ReadStoredAsync(cancellationToken), options.Smtp);

    public async Task<string?> ReadStoredPasswordAsync(CancellationToken cancellationToken) =>
        (await ReadStoredAsync(cancellationToken)).Password;

    private async Task<StoredSmtpValues> ReadStoredAsync(CancellationToken cancellationToken)
    {
        var rows = await dbContext.Settings
            .AsNoTracking()
            .Where(setting => Keys.Contains(setting.Key))
            .Select(setting => new { setting.Key, setting.Value, setting.IsSecret })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return StoredSmtpValues.Empty;
        }

        // Case-insensitively, to match Latin1_General_100_CI_AS_SC (see SettingCatalog.ByKey).
        var values = rows.ToDictionary(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase);

        string? Value(string key) => values.GetValueOrDefault(key);

        var protectedPassword = Value(SettingKeys.Smtp.Password);
        string? password = null;
        var unreadable = false;

        if (!string.IsNullOrEmpty(protectedPassword))
        {
            // A password row that is not flagged secret would be plaintext somebody wrote by hand; it is never
            // read as a password, because then the column would be holding the credential in the clear.
            var flaggedSecret = rows.Exists(row =>
                string.Equals(row.Key, SettingKeys.Smtp.Password, StringComparison.OrdinalIgnoreCase) && row.IsSecret);

            if (flaggedSecret && secrets.TryUnprotect(protectedPassword, out var plaintext) && plaintext.Length > 0)
            {
                password = plaintext;
            }
            else
            {
                unreadable = true;
            }
        }

        return new StoredSmtpValues(
            Value(SettingKeys.Smtp.Host),
            Value(SettingKeys.Smtp.Port),
            Value(SettingKeys.Smtp.Username),
            password,
            unreadable,
            Value(SettingKeys.Smtp.FromAddress),
            Value(SettingKeys.Smtp.FromName),
            Value(SettingKeys.Smtp.UseStartTls));
    }
}
