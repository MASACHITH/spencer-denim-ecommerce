using System.Globalization;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Application.Catalog.Admin;

/// <summary>
/// The field rules for an ADM-11 write, in one place, as pure functions.
/// <para>
/// <b>Why validation lives here and not only on the entity.</b> <see cref="CatalogSlug.Require"/> throws
/// from the property setter, which is the right last line of defence and the wrong first one: binding an
/// admin-typed slug straight onto the entity turns a typo into a 500 with a stack trace instead of a 400
/// naming the field. That is exactly issue <b>#24</b>, raised against W1-03's product editor; W1-12 is
/// the first write path for <c>Category.Slug</c> and <c>Department.Slug</c>, so it is where the same
/// answer is owed for them. The setter guard stays — both guards, in that order.
/// </para>
/// <para>
/// The lengths are doc 08 §3.2's column widths. They are checked here rather than left to SQL Server
/// because a truncation error arrives as a <c>DbUpdateException</c> naming no field, and because the
/// admin screen needs to say which box is too long while the admin is still looking at it.
/// </para>
/// </summary>
public static class CatalogFieldRules
{
    /// <summary><c>catalog.Categories.Name</c> is <c>nvarchar(80)</c>.</summary>
    public const int CategoryNameMaxLength = 80;

    /// <summary><c>catalog.Categories.Slug</c> is <c>varchar(100)</c>.</summary>
    public const int CategorySlugMaxLength = 100;

    /// <summary><c>catalog.Categories.Description</c> is <c>nvarchar(2000)</c>.</summary>
    public const int CategoryDescriptionMaxLength = 2000;

    /// <summary><c>catalog.Departments.Name</c> is <c>nvarchar(60)</c>.</summary>
    public const int DepartmentNameMaxLength = 60;

    /// <summary><c>catalog.Departments.Slug</c> is <c>varchar(60)</c>.</summary>
    public const int DepartmentSlugMaxLength = 60;

    /// <summary><c>catalog.Departments.Description</c> is <c>nvarchar(1000)</c>.</summary>
    public const int DepartmentDescriptionMaxLength = 1000;

    /// <summary><c>BannerLinkUrl</c> is <c>nvarchar(500)</c>.</summary>
    public const int BannerLinkUrlMaxLength = 500;

    /// <summary><c>SeoTitle</c> is <c>nvarchar(160)</c> on both tables.</summary>
    public const int SeoTitleMaxLength = 160;

    /// <summary><c>SeoDescription</c> is <c>nvarchar(320)</c> on both tables.</summary>
    public const int SeoDescriptionMaxLength = 320;

    /// <summary>Doc 08 §3.2: <c>CK_Categories_MegaMenuColumn</c> allows 1–4.</summary>
    public const byte MinMegaMenuColumn = 1;

    /// <inheritdoc cref="MinMegaMenuColumn"/>
    public const byte MaxMegaMenuColumn = 4;

    /// <summary>
    /// Trims a value a person typed, turning blank into <c>null</c> for a nullable column.
    /// <para>
    /// An empty string and NULL are the same thing to an admin who cleared a box, and storing <c>""</c>
    /// would make "has a description" true for a category that has none — the kind of difference that only
    /// shows up as an empty paragraph on a listing page months later.
    /// </para>
    /// </summary>
    public static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Checks a required text field's presence and length, adding at most one error.</summary>
    public static void RequireText(
        ICollection<ValidationError> errors,
        string field,
        string? value,
        int maxLength,
        string label)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var cleaned = Clean(value);
        if (cleaned is null)
        {
            errors.Add(new ValidationError(field, $"{label} is required."));
            return;
        }

        if (cleaned.Length > maxLength)
        {
            errors.Add(new ValidationError(
                field,
                string.Create(CultureInfo.InvariantCulture, $"{label} cannot be longer than {maxLength} characters.")));
        }
    }

    /// <summary>Checks an optional text field's length.</summary>
    public static void LimitText(
        ICollection<ValidationError> errors,
        string field,
        string? value,
        int maxLength,
        string label)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var cleaned = Clean(value);
        if (cleaned is not null && cleaned.Length > maxLength)
        {
            errors.Add(new ValidationError(
                field,
                string.Create(CultureInfo.InvariantCulture, $"{label} cannot be longer than {maxLength} characters.")));
        }
    }

    /// <summary>
    /// The W1-02 follow-up and issue #24, for the two columns that now have a write path: the slug is
    /// checked with <see cref="CatalogSlug.IsValid"/> in the command, so a bad one is a 400 naming the
    /// field instead of an <see cref="ArgumentException"/> out of the property setter.
    /// </summary>
    public static void RequireSlug(ICollection<ValidationError> errors, string field, string? value, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var cleaned = Clean(value);
        if (cleaned is null)
        {
            errors.Add(new ValidationError(field, "The URL slug is required."));
            return;
        }

        if (!CatalogSlug.IsValid(cleaned))
        {
            errors.Add(new ValidationError(
                field,
                "The URL slug may contain only lower-case letters, digits and single hyphens between them, " +
                "for example 'denim-jackets'."));
            return;
        }

        if (cleaned.Length > maxLength)
        {
            errors.Add(new ValidationError(
                field,
                string.Create(CultureInfo.InvariantCulture, $"The URL slug cannot be longer than {maxLength} characters.")));
        }
    }

    /// <summary>
    /// A banner link an admin typed: either a path on this site or an absolute <c>http</c>/<c>https</c>
    /// URL, and nothing else.
    /// <para>
    /// The point is <c>javascript:</c> and <c>data:</c>. Nothing projects this column to the storefront
    /// today, so the value is inert — which is exactly why the rule belongs here now rather than in
    /// whichever task first renders it, by which time the rows are already in the database. It is also the
    /// only field on these two tables that a browser could ever be asked to follow; the text fields are
    /// interpolated, never bound to <c>innerHTML</c> (see the report's note on issue #70).
    /// </para>
    /// </summary>
    public static void CheckBannerLink(ICollection<ValidationError> errors, string field, string? value)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var cleaned = Clean(value);
        if (cleaned is null)
        {
            return;
        }

        if (cleaned.Length > BannerLinkUrlMaxLength)
        {
            errors.Add(new ValidationError(
                field,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The banner link cannot be longer than {BannerLinkUrlMaxLength} characters.")));
            return;
        }

        // A path on this site. "//host" is not one: a protocol-relative URL leaves the site.
        if (cleaned.StartsWith('/') && !cleaned.StartsWith("//", StringComparison.Ordinal))
        {
            return;
        }

        if (Uri.TryCreate(cleaned, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return;
        }

        errors.Add(new ValidationError(
            field,
            "The banner link must be a path on this site starting with '/', or a full http:// or https:// address."));
    }

    /// <summary>Doc 08 §3.2's <c>CK_Categories_MegaMenuColumn</c>, as a named field error.</summary>
    public static void CheckMegaMenuColumn(ICollection<ValidationError> errors, string field, byte? value)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (value is { } column && (column < MinMegaMenuColumn || column > MaxMegaMenuColumn))
        {
            errors.Add(new ValidationError(
                field,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The mega-menu column must be between {MinMegaMenuColumn} and {MaxMegaMenuColumn}.")));
        }
    }

    /// <summary>
    /// Reads the base64 row version a client sent back, or <c>null</c> when it is missing or not base64.
    /// <para>
    /// A malformed token is treated as "no token", which the callers refuse with
    /// <see cref="CatalogConflictCodes.StaleRowVersion"/> — the same answer as a stale one, because from
    /// the caller's point of view both mean "fetch the row again". It is never an exception: an admin
    /// screen on an old build is a client bug, not a server fault.
    /// </para>
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
