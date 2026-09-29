using System.Globalization;
using System.Text;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// Normalises what a person types for a 2FA code (W1-10) before Identity compares it, and formats the key they
/// type into an authenticator app by hand. Identity compares both kinds of code as exact strings.
/// </summary>
internal static class TwoFactorCodeFormat
{
    /// <summary>
    /// Authenticator codes: apps often show "123 456", and phones paste "123-456". Only spaces and hyphens are
    /// dropped; any other stray character is left in, so it makes the code wrong instead of being quietly removed.
    /// </summary>
    public static string NormaliseAuthenticatorCode(string code) =>
        new([.. code.Where(c => !char.IsWhiteSpace(c) && c != '-')]);

    /// <summary>
    /// Recovery codes are issued in upper case with one hyphen (<c>XXXXX-XXXXX</c>); spaces are dropped and case is
    /// ignored, the hyphen is kept because it is part of the stored code.
    /// </summary>
    public static string NormaliseRecoveryCode(string code) =>
        new string([.. code.Where(c => !char.IsWhiteSpace(c))]).ToUpperInvariant();

    /// <summary>The key in lower-case groups of four, the way authenticator apps ask for it to be typed.</summary>
    public static string FormatSharedKey(string unformattedKey)
    {
        var result = new StringBuilder();
        for (var position = 0; position < unformattedKey.Length; position += 4)
        {
            if (position > 0)
            {
                result.Append(' ');
            }

            result.Append(unformattedKey.AsSpan(position, Math.Min(4, unformattedKey.Length - position)));
        }

        return result.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// The <c>otpauth://totp/</c> URI authenticator apps read from the QR code (Key Uri Format): the label is
    /// "issuer:account", and the issuer is repeated as a parameter for apps that only read that.
    /// </summary>
    public static string BuildAuthenticatorUri(string issuer, string accountName, string unformattedKey) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(accountName)}?secret={unformattedKey}&issuer={Uri.EscapeDataString(issuer)}&digits=6");
}
