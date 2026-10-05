using System.Globalization;
using SpencerDenim.Application.Abstractions.Results;

namespace SpencerDenim.Application.Settings;

/// <summary>
/// The field rules for an ADM-22 write, as pure functions — the shape
/// <c>SpencerDenim.Application.Catalog.Admin.CatalogFieldRules</c> established in W1-12.
/// <para>
/// <b>Why not reuse <c>CheckBannerLink</c>.</b> It accepts a path on this site <i>or</i> an absolute
/// address, because a banner link usually stays on the site. A social profile address is the opposite
/// case: it is <b>always</b> absolute and <b>always</b> leaves the site, so accepting <c>/women/jeans</c>
/// would store a value the footer renders as a social icon that goes to a listing page. Different
/// question, different rule — but the same hard-won lesson about how to ask it.
/// </para>
/// </summary>
public static class SettingFieldRules
{
    /// <summary>
    /// How long a social profile address may be. Doc 08 §3.8 gives <c>Value</c> as <c>nvarchar(max)</c>,
    /// which is not a limit anybody should rely on: a URL a person can paste out of a browser is well
    /// under 2,048 characters, and an unbounded text field reachable from a form is a field somebody will
    /// eventually put a megabyte in.
    /// </summary>
    public const int UrlMaxLength = 2_048;

    // A `WhatsAppMaxLength = 20` stood here, documented as "extra room so an over-long value gets the shape
    // message". Nothing ever read it, so that behaviour did not happen — a 20-character value is refused by
    // the digit bounds below like any other. Removed rather than wired up: this field has one message, and a
    // constant promising a second one is worse than no constant at all.

    /// <summary>E.164's own limit, which is what the digits are actually checked against.</summary>
    public const int WhatsAppMaxDigits = 15;

    /// <summary>
    /// The shortest number worth dialling. Seven is the shortest national significant number in use;
    /// below that the value is a typo, and a <c>wa.me</c> link built from it is a dead link on every page
    /// of the shop.
    /// </summary>
    public const int WhatsAppMinDigits = 7;

    /// <summary>
    /// Trims what a person typed and turns blank into <see langword="null"/>.
    /// <para>
    /// Blank <i>is</i> the clear action here, and it is the one a client will use most: "we have no TikTok
    /// account" and "the box is empty" are the same statement. Storing <c>""</c> instead would make
    /// "has a TikTok URL" true for a shop that has none, and the footer would render an icon pointing at
    /// nothing — the exact defect this task exists to stop.
    /// </para>
    /// </summary>
    public static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// An absolute external profile address, <b>normalised</b>, or nothing at all.
    /// <para>
    /// <b>This is an allow-list, written as one.</b> W1-12's banner-link validator was defeated three
    /// times by values that <i>looked</i> like they started with a slash — <c>/\host</c>, a tab-split
    /// value and a newline-split one — because it decided what to <i>reject</i>. The lesson generalises
    /// even though the rule does not: decide what you accept.
    /// </para>
    /// <para>
    /// <b>And the accepted value is rewritten rather than stored as typed, which reverses this method's
    /// first version.</b> That version kept the raw text, reasoning that round-tripping through
    /// <see cref="Uri"/> "would show a client something other than what they pasted". The reasoning was
    /// backwards and it left a homograph hole: <c>https://instagram.com</c>U+3002<c>evil.example/</c> parsed
    /// fine, and both .NET's <see cref="Uri.IdnHost"/> and Chromium map U+3002 (IDEOGRAPHIC FULL STOP) to an
    /// ordinary dot — so the <i>stored</i> text read <c>instagram.com</c> while the link went to
    /// <c>evil.example</c>. Showing a client the normalised form is better: it shows them where the link
    /// actually goes.
    /// </para>
    /// <para>
    /// <b><see cref="Uri.AbsoluteUri"/> alone does not do it.</b> Measured, not assumed: it lower-cases the
    /// host, drops a default port and fills an empty path, and it leaves the host's Unicode exactly as
    /// typed. The ASCII host has to be substituted deliberately.
    /// </para>
    /// <para>What is accepted:</para>
    /// <list type="number">
    /// <item>no backslash, control character or whitespace anywhere — a browser deletes TAB, CR and LF
    /// before it parses a URL and reads <c>\</c> as <c>/</c> for a special scheme, so a value containing
    /// either is not the value this method is looking at;</item>
    /// <item>it parses as <b>absolute</b>;</item>
    /// <item>the scheme is exactly <c>http</c> or <c>https</c> — which is what refuses <c>javascript:</c>,
    /// <c>data:</c>, <c>vbscript:</c>, <c>file:</c> and every scheme nobody has thought of yet;</item>
    /// <item>no credentials: <c>https://instagram.com@evil.example/</c> is a valid URL whose host is
    /// <c>evil.example</c>, so it reads as one destination and goes to another;</item>
    /// <item><see cref="Uri.IdnHost"/> can be computed. It <b>throws</b> for a host containing U+FF20
    /// (FULLWIDTH COMMERCIAL AT) or U+FF0F (FULLWIDTH SOLIDUS) — both of which the raw-value version
    /// accepted and stored, and both of which then threw in the browser, so the client got a 204, no error
    /// anywhere, and an icon that never appeared;</item>
    /// <item>and that ASCII host is a plausible hostname (see <see cref="IsPlausibleAsciiHost"/>).</item>
    /// </list>
    /// <para>
    /// <b>The empty-host case is handled by the parse itself.</b> An earlier comment here claimed .NET gives
    /// <c>http:///profile</c> an empty host; it does not — <c>Uri.TryCreate</c> returns
    /// <see langword="false"/> for it outright. Measured, after the same claim turned out to be wrong on the
    /// front end as well.
    /// </para>
    /// </summary>
    /// <param name="value">The value as a person typed it.</param>
    /// <param name="normalised">
    /// The form to store and to publish: ASCII host, lower-cased, default port dropped, path filled in.
    /// </param>
    public static bool TryNormaliseProfileUrl(string? value, out string normalised)
    {
        normalised = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();

        if (candidate.Length > UrlMaxLength)
        {
            return false;
        }

        foreach (var character in candidate)
        {
            if (character == '\\' || char.IsControl(character) || char.IsWhiteSpace(character))
            {
                return false;
            }
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
        {
            return false;
        }

        if (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(absolute.UserInfo))
        {
            return false;
        }

        string ascii;
        try
        {
            ascii = absolute.IdnHost;
        }
        catch (UriFormatException)
        {
            // A host .NET cannot map to IDN. The browser cannot either, so the alternative to refusing it
            // here is a stored value that silently never renders and never reports anything.
            return false;
        }

        if (!IsPlausibleAsciiHost(ascii))
        {
            return false;
        }

        var builder = new UriBuilder(absolute) { Host = ascii };
        if (absolute.IsDefaultPort)
        {
            // UriBuilder writes the default port back out explicitly unless it is cleared.
            builder.Port = -1;
        }

        normalised = builder.Uri.AbsoluteUri;

        return normalised.Length <= UrlMaxLength;
    }

    /// <summary>
    /// At least two non-empty labels of <c>a-z</c>, <c>0-9</c> and <c>-</c>, none starting or ending with a
    /// hyphen.
    /// <para>
    /// A single label with no dot is refused: <c>https://intranet/profile</c> is a valid URL and is not a
    /// public social profile, and nothing a client pastes out of a browser looks like that. Empty labels are
    /// refused for the same reason — <c>example..com</c> is not a host anybody meant to type.
    /// </para>
    /// </summary>
    private static bool IsPlausibleAsciiHost(string ascii)
    {
        if (ascii.Length == 0 || !ascii.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var label in ascii.Split('.'))
        {
            if (label.Length == 0 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (var character in label)
            {
                var allowed = character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-';
                if (!allowed)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is an acceptable profile address, discarding the normalised form.
    /// <para>
    /// For callers that only have to <i>decide</i>: the tests, and the storefront read model's defensive
    /// re-check. Everything that <b>stores</b> a value uses <see cref="TryNormaliseProfileUrl"/>, so one
    /// implementation governs both and they cannot drift.
    /// </para>
    /// </summary>
    public static bool IsAcceptableProfileUrl(string? value) => TryNormaliseProfileUrl(value, out _);

    /// <summary>
    /// Adds a named field error unless <paramref name="value"/> is blank or an acceptable profile address.
    /// </summary>
    public static void CheckProfileUrl(ICollection<ValidationError> errors, string field, string? value, string label)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var cleaned = Clean(value);
        if (cleaned is null)
        {
            return;
        }

        if (cleaned.Length > UrlMaxLength)
        {
            errors.Add(new ValidationError(
                field,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The {label} address cannot be longer than {UrlMaxLength} characters.")));
            return;
        }

        if (TryNormaliseProfileUrl(cleaned, out _))
        {
            return;
        }

        errors.Add(new ValidationError(
            field,
            $"The {label} address must be a full web address starting with https:// — for example "
            + "https://www.instagram.com/yourshop. Leave the box empty to hide the icon."));
    }

    /// <summary>
    /// A WhatsApp number: digits only, in international form, or nothing at all.
    /// <para>
    /// Digits only because that is the form <c>wa.me/&lt;number&gt;</c> takes, and because every other
    /// form a person types is ambiguous about the country code in a way nothing downstream can resolve. A
    /// leading zero is refused for the same reason: it is the giveaway that somebody typed a national
    /// number, and a <c>wa.me</c> link built from one is a link to nobody.
    /// </para>
    /// </summary>
    public static void CheckWhatsAppNumber(ICollection<ValidationError> errors, string field, string? value)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var cleaned = Clean(value);
        if (cleaned is null)
        {
            return;
        }

        if (!IsAcceptableWhatsAppNumber(cleaned))
        {
            errors.Add(new ValidationError(field, WhatsAppMessage));
        }
    }

    /// <summary>
    /// The one message a bad WhatsApp number gets, built once from the two bounds.
    /// <para>
    /// A constant rather than an interpolation at the call site: both bounds are compile-time constants, so
    /// there is nothing to format at run time, and the digits in the example have to stay in step with them.
    /// </para>
    /// </summary>
    private static readonly string WhatsAppMessage = string.Create(
        CultureInfo.InvariantCulture,
        $"The WhatsApp number must be {WhatsAppMinDigits} to {WhatsAppMaxDigits} digits in international form, with no plus sign, spaces or leading zero — for example 94771234567.");

    /// <summary>Whether <paramref name="value"/> is digits only, in international form.</summary>
    public static bool IsAcceptableWhatsAppNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();

        if (candidate.Length is < WhatsAppMinDigits or > WhatsAppMaxDigits)
        {
            return false;
        }

        // `char.IsDigit` is true for every Unicode decimal digit, including Devanagari and the fullwidth
        // forms, which would reach `wa.me` percent-escaped. ASCII only, deliberately.
        foreach (var character in candidate)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return candidate[0] != '0';
    }

    /// <summary>
    /// Reads the base64 row version a client sent back, or <see langword="null"/> when it is missing or
    /// not base64 — the same treatment, and for the same reason, as
    /// <c>CatalogFieldRules.ReadRowVersion</c>: a malformed token means "fetch the row again", never an
    /// exception.
    /// </summary>
    public static byte[]? ReadRowVersion(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[64];
        return Convert.TryFromBase64String(base64, buffer, out var written) && written > 0
            ? buffer[..written].ToArray()
            : null;
    }
}
