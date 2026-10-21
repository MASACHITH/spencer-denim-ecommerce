using System.Globalization;
using System.Net;
using System.Net.Mail;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Application.Ops.Email;

/// <summary>
/// The rules for an <c>Email.Smtp</c> value, used by three callers that must agree: the save
/// (<c>UpdateSmtpSettingsCommandHandler</c>), the test send, and the reader that decides whether stored rows make a
/// usable account (<see cref="SmtpSettingsResolver"/>). One set of rules, so a value the form accepts is a value the
/// sender will use, and a row edited by hand into something the form would refuse is passed over rather than used.
/// <para>
/// <b>Every free-text field refuses control characters</b>, carriage return and line feed above all. The from name
/// and the user name end up in protocol lines and mail headers; a line break inside one is header injection, and
/// <c>MailAddress</c> refusing it at send time would be a dead email rather than a named field on the form.
/// </para>
/// </summary>
public static class SmtpSettingRules
{
    /// <summary>Whether <paramref name="host"/> is a host name or an IP address, with no scheme, port, path or space.</summary>
    public static bool IsValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > SettingKeys.Smtp.HostMaxLength)
        {
            return false;
        }

        if (IPAddress.TryParse(host, out _))
        {
            // An IPv6 literal carries colons, which the name check below refuses; an address is a fine target.
            return true;
        }

        // Uri.CheckHostName rejects "smtp.example.com:587", "https://…", spaces and "/", and accepts a DNS name.
        return Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    /// <summary>The port as a number, or <see langword="null"/> when it is not whole invariant digits in 1–65535.</summary>
    public static int? ParsePort(string? text) =>
        text is not null
        && int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
        && port is >= 1 and <= 65535
            ? port
            : null;

    /// <summary>A stored boolean (<c>true</c>/<c>false</c>, any case), or <see langword="null"/> when it is neither.</summary>
    public static bool? ParseBool(string? text) =>
        bool.TryParse(text?.Trim(), out var value) ? value : null;

    /// <summary>Whether <paramref name="address"/> is a bare mailbox address a server can send from.</summary>
    public static bool IsValidAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)
            || address.Length > SettingKeys.Smtp.FromAddressMaxLength
            || HasControlCharacter(address)
            || address.Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        // "Shop <shop@x>" parses too, with a display name; only the bare address is a from *address*.
        return MailAddress.TryCreate(address, out var parsed)
            && string.Equals(parsed.Address, address, StringComparison.Ordinal)
            && address.Contains('@', StringComparison.Ordinal);
    }

    /// <summary>Whether a free-text value (from name, user name, password) is free of control characters and within <paramref name="max"/>.</summary>
    public static bool IsPlainLine(string? value, int max) =>
        value is null || (value.Length <= max && !HasControlCharacter(value));

    /// <summary>Whether <paramref name="value"/> holds a control character (C0, DEL, C1, or U+2028/U+2029).</summary>
    public static bool HasControlCharacter(string value) =>
        value.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029');

    /// <summary>Trims, and treats whitespace as absent.</summary>
    public static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
