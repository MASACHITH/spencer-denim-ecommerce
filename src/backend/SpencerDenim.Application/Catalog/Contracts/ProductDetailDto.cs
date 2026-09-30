using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Inventory;

namespace SpencerDenim.Application.Catalog.Contracts;

/// <summary>A breadcrumb step (PLP-01, PDP-01).</summary>
public sealed record CategoryRefDto(int Id, string Name, string Slug);

/// <summary>The PDP-05 details block: material, fit, occasion and age group, resolved to names.</summary>
public sealed record ProductAttributesDto(
    string? Material,
    string? MaterialSlug,
    string? Fit,
    string? FitSlug,
    string? Occasion,
    string? OccasionSlug,
    string? AgeGroup,
    string? AgeGroupSlug);

/// <summary>
/// One gallery item (PDP-01). Videos carry a poster image.
/// <para>
/// The gallery draws the same asset in three places at three very different sizes, so it is given
/// three URLs rather than being left to guess: the rail, the stage and the full-screen viewer. Each of
/// the rendition fields falls back to <paramref name="Url"/> being the original when the asset has no
/// rendition set, so a gallery never has a slot with nothing in it.
/// </para>
/// </summary>
/// <param name="Url">The stage: the <c>large</c> rendition, or the original when there is none.</param>
/// <param name="SrcSet">
/// Candidates for the stage, up to <c>zoom</c> — a full-width stage on a retina tablet genuinely wants
/// more than <c>large</c>. Null when the asset has no renditions.
/// </param>
/// <param name="ThumbnailUrl">The rail: the <c>thumb</c> rendition, or the original when there is none.</param>
/// <param name="ZoomUrl">
/// The full-screen viewer: the largest rendition that exists — <c>zoom</c>, or <c>large</c> when the
/// source was too small for the two to differ — or the original when the asset has no renditions at
/// all. It is a separate field rather than the largest <paramref name="SrcSet"/> candidate because the
/// viewer is opened deliberately and magnifies past 1×, so it wants the biggest file unconditionally —
/// which is exactly the choice a <c>srcset</c> is designed to stop the browser making.
/// </param>
/// <param name="PosterUrl">
/// A video's poster frame. Always an original: a poster is a separate asset that the pipeline renders
/// no renditions for.
/// </param>
public sealed record ProductImageDto(
    int Id,
    string Url,
    string? SrcSet,
    string? ThumbnailUrl,
    string? ZoomUrl,
    string? AltText,
    ProductMediaRole Role,
    int? ColourId,
    int? Width,
    int? Height,
    string? PosterUrl,
    short SortOrder);

/// <summary>One size offered by a product, with the scale it belongs to (PDP-03, PLP-02).</summary>
public sealed record ProductSizeDto(int Id, string Label, string ScaleName, short SortOrder, bool IsInStock);

/// <summary>
/// One buyable colour × size combination (PDP-03). Price comes from the variant override where there
/// is one, so the page can re-price when the customer picks a size.
/// </summary>
public sealed record ProductVariantDto(
    int Id,
    string Sku,
    int ColourId,
    string ColourName,
    int SizeId,
    string SizeLabel,
    string ScaleName,
    PriceDto Price,
    StockStatus StockStatus,
    bool IsInStock,
    int AvailableQuantity);

/// <summary>The size guide the product page opens (PDP-04), inherited from the category when unset.</summary>
public sealed record SizeGuideRefDto(int Id, string Name);

/// <summary>Meta tags for server-side rendering (GLB-11).</summary>
public sealed record SeoDto(string Title, string? Description);

/// <summary>
/// The whole product page payload (PDP-01..06) in one response: gallery, colours, sizes, variants with
/// stock, price and discount, attributes and related products.
/// </summary>
public sealed record ProductDetailDto(
    int Id,
    string ProductCode,
    string Name,
    string Slug,
    CategoryRefDto Department,
    IReadOnlyList<CategoryRefDto> Breadcrumb,
    string? ShortDescription,
    string? Description,
    string? CareInstructions,
    ProductAttributesDto Attributes,
    PriceDto Price,
    decimal MinPrice,
    decimal MaxPrice,
    RatingDto Rating,
    ProductBadge? Badge,
    StockStatus StockStatus,
    bool IsInStock,
    SeoDto Seo,
    SizeGuideRefDto? SizeGuide,
    DateTime? NewUntilUtc,
    IReadOnlyList<ProductImageDto> Images,
    IReadOnlyList<ColourSwatchDto> Colours,
    IReadOnlyList<ProductSizeDto> Sizes,
    IReadOnlyList<ProductVariantDto> Variants,
    IReadOnlyList<ProductCardDto> RelatedProducts);
