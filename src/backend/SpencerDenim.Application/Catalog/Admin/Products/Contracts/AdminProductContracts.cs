using SpencerDenim.Application.Catalog.Admin.Contracts;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Application.Catalog.Admin.Products.Contracts;

/// <summary>
/// One row of the admin product list (ADM-10, doc 11 P1 part B).
/// <para>
/// <b>Not <c>ProductCardDto</c>.</b> The storefront card is what a shopper sees, so it carries a resolved
/// badge, a sale price and nothing an admin needs: it omits unpublished products entirely (that is what
/// <c>ProductCatalogReader.BaseQuery</c> is for), which is most of what this screen exists to manage.
/// </para>
/// </summary>
/// <param name="RowVersion">
/// Base64 of <c>catalog.Products.RowVersion</c>, so the list's publish and remove buttons can carry a
/// precondition without a second round trip (doc 08 §1.5 [RV]).
/// </param>
/// <param name="VariantCount">Active variants. Zero is the state G-01 refuses to publish.</param>
/// <param name="CardImageUrl">Built from <c>Catalog:MediaBaseUrl</c>, as every other payload builds one.</param>
public sealed record AdminProductListItemDto(
    int Id,
    string ProductCode,
    string Name,
    string Slug,
    int DepartmentId,
    string DepartmentName,
    int PrimaryCategoryId,
    string PrimaryCategoryName,
    decimal BasePrice,
    decimal? CompareAtPrice,
    ProductBadge? BadgeOverride,
    bool IsPublished,
    DateTime? FirstPublishedAtUtc,
    DateTime? UpdatedAtUtc,
    int VariantCount,
    int ColourCount,
    bool HasVideo,
    string? CardImageUrl,
    string RowVersion);

/// <summary>One colourway as the product form reads and writes it (doc 11 P1 step 8).</summary>
/// <param name="Id">
/// The existing row's id, or <c>null</c> for one the form has just added. A client-side temporary key is
/// deliberately absent: a colour is identified by its <see cref="Name"/> within the product, which is what
/// <c>UX_ProductColours_Product_Name</c> already guarantees, so the variants and media that reference it
/// can name the colour rather than a number the server has never seen.
/// </param>
public sealed record AdminProductColourDto(
    int? Id,
    string Name,
    int ColourFamilyId,
    string? ColourFamilyName,
    string? SwatchHex,
    Guid? SwatchAssetPublicId,
    string? SwatchAssetUrl,
    short SortOrder);

/// <summary>
/// One buyable colour × size combination (doc 11 P1 step 9).
/// </summary>
/// <param name="ColourName">
/// Which colourway this variant belongs to, by name rather than by id — see
/// <see cref="AdminProductColourDto.Id"/>. It is how a variant can be sent in the same request as the
/// colour it belongs to.
/// </param>
/// <param name="HasOrders">
/// Whether an order line already names this variant. The form shows it as deactivate-only, because doc 08
/// §3.2 says an ordered variant is never deleted, and the API refuses the delete for the same reason.
/// </param>
/// <param name="OnHand">
/// Read-only here. Stock is P2's (ADM-12) and moves through the inventory ledger; the number is shown so an
/// admin deactivating a variant can see what they are hiding.
/// </param>
public sealed record AdminProductVariantDto(
    int? Id,
    string ColourName,
    int SizeId,
    string? SizeLabel,
    string? SizeScaleName,
    string Sku,
    string? Barcode,
    decimal? PriceOverride,
    decimal? CompareAtPriceOverride,
    bool IsActive,
    short SortOrder,
    bool HasOrders,
    int OnHand);

/// <summary>
/// One gallery image, hover image or video attached to a product (doc 11 P1 step 11).
/// </summary>
/// <param name="ColourName">
/// The colourway whose gallery this belongs to, or <c>null</c> for one that belongs to every colourway.
/// </param>
/// <param name="AltTextOverride">
/// What this product calls the picture, overriding the library asset's own alt text. Required on every
/// public image before the product may be published (doc 11 P1 step 14, §7 G-01).
/// </param>
/// <param name="AssetAltText">The library asset's own alt text, so the form can show what it would fall back to.</param>
public sealed record AdminProductMediaDto(
    int? Id,
    Guid AssetPublicId,
    MediaAssetType MediaType,
    string? Url,
    string? PosterUrl,
    string? ColourName,
    ProductMediaRole MediaRole,
    short SortOrder,
    string? AltTextOverride,
    string? AssetAltText);

/// <summary>A merchandiser's related-product pick (PDP-06, doc 11 P1 step 12).</summary>
public sealed record AdminRelatedProductDto(
    int RelatedProductId,
    string Name,
    string ProductCode,
    bool IsPublished,
    short SortOrder);

/// <summary>
/// A product as the add/edit form reads it (ADM-10). It is the whole aggregate: the scalar fields, the
/// extra category links, the colourways, the variant matrix, the media and the related picks.
/// </summary>
/// <param name="RowVersion">Base64 of <c>catalog.Products.RowVersion</c> (doc 08 §1.5 [RV]).</param>
/// <param name="ExtraCategoryIds">
/// Categories the product appears in <b>besides</b> its primary one. The primary category is stored in
/// <c>catalog.ProductCategories</c> too (doc 08 §3.2), and is deliberately not repeated here: a form that
/// could leave it out of the list would be a form that could unlink a product from its own category.
/// </param>
/// <param name="CanBePublished">
/// Whether the publish checklist passes right now (doc 11 §7 G-01). The screen uses it to explain the
/// button before it is pressed; the API checks it again regardless.
/// </param>
/// <param name="PublishBlockers">One sentence per unmet publish rule, empty when <paramref name="CanBePublished"/>.</param>
public sealed record AdminProductDetailDto(
    int Id,
    string ProductCode,
    string Name,
    string Slug,
    int DepartmentId,
    int PrimaryCategoryId,
    IReadOnlyList<int> ExtraCategoryIds,
    int? AgeGroupId,
    string? ShortDescription,
    string? Description,
    string? CareInstructions,
    int? MaterialId,
    int? FitId,
    int? OccasionId,
    decimal BasePrice,
    decimal? CompareAtPrice,
    ProductBadge? BadgeOverride,
    DateTime? NewUntilUtc,
    int? RecommendedRank,
    int? SizeGuideId,
    string? SeoTitle,
    string? SeoDescription,
    bool IsPublished,
    DateTime? FirstPublishedAtUtc,
    string? CardImageUrl,
    string? HoverImageUrl,
    IReadOnlyList<AdminProductColourDto> Colours,
    IReadOnlyList<AdminProductVariantDto> Variants,
    IReadOnlyList<AdminProductMediaDto> Media,
    IReadOnlyList<AdminRelatedProductDto> RelatedProducts,
    bool CanBePublished,
    IReadOnlyList<string> PublishBlockers,
    string RowVersion);

/// <summary>A category a product may be filed under, flattened with its indented display name.</summary>
/// <param name="DisplayName">
/// "Clothing › Dresses › Midi". The form shows a flat <c>&lt;select&gt;</c> rather than a tree: a tree
/// control inside a form is a second keyboard model to learn, and at 320 px it is a scrolling box inside a
/// scrolling page.
/// </param>
public sealed record ProductCategoryOptionDto(
    int Id,
    int DepartmentId,
    int? ParentCategoryId,
    byte Depth,
    string Name,
    string DisplayName,
    bool IsActive);

/// <summary>One size in a scale, for the size-scale picker that generates the variant matrix.</summary>
public sealed record ProductSizeOptionDto(int Id, string ScaleName, string Label, short SortOrder);

/// <summary>A colour family, with its hex so the picker can show the swatch it stands for.</summary>
public sealed record ColourFamilyOptionDto(int Id, string Name, string HexCode);

/// <summary>
/// Everything the product form has to choose from (ADM-10, doc 11 P1 steps 6–12).
/// <para>
/// One payload rather than eight endpoints: the form needs all of it before it can render a single row of
/// the variant matrix, and eight requests is eight chances for a half-populated form. Every list here is a
/// small seeded reference table (doc 08 §9) — the largest is <c>catalog.Sizes</c>, which the demo seeds with
/// a few dozen rows.
/// </para>
/// <para>
/// <b>Deactivated rows are included and flagged</b>, not filtered. A product already filed under a
/// deactivated category must still show which one, or saving the form would silently move it.
/// </para>
/// </summary>
public sealed record ProductFormOptionsDto(
    IReadOnlyList<CatalogOptionDto> Departments,
    IReadOnlyList<ProductCategoryOptionDto> Categories,
    IReadOnlyList<CatalogOptionDto> AgeGroups,
    IReadOnlyList<CatalogOptionDto> Materials,
    IReadOnlyList<CatalogOptionDto> Fits,
    IReadOnlyList<CatalogOptionDto> Occasions,
    IReadOnlyList<ColourFamilyOptionDto> ColourFamilies,
    IReadOnlyList<ProductSizeOptionDto> Sizes,
    IReadOnlyList<CatalogOptionDto> SizeGuides);
