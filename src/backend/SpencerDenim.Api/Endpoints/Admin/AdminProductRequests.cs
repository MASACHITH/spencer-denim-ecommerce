using SpencerDenim.Application.Catalog.Admin.Products;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The JSON bodies the ADM-10 product endpoints accept.
/// <para>
/// <b>Every field is nullable, including the booleans and the numbers</b>, for the reason
/// <c>AdminCatalogRequests</c> states: a non-nullable <c>int</c> binds a missing field to 0, which reads as
/// "the admin chose zero" and cannot be told apart from a client that forgot to send it; and an
/// out-of-range value on a <c>byte</c> or an <c>enum</c> property is a <c>JsonException</c>, which would
/// come back as "malformed JSON" when what is wrong is one box an admin filled in. Nullable everywhere
/// means the use case decides and every refusal names its field.
/// </para>
/// <para>
/// <b>The three collections are nullable for a second, sharper reason.</b> A <c>null</c> collection means
/// "this request says nothing about it"; an empty list means "make it empty". W1-12 conflated those and
/// every save silently cleared a block's images. Here that distinction is honoured for
/// <see cref="ProductRequestBody.Media"/> — a request with no media field leaves a product's photographs
/// and video exactly as they are.
/// </para>
/// </summary>
public sealed record ProductColourRequest(
    int? Id,
    string? Name,
    int? ColourFamilyId,
    string? SwatchHex,
    Guid? SwatchAssetPublicId,
    int? SortOrder);

/// <inheritdoc cref="ProductColourRequest"/>
public sealed record ProductVariantRequest(
    int? Id,
    string? ColourName,
    int? SizeId,
    string? Sku,
    string? Barcode,
    decimal? PriceOverride,
    decimal? CompareAtPriceOverride,
    bool? IsActive,
    int? SortOrder);

/// <inheritdoc cref="ProductColourRequest"/>
/// <param name="MediaRole">1 Gallery, 2 Hover, 3 Video (doc 08 §3.2's <c>CK_ProductMedia_MediaRole</c>).</param>
public sealed record ProductMediaRequest(
    Guid? AssetPublicId,
    string? ColourName,
    int? MediaRole,
    int? SortOrder,
    string? AltTextOverride);

/// <inheritdoc cref="ProductColourRequest"/>
public sealed record RelatedProductRequest(int? RelatedProductId, int? SortOrder);

/// <summary>
/// The product form's body, shared by <c>POST /products</c> and <c>PUT /products/{id}</c>.
/// </summary>
/// <param name="ConfirmSlugChange">
/// Required to change the slug of a product that has been published at least once (doc 11 P1's "Slug
/// change after first publish shows a 'this breaks existing links' confirmation"). Ignored on a create and
/// on a product that has never been published.
/// </param>
/// <param name="RowVersion">
/// Base64 of <c>catalog.Products.RowVersion</c>, from the admin read model. May be sent in the
/// <c>If-Match</c> header instead; see <c>AdminWriteRequest.RowVersion</c>.
/// </param>
public sealed record ProductRequestBody(
    string? ProductCode,
    string? Name,
    string? Slug,
    int? DepartmentId,
    int? PrimaryCategoryId,
    IReadOnlyList<int>? ExtraCategoryIds,
    int? AgeGroupId,
    string? ShortDescription,
    string? Description,
    string? CareInstructions,
    int? MaterialId,
    int? FitId,
    int? OccasionId,
    decimal? BasePrice,
    decimal? CompareAtPrice,
    int? BadgeOverride,
    int? RecommendedRank,
    int? SizeGuideId,
    string? SeoTitle,
    string? SeoDescription,
    IReadOnlyList<ProductColourRequest>? Colours,
    IReadOnlyList<ProductVariantRequest>? Variants,
    IReadOnlyList<ProductMediaRequest>? Media,
    IReadOnlyList<RelatedProductRequest>? RelatedProducts,
    bool? ConfirmSlugChange,
    string? RowVersion);

/// <summary>Publish or unpublish a product (ADM-10, doc 11 P1 steps 14–15).</summary>
public sealed record SetProductPublishedRequest(bool? IsPublished, string? RowVersion);

/// <summary>The id of a product that has just been created, so the screen can open it without a reload.</summary>
public sealed record CreatedProductResponse(int Id);

/// <summary>
/// Turns a request body into the use case's input, refusing the handful of values that cannot be expressed
/// as a field error later because they would not reach a field at all.
/// </summary>
internal static class ProductRequestMapper
{
    /// <summary>
    /// The three ids a form cannot do without, and the badge, which is an enum rather than a free number.
    /// </summary>
    /// <returns>
    /// The mapped form, or the field name and message to refuse with. A tuple rather than an exception,
    /// because every one of these is ordinary input from a screen that may be a build behind.
    /// </returns>
    public static (ProductFormInput? Form, string? Field, string? Message) Map(ProductRequestBody body)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (body.DepartmentId is null)
        {
            return (null, "departmentId", "Choose a department.");
        }

        if (body.PrimaryCategoryId is null)
        {
            return (null, "primaryCategoryId", "Choose the category this product belongs in.");
        }

        if (body.BasePrice is null)
        {
            return (null, "basePrice", "Enter the price.");
        }

        ProductBadge? badge = null;
        if (body.BadgeOverride is { } badgeValue)
        {
            if (!Enum.IsDefined(typeof(ProductBadge), badgeValue) || badgeValue is < 1 or > 4)
            {
                return (
                    null,
                    "badgeOverride",
                    "A badge is 1 New, 2 Sale, 3 Online Exclusive or 4 Best Seller. Low Stock is worked out "
                    + "from stock when the card is shown, so it cannot be set here.");
            }

            badge = (ProductBadge)badgeValue;
        }

        var colours = new List<ProductColourInput>(body.Colours?.Count ?? 0);
        foreach (var colour in body.Colours ?? [])
        {
            if (colour.ColourFamilyId is null)
            {
                return (
                    null,
                    "colours",
                    $"The colour '{colour.Name ?? "(unnamed)"}' has no colour family. Choose the family its "
                    + "filter groups under.");
            }

            colours.Add(new ProductColourInput(
                colour.Id,
                colour.Name,
                colour.ColourFamilyId.Value,
                colour.SwatchHex,
                colour.SwatchAssetPublicId,
                ToSortOrder(colour.SortOrder)));
        }

        var variants = new List<ProductVariantInput>(body.Variants?.Count ?? 0);
        foreach (var variant in body.Variants ?? [])
        {
            if (variant.SizeId is null)
            {
                return (null, "variants", "Every size row has to say which size it is.");
            }

            variants.Add(new ProductVariantInput(
                variant.Id,
                variant.ColourName,
                variant.SizeId.Value,
                variant.Sku,
                variant.Barcode,
                variant.PriceOverride,
                variant.CompareAtPriceOverride,

                // A row that does not say is on: a staff member adding a size means to sell it. Switching
                // one off is the deliberate act (doc 08 §3.2's "deactivate once ordered, never delete").
                variant.IsActive ?? true,
                ToSortOrder(variant.SortOrder)));
        }

        List<ProductMediaInput>? media = null;
        if (body.Media is not null)
        {
            media = new List<ProductMediaInput>(body.Media.Count);

            foreach (var row in body.Media)
            {
                if (row.AssetPublicId is null)
                {
                    return (null, "media", "Every photograph or video row has to name a media asset.");
                }

                if (row.MediaRole is not (1 or 2 or 3))
                {
                    return (
                        null,
                        "media",
                        "A media row is 1 a gallery photograph, 2 the hover photograph or 3 the video.");
                }

                media.Add(new ProductMediaInput(
                    row.AssetPublicId.Value,
                    row.ColourName,
                    (ProductMediaRole)row.MediaRole.Value,
                    ToSortOrder(row.SortOrder),
                    row.AltTextOverride));
            }
        }

        var related = new List<RelatedProductInput>(body.RelatedProducts?.Count ?? 0);
        foreach (var pick in body.RelatedProducts ?? [])
        {
            if (pick.RelatedProductId is null)
            {
                return (null, "relatedProducts", "Every related row has to name a product.");
            }

            related.Add(new RelatedProductInput(pick.RelatedProductId.Value, ToSortOrder(pick.SortOrder)));
        }

        return (
            new ProductFormInput(
                body.ProductCode,
                body.Name,
                body.Slug,
                body.DepartmentId.Value,
                body.PrimaryCategoryId.Value,
                body.ExtraCategoryIds,
                body.AgeGroupId,
                body.ShortDescription,
                body.Description,
                body.CareInstructions,
                body.MaterialId,
                body.FitId,
                body.OccasionId,
                body.BasePrice.Value,
                body.CompareAtPrice,
                badge,
                body.RecommendedRank,
                body.SizeGuideId,
                body.SeoTitle,
                body.SeoDescription,
                colours,
                variants,
                media,
                related),
            null,
            null);
    }

    /// <summary>
    /// Clamps a sort order into <c>smallint</c>, the column's type (doc 08 §3.2).
    /// <para>
    /// Clamped rather than refused: a sort order is the screen's own arithmetic, not something an admin
    /// types, so a number out of range is a client bug and the useful answer is the end of the list rather
    /// than a validation error about a control that is not on the form.
    /// </para>
    /// </summary>
    private static short ToSortOrder(int? value) => value switch
    {
        null => 0,
        < short.MinValue => short.MinValue,
        > short.MaxValue => short.MaxValue,
        _ => (short)value.Value,
    };
}
