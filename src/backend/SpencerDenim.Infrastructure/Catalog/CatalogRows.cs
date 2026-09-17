using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Infrastructure.Catalog;

/// <summary>
/// The flat shape a listing projection returns. Badge and stock status are <b>not</b> in it: they are
/// derived in memory by the domain rules (<c>ProductBadgeResolver</c>, <c>StockAvailability</c>) from
/// the raw numbers below, so the same rules are unit tested once and used everywhere.
/// </summary>
internal sealed record ProductCardRow(
    int Id,
    string ProductCode,
    string Name,
    string Slug,
    int DepartmentId,
    string DepartmentName,
    string DepartmentSlug,
    string CategoryName,
    string CategorySlug,
    string? CardImageKey,
    string? CardImageAltText,
    string? HoverImageKey,
    decimal MinSalePrice,
    decimal MaxOriginalPrice,
    decimal MaxDiscountPercent,
    bool HasDiscount,
    decimal? RatingAverage,
    int RatingCount,
    int SoldQty30d,
    ProductBadge? BadgeOverride,
    DateTime? NewUntilUtc,
    int TotalAvailable,
    int TotalLowStockThreshold,
    IReadOnlyList<ColourRow> Colours);

/// <summary>One colourway as the projection returns it.</summary>
internal sealed record ColourRow(
    int Id,
    string Name,
    string FamilyName,
    string FamilySlug,
    string? SwatchHex,
    string? SwatchImageKey,
    short SortOrder);

/// <summary>One gallery row as the projection returns it.</summary>
internal sealed record MediaRow(
    int Id,
    string StorageKey,
    string? AltText,
    ProductMediaRole Role,
    int? ColourId,
    int? Width,
    int? Height,
    string? PosterKey,
    short SortOrder);

/// <summary>One variant as the projection returns it, before pricing and stock rules are applied.</summary>
internal sealed record VariantRow(
    int Id,
    string Sku,
    int ColourId,
    string ColourName,
    int SizeId,
    string SizeLabel,
    string ScaleName,
    short SortOrder,
    decimal? PriceOverride,
    decimal? CompareAtPriceOverride,
    int Available,
    int? LowStockThreshold);

/// <summary>The product page as the projection returns it, before mapping to the public DTO.</summary>
internal sealed record ProductDetailRow(
    int Id,
    string ProductCode,
    string Name,
    string Slug,
    decimal BasePrice,
    decimal? CompareAtPrice,
    int DepartmentId,
    string DepartmentName,
    string DepartmentSlug,
    int PrimaryCategoryId,
    string CategoryPath,
    string? ShortDescription,
    string? Description,
    string? CareInstructions,
    string? MaterialName,
    string? MaterialSlug,
    string? FitName,
    string? FitSlug,
    string? OccasionName,
    string? OccasionSlug,
    string? AgeGroupName,
    string? AgeGroupSlug,
    string? SeoTitle,
    string? SeoDescription,
    int? SizeGuideId,
    string? SizeGuideName,
    ProductBadge? BadgeOverride,
    DateTime? NewUntilUtc,
    decimal MinSalePrice,
    decimal MaxSalePrice,
    decimal MaxOriginalPrice,
    decimal MaxDiscountPercent,
    bool HasDiscount,
    decimal? RatingAverage,
    int RatingCount,
    int SoldQty30d,
    IReadOnlyList<ColourRow> Colours,
    IReadOnlyList<MediaRow> Media,
    IReadOnlyList<VariantRow> Variants,
    IReadOnlyList<int> RelatedProductIds);
