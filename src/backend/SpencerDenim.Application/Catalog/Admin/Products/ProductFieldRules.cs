using System.Globalization;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Application.Catalog.Admin.Products;

/// <summary>
/// The field rules for an ADM-10 write, as pure functions, next to <see cref="CatalogFieldRules"/> and for
/// the same reasons: a bad value has to be a 400 naming the box the admin is looking at, not an
/// <see cref="ArgumentException"/> out of a property setter (issue <b>#24</b>, raised against exactly this
/// task) and not a <c>DbUpdateException</c> naming a constraint.
/// <para>
/// The lengths are doc 08 §3.2's column widths.
/// </para>
/// </summary>
public static class ProductFieldRules
{
    /// <summary><c>catalog.Products.ProductCode</c> is <c>varchar(40)</c>.</summary>
    public const int ProductCodeMaxLength = 40;

    /// <summary><c>catalog.Products.Name</c> is <c>nvarchar(200)</c>.</summary>
    public const int NameMaxLength = 200;

    /// <summary><c>catalog.Products.Slug</c> is <c>varchar(220)</c>.</summary>
    public const int SlugMaxLength = 220;

    /// <summary><c>catalog.Products.ShortDescription</c> is <c>nvarchar(500)</c>.</summary>
    public const int ShortDescriptionMaxLength = 500;

    /// <summary>
    /// <c>catalog.Products.Description</c> is <c>nvarchar(max)</c>, so this bound is ours rather than the
    /// column's.
    /// <para>
    /// A column with no length is not a reason to accept a body with no length: the field is a product
    /// description typed into a box, and without a bound one request can put a megabyte into a row that
    /// every product page then renders. 20,000 characters is roughly ten pages of prose.
    /// </para>
    /// </summary>
    public const int DescriptionMaxLength = 20_000;

    /// <summary><c>catalog.Products.CareInstructions</c> is <c>nvarchar(1000)</c>.</summary>
    public const int CareInstructionsMaxLength = 1000;

    /// <summary><c>catalog.ProductColours.Name</c> is <c>nvarchar(60)</c>.</summary>
    public const int ColourNameMaxLength = 60;

    /// <summary><c>catalog.ProductVariants.Sku</c> is <c>varchar(64)</c>.</summary>
    public const int SkuMaxLength = 64;

    /// <summary><c>catalog.ProductVariants.Barcode</c> is <c>varchar(32)</c>.</summary>
    public const int BarcodeMaxLength = 32;

    /// <summary><c>catalog.ProductMedia.AltTextOverride</c> is <c>nvarchar(250)</c>.</summary>
    public const int AltTextMaxLength = 250;

    /// <summary>
    /// The most colourways one product may carry, and the most sizes one variant matrix may cover.
    /// <para>
    /// Neither is a database limit; both are the limit that stops one request becoming an unbounded write.
    /// 40 × 40 is 1,600 variants, which is already far past any fashion product and still a request the
    /// server can answer. Without a bound, a single malformed body inserts as many rows as it likes and the
    /// variant matrix the admin then opens is a page that never finishes rendering.
    /// </para>
    /// </summary>
    public const int MaxColours = 40;

    /// <inheritdoc cref="MaxColours"/>
    public const int MaxVariants = 1_600;

    /// <summary>The most media rows one product may carry: a gallery, a hover image and one video.</summary>
    public const int MaxMedia = 60;

    /// <summary>The most related products a merchandiser may pin (PDP-06 shows four and tops up from the category).</summary>
    public const int MaxRelatedProducts = 12;

    /// <summary>The most extra categories a product may also appear in.</summary>
    public const int MaxExtraCategories = 20;

    /// <summary>
    /// Doc 08 §1.4: money is <c>decimal(18,2)</c>. A price with more decimals would be rounded by SQL
    /// Server, so "5490.999" would be stored as 5491.00 and the admin would never be told.
    /// </summary>
    public const int MoneyDecimals = 2;

    /// <summary>
    /// An upper bound on a price, so a mistyped one is caught here rather than overflowing
    /// <c>decimal(18,2)</c> on the way in. Rs. 10,000,000 is two orders of magnitude above anything in the
    /// catalogue.
    /// </summary>
    public const decimal MaxPrice = 10_000_000m;

    /// <summary>Checks the scalar text fields of the product form.</summary>
    public static void CheckText(ICollection<ValidationError> errors, ProductFormInput form)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(form);

        CatalogFieldRules.RequireText(errors, "productCode", form.ProductCode, ProductCodeMaxLength, "The product code");
        CatalogFieldRules.RequireText(errors, "name", form.Name, NameMaxLength, "The name");
        CatalogFieldRules.RequireSlug(errors, "slug", form.Slug, SlugMaxLength);
        CatalogFieldRules.LimitText(
            errors, "shortDescription", form.ShortDescription, ShortDescriptionMaxLength, "The short description");
        CatalogFieldRules.LimitText(errors, "description", form.Description, DescriptionMaxLength, "The description");
        CatalogFieldRules.LimitText(
            errors, "careInstructions", form.CareInstructions, CareInstructionsMaxLength, "The care instructions");
        CatalogFieldRules.LimitText(errors, "seoTitle", form.SeoTitle, CatalogFieldRules.SeoTitleMaxLength, "The SEO title");
        CatalogFieldRules.LimitText(
            errors,
            "seoDescription",
            form.SeoDescription,
            CatalogFieldRules.SeoDescriptionMaxLength,
            "The SEO description");

        // A product code is a human-typed identifier that appears in a URL-free context only, so it is not
        // held to the slug grammar — but it is varchar, so a non-ASCII character would be stored as '?'.
        var code = CatalogFieldRules.Clean(form.ProductCode);
        if (code is not null && code.Any(character => character > '\u007f'))
        {
            errors.Add(new ValidationError(
                "productCode",
                "The product code is stored as plain ASCII, so it can contain only unaccented letters, "
                + "digits and punctuation."));
        }
    }

    /// <summary>
    /// Doc 08 §3.2's <c>CK_Products_BasePrice</c> and <c>CK_Products_CompareAtGtBase</c>, as named field
    /// errors, plus the scale and bound rules the constraints cannot express.
    /// </summary>
    public static void CheckPrices(
        ICollection<ValidationError> errors,
        decimal basePrice,
        decimal? compareAtPrice)
    {
        ArgumentNullException.ThrowIfNull(errors);

        CheckMoney(errors, "basePrice", basePrice, "The price", mustBePositive: true);

        if (compareAtPrice is { } compareAt)
        {
            CheckMoney(errors, "compareAtPrice", compareAt, "The original price", mustBePositive: true);

            if (compareAt <= basePrice && basePrice > 0m)
            {
                errors.Add(new ValidationError(
                    "compareAtPrice",
                    "The original price has to be higher than the price — that is what makes it a markdown. "
                    + "Leave it empty if the product is not reduced."));
            }
        }
    }

    /// <summary>
    /// A money value that an admin typed: positive where required, within bounds, and with no more than
    /// two decimal places.
    /// </summary>
    public static void CheckMoney(
        ICollection<ValidationError> errors,
        string field,
        decimal value,
        string label,
        bool mustBePositive)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (mustBePositive && value <= 0m)
        {
            errors.Add(new ValidationError(field, $"{label} has to be more than Rs. 0.00."));
            return;
        }

        if (value > MaxPrice)
        {
            errors.Add(new ValidationError(
                field,
                string.Create(CultureInfo.InvariantCulture, $"{label} cannot be more than {MaxPrice:N2}.")));
            return;
        }

        if (decimal.Round(value, MoneyDecimals) != value)
        {
            errors.Add(new ValidationError(
                field,
                $"{label} can have at most two decimal places — rupees and cents."));
        }
    }

    /// <summary>Doc 08 §3.2's <c>CK_Products_BadgeOverride</c>: only the four admin-assignable badges.</summary>
    public static void CheckBadge(ICollection<ValidationError> errors, ProductBadge? badge)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (badge is null)
        {
            return;
        }

        if (badge is not (ProductBadge.New or ProductBadge.Sale or ProductBadge.OnlineExclusive or ProductBadge.BestSeller))
        {
            errors.Add(new ValidationError(
                "badgeOverride",
                "Only New, Sale, Online Exclusive and Best Seller can be assigned by hand. Low Stock is "
                + "worked out from stock at the moment the card is shown, so it cannot be set here."));
        }
    }

    /// <summary>
    /// A swatch colour an admin typed, <c>#rrggbb</c> — the shape
    /// <c>CK_ProductColours_SwatchHex</c> enforces.
    /// </summary>
    public static bool IsSwatchHex(string? value) =>
        value is { Length: 7 }
        && value[0] == '#'
        && value.Skip(1).All(Uri.IsHexDigit);

    /// <summary>
    /// A SKU an admin typed. <c>varchar</c>, so ASCII; no whitespace, because a SKU with a trailing space is
    /// a SKU nobody can search for and a duplicate the unique index will not catch.
    /// </summary>
    public static void CheckSku(ICollection<ValidationError> errors, string field, string? value)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var cleaned = CatalogFieldRules.Clean(value);
        if (cleaned is null)
        {
            errors.Add(new ValidationError(field, "Every variant needs a SKU."));
            return;
        }

        if (cleaned.Length > SkuMaxLength)
        {
            errors.Add(new ValidationError(
                field,
                string.Create(CultureInfo.InvariantCulture, $"A SKU cannot be longer than {SkuMaxLength} characters.")));
            return;
        }

        if (cleaned.Any(character => character > '\u007f' || char.IsWhiteSpace(character)))
        {
            errors.Add(new ValidationError(
                field,
                "A SKU can contain only unaccented letters, digits and punctuation, with no spaces."));
        }
    }

    /// <inheritdoc cref="CheckSku"/>
    public static void CheckBarcode(ICollection<ValidationError> errors, string field, string? value)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var cleaned = CatalogFieldRules.Clean(value);
        if (cleaned is null)
        {
            return;
        }

        if (cleaned.Length > BarcodeMaxLength)
        {
            errors.Add(new ValidationError(
                field,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A barcode cannot be longer than {BarcodeMaxLength} characters.")));
            return;
        }

        if (cleaned.Any(character => character > '\u007f' || char.IsWhiteSpace(character)))
        {
            errors.Add(new ValidationError(
                field,
                "A barcode can contain only unaccented letters, digits and punctuation, with no spaces."));
        }
    }
}
