using System.Globalization;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Catalog.Admin.Abstractions;
using SpencerDenim.Application.Catalog.Admin.Products.Abstractions;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Application.Catalog.Admin.Products;

/// <summary>A colourway after trimming, with its swatch asset resolved to an internal id.</summary>
internal sealed record NormalisedColour(
    int? Id,
    string Name,
    int ColourFamilyId,
    string? SwatchHex,
    int? SwatchAssetId,
    short SortOrder);

/// <summary>A variant after trimming.</summary>
internal sealed record NormalisedVariant(
    int? Id,
    string ColourName,
    int SizeId,
    string Sku,
    string? Barcode,
    decimal? PriceOverride,
    decimal? CompareAtPriceOverride,
    bool IsActive,
    short SortOrder);

/// <summary>A media link with its asset resolved to an internal id.</summary>
internal sealed record NormalisedMedia(
    int AssetId,
    string? ColourName,
    ProductMediaRole MediaRole,
    short SortOrder,
    string? AltTextOverride);

/// <summary>
/// Everything a validated product form adds up to: the normalised collections and the reference rows the
/// derived data needs. Produced once, used by both the create and the edit handler.
/// </summary>
/// <param name="CategoryIds">
/// Every category the product should be in, primary first — or <c>null</c> when the request said nothing
/// about the extras and the existing links stand. See <c>ProductFormInput.ExtraCategoryIds</c>.
/// </param>
/// <param name="Colours">The colourways, or <c>null</c> to leave them alone.</param>
/// <param name="Variants">The whole matrix, or <c>null</c> to leave it alone.</param>
/// <param name="Media">The gallery, or <c>null</c> to leave it alone.</param>
/// <param name="RelatedProducts">The picks, or <c>null</c> to leave them alone.</param>
internal sealed record ValidatedProductForm(
    ProductFormInput Form,
    string ProductCode,
    string Name,
    string Slug,
    IReadOnlyList<int>? CategoryIds,
    IReadOnlyList<NormalisedColour>? Colours,
    IReadOnlyList<NormalisedVariant>? Variants,
    IReadOnlyList<NormalisedMedia>? Media,
    IReadOnlyList<RelatedProductInput>? RelatedProducts,
    ProductReferenceData References)
{
    /// <summary>
    /// The categories whose names go into the search text (doc 08 §3.2).
    /// <para>
    /// Only the ones this request named. When <see cref="CategoryIds"/> is <c>null</c> the request said
    /// nothing about the extra links, so the primary category is all that is certainly known here and the
    /// aggregate writer fills the rest in from the links already on the product.
    /// </para>
    /// </summary>
    public IReadOnlyList<CategoryReference> Categories { get; } = (CategoryIds ?? [Form.PrimaryCategoryId])
        .Where(References.Categories.ContainsKey)
        .Select(id => References.Categories[id])
        .ToList();
}

/// <summary>
/// The field and reference rules shared by creating and editing a product (ADM-10, doc 11 P1 part B), so
/// the two cannot drift apart — a create that accepts a value the edit refuses is a row an admin can make
/// and then never save again, which is the argument <see cref="CategoryWriteRules"/> already makes for
/// categories.
/// </summary>
internal static class ProductWriteRules
{
    /// <summary>
    /// Validates the form and resolves everything it names, or returns the rejection.
    /// <para>
    /// One function, because the rules are not independent: a variant's colour has to be a colour the same
    /// request declares, and a gallery row's colour likewise, so they cannot be checked in separate passes
    /// without one of them guessing.
    /// </para>
    /// </summary>
    public static CatalogWriteResult.Invalid? Validate(
        ProductFormInput form,
        ProductReferenceData references,
        int? productId,
        out ValidatedProductForm? validated)
    {
        validated = null;

        var errors = new List<ValidationError>();

        ProductFieldRules.CheckText(errors, form);
        ProductFieldRules.CheckPrices(errors, form.BasePrice, form.CompareAtPrice);
        ProductFieldRules.CheckBadge(errors, form.BadgeOverride);

        if (form.RecommendedRank is { } rank && rank < 0)
        {
            errors.Add(new ValidationError(
                "recommendedRank", "The recommended rank cannot be negative. Leave it empty for 'not pinned'."));
        }

        if (errors.Count > 0)
        {
            return new CatalogWriteResult.Invalid(errors);
        }

        CheckDepartmentAndCategories(errors, form, references, out var categoryIds);
        CheckAttributes(errors, form, references);
        CheckSizeGuide(errors, form, references);

        // Each of these returns null when the request carried no such field, which means "leave it
        // alone" (ProductFormInput). The variants and the gallery still have to be validated against the
        // colourways, so a request that sends variants without colours is checked against whatever
        // colours it did send — and when it sent none, against none, which refuses a variant naming a
        // colour that is not in the body. That is the right answer: the aggregate writer matches an
        // unchanged colour list by name, so a variant can only name a colour the request has shown it.
        var colours = NormaliseColours(errors, form, references);
        var variants = NormaliseVariants(errors, form, references, colours ?? []);
        var media = NormaliseMedia(errors, form, references, colours ?? []);
        var related = CheckRelated(errors, form, references, productId);

        if (errors.Count > 0)
        {
            return new CatalogWriteResult.Invalid(errors);
        }

        validated = new ValidatedProductForm(
            form,
            CatalogFieldRules.Clean(form.ProductCode)!,
            CatalogFieldRules.Clean(form.Name)!,
            CatalogFieldRules.Clean(form.Slug)!,
            categoryIds,
            colours,
            variants,
            media,
            related,
            references);

        return null;
    }

    /// <summary>What the handler has to ask the store to look up before it can validate anything.</summary>
    public static ProductReferenceRequest ReferenceRequest(ProductFormInput form)
    {
        ArgumentNullException.ThrowIfNull(form);

        var categoryIds = new HashSet<int> { form.PrimaryCategoryId };
        foreach (var id in form.ExtraCategoryIds ?? [])
        {
            categoryIds.Add(id);
        }

        var attributeIds = new HashSet<int>();
        foreach (var id in new[] { form.MaterialId, form.FitId, form.OccasionId })
        {
            if (id is { } value)
            {
                attributeIds.Add(value);
            }
        }

        var assetIds = new HashSet<Guid>();
        foreach (var colour in form.Colours ?? [])
        {
            if (colour.SwatchAssetPublicId is { } swatch)
            {
                assetIds.Add(swatch);
            }
        }

        foreach (var media in form.Media ?? [])
        {
            assetIds.Add(media.AssetPublicId);
        }

        return new ProductReferenceRequest(
            form.DepartmentId,
            categoryIds,
            form.AgeGroupId,
            attributeIds,
            (form.Colours ?? []).Select(colour => colour.ColourFamilyId).ToHashSet(),
            (form.Variants ?? []).Select(variant => variant.SizeId).ToHashSet(),
            form.SizeGuideId,
            assetIds,
            (form.RelatedProducts ?? []).Select(related => related.RelatedProductId).ToHashSet());
    }

    /// <summary>
    /// The fields an audit diff is taken over (doc 08 §3.8: changed properties only).
    /// <para>
    /// The collections are included as compact summaries rather than as nested objects, because the trail
    /// has to answer "who changed the prices" and "who took the photographs off" and a reader cannot see
    /// either in a <c>null → null</c> diff of scalar columns. They are rendered as stable, ordered strings
    /// so an unchanged collection produces an identical value and no spurious row.
    /// </para>
    /// </summary>
    public static Dictionary<string, object?> Snapshot(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["productCode"] = product.ProductCode,
            ["name"] = product.Name,
            ["slug"] = product.Slug,
            ["departmentId"] = product.DepartmentId,
            ["primaryCategoryId"] = product.PrimaryCategoryId,
            ["categoryIds"] = Join(product.CategoryLinks.Select(link => link.CategoryId).Order()),
            ["ageGroupId"] = product.AgeGroupId,
            ["shortDescription"] = product.ShortDescription,
            ["description"] = product.Description,
            ["careInstructions"] = product.CareInstructions,
            ["materialId"] = product.MaterialId,
            ["fitId"] = product.FitId,
            ["occasionId"] = product.OccasionId,
            ["basePrice"] = product.BasePrice,
            ["compareAtPrice"] = product.CompareAtPrice,
            ["badgeOverride"] = product.BadgeOverride,
            ["newUntilUtc"] = product.NewUntilUtc,
            ["recommendedRank"] = product.RecommendedRank,
            ["sizeGuideId"] = product.SizeGuideId,
            ["cardImageAssetId"] = product.CardImageAssetId,
            ["hoverImageAssetId"] = product.HoverImageAssetId,
            ["seoTitle"] = product.SeoTitle,
            ["seoDescription"] = product.SeoDescription,
            ["isPublished"] = product.IsPublished,
            ["colours"] = Join(product.Colours
                .OrderBy(colour => colour.SortOrder)
                .ThenBy(colour => colour.Name, StringComparer.Ordinal)
                .Select(colour => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{colour.Name}/{colour.ColourFamilyId}/{colour.SwatchHex ?? "-"}/{colour.SwatchAssetId?.ToString(CultureInfo.InvariantCulture) ?? "-"}"))),
            ["variants"] = Join(product.Variants
                .OrderBy(variant => variant.Sku, StringComparer.Ordinal)
                .Select(variant => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{variant.Sku}:size={variant.SizeId},price={variant.PriceOverride?.ToString(CultureInfo.InvariantCulture) ?? "-"},was={variant.CompareAtPriceOverride?.ToString(CultureInfo.InvariantCulture) ?? "-"},active={variant.IsActive},barcode={variant.Barcode ?? "-"}"))),
            ["media"] = Join(product.Media
                .OrderBy(media => media.MediaRole)
                .ThenBy(media => media.SortOrder)
                .ThenBy(media => media.MediaAssetId)
                .Select(media => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{media.MediaRole}:asset={media.MediaAssetId},colour={media.ProductColourId?.ToString(CultureInfo.InvariantCulture) ?? "all"},alt={(string.IsNullOrWhiteSpace(media.AltTextOverride) ? "-" : "set")}"))),
            ["relatedProductIds"] = Join(product.RelatedProducts
                .OrderBy(related => related.SortOrder)
                .ThenBy(related => related.RelatedProductId)
                .Select(related => related.RelatedProductId)),
        };
    }

    /// <summary>
    /// The <c>media.MediaUsages</c> rows this product should own (doc 08 §3.3).
    /// <para>
    /// Every gallery, hover and video asset in its own role, plus the two denormalised columns' assets as
    /// <c>Card</c> and <c>Hover</c> — the set <c>DemoCatalogueSeeder.AddMediaUsages</c> writes — plus every
    /// colourway swatch as <c>Swatch</c>.
    /// </para>
    /// <para>
    /// <b>A swatch is recorded against the product, not the colourway</b>, although
    /// <c>MediaUsageOwnerTypes.ProductColour</c> exists. Two reasons: a colourway added in the same request
    /// has no id until the insert, so an owner id of the colour cannot be computed before the write; and
    /// the library's "used in…" panel resolves a label per owner type, and "Wide-Leg Rinse Jeans" is a name
    /// a staff member recognises where "ProductColour 418" is not. The guard is unaffected — what it counts
    /// is rows, not owner types.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ProductMediaUsage> DesiredMediaUsages(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);

        var desired = new HashSet<ProductMediaUsage>();

        foreach (var media in product.Media)
        {
            desired.Add(new ProductMediaUsage(media.MediaAssetId, RoleName(media.MediaRole)));
        }

        if (product.CardImageAssetId is { } card)
        {
            desired.Add(new ProductMediaUsage(card, MediaUsageRoles.Card));
        }

        if (product.HoverImageAssetId is { } hover)
        {
            desired.Add(new ProductMediaUsage(hover, MediaUsageRoles.Hover));
        }

        foreach (var colour in product.Colours)
        {
            if (colour.SwatchAssetId is { } swatch)
            {
                desired.Add(new ProductMediaUsage(swatch, MediaUsageRoles.Swatch));
            }
        }

        return desired.ToList();
    }

    /// <summary>
    /// The product's pictures and video as a <see cref="CatalogMediaClaim"/>, for the store to lock and re-check
    /// inside the save's transaction (#223). One slot per <see cref="DesiredMediaUsages"/> row, each against the
    /// request field that carried the asset, so a refusal names it the way the handler's own check does: a gallery
    /// row's <c>media[i].assetPublicId</c>, or a colourway's <c>colours[i].swatchAssetPublicId</c>. An asset the
    /// request did not name (a request that left <c>media</c> out keeps the rows it has) is reported on
    /// <c>media</c>.
    /// <para>
    /// A video's poster is not claimed. The media library refuses to delete a poster while its video is live (the
    /// <c>PosterAssetId</c> check in <c>MediaService.DeleteAsync</c>), and the video's own row is locked here, so
    /// the poster cannot be deleted out from under this save either.
    /// </para>
    /// </summary>
    public static CatalogMediaClaim MediaClaim(Product product, ProductFormInput form, ProductReferenceData references)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(references);

        var fields = new Dictionary<int, string>();

        void Name(Guid? publicId, string field)
        {
            if (publicId is { } id && references.MediaAssets.TryGetValue(id, out var asset))
            {
                fields.TryAdd(asset.Id, field);
            }
        }

        for (var index = 0; index < (form.Media?.Count ?? 0); index++)
        {
            Name(form.Media![index].AssetPublicId, string.Create(CultureInfo.InvariantCulture, $"media[{index}].assetPublicId"));
        }

        for (var index = 0; index < (form.Colours?.Count ?? 0); index++)
        {
            Name(form.Colours![index].SwatchAssetPublicId, string.Create(CultureInfo.InvariantCulture, $"colours[{index}].swatchAssetPublicId"));
        }

        return new CatalogMediaClaim(DesiredMediaUsages(product)
            .Select(usage => new CatalogMediaSlot(
                usage.MediaAssetId,
                usage.UsageRole,
                fields.TryGetValue(usage.MediaAssetId, out var field) ? field : "media")));
    }

    private static string RoleName(ProductMediaRole role) => role switch
    {
        ProductMediaRole.Gallery => MediaUsageRoles.Gallery,
        ProductMediaRole.Hover => MediaUsageRoles.Hover,
        ProductMediaRole.Video => MediaUsageRoles.Video,
        _ => throw new InvalidOperationException($"Unhandled media role '{role}'."),
    };

    private static string Join<T>(IEnumerable<T> values) => string.Join('|', values);

    // ---- department, categories ---------------------------------------------------------------

    private static void CheckDepartmentAndCategories(
        List<ValidationError> errors,
        ProductFormInput form,
        ProductReferenceData references,
        out IReadOnlyList<int>? categoryIds)
    {
        categoryIds = null;

        if (references.DepartmentName is null)
        {
            errors.Add(new ValidationError(
                "departmentId",
                string.Create(CultureInfo.InvariantCulture, $"No department has the id {form.DepartmentId}.")));
            return;
        }

        if (!references.Categories.TryGetValue(form.PrimaryCategoryId, out var primary))
        {
            errors.Add(new ValidationError(
                "primaryCategoryId",
                string.Create(
                    CultureInfo.InvariantCulture, $"No category has the id {form.PrimaryCategoryId}.")));
            return;
        }

        if (primary.DepartmentId != form.DepartmentId)
        {
            errors.Add(new ValidationError(
                "primaryCategoryId",
                $"'{primary.Name}' is in another department. A product's main category has to be one of its "
                + "own department's (doc 08 §3.2)."));
            return;
        }

        // Doc 08 §3.2: "Required for Kids (app)". The age group drives the Kids filters (PLP-02, DEPT-01),
        // and a Kids product without one is invisible to every one of them.
        if (string.Equals(references.DepartmentSlug, CatalogDepartments.KidsSlug, StringComparison.Ordinal))
        {
            if (form.AgeGroupId is null)
            {
                errors.Add(new ValidationError(
                    "ageGroupId",
                    "A Kids product needs an age group: it is what the Baby / Toddler / Kids / Juniors "
                    + "filters are built from."));
            }
        }

        if (form.AgeGroupId is not null && !references.AgeGroupExists)
        {
            errors.Add(new ValidationError("ageGroupId", "That age group does not exist."));
        }

        // No extras field at all: the request says nothing about the extra links, so they stand and
        // CategoryIds is left null for the aggregate writer to read as "leave them alone".
        //
        // The age-group rules above deliberately run *before* this return. They belong to the department,
        // not to the extras, and putting this guard in front of them silently stopped a Kids product
        // without an age group from being refused — caught by
        // ProductWriteHandlerTests.A_kids_product_without_an_age_group_is_refused_by_name.
        if (form.ExtraCategoryIds is null)
        {
            return;
        }

        var extras = form.ExtraCategoryIds.Where(id => id != form.PrimaryCategoryId).Distinct().ToList();

        if (extras.Count > ProductFieldRules.MaxExtraCategories)
        {
            errors.Add(new ValidationError(
                "extraCategoryIds",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A product can appear in at most {ProductFieldRules.MaxExtraCategories} extra categories.")));
            return;
        }

        foreach (var id in extras)
        {
            if (!references.Categories.TryGetValue(id, out var extra))
            {
                errors.Add(new ValidationError(
                    "extraCategoryIds",
                    string.Create(CultureInfo.InvariantCulture, $"No category has the id {id}.")));
                continue;
            }

            // A category belongs to exactly one department (catalog.Categories.DepartmentId), so an extra
            // category from another department would put this product in that department's listing — which
            // is never what an admin means by "also show it here".
            if (extra.DepartmentId != form.DepartmentId)
            {
                errors.Add(new ValidationError(
                    "extraCategoryIds",
                    $"'{extra.Name}' belongs to another department, so listing this product in it would put "
                    + "it in that department's pages."));
            }
        }

        if (errors.Count == 0)
        {
            categoryIds = new[] { form.PrimaryCategoryId }.Concat(extras).ToList();
        }
    }

    // ---- attribute options --------------------------------------------------------------------

    private static void CheckAttributes(
        List<ValidationError> errors,
        ProductFormInput form,
        ProductReferenceData references)
    {
        Check("materialId", form.MaterialId, AttributeOptionType.Material, "material");
        Check("fitId", form.FitId, AttributeOptionType.Fit, "fit");
        Check("occasionId", form.OccasionId, AttributeOptionType.Occasion, "occasion");

        void Check(string field, int? id, AttributeOptionType expected, string label)
        {
            if (id is not { } value)
            {
                return;
            }

            if (!references.AttributeOptions.TryGetValue(value, out var option))
            {
                errors.Add(new ValidationError(
                    field, string.Create(CultureInfo.InvariantCulture, $"No {label} option has the id {value}.")));
                return;
            }

            // Doc 08 §3.2: all three columns are foreign keys into catalog.AttributeOptions, so the type is
            // the only thing stopping a fit being saved as a material. The database cannot check it.
            if (option.Type != expected)
            {
                errors.Add(new ValidationError(
                    field,
                    $"'{option.Name}' is a {option.Type.ToString().ToLowerInvariant()} option, not a {label} one."));
            }
        }
    }

    private static void CheckSizeGuide(
        List<ValidationError> errors,
        ProductFormInput form,
        ProductReferenceData references)
    {
        if (form.SizeGuideId is not null && !references.SizeGuideExists)
        {
            errors.Add(new ValidationError("sizeGuideId", "That size guide does not exist."));
        }
    }

    // ---- colours ------------------------------------------------------------------------------

    private static List<NormalisedColour>? NormaliseColours(
        List<ValidationError> errors,
        ProductFormInput form,
        ProductReferenceData references)
    {
        if (form.Colours is null)
        {
            return null;
        }

        var input = form.Colours;

        if (input.Count > ProductFieldRules.MaxColours)
        {
            errors.Add(new ValidationError(
                "colours",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A product can have at most {ProductFieldRules.MaxColours} colours.")));
            return [];
        }

        var result = new List<NormalisedColour>(input.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < input.Count; index++)
        {
            var colour = input[index];
            var field = string.Create(CultureInfo.InvariantCulture, $"colours[{index}]");

            var name = CatalogFieldRules.Clean(colour.Name);
            if (name is null)
            {
                errors.Add(new ValidationError($"{field}.name", "Every colour needs a name."));
                continue;
            }

            if (name.Length > ProductFieldRules.ColourNameMaxLength)
            {
                errors.Add(new ValidationError(
                    $"{field}.name",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"A colour name cannot be longer than {ProductFieldRules.ColourNameMaxLength} characters.")));
                continue;
            }

            // UX_ProductColours_Product_Name, and the reason variants can name their colour rather than
            // carry an id it does not have yet.
            if (!names.Add(name))
            {
                errors.Add(new ValidationError(
                    $"{field}.name",
                    $"'{name}' is listed twice. Each colour of a product needs its own name — it is how the "
                    + "sizes and photographs below say which colour they belong to."));
                continue;
            }

            if (!references.ColourFamilies.ContainsKey(colour.ColourFamilyId))
            {
                errors.Add(new ValidationError(
                    $"{field}.colourFamilyId",
                    "Choose the colour family this shade filters under — it is what the colour filter on the "
                    + "listing page groups by."));
            }

            var hex = CatalogFieldRules.Clean(colour.SwatchHex);
            if (hex is not null && !ProductFieldRules.IsSwatchHex(hex))
            {
                errors.Add(new ValidationError(
                    $"{field}.swatchHex",
                    "A swatch colour looks like #1a2b3c: a hash and six hexadecimal digits."));
                hex = null;
            }

            int? swatchAssetId = null;
            if (colour.SwatchAssetPublicId is { } swatchPublicId)
            {
                if (ResolveImageAsset(errors, $"{field}.swatchAssetPublicId", swatchPublicId, references, "swatch")
                    is { } asset)
                {
                    swatchAssetId = asset.Id;
                }
            }

            result.Add(new NormalisedColour(
                colour.Id, name, colour.ColourFamilyId, hex, swatchAssetId, colour.SortOrder));
        }

        return result;
    }

    // ---- variants -----------------------------------------------------------------------------

    private static List<NormalisedVariant>? NormaliseVariants(
        List<ValidationError> errors,
        ProductFormInput form,
        ProductReferenceData references,
        IReadOnlyList<NormalisedColour> colours)
    {
        if (form.Variants is null)
        {
            return null;
        }

        var input = form.Variants;

        if (input.Count > ProductFieldRules.MaxVariants)
        {
            errors.Add(new ValidationError(
                "variants",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A product can have at most {ProductFieldRules.MaxVariants} colour-and-size combinations.")));
            return [];
        }

        var colourNames = colours.Select(colour => colour.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<NormalisedVariant>(input.Count);
        var pairs = new HashSet<(string Colour, int SizeId)>();
        var skus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var barcodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < input.Count; index++)
        {
            var variant = input[index];
            var field = string.Create(CultureInfo.InvariantCulture, $"variants[{index}]");

            var colourName = CatalogFieldRules.Clean(variant.ColourName);
            if (colourName is null || !colourNames.Contains(colourName))
            {
                errors.Add(new ValidationError(
                    $"{field}.colourName",
                    colourName is null
                        ? "Every size row has to say which colour it belongs to."
                        : $"'{colourName}' is not one of this product's colours."));
                continue;
            }

            // Match the declared colour's spelling, so the reconciliation and the unique index agree about
            // which colourway this is whatever case the client sent.
            colourName = colours.First(
                colour => string.Equals(colour.Name, colourName, StringComparison.OrdinalIgnoreCase)).Name;

            if (!references.Sizes.ContainsKey(variant.SizeId))
            {
                errors.Add(new ValidationError(
                    $"{field}.sizeId",
                    string.Create(CultureInfo.InvariantCulture, $"No size has the id {variant.SizeId}.")));
                continue;
            }

            // UX_ProductVariants_Colour_Size: one variant per colour × size (doc 08 §3.2).
            if (!pairs.Add((colourName, variant.SizeId)))
            {
                var label = references.Sizes[variant.SizeId].Label;
                errors.Add(new ValidationError(
                    $"{field}.sizeId",
                    $"{colourName} in size {label} is listed twice. A product has one row per colour and size."));
                continue;
            }

            var errorsBefore = errors.Count;
            ProductFieldRules.CheckSku(errors, $"{field}.sku", variant.Sku);
            ProductFieldRules.CheckBarcode(errors, $"{field}.barcode", variant.Barcode);

            if (variant.PriceOverride is { } price)
            {
                ProductFieldRules.CheckMoney(
                    errors, $"{field}.priceOverride", price, "A variant price", mustBePositive: true);
            }

            if (variant.CompareAtPriceOverride is { } compareAt)
            {
                ProductFieldRules.CheckMoney(
                    errors, $"{field}.compareAtPriceOverride", compareAt, "A variant original price", mustBePositive: true);
            }

            if (errors.Count != errorsBefore)
            {
                continue;
            }

            var sku = CatalogFieldRules.Clean(variant.Sku)!;
            if (!skus.Add(sku))
            {
                errors.Add(new ValidationError(
                    $"{field}.sku",
                    $"SKU '{sku}' is used by two rows of this product. Every variant in the shop needs its own."));
                continue;
            }

            var barcode = CatalogFieldRules.Clean(variant.Barcode);
            if (barcode is not null && !barcodes.Add(barcode))
            {
                errors.Add(new ValidationError(
                    $"{field}.barcode", $"Barcode '{barcode}' is used by two rows of this product."));
                continue;
            }

            // The effective pair, which is the rule doc 08 §3.2 says "is validated on save" because a check
            // constraint cannot see across the two tables. CK_ProductVariants_CompareAtGtPrice covers the
            // case where both overrides are set; this covers the three mixed cases, e.g. a variant priced
            // above the product's own original price.
            var effectiveList = CatalogPricing.ListPrice(form.BasePrice, variant.PriceOverride);
            var statedOriginal = variant.CompareAtPriceOverride ?? form.CompareAtPrice;

            if (statedOriginal is { } original && original <= effectiveList)
            {
                var sellsAt = effectiveList.ToString("N2", CultureInfo.InvariantCulture);
                var wasAt = original.ToString("N2", CultureInfo.InvariantCulture);

                errors.Add(new ValidationError(
                    $"{field}.priceOverride",
                    $"This row would sell at Rs. {sellsAt} with an original price of Rs. {wasAt}, which is not "
                    + "a markdown. Either raise the original price or lower this row's price."));
                continue;
            }

            result.Add(new NormalisedVariant(
                variant.Id,
                colourName,
                variant.SizeId,
                sku,
                barcode,
                variant.PriceOverride,
                variant.CompareAtPriceOverride,
                variant.IsActive,
                variant.SortOrder));
        }

        // A variant of a colour that is not declared is already refused above; the opposite — a declared
        // colour with no sizes — is allowed, because an admin adding a colourway naturally adds the colour
        // first and the sizes next, and refusing the intermediate save would make the form unusable. It is
        // the publish checklist that insists on at least one active variant (doc 11 §7 G-01).
        return result;
    }

    // ---- media --------------------------------------------------------------------------------

    /// <summary>
    /// Resolves the media list, or returns <c>null</c> when the request did not carry one.
    /// <para>
    /// <b>This distinction is the W1-12 defect, and it is the single most likely way this task ships
    /// broken.</b> There, every save of a homepage block silently cleared its images, because a request
    /// that omitted the field was treated the same as one that sent an empty list — and 1,571 passing tests
    /// missed it, because every fixture set those columns to null to begin with. A product has images
    /// <i>and</i> a video, so the same mistake here would be worse.
    /// </para>
    /// <para>
    /// So: <c>null</c> means "the request says nothing about media, leave it alone", and an empty list means
    /// "remove all of it". A PUT that replaces the row still has to be able to clear a collection, and a
    /// client that has not been updated to send the field must not be able to wipe one.
    /// </para>
    /// </summary>
    private static List<NormalisedMedia>? NormaliseMedia(
        List<ValidationError> errors,
        ProductFormInput form,
        ProductReferenceData references,
        IReadOnlyList<NormalisedColour> colours)
    {
        if (form.Media is null)
        {
            return null;
        }

        var input = form.Media;

        if (input.Count > ProductFieldRules.MaxMedia)
        {
            errors.Add(new ValidationError(
                "media",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A product can have at most {ProductFieldRules.MaxMedia} photographs and videos.")));
            return [];
        }

        var colourNames = colours.Select(colour => colour.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<NormalisedMedia>(input.Count);
        var slots = new HashSet<(int AssetId, string Colour)>();
        var hoverPerColour = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var videos = 0;

        for (var index = 0; index < input.Count; index++)
        {
            var media = input[index];
            var field = string.Create(CultureInfo.InvariantCulture, $"media[{index}]");

            if (media.MediaRole is not (ProductMediaRole.Gallery or ProductMediaRole.Hover or ProductMediaRole.Video))
            {
                errors.Add(new ValidationError(
                    $"{field}.mediaRole", "A product media row is a gallery image, a hover image or the video."));
                continue;
            }

            string? colourName = null;
            if (CatalogFieldRules.Clean(media.ColourName) is { } requested)
            {
                if (!colourNames.Contains(requested))
                {
                    errors.Add(new ValidationError(
                        $"{field}.colourName", $"'{requested}' is not one of this product's colours."));
                    continue;
                }

                colourName = colours.First(
                    colour => string.Equals(colour.Name, requested, StringComparison.OrdinalIgnoreCase)).Name;
            }

            if (media.MediaRole == ProductMediaRole.Video)
            {
                if (colourName is not null)
                {
                    errors.Add(new ValidationError(
                        $"{field}.colourName",
                        "The video belongs to the whole product, not to one colour. Doc 08's gallery swap is "
                        + "per colour and a product has one video."));
                    continue;
                }

                if (++videos > 1)
                {
                    errors.Add(new ValidationError(
                        $"{field}.mediaRole",
                        "A product has one video. Remove the other one before adding this."));
                    continue;
                }
            }

            if (media.MediaRole == ProductMediaRole.Hover && !hoverPerColour.Add(colourName ?? string.Empty))
            {
                errors.Add(new ValidationError(
                    $"{field}.mediaRole",
                    colourName is null
                        ? "Only one picture can be the hover image."
                        : $"Only one picture can be {colourName}'s hover image."));
                continue;
            }

            var asset = media.MediaRole == ProductMediaRole.Video
                ? ResolveAsset(errors, $"{field}.assetPublicId", media.AssetPublicId, references, MediaAssetType.Video, "video")
                : ResolveImageAsset(errors, $"{field}.assetPublicId", media.AssetPublicId, references, "photograph");

            if (asset is null)
            {
                continue;
            }

            // UX_ProductMedia_Product_Asset_Colour (ProductId, MediaAssetId, ProductColourId), with NULLs
            // compared as equal. So the same picture cannot be used twice for one colour — not even once as
            // a gallery image and once as the hover image.
            if (!slots.Add((asset.Id, colourName ?? string.Empty)))
            {
                errors.Add(new ValidationError(
                    $"{field}.assetPublicId",
                    colourName is null
                        ? "That picture is already attached to this product. Change its role instead of adding "
                          + "it twice."
                        : $"That picture is already attached to {colourName}."));
                continue;
            }

            var altText = CatalogFieldRules.Clean(media.AltTextOverride);
            if (altText is not null && altText.Length > ProductFieldRules.AltTextMaxLength)
            {
                errors.Add(new ValidationError(
                    $"{field}.altTextOverride",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Alt text cannot be longer than {ProductFieldRules.AltTextMaxLength} characters.")));
                continue;
            }

            result.Add(new NormalisedMedia(asset.Id, colourName, media.MediaRole, media.SortOrder, altText));
        }

        return result;
    }

    private static MediaAssetReference? ResolveImageAsset(
        List<ValidationError> errors,
        string field,
        Guid publicId,
        ProductReferenceData references,
        string label) =>
        ResolveAsset(errors, field, publicId, references, MediaAssetType.Image, label);

    /// <summary>
    /// Finds a library asset and refuses one that would render as nothing: the wrong kind of file, one still
    /// being processed, one that failed, or a private one (nothing on the private volume is reachable
    /// through <c>/media</c>, doc 08 §3.3).
    /// </summary>
    private static MediaAssetReference? ResolveAsset(
        List<ValidationError> errors,
        string field,
        Guid publicId,
        ProductReferenceData references,
        MediaAssetType expected,
        string label)
    {
        if (!references.MediaAssets.TryGetValue(publicId, out var asset))
        {
            errors.Add(new ValidationError(field, $"That {label} is not in the media library."));
            return null;
        }

        if (asset.MediaType != expected)
        {
            errors.Add(new ValidationError(
                field,
                expected == MediaAssetType.Image
                    ? $"That file is a {asset.MediaType.ToString().ToLowerInvariant()}, and this slot needs an image."
                    : "That file is not a video."));
            return null;
        }

        if (asset.Status != MediaAssetStatus.Ready)
        {
            errors.Add(new ValidationError(
                field,
                asset.Status == MediaAssetStatus.Processing
                    ? $"That {label} is still being processed. Wait for it to finish, then try again."
                    : $"That {label} failed to upload, so it cannot be used. Upload it again."));
            return null;
        }

        if (asset.Visibility != MediaVisibility.Public)
        {
            errors.Add(new ValidationError(
                field, $"That {label} is a private file and cannot be shown on the shop."));
            return null;
        }

        return asset;
    }

    // ---- related ------------------------------------------------------------------------------

    private static List<RelatedProductInput>? CheckRelated(
        List<ValidationError> errors,
        ProductFormInput form,
        ProductReferenceData references,
        int? productId)
    {
        if (form.RelatedProducts is null)
        {
            return null;
        }

        var input = form.RelatedProducts;

        if (input.Count > ProductFieldRules.MaxRelatedProducts)
        {
            errors.Add(new ValidationError(
                "relatedProducts",
                $"At most {ProductFieldRules.MaxRelatedProducts} related products can be pinned. The product "
                + "page tops the list up from the same category anyway."));
            return [];
        }

        var result = new List<RelatedProductInput>(input.Count);
        var seen = new HashSet<int>();

        for (var index = 0; index < input.Count; index++)
        {
            var related = input[index];
            var field = string.Create(CultureInfo.InvariantCulture, $"relatedProducts[{index}]");

            // CK_RelatedProducts_NotSelf, as a named field error.
            if (productId is { } self && related.RelatedProductId == self)
            {
                errors.Add(new ValidationError(
                    $"{field}.relatedProductId", "A product cannot be related to itself."));
                continue;
            }

            if (!references.RelatedProducts.ContainsKey(related.RelatedProductId))
            {
                errors.Add(new ValidationError(
                    $"{field}.relatedProductId",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"No product has the id {related.RelatedProductId}, or it has been removed.")));
                continue;
            }

            if (!seen.Add(related.RelatedProductId))
            {
                errors.Add(new ValidationError(
                    $"{field}.relatedProductId", "That product is pinned twice."));
                continue;
            }

            result.Add(related);
        }

        return result;
    }
}
