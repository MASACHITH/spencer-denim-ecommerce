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

/// <summary>One colour dot on a card, and one swatch on the product page.</summary>
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
    string? HoverImageUrl,
    string? ImageAltText,
    PriceDto Price,
    RatingDto Rating,
    ProductBadge? Badge,
    StockStatus StockStatus,
    bool IsInStock,
    IReadOnlyList<ColourSwatchDto> Colours);
