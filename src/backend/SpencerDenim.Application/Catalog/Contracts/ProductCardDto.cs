using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Inventory;

namespace SpencerDenim.Application.Catalog.Contracts;

/// <summary>Price block shown on a card and on the product page (CARD-01, PDP-02).</summary>
/// <param name="Current">What the customer pays today, after the best active campaign.</param>
/// <param name="Original">The struck-through "was" price. Equal to <paramref name="Current"/> with no discount.</param>
/// <param name="DiscountPercent">Displayed discount, floored to a whole percent (doc 08 §6 step 4).</param>
/// <param name="HasDiscount">True when the card shows the original price and the discount.</param>
/// <param name="Currency">Always <c>LKR</c> in v1.</param>
public sealed record PriceDto(
    decimal Current,
    decimal Original,
    decimal DiscountPercent,
    bool HasDiscount,
    string Currency)
{
    public const string Lkr = "LKR";
}

/// <summary>Review summary (PDP-08, CARD-01). <paramref name="Average"/> is null until a product has an approved review.</summary>
public sealed record RatingDto(decimal? Average, int Count);

/// <summary>
/// One colour dot on a card, and one swatch on the product page.
/// </summary>
/// <param name="SwatchImageUrl">
/// The <c>thumb</c> rendition. A swatch is drawn at 14 px on a card and a little larger on the product
/// page, so it is the one slot where the smallest rendition is genuinely the right file and there is
/// nothing for a <c>srcset</c> to choose between.
/// </param>
public sealed record ColourSwatchDto(
    int Id,
    string Name,
    string FamilyName,
    string FamilySlug,
    string? HexCode,
    string? SwatchImageUrl);

/// <summary>
/// Everything CARD-01 renders: main and hover image, badge, name, category, price, rating and colour
/// dots. Built by one projection, so a listing page is a single round trip.
/// </summary>
/// <param name="ImageUrl">
/// The <c>card</c> rendition (doc 03 §3b), or the original when the asset has none. A listing draws
/// 24 of these, so this is the field that decides whether a page on mobile data weighs a few hundred
/// kilobytes or nearly two megabytes.
/// </param>
/// <param name="ImageSrcSet">
/// The candidate widths for <paramref name="ImageUrl"/>, up to <c>large</c>; null when the asset has no
/// renditions. The client pairs it with a <c>sizes</c> attribute describing its own grid — see
/// <c>MediaUrlBuilder.ToImageSources</c> for why <c>sizes</c> is not the server's to give.
/// </param>
/// <param name="HoverImageUrl">The second photo, same rendition as <paramref name="ImageUrl"/>.</param>
/// <param name="HoverImageSrcSet">The candidate widths for <paramref name="HoverImageUrl"/>.</param>
public sealed record ProductCardDto(
    int Id,
    string ProductCode,
    string Name,
    string Slug,
    string DepartmentName,
    string DepartmentSlug,
    string CategoryName,
    string CategorySlug,
    string? ImageUrl,
    string? ImageSrcSet,
    string? HoverImageUrl,
    string? HoverImageSrcSet,
    string? ImageAltText,
    PriceDto Price,
    RatingDto Rating,
    ProductBadge? Badge,
    StockStatus StockStatus,
    bool IsInStock,
    IReadOnlyList<ColourSwatchDto> Colours);
