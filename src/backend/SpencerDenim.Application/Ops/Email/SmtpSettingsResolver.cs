namespace SpencerDenim.Application.Ops.Email;

/// <summary>
/// The <c>Email.Smtp</c> rows as the reader found them, the password already decrypted.
/// </summary>
/// <param name="Host">Stored server name, or null.</param>
/// <param name="Port">Stored port text, or null.</param>
/// <param name="Username">Stored user name, or null.</param>
/// <param name="Password">The decrypted password, or null when none is stored or it could not be decrypted.</param>
/// <param name="PasswordUnreadable">
/// True when a password <i>is</i> stored but could not be decrypted (a lost Data Protection key, or plaintext
/// somebody put in the column). Such an account is never used: sending with no password would fail at every
/// message, and the configured account is the better guess.
/// </param>
/// <param name="FromAddress">Stored sender, or null.</param>
/// <param name="FromName">Stored sender name, or null.</param>
/// <param name="UseStartTls">Stored switch text, or null.</param>
public sealed record StoredSmtpValues(
    string? Host,
    string? Port,
    string? Username,
    string? Password,
    bool PasswordUnreadable,
    string? FromAddress,
    string? FromName,
    string? UseStartTls)
{
    /// <summary>No rows at all: an unseeded database.</summary>
    public static StoredSmtpValues Empty { get; } = new(null, null, null, null, false, null, null, null);
}

/// <summary>
/// The one place the precedence rule lives (ADM-22, 2026-10-21; docs/15 "Email"): <b>a complete, valid settings
/// account wins; otherwise <c>Email__Smtp__*</c>; otherwise nothing.</b>
/// <para>
/// <b>Why the rows win.</b> They are what a Super Admin sees and can change, so they are what they will believe; a
/// configuration value silently overriding the screen would make the form lie — <c>TrafficRetentionPeriods</c>'
/// argument. <b>Why configuration is the fallback</b> rather than an error: every deployment before this release
/// sent mail from configuration, and an empty or half-written row must not stop a working shop's order emails.
/// </para>
/// <para>
/// <b>All or nothing per source, never a mixture.</b> A stored server with the configured password would be an
/// account nobody configured. If the rows are used, every value comes from the rows.
/// </para>
/// <para>
/// <b>"Valid" is <see cref="SmtpSettingRules"/>, the same rules the form applies.</b> A row the form would refuse —
/// typed in by hand, or left over from a future format — is passed over with a reason (never its value) that the
/// sender logs, instead of being used to send.
/// </para>
/// </summary>
public static class SmtpSettingsResolver
{
    public static EffectiveSmtpSettings Resolve(StoredSmtpValues stored, SmtpOptions configured)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(configured);

        var (fromRows, reason) = FromStored(stored);

        if (fromRows is not null)
        {
            return new EffectiveSmtpSettings(fromRows, SmtpSettingsSource.Settings);
        }

        return SmtpSettings.FromOptions(configured) is { } fromConfiguration
            ? new EffectiveSmtpSettings(fromConfiguration, SmtpSettingsSource.Configuration, reason)
            : new EffectiveSmtpSettings(null, SmtpSettingsSource.None, reason);
    }

    /// <summary>
    /// The stored account, or null with the reason it cannot be used. No server name is the ordinary "not set" and
    /// carries no reason.
    /// </summary>
    internal static (SmtpSettings? Settings, string? Reason) FromStored(StoredSmtpValues stored)
    {
        var host = SmtpSettingRules.Clean(stored.Host);

        if (host is null)
        {
            return (null, null);
        }

        if (!SmtpSettingRules.IsValidHost(host))
        {
            return (null, "the saved mail server is not a valid server name");
        }

        if (SmtpSettingRules.ParsePort(stored.Port) is not { } port)
        {
            return (null, "the saved port is not a whole number between 1 and 65535");
        }

        var fromAddress = SmtpSettingRules.Clean(stored.FromAddress);

        if (!SmtpSettingRules.IsValidAddress(fromAddress))
        {
            return (null, "the saved send-from address is missing or not a valid address");
        }

        var fromName = SmtpSettingRules.Clean(stored.FromName);

        if (!SmtpSettingRules.IsPlainLine(fromName, Domain.Settings.SettingKeys.Smtp.FromNameMaxLength))
        {
            return (null, "the saved send-from name is too long or holds a line break");
        }

        var username = SmtpSettingRules.Clean(stored.Username);

        if (!SmtpSettingRules.IsPlainLine(username, Domain.Settings.SettingKeys.Smtp.UsernameMaxLength))
        {
            return (null, "the saved user name is too long or holds a line break");
        }

        // Absent means the seeded default (on); text that is not a boolean is a row edited by hand.
        var startTls = stored.UseStartTls is null ? true : SmtpSettingRules.ParseBool(stored.UseStartTls);

        if (startTls is not { } useStartTls)
        {
            return (null, "the saved encryption switch is not true or false");
        }

        if (stored.PasswordUnreadable)
        {
            return (null, "the saved password cannot be decrypted on this server");
        }

        if (username is not null && !useStartTls)
        {
            // The form refuses this pair: a password must never cross the network unencrypted.
            return (null, "a user name is saved with encryption switched off");
        }

        return (new SmtpSettings(host, port, username, stored.Password, fromAddress!, fromName, useStartTls), null);
    }
}
