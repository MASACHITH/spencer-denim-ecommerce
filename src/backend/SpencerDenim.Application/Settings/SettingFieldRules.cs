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

    /// <summary>
    /// How long a WhatsApp number may be before it is refused for length rather than for shape. E.164
    /// allows fifteen digits; the extra room exists so an over-long value gets the shape message.
    /// </summary>
    public const int WhatsAppMaxLength = 20;

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
    /// An absolute external profile address, or nothing at all.
    /// <para>
    /// <b>This is an allow-list, written as one.</b> W1-12's banner-link validator was defeated three
    /// times by values that <i>looked</i> like they started with a slash — <c>/\host</c>, a tab-split
    /// value and a newline-split one — because it decided what to <i>reject</i>. The lesson generalises
    /// even though the rule does not: decide what you accept. What is accepted here is
    /// </para>
    /// <list type="number">
    /// <item>a value with no backslash, no control character and no whitespace anywhere in it — a browser
    /// deletes TAB, CR and LF before it parses a URL, and reads <c>\</c> as <c>/</c> for a special
    /// scheme, so a value containing either is not the value this method is looking at;</item>
    /// <item>that <see cref="Uri.TryCreate(string?,UriKind,out Uri)"/> parses as <b>absolute</b>;</item>
    /// <item>whose scheme is exactly <c>http</c> or <c>https</c> — which is what refuses
    /// <c>javascript:</c>, <c>data:</c>, <c>vbscript:</c>, <c>file:</c> and every scheme nobody has
    /// thought of yet, because they are simply not the two;</item>
    /// <item>that has a host. <c>http:///profile</c> parses as an absolute http URL with an empty host,
    /// and an <c>href</c> built from it resolves against the current page — so the footer's "Instagram"
    /// icon would quietly link back into the shop;</item>
    /// <item>and that carries no credentials. <c>https://instagram.com@evil.example/</c> is a valid URL
    /// and a phishing shape: what a shopper reads is not where the link goes.</item>
    /// </list>
    /// <para>
    /// The value stored is the trimmed original rather than <see cref="Uri.AbsoluteUri"/>: round-tripping
    /// through <see cref="Uri"/> normalises escaping and would show the client something other than what
    /// they pasted. The parse is the check, not a rewrite.
    /// </para>
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

        if (IsAcceptableProfileUrl(cleaned))
        {
            return;
        }

        errors.Add(new ValidationError(
            field,
            $"The {label} address must be a full web address starting with https:// — for example "
            + "https://www.instagram.com/yourshop. Leave the box empty to hide the icon."));
    }

    /// <summary>
    /// The predicate behind <see cref="CheckProfileUrl"/>, exposed so the storefront read model can apply
    /// the <i>same</i> test to a value on its way out.
    /// <para>
    /// <b>Checking twice is the point.</b> A row can hold a value this rule would refuse today — written
    /// by an earlier build, by a migration, by somebody editing the database, or by this rule being
    /// loosened in a future change nobody connected to the footer. The read model is what a browser
    /// actually follows, so it refuses there as well and the link disappears rather than going live. One
    /// predicate, two call sites: they cannot drift.
    /// </para>
    /// </summary>
    public static bool IsAcceptableProfileUrl(string? value)
    {
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

        // An empty host makes the address resolve against whatever page renders it.
        if (string.IsNullOrEmpty(absolute.Host))
        {
            return false;
        }

        // Credentials in the authority read as one destination and go to another.
        return string.IsNullOrEmpty(absolute.UserInfo);
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

    /// <inheritdoc cref="IsAcceptableProfileUrl"/>
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
