using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Catalog.Abstractions;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Application.Catalog.Products;
using SpencerDenim.Application.Catalog.Search;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Inventory;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Catalog;

/// <summary>
/// The storefront read model (PLP-01..07, CARD-01, PDP-01..06, SRCH-02), implemented exactly as
/// doc 08 §7 describes: predicates are composed only for the filters that are present, the published
/// and not-deleted predicates stay literals so the filtered indexes match (DB28), stock is derived at
/// read time (DB6), and every query is <c>AsNoTracking</c> with a projection, so no EF entity leaves
/// this class.
/// </summary>
internal sealed class ProductCatalogReader(
    AppDbContext dbContext,
    IOptions<CatalogOptions> options,
    IClock clock)
    : IProductCatalogReader
{
    private readonly CatalogOptions _options = options.Value;

    public async Task<PagedResult<ProductCardDto>> ListAsync(
        ProductListQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var filters = await ResolveFiltersAsync(query, cancellationToken);
        if (filters.MatchesNothing)
        {
            // A filter named a slug or size that does not exist: the correct answer is the PLP-06
            // "No products found" state, not an error and not an unfiltered listing.
            return PagedResult.Empty<ProductCardDto>(query.Page, query.PageSize);
        }

        var rows = ApplyFilters(BaseQuery(), query, filters);

        var totalCount = await rows.CountAsync(cancellationToken);
        if (totalCount == 0)
        {
            return PagedResult.Empty<ProductCardDto>(query.Page, query.PageSize);
        }

        var page = await ApplySort(rows, query, filters.CategoryId)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(CardProjection(_options.DefaultLowStockThreshold))
            .ToListAsync(cancellationToken);

        var cards = await ToCardsAsync(page, cancellationToken);
        return new PagedResult<ProductCardDto>(cards, query.Page, query.PageSize, totalCount);
    }

    public async Task<PagedResult<ProductCardDto>> SearchAsync(
        ProductSearchQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var listQuery = query.ListQuery;
        var filters = await ResolveFiltersAsync(listQuery, cancellationToken);
        if (filters.MatchesNothing)
        {
            return PagedResult.Empty<ProductCardDto>(listQuery.Page, listQuery.PageSize);
        }

        var rows = ApplyFilters(BaseQuery(), listQuery, filters);

        // Doc 08 §7.6: every token must match, as an escaped LIKE over the denormalised search text.
        var searchRows = rows.Join(
            dbContext.ProductSearchIndexes,
            row => row.Product.Id,
            index => index.ProductId,
            (row, index) => new SearchRow(row.Product, row.Stats, index));

        foreach (var token in query.Tokens)
        {
            var pattern = SearchTerms.ToContainsPattern(token);
            searchRows = searchRows.Where(row =>
                EF.Functions.Like(row.Index.SearchText, pattern, EscapeString));
        }

        var totalCount = await searchRows.CountAsync(cancellationToken);
        if (totalCount == 0)
        {
            return PagedResult.Empty<ProductCardDto>(listQuery.Page, listQuery.PageSize);
        }

        IQueryable<ProductRow> ordered = query.SortByRelevance
            ? ApplyRelevanceSort(searchRows, query.Tokens[0])
            : ApplySort(searchRows.Select(row => new ProductRow(row.Product, row.Stats)), listQuery, filters.CategoryId);

        var page = await ordered
            .Skip(listQuery.Skip)
            .Take(listQuery.PageSize)
            .Select(CardProjection(_options.DefaultLowStockThreshold))
            .ToListAsync(cancellationToken);

        var cards = await ToCardsAsync(page, cancellationToken);
        return new PagedResult<ProductCardDto>(cards, listQuery.Page, listQuery.PageSize, totalCount);
    }

    public async Task<ProductDetailDto?> GetBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var defaultThreshold = _options.DefaultLowStockThreshold;

        var row = await BaseQuery()
            .Where(product => product.Product.Slug == slug)
            .Select(source => new ProductDetailRow(
                source.Product.Id,
                source.Product.ProductCode,
                source.Product.Name,
                source.Product.Slug,
                source.Product.BasePrice,
                source.Product.CompareAtPrice,
                source.Product.DepartmentId,
                source.Product.Department!.Name,
                source.Product.Department.Slug,
                source.Product.PrimaryCategoryId,
                source.Product.PrimaryCategory!.Path,
                source.Product.ShortDescription,
                source.Product.Description,
                source.Product.CareInstructions,
                source.Product.Material != null ? source.Product.Material.Name : null,
                source.Product.Material != null ? source.Product.Material.Slug : null,
                source.Product.Fit != null ? source.Product.Fit.Name : null,
                source.Product.Fit != null ? source.Product.Fit.Slug : null,
                source.Product.Occasion != null ? source.Product.Occasion.Name : null,
                source.Product.Occasion != null ? source.Product.Occasion.Slug : null,
                source.Product.AgeGroup != null ? source.Product.AgeGroup.Name : null,
                source.Product.AgeGroup != null ? source.Product.AgeGroup.Slug : null,
                source.Product.SeoTitle,
                source.Product.SeoDescription,
                source.Product.SizeGuideId ?? source.Product.PrimaryCategory.SizeGuideId,
                source.Product.SizeGuide != null
                    ? source.Product.SizeGuide.Name
                    : source.Product.PrimaryCategory.SizeGuide != null
                        ? source.Product.PrimaryCategory.SizeGuide.Name
                        : null,
                source.Product.BadgeOverride,
                source.Product.NewUntilUtc,
                source.Stats.MinSalePrice,
                source.Stats.MaxSalePrice,
                source.Stats.MaxOriginalPrice,
                source.Stats.MaxDiscountPercent,
                source.Stats.HasDiscount,
                source.Stats.RatingAverage,
                source.Stats.RatingCount,
                source.Stats.SoldQty30d,
                source.Product.Colours
                    .OrderBy(colour => colour.SortOrder)
                    .ThenBy(colour => colour.Id)
                    .Select(colour => new ColourRow(
                        colour.Id,
                        colour.Name,
                        colour.ColourFamily!.Name,
                        colour.ColourFamily.Slug,
                        colour.SwatchHex,
                        colour.SwatchAsset != null ? colour.SwatchAsset.StorageKey : null,
                        colour.SortOrder))
                    .ToList(),
                source.Product.Media
                    .OrderBy(media => media.SortOrder)
                    .ThenBy(media => media.Id)
                    .Select(media => new MediaRow(
                        media.Id,
                        media.MediaAsset!.StorageKey,
                        media.AltTextOverride ?? media.MediaAsset.AltText,
                        media.MediaRole,
                        media.ProductColourId,
                        media.MediaAsset.Width,
                        media.MediaAsset.Height,
                        media.MediaAsset.PosterAsset != null ? media.MediaAsset.PosterAsset.StorageKey : null,
                        media.SortOrder))
                    .ToList(),
                source.Product.Variants
                    .Where(variant => variant.IsActive)
                    .OrderBy(variant => variant.ProductColourId)
                    .ThenBy(variant => variant.Size!.SortOrder)
                    .ThenBy(variant => variant.Id)
                    .Select(variant => new VariantRow(
                        variant.Id,
                        variant.Sku,
                        variant.ProductColourId,
                        variant.ProductColour!.Name,
                        variant.SizeId,
                        variant.Size!.Label,
                        variant.Size.ScaleName,
                        variant.Size.SortOrder,
                        variant.PriceOverride,
                        variant.CompareAtPriceOverride,
                        variant.StockLevel != null ? variant.StockLevel.Available : 0,
                        variant.StockLevel != null ? variant.StockLevel.LowStockThreshold : null))
                    .ToList(),
                source.Product.RelatedProducts
                    .OrderBy(related => related.SortOrder)
                    .ThenBy(related => related.RelatedProductId)
                    .Select(related => related.RelatedProductId)
                    .ToList()))
            .AsSplitQuery()
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var breadcrumb = await LoadBreadcrumbAsync(row.CategoryPath, cancellationToken);
        var related = await LoadRelatedAsync(row, cancellationToken);

        return MapDetail(row, breadcrumb, related, defaultThreshold);
    }

    // ---------------------------------------------------------------- query building

    /// <summary>
    /// The listing's starting point (doc 08 §7.1). <c>IsPublished</c> is written as a constant and the
    /// soft-delete query filter is a constant too, so both render as literals and
    /// <c>IX_Products_Listing</c> (filtered on exactly those two columns) can be matched (DB28).
    /// </summary>
    private IQueryable<ProductRow> BaseQuery() =>
        dbContext.Products
            .AsNoTracking()
            .Where(product => product.IsPublished)
            .Join(
                dbContext.ProductStats,
                product => product.Id,
                stats => stats.ProductId,
                (product, stats) => new ProductRow(product, stats));

    private static IQueryable<ProductRow> ApplyFilters(
        IQueryable<ProductRow> rows,
        ProductListQuery query,
        ResolvedFilters filters)
    {
        if (filters.DepartmentId is { } departmentId)
        {
            rows = rows.Where(row => row.Product.DepartmentId == departmentId);
        }

        if (filters.CategorySubtreeIds.Count > 0)
        {
            var categoryIds = filters.CategorySubtreeIds;
            rows = rows.Where(row => row.Product.CategoryLinks.Any(link => categoryIds.Contains(link.CategoryId)));
        }

        if (filters.AgeGroupIds.Count > 0)
        {
            var ageGroupIds = filters.AgeGroupIds;
            rows = rows.Where(row => row.Product.AgeGroupId != null && ageGroupIds.Contains(row.Product.AgeGroupId.Value));
        }

        if (filters.MaterialIds.Count > 0)
        {
            var materialIds = filters.MaterialIds;
            rows = rows.Where(row => row.Product.MaterialId != null && materialIds.Contains(row.Product.MaterialId.Value));
        }

        if (filters.FitIds.Count > 0)
        {
            var fitIds = filters.FitIds;
            rows = rows.Where(row => row.Product.FitId != null && fitIds.Contains(row.Product.FitId.Value));
        }

        if (filters.OccasionIds.Count > 0)
        {
            var occasionIds = filters.OccasionIds;
            rows = rows.Where(row => row.Product.OccasionId != null && occasionIds.Contains(row.Product.OccasionId.Value));
        }

        if (filters.ColourFamilyIds.Count > 0)
        {
            var colourFamilyIds = filters.ColourFamilyIds;
            rows = rows.Where(row => row.Product.Colours.Any(colour => colourFamilyIds.Contains(colour.ColourFamilyId)));
        }

        // Size and availability are one EXISTS over the active variants (doc 08 §7.1), so SQL Server
        // touches ProductVariants and StockLevels once instead of twice.
        var sizeIds = filters.SizeIds;
        if (sizeIds.Count > 0 && query.InStockOnly)
        {
            rows = rows.Where(row => row.Product.Variants.Any(variant =>
                variant.IsActive
                && sizeIds.Contains(variant.SizeId)
                && variant.StockLevel!.Available > 0));
        }
        else if (sizeIds.Count > 0)
        {
            rows = rows.Where(row => row.Product.Variants.Any(variant =>
                variant.IsActive && sizeIds.Contains(variant.SizeId)));
        }
        else if (query.InStockOnly)
        {
            rows = rows.Where(row => row.Product.Variants.Any(variant =>
                variant.IsActive && variant.StockLevel!.Available > 0));
        }

        if (query.MinPrice is { } minPrice)
        {
            rows = rows.Where(row => row.Stats.MinSalePrice >= minPrice);
        }

        if (query.MaxPrice is { } maxPrice)
        {
            rows = rows.Where(row => row.Stats.MinSalePrice <= maxPrice);
        }

        if (query.MinRating is { } minRating)
        {
            rows = rows.Where(row => row.Stats.RatingAverage >= minRating);
        }

        if (query.OnSaleOnly)
        {
            rows = rows.Where(row => row.Stats.HasDiscount);
        }

        if (query.MinDiscountPercent is { } minDiscount and > 0m)
        {
            rows = rows.Where(row => row.Stats.MaxDiscountPercent >= minDiscount);
        }

        if (query.NewArrivalsOnly)
        {
            var now = filters.UtcNow;
            rows = rows.Where(row => row.Product.NewUntilUtc != null && row.Product.NewUntilUtc > now);
        }

        return rows;
    }

    /// <summary>Doc 08 §7.3. Every sort ends with the product ID, so paging is stable.</summary>
    private static IOrderedQueryable<ProductRow> ApplySort(
        IQueryable<ProductRow> rows,
        ProductListQuery query,
        int? categoryId)
    {
        switch (query.Sort)
        {
            case ProductSortOption.Newest:
                return rows
                    .OrderByDescending(row => row.Product.FirstPublishedAtUtc)
                    .ThenBy(row => row.Product.Id);

            case ProductSortOption.BestSelling:
                return rows
                    .OrderByDescending(row => row.Stats.SoldQty30d)
                    .ThenByDescending(row => row.Stats.SoldQtyTotal)
                    .ThenBy(row => row.Product.Id);

            case ProductSortOption.PriceAscending:
                return rows
                    .OrderBy(row => row.Stats.MinSalePrice)
                    .ThenBy(row => row.Product.Id);

            case ProductSortOption.PriceDescending:
                return rows
                    .OrderByDescending(row => row.Stats.MinSalePrice)
                    .ThenBy(row => row.Product.Id);

            case ProductSortOption.HighestRated:
                // NULLs last: an unrated product must not outrank a five-star one.
                return rows
                    .OrderBy(row => row.Stats.RatingAverage == null)
                    .ThenByDescending(row => row.Stats.RatingAverage)
                    .ThenByDescending(row => row.Stats.RatingCount)
                    .ThenBy(row => row.Product.Id);

            case ProductSortOption.BiggestDiscount:
                return rows
                    .OrderByDescending(row => row.Stats.MaxDiscountPercent)
                    .ThenBy(row => row.Product.Id);

            case ProductSortOption.Recommended:
            default:
                if (categoryId is { } selectedCategory)
                {
                    // Category page: the merchandiser's manual order inside this category wins,
                    // NULLs last, then the global pin, then sales and recency (doc 08 §7.3).
                    return rows
                        .OrderBy(row => row.Product.CategoryLinks
                            .Where(link => link.CategoryId == selectedCategory)
                            .Select(link => link.SortOrder)
                            .FirstOrDefault() == null)
                        .ThenBy(row => row.Product.CategoryLinks
                            .Where(link => link.CategoryId == selectedCategory)
                            .Select(link => link.SortOrder)
                            .FirstOrDefault())
                        .ThenBy(row => row.Product.RecommendedRank == null)
                        .ThenBy(row => row.Product.RecommendedRank)
                        .ThenByDescending(row => row.Stats.SoldQty30d)
                        .ThenByDescending(row => row.Product.FirstPublishedAtUtc)
                        .ThenBy(row => row.Product.Id);
                }

                return rows
                    .OrderBy(row => row.Product.RecommendedRank == null)
                    .ThenBy(row => row.Product.RecommendedRank)
                    .ThenByDescending(row => row.Stats.SoldQty30d)
                    .ThenByDescending(row => row.Product.FirstPublishedAtUtc)
                    .ThenBy(row => row.Product.Id);
        }
    }

    /// <summary>
    /// Doc 08 §7.6 relevance: name prefix 100, name contains 50, search text contains 10, plus
    /// <c>SoldQty30d</c>. Scored on the first token, which is the one a shopper leads with.
    /// </summary>
    private static IQueryable<ProductRow> ApplyRelevanceSort(IQueryable<SearchRow> rows, string leadToken)
    {
        var prefix = SearchTerms.ToPrefixPattern(leadToken);
        var wordPrefix = SearchTerms.ToWordPrefixPattern(leadToken);
        var contains = SearchTerms.ToContainsPattern(leadToken);

        // The ORDER BY survives the projection below: EF keeps it in the generated SQL.
        return rows
            .OrderByDescending(row =>
                (EF.Functions.Like(row.Index.NameText, prefix, EscapeString) ? 100 : 0)
                + (EF.Functions.Like(row.Index.NameText, wordPrefix, EscapeString) ? 50 : 0)
                + (EF.Functions.Like(row.Index.NameText, contains, EscapeString) ? 20 : 0)
                + 10
                + row.Stats.SoldQty30d)
            .ThenBy(row => row.Product.Id)
            .Select(row => new ProductRow(row.Product, row.Stats));
    }

    // ---------------------------------------------------------------- projections and mapping

    private static System.Linq.Expressions.Expression<Func<ProductRow, ProductCardRow>> CardProjection(
        int defaultLowStockThreshold) =>
        source => new ProductCardRow(
            source.Product.Id,
            source.Product.ProductCode,
            source.Product.Name,
            source.Product.Slug,
            source.Product.DepartmentId,
            source.Product.Department!.Name,
            source.Product.Department.Slug,
            source.Product.PrimaryCategory!.Name,
            source.Product.PrimaryCategory.Slug,
            source.Product.CardImageAsset != null ? source.Product.CardImageAsset.StorageKey : null,
            source.Product.CardImageAsset != null ? source.Product.CardImageAsset.AltText : null,
            source.Product.HoverImageAsset != null ? source.Product.HoverImageAsset.StorageKey : null,
            source.Stats.MinSalePrice,
            source.Stats.MaxOriginalPrice,
            source.Stats.MaxDiscountPercent,
            source.Stats.HasDiscount,
            source.Stats.RatingAverage,
            source.Stats.RatingCount,
            source.Stats.SoldQty30d,
            source.Product.BadgeOverride,
            source.Product.NewUntilUtc,
            source.Product.Variants
                .Where(variant => variant.IsActive && variant.StockLevel != null)
                .Sum(variant => variant.StockLevel!.Available),
            source.Product.Variants
                .Where(variant => variant.IsActive && variant.StockLevel != null)
                .Sum(variant => variant.StockLevel!.LowStockThreshold ?? defaultLowStockThreshold),
            source.Product.Colours
                .OrderBy(colour => colour.SortOrder)
                .ThenBy(colour => colour.Id)
                .Select(colour => new ColourRow(
                    colour.Id,
                    colour.Name,
                    colour.ColourFamily!.Name,
                    colour.ColourFamily.Slug,
                    colour.SwatchHex,
                    colour.SwatchAsset != null ? colour.SwatchAsset.StorageKey : null,
                    colour.SortOrder))
                .ToList());

    private async Task<IReadOnlyList<ProductCardDto>> ToCardsAsync(
        List<ProductCardRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var bestSellerThresholds = await LoadBestSellerThresholdsAsync(
            rows.Select(row => row.DepartmentId).Distinct().ToList(),
            cancellationToken);

        var utcNow = clock.UtcNow.UtcDateTime;
        return rows.Select(row => ToCard(row, bestSellerThresholds, utcNow)).ToList().AsReadOnly();
    }

    private ProductCardDto ToCard(
        ProductCardRow row,
        Dictionary<int, int> bestSellerThresholds,
        DateTime utcNow)
    {
        var stockStatus = StockAvailability.Evaluate(row.TotalAvailable, row.TotalLowStockThreshold);

        var isBestSeller = bestSellerThresholds.TryGetValue(row.DepartmentId, out var threshold)
            && row.SoldQty30d > 0
            && row.SoldQty30d >= threshold;

        var badge = ProductBadgeResolver.Resolve(
            row.BadgeOverride,
            row.HasDiscount,
            stockStatus,
            isBestSeller,
            row.NewUntilUtc,
            utcNow);

        return new ProductCardDto(
            row.Id,
            row.ProductCode,
            row.Name,
            row.Slug,
            row.DepartmentName,
            row.DepartmentSlug,
            row.CategoryName,
            row.CategorySlug,
            MediaUrlBuilder.ToPublicUrl(_options.MediaBaseUrl, row.CardImageKey),
            MediaUrlBuilder.ToPublicUrl(_options.MediaBaseUrl, row.HoverImageKey),
            row.CardImageAltText,
            new PriceDto(
                row.MinSalePrice,
                row.MaxOriginalPrice,
                row.MaxDiscountPercent,
                row.HasDiscount,
                PriceDto.Lkr),
            new RatingDto(row.RatingAverage, row.RatingCount),
            badge,
            stockStatus,
            StockAvailability.IsInStock(row.TotalAvailable),
            row.Colours.Select(ToSwatch).ToList().AsReadOnly());
    }

    private ColourSwatchDto ToSwatch(ColourRow colour) => new(
        colour.Id,
        colour.Name,
        colour.FamilyName,
        colour.FamilySlug,
        colour.SwatchHex,
        MediaUrlBuilder.ToPublicUrl(_options.MediaBaseUrl, colour.SwatchImageKey));

    private ProductDetailDto MapDetail(
        ProductDetailRow row,
        IReadOnlyList<CategoryRefDto> breadcrumb,
        IReadOnlyList<ProductCardDto> related,
        int defaultThreshold)
    {
        var utcNow = clock.UtcNow.UtcDateTime;

        var variants = row.Variants.Select(variant =>
        {
            var listPrice = CatalogPricing.ListPrice(row.BasePrice, variant.PriceOverride);
            var originalPrice = CatalogPricing.OriginalPrice(listPrice, row.CompareAtPrice, variant.CompareAtPriceOverride);

            // Campaigns (doc 08 §6 step 3) arrive with marketing in week 3, so today sale == list.
            var salePrice = CatalogPricing.SalePrice(listPrice, campaignPrice: null);
            var threshold = StockAvailability.ResolveThreshold(variant.LowStockThreshold, defaultThreshold);
            var status = StockAvailability.Evaluate(variant.Available, threshold);

            return new ProductVariantDto(
                variant.Id,
                variant.Sku,
                variant.ColourId,
                variant.ColourName,
                variant.SizeId,
                variant.SizeLabel,
                variant.ScaleName,
                new PriceDto(
                    salePrice,
                    originalPrice,
                    CatalogPricing.DiscountPercent(originalPrice, salePrice),
                    CatalogPricing.HasDiscount(originalPrice, salePrice),
                    PriceDto.Lkr),
                status,
                StockAvailability.IsInStock(variant.Available),
                Math.Max(variant.Available, 0));
        })
            .ToList();

        var totalAvailable = row.Variants.Sum(variant => variant.Available);
        var totalThreshold = row.Variants.Sum(variant =>
            StockAvailability.ResolveThreshold(variant.LowStockThreshold, defaultThreshold));
        var stockStatus = StockAvailability.Evaluate(totalAvailable, totalThreshold);

        var badge = ProductBadgeResolver.Resolve(
            row.BadgeOverride,
            row.HasDiscount,
            stockStatus,
            isBestSeller: false,
            row.NewUntilUtc,
            utcNow);

        var sizes = row.Variants
            .GroupBy(variant => new { variant.SizeId, variant.SizeLabel, variant.ScaleName, variant.SortOrder })
            .OrderBy(group => group.Key.SortOrder)
            .ThenBy(group => group.Key.SizeId)
            .Select(group => new ProductSizeDto(
                group.Key.SizeId,
                group.Key.SizeLabel,
                group.Key.ScaleName,
                group.Key.SortOrder,
                group.Any(variant => variant.Available > 0)))
            .ToList();

        var images = row.Media
            .Select(media => new ProductImageDto(
                media.Id,
                MediaUrlBuilder.ToPublicUrl(_options.MediaBaseUrl, media.StorageKey) ?? string.Empty,
                media.AltText,
                media.Role,
                media.ColourId,
                media.Width,
                media.Height,
                MediaUrlBuilder.ToPublicUrl(_options.MediaBaseUrl, media.PosterKey),
                media.SortOrder))
            .ToList();

        return new ProductDetailDto(
            row.Id,
            row.ProductCode,
            row.Name,
            row.Slug,
            new CategoryRefDto(row.DepartmentId, row.DepartmentName, row.DepartmentSlug),
            breadcrumb,
            row.ShortDescription,
            row.Description,
            row.CareInstructions,
            new ProductAttributesDto(
                row.MaterialName,
                row.MaterialSlug,
                row.FitName,
                row.FitSlug,
                row.OccasionName,
                row.OccasionSlug,
                row.AgeGroupName,
                row.AgeGroupSlug),
            new PriceDto(
                row.MinSalePrice,
                row.MaxOriginalPrice,
                row.MaxDiscountPercent,
                row.HasDiscount,
                PriceDto.Lkr),
            row.MinSalePrice,
            row.MaxSalePrice,
            new RatingDto(row.RatingAverage, row.RatingCount),
            badge,
            stockStatus,
            StockAvailability.IsInStock(totalAvailable),
            new SeoDto(row.SeoTitle ?? row.Name, row.SeoDescription ?? row.ShortDescription),
            row.SizeGuideId is { } guideId && row.SizeGuideName is { } guideName
                ? new SizeGuideRefDto(guideId, guideName)
                : null,
            row.NewUntilUtc,
            images.AsReadOnly(),
            row.Colours.Select(ToSwatch).ToList().AsReadOnly(),
            sizes.AsReadOnly(),
            variants.AsReadOnly(),
            related);
    }

    // ---------------------------------------------------------------- supporting lookups

    /// <summary>
    /// Resolves the slugs and labels in the query to the IDs the predicates use. A filter that names
    /// something the catalogue does not have sets <see cref="ResolvedFilters.MatchesNothing"/>, so the
    /// listing answers "no products found" instead of quietly dropping the filter.
    /// </summary>
    private async Task<ResolvedFilters> ResolveFiltersAsync(ProductListQuery query, CancellationToken cancellationToken)
    {
        var filters = new ResolvedFilters { UtcNow = clock.UtcNow.UtcDateTime };

        if (query.DepartmentSlug is { } departmentSlug)
        {
            filters.DepartmentId = await dbContext.Departments
                .AsNoTracking()
                .Where(department => department.Slug == departmentSlug && department.IsActive)
                .Select(department => (int?)department.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (filters.DepartmentId is null)
            {
                filters.MatchesNothing = true;
                return filters;
            }
        }

        if (query.CategorySlug is { } categorySlug)
        {
            var departmentId = filters.DepartmentId;
            var category = await dbContext.Categories
                .AsNoTracking()
                .Where(candidate => candidate.Slug == categorySlug && candidate.IsActive)
                .Where(candidate => departmentId == null || candidate.DepartmentId == departmentId)
                .OrderBy(candidate => candidate.Depth)
                .ThenBy(candidate => candidate.Id)
                .Select(candidate => new { candidate.Id, candidate.Path })
                .FirstOrDefaultAsync(cancellationToken);

            if (category is null)
            {
                filters.MatchesNothing = true;
                return filters;
            }

            filters.CategoryId = category.Id;

            // Doc 08 §3.2: the materialised path turns "this category and its children" into one
            // range predicate instead of a recursive query.
            filters.CategorySubtreeIds = await dbContext.Categories
                .AsNoTracking()
                .Where(candidate => candidate.IsActive && candidate.Path.StartsWith(category.Path))
                .Select(candidate => candidate.Id)
                .ToListAsync(cancellationToken);
        }

        filters.AgeGroupIds = await ResolveIdsAsync(
            query.AgeGroupSlugs,
            slugs => dbContext.AgeGroups.AsNoTracking().Where(group => group.IsActive && slugs.Contains(group.Slug)).Select(group => group.Id),
            filters,
            cancellationToken);
        if (filters.MatchesNothing)
        {
            return filters;
        }

        filters.ColourFamilyIds = await ResolveIdsAsync(
            query.ColourFamilySlugs,
            slugs => dbContext.ColourFamilies.AsNoTracking().Where(family => slugs.Contains(family.Slug)).Select(family => family.Id),
            filters,
            cancellationToken);
        if (filters.MatchesNothing)
        {
            return filters;
        }

        filters.MaterialIds = await ResolveAttributeIdsAsync(query.MaterialSlugs, AttributeOptionType.Material, filters, cancellationToken);
        if (filters.MatchesNothing)
        {
            return filters;
        }

        filters.FitIds = await ResolveAttributeIdsAsync(query.FitSlugs, AttributeOptionType.Fit, filters, cancellationToken);
        if (filters.MatchesNothing)
        {
            return filters;
        }

        filters.OccasionIds = await ResolveAttributeIdsAsync(query.OccasionSlugs, AttributeOptionType.Occasion, filters, cancellationToken);
        if (filters.MatchesNothing)
        {
            return filters;
        }

        filters.SizeIds = await ResolveIdsAsync(
            query.SizeLabels,
            labels => dbContext.Sizes.AsNoTracking().Where(size => labels.Contains(size.Label)).Select(size => size.Id),
            filters,
            cancellationToken);

        return filters;
    }

    private static async Task<IReadOnlyList<int>> ResolveIdsAsync(
        IReadOnlyList<string> values,
        Func<IReadOnlyList<string>, IQueryable<int>> lookup,
        ResolvedFilters filters,
        CancellationToken cancellationToken)
    {
        if (values.Count == 0)
        {
            return [];
        }

        var ids = await lookup(values).ToListAsync(cancellationToken);
        if (ids.Count == 0)
        {
            filters.MatchesNothing = true;
        }

        return ids;
    }

    private Task<IReadOnlyList<int>> ResolveAttributeIdsAsync(
        IReadOnlyList<string> slugs,
        AttributeOptionType type,
        ResolvedFilters filters,
        CancellationToken cancellationToken) =>
        ResolveIdsAsync(
            slugs,
            values => dbContext.AttributeOptions
                .AsNoTracking()
                .Where(option => option.IsActive && option.AttributeType == type && values.Contains(option.Slug))
                .Select(option => option.Id),
            filters,
            cancellationToken);

    /// <summary>
    /// Doc 08 §3.2: the Best Seller badge compares <c>SoldQty30d</c> with the department's top-N
    /// threshold. One small query per department on the page (at most three), never per row.
    /// </summary>
    private async Task<Dictionary<int, int>> LoadBestSellerThresholdsAsync(
        List<int> departmentIds,
        CancellationToken cancellationToken)
    {
        var topN = Math.Max(_options.BestSellerTopN, 1);
        var thresholds = new Dictionary<int, int>(departmentIds.Count);

        foreach (var departmentId in departmentIds)
        {
            var threshold = await dbContext.Products
                .AsNoTracking()
                .Where(product => product.IsPublished && product.DepartmentId == departmentId)
                .Join(dbContext.ProductStats, product => product.Id, stats => stats.ProductId, (product, stats) => stats.SoldQty30d)
                .OrderByDescending(soldQty => soldQty)
                .Skip(topN - 1)
                .Take(1)
                .FirstOrDefaultAsync(cancellationToken);

            thresholds[departmentId] = threshold;
        }

        return thresholds;
    }

    /// <summary>Turns the materialised path <c>/3/17/</c> into the PLP-01 / PDP breadcrumb.</summary>
    private async Task<IReadOnlyList<CategoryRefDto>> LoadBreadcrumbAsync(
        string categoryPath,
        CancellationToken cancellationToken)
    {
        var ids = categoryPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => int.TryParse(segment, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        var categories = await dbContext.Categories
            .AsNoTracking()
            .Where(category => ids.Contains(category.Id))
            .Select(category => new CategoryRefDto(category.Id, category.Name, category.Slug))
            .ToListAsync(cancellationToken);

        // Keep the path's order, which is root → leaf.
        return ids
            .Select(id => categories.Find(category => category.Id == id))
            .Where(category => category is not null)
            .Select(category => category!)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// PDP-06: the merchandiser's picks first, then same-category products to top the list up to
    /// <c>Catalog.RelatedProductCount</c> (doc 08 §3.2).
    /// </summary>
    private async Task<IReadOnlyList<ProductCardDto>> LoadRelatedAsync(
        ProductDetailRow row,
        CancellationToken cancellationToken)
    {
        var wanted = Math.Max(_options.RelatedProductCount, 0);
        if (wanted == 0)
        {
            return [];
        }

        var curatedIds = row.RelatedProductIds.Take(wanted).ToList();
        var rows = new List<ProductCardRow>(wanted);

        if (curatedIds.Count > 0)
        {
            var curated = await BaseQuery()
                .Where(source => curatedIds.Contains(source.Product.Id))
                .Select(CardProjection(_options.DefaultLowStockThreshold))
                .ToListAsync(cancellationToken);

            // Preserve the merchandiser's order, which the IN list loses.
            rows.AddRange(curatedIds
                .Select(id => curated.Find(card => card.Id == id))
                .Where(card => card is not null)
                .Select(card => card!));
        }

        if (rows.Count < wanted)
        {
            var excluded = rows.Select(card => card.Id).Append(row.Id).ToList();
            var categoryId = row.PrimaryCategoryId;

            var fallback = await BaseQuery()
                .Where(source => source.Product.PrimaryCategoryId == categoryId && !excluded.Contains(source.Product.Id))
                .OrderByDescending(source => source.Stats.SoldQty30d)
                .ThenBy(source => source.Product.Id)
                .Take(wanted - rows.Count)
                .Select(CardProjection(_options.DefaultLowStockThreshold))
                .ToListAsync(cancellationToken);

            rows.AddRange(fallback);
        }

        return await ToCardsAsync(rows, cancellationToken);
    }

    /// <summary>The <c>ESCAPE</c> clause value for every generated <c>LIKE</c> (doc 08 §7.6).</summary>
    private const string EscapeString = "\\";

    private sealed record ProductRow(Product Product, ProductStats Stats);

    private sealed record SearchRow(Product Product, ProductStats Stats, ProductSearchIndex Index);

    private sealed class ResolvedFilters
    {
        public bool MatchesNothing { get; set; }

        public DateTime UtcNow { get; set; }

        public int? DepartmentId { get; set; }

        public int? CategoryId { get; set; }

        public List<int> CategorySubtreeIds { get; set; } = [];

        public IReadOnlyList<int> AgeGroupIds { get; set; } = [];

        public IReadOnlyList<int> ColourFamilyIds { get; set; } = [];

        public IReadOnlyList<int> MaterialIds { get; set; } = [];

        public IReadOnlyList<int> FitIds { get; set; } = [];

        public IReadOnlyList<int> OccasionIds { get; set; } = [];

        public IReadOnlyList<int> SizeIds { get; set; } = [];
    }
}
