using System.Globalization;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Domain.Common;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.Application.Content.Admin;

/// <summary>
/// The field rules every ADM-17 homepage, announcement and size-guide write shares: plain text, links,
/// positions and schedules. Each check adds a named <see cref="ValidationError"/> and returns the cleaned
/// value, so a handler collects every problem in one pass and the screen can mark each control.
/// <para>
/// <b>Plain text is stored as typed and escaped where it is shown</b> — every one of these fields is
/// rendered by Angular interpolation, never as markup — so the only thing refused here is what has no
/// business in a heading at all: control characters (a pasted line break, a NUL, a bidi override is not
/// one of them, it is a format character and is allowed for Sinhala and Tamil copy).
/// </para>
/// </summary>
public static class ContentFieldRules
{
    /// <summary>The base a site path is resolved against to prove it stays on the site.</summary>
    private static readonly Uri SameSiteBase = new("https://shop.invalid/", UriKind.Absolute);

    /// <summary>
    /// Optional plain text: trimmed, blank read as <see langword="null"/>, bounded, no control characters.
    /// </summary>
    public static string? OptionalText(ICollection<ValidationError> errors, string field, string? value, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var cleaned = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (cleaned is null)
        {
            return null;
        }

        if (cleaned.Length > maxLength)
        {
            errors.Add(new ValidationError(
                field,
                string.Create(CultureInfo.InvariantCulture, $"Keep this to {maxLength} characters or fewer.")));
            return null;
        }

        if (cleaned.Any(char.IsControl))
        {
            errors.Add(new ValidationError(field, "This is a single line of plain text: remove line breaks and tabs."));
            return null;
        }

        return cleaned;
    }

    /// <summary>Required plain text, with the same rules as <see cref="OptionalText"/>.</summary>
    public static string? RequiredText(ICollection<ValidationError> errors, string field, string? value, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new ValidationError(field, "This is required."));
            return null;
        }

        return OptionalText(errors, field, value, maxLength);
    }

    /// <summary>
    /// A link a shopper will follow: a path on this site starting with one <c>/</c>, or — only when
    /// <paramref name="mayLeaveSite"/> — a full <c>http://</c> or <c>https://</c> address with no
    /// credentials in it.
    /// <para>
    /// The same question <c>CatalogFieldRules.CheckBannerLink</c> asks, for the same reasons: a reverse
    /// solidus and a control character are refused before anything is parsed, because a browser treats the
    /// first as <c>/</c> and strips the second, and a site path is <i>resolved</i> against a base so
    /// <c>//evil.example</c> and every shape nobody has thought of yet prove themselves off-site rather than
    /// being guessed at. <c>javascript:</c>, <c>data:</c>, <c>vbscript:</c> and every other scheme fail the
    /// scheme test. The storefront's <c>toLink</c> refuses the same shapes on render; this is the half that
    /// stops them being stored.
    /// </para>
    /// </summary>
    public static string? OptionalLink(
        ICollection<ValidationError> errors,
        string field,
        string? value,
        bool mayLeaveSite)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var cleaned = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (cleaned is null)
        {
            return null;
        }

        if (cleaned.Length > ContentFieldLimits.LinkUrlMaxLength)
        {
            errors.Add(new ValidationError(
                field,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A link cannot be longer than {ContentFieldLimits.LinkUrlMaxLength} characters.")));
            return null;
        }

        if (IsSitePath(cleaned) || (mayLeaveSite && IsWebAddress(cleaned)))
        {
            return cleaned;
        }

        errors.Add(new ValidationError(
            field,
            mayLeaveSite
                ? "Use a path on this site starting with '/', or a full https:// address."
                : "Use a path on this site starting with '/', such as /women/dresses."));
        return null;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a path on this site: one leading <c>/</c>, no reverse solidus,
    /// no control or whitespace character, and resolving it leaves the origin unchanged.
    /// </summary>
    public static bool IsSitePath(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!value.StartsWith('/') || HasUnsafeCharacter(value))
        {
            return false;
        }

        return Uri.TryCreate(SameSiteBase, value, out var resolved)
            && resolved.GetLeftPart(UriPartial.Authority) == SameSiteBase.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>Whether <paramref name="value"/> is an absolute <c>http(s)</c> address with no credentials.</summary>
    public static bool IsWebAddress(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return !HasUnsafeCharacter(value)
            && Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
            && string.IsNullOrEmpty(absolute.UserInfo)
            && absolute.Host.Length > 0;
    }

    /// <summary>A position between 0 and <see cref="ContentFieldLimits.MaxSortOrder"/>.</summary>
    public static short SortOrder(ICollection<ValidationError> errors, string field, int? value)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (value is null)
        {
            errors.Add(new ValidationError(field, "Give this a position."));
            return 0;
        }

        if (value < 0 || value > ContentFieldLimits.MaxSortOrder)
        {
            errors.Add(new ValidationError(
                field,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The position must be between 0 and {ContentFieldLimits.MaxSortOrder}.")));
            return 0;
        }

        return (short)value.Value;
    }

    /// <summary>
    /// A schedule typed in the shop's own time, as UTC instants. The end must come after the start when
    /// both are given; either may be left open.
    /// </summary>
    /// <param name="errors">Where a problem is recorded, against <paramref name="endsField"/>.</param>
    /// <param name="endsField">The end's field name.</param>
    /// <param name="startsLocal">Colombo wall-clock, or null.</param>
    /// <param name="endsLocal">Colombo wall-clock, or null.</param>
    public static (DateTime? StartsAtUtc, DateTime? EndsAtUtc) Schedule(
        ICollection<ValidationError> errors,
        string endsField,
        DateTime? startsLocal,
        DateTime? endsLocal)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var starts = ToUtc(startsLocal);
        var ends = ToUtc(endsLocal);

        if (starts is { } start && ends is { } end && end <= start)
        {
            errors.Add(new ValidationError(endsField, "The end must be after the start."));
        }

        return (starts, ends);
    }

    /// <summary>A Colombo wall-clock value as the UTC instant it names, to the minute.</summary>
    public static DateTime? ToUtc(DateTime? colomboWallClock) =>
        colomboWallClock is { } local
            ? new DateTimeOffset(
                    DateTime.SpecifyKind(TruncateToMinute(local), DateTimeKind.Unspecified),
                    ColomboTime.Offset)
                .UtcDateTime
            : null;

    /// <summary>A UTC instant as the Colombo wall-clock string an admin form's <c>datetime-local</c> shows.</summary>
    public static string? ToColomboText(DateTime? utc) =>
        utc is { } instant
            ? ColomboTime.ToLocal(new DateTimeOffset(DateTime.SpecifyKind(instant, DateTimeKind.Utc)))
                .ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture)
            : null;

    private static DateTime TruncateToMinute(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, DateTimeKind.Unspecified);

    private static bool HasUnsafeCharacter(string value) =>
        value.Any(character => character == '\\' || char.IsControl(character) || char.IsWhiteSpace(character));
}
