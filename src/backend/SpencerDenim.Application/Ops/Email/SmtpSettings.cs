namespace SpencerDenim.Application.Ops.Email;

/// <summary>
/// One complete mail account: everything <c>IEmailTransport</c> needs to hand a message to a server.
/// <para>
/// <b>Built in exactly three places</b>: from the <c>Email.Smtp</c> settings rows (<see cref="SmtpSettingsResolver"/>),
/// from <c>Email__Smtp__*</c> configuration (<see cref="FromOptions"/>), and from the "Send test email" form. It is
/// immutable, so the account a sweep read is the account every message in that sweep uses.
/// </para>
/// <para>
/// <b><see cref="ToString"/> never prints the password</b>, which a positional record would: a record's generated
/// <c>ToString</c> lists every property, and one stray <c>{Settings}</c> in a log template would write the mail
/// password to a file Docker keeps. This is a class with its own <c>ToString</c> for that reason.
/// </para>
/// </summary>
public sealed class SmtpSettings
{
    /// <summary>What <see cref="ToString"/> prints instead of a password.</summary>
    public const string RedactedPassword = "[redacted]";

    public SmtpSettings(
        string host,
        int port,
        string? username,
        string? password,
        string fromAddress,
        string? fromName,
        bool useStartTls)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromAddress);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        Host = host;
        Port = port;
        Username = string.IsNullOrEmpty(username) ? null : username;
        Password = string.IsNullOrEmpty(password) ? null : password;
        FromAddress = fromAddress;
        FromName = string.IsNullOrEmpty(fromName) ? null : fromName;
        UseStartTls = useStartTls;
    }

    public string Host { get; }

    public int Port { get; }

    /// <summary>Null for a relay that authenticates by address rather than by credentials.</summary>
    public string? Username { get; }

    /// <summary>The password in plain text, for the length of one send. Never logged, never audited.</summary>
    public string? Password { get; }

    public string FromAddress { get; }

    public string? FromName { get; }

    public bool UseStartTls { get; }

    /// <summary>
    /// The configured account, or <see langword="null"/> when configuration does not hold enough to send
    /// (<see cref="SmtpOptions.IsConfigured"/>). The configured values are taken as they are — the same values the
    /// transport used before the settings group existed — so a deployment's mail does not change by this release.
    /// </summary>
    public static SmtpSettings? FromOptions(SmtpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.IsConfigured || options.Port is < 1 or > 65535)
        {
            return null;
        }

        return new SmtpSettings(
            options.Host!.Trim(),
            options.Port,
            options.Username,
            options.Password,
            options.FromAddress!.Trim(),
            options.FromName,
            options.UseStartTls);
    }

    /// <summary>Whether two accounts would send the same way. Compares the password by value, ordinally.</summary>
    public bool SameAccountAs(SmtpSettings? other) =>
        other is not null
        && string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase)
        && Port == other.Port
        && string.Equals(Username, other.Username, StringComparison.Ordinal)
        && string.Equals(Password, other.Password, StringComparison.Ordinal)
        && string.Equals(FromAddress, other.FromAddress, StringComparison.OrdinalIgnoreCase)
        && string.Equals(FromName, other.FromName, StringComparison.Ordinal)
        && UseStartTls == other.UseStartTls;

    /// <summary>Host, port, user and sender — and <see cref="RedactedPassword"/> in place of any password.</summary>
    public override string ToString() =>
        $"SmtpSettings {{ Host = {Host}, Port = {Port}, Username = {Username ?? "(none)"}, "
        + $"Password = {(Password is null ? "(none)" : RedactedPassword)}, FromAddress = {FromAddress}, "
        + $"FromName = {FromName ?? "(none)"}, UseStartTls = {UseStartTls} }}";
}

/// <summary>Where the account a sender uses came from.</summary>
public enum SmtpSettingsSource
{
    /// <summary>Neither the settings rows nor configuration make a usable account. Nothing can be sent.</summary>
    None = 0,

    /// <summary>The <c>Email.Smtp</c> rows a Super Admin saved (they win when complete and valid).</summary>
    Settings = 1,

    /// <summary><c>Email__Smtp__*</c>, the fallback — what every deployment used before the settings group.</summary>
    Configuration = 2,
}

/// <summary>The account to send through now, where it came from, and why the rows were passed over if they were.</summary>
/// <param name="Settings">The account, or <see langword="null"/> when <paramref name="Source"/> is <c>None</c>.</param>
/// <param name="Source">Settings, Configuration or None.</param>
/// <param name="IgnoredStoredReason">
/// When the rows hold a server name but were not used, a sentence saying why — never a value. Null otherwise.
/// </param>
public sealed record EffectiveSmtpSettings(
    SmtpSettings? Settings,
    SmtpSettingsSource Source,
    string? IgnoredStoredReason = null)
{
    /// <summary>Nothing to send through.</summary>
    public static EffectiveSmtpSettings None { get; } = new(null, SmtpSettingsSource.None);

    public bool IsConfigured => Settings is not null;
}
