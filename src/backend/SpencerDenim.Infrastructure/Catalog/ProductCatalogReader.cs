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
        foreach (var token in query.Tokens)
        {
            var pattern = SearchTerms.ToContainsPattern(token);
            rows = rows.Where(product =>
                EF.Functions.Like(product.SearchIndex!.SearchText, pattern, EscapeString));
        }

        var totalCount = await rows.CountAsync(cancellationToken);
        if (totalCount == 0)
        {
            return PagedResult.Empty<ProductCardDto>(listQuery.Page, listQuery.PageSize);
        }

        var ordered = query.SortByRelevance
            ? ApplyRelevanceSort(rows, query.Tokens[0])
            : ApplySort(rows, listQuery, filters.CategoryId);

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
            .Where(product => product.Slug == slug)
            .Select(product => new ProductDetailRow(
                product.Id,
                product.ProductCode,
                product.Name,
                product.Slug,
                product.BasePrice,
                product.CompareAtPrice,
                product.DepartmentId,
                product.Department!.Name,
                product.Department.Slug,
                product.PrimaryCategoryId,
                product.PrimaryCategory!.Path,
                product.ShortDescription,
                product.Description,
                product.CareInstructions,
                product.Material != null ? product.Material.Name : null,
                product.Material != null ? product.Material.Slug : null,
                product.Fit != null ? product.Fit.Name : null,
                product.Fit != null ? product.Fit.Slug : null,
                product.Occasion != null ? product.Occasion.Name : null,
                product.Occasion != null ? product.Occasion.Slug : null,
                product.AgeGroup != null ? product.AgeGroup.Name : null,
                product.AgeGroup != null ? product.AgeGroup.Slug : null,
                product.SeoTitle,
                product.SeoDescription,
                product.SizeGuideId ?? product.PrimaryCategory.SizeGuideId,
                product.SizeGuide != null
                    ? product.SizeGuide.Name
                    : product.PrimaryCategory.SizeGuide != null
                        ? product.PrimaryCategory.SizeGuide.Name
                        : null,
                product.BadgeOverride,
                product.NewUntilUtc,
                product.Stats!.MinSalePrice,
                product.Stats!.MaxSalePrice,
                product.Stats!.MaxOriginalPrice,
                product.Stats!.MaxDiscountPercent,
                product.Stats!.HasDiscount,
                product.Stats!.RatingAverage,
                product.Stats!.RatingCount,
                product.Stats!.SoldQty30d,
                product.Colours
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
                product.Media
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
                product.Variants
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
                product.RelatedProducts
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
    private IQueryable<Product> BaseQuery() =>
        dbContext.Products
            .AsNoTracking()
            .Where(product => product.IsPublished)
            // ProductStats is 1:1 with a product and is written in the same transaction, so this is
            // never false in practice; it keeps the join an inner one and the projection total.
            .Where(product => product.Stats != null);

    private static IQueryable<Product> ApplyFilters(
        IQueryable<Product> rows,
        ProductListQuery query,
        ResolvedFilters filters)
    {
        if (filters.DepartmentId is { } departmentId)
        {
            rows = rows.Where(product => product.DepartmentId == departmentId);
        }

        if (filters.CategorySubtreeIds.Count > 0)
        {
            var categoryIds = filters.CategorySubtreeIds;
            rows = rows.Where(product => product.CategoryLinks.Any(link => categoryIds.Contains(link.CategoryId)));
        }

        if (filters.AgeGroupIds.Count > 0)
        {
            var ageGroupIds = filters.AgeGroupIds;
            rows = rows.Where(product => product.AgeGroupId != null && ageGroupIds.Contains(product.AgeGroupId.Value));
        }

        if (filters.MaterialIds.Count > 0)
        {
            var materialIds = filters.MaterialIds;
            rows = rows.Where(product => product.MaterialId != null && materialIds.Contains(product.MaterialId.Value));
        }

        if (filters.FitIds.Count > 0)
        {
            var fitIds = filters.FitIds;
            rows = rows.Where(product => product.FitId != null && fitIds.Contains(product.FitId.Value));
        }

        if (filters.OccasionIds.Count > 0)
        {
            var occasionIds = filters.OccasionIds;
            rows = rows.Where(product => product.OccasionId != null && occasionIds.Contains(product.OccasionId.Value));
        }

        if (filters.ColourFamilyIds.Count > 0)
        {
            var colourFamilyIds = filters.ColourFamilyIds;
            rows = rows.Where(product => product.Colours.Any(colour => colourFamilyIds.Contains(colour.ColourFamilyId)));
        }

        // Size and availability are one EXISTS over the active variants (doc 08 §7.1), so SQL Server
        // touches ProductVariants and StockLevels once instead of twice.
        var sizeIds = filters.SizeIds;
        if (sizeIds.Count > 0 && query.InStockOnly)
        {
            rows = rows.Where(product => product.Variants.Any(variant =>
                variant.IsActive
                && sizeIds.Contains(variant.SizeId)
                && variant.StockLevel!.Available > 0));
        }
        else if (sizeIds.Count > 0)
        {
            rows = rows.Where(product => product.Variants.Any(variant =>
                variant.IsActive && sizeIds.Contains(variant.SizeId)));
        }
        else if (query.InStockOnly)
        {
            rows = rows.Where(product => product.Variants.Any(variant =>
                variant.IsActive && variant.StockLevel!.Available > 0));
        }

        if (query.MinPrice is { } minPrice)
        {
            rows = rows.Where(product => product.Stats!.MinSalePrice >= minPrice);
        }

        if (query.MaxPrice is { } maxPrice)
        {
            rows = rows.Where(product => product.Stats!.MinSalePrice <= maxPrice);
        }

        if (query.MinRating is { } minRating)
        {
            rows = rows.Where(product => product.Stats!.RatingAverage >= minRating);
        }

        if (query.OnSaleOnly)
        {
            rows = rows.Where(product => product.Stats!.HasDiscount);
        }

        if (query.MinDiscountPercent is { } minDiscount and > 0m)
        {
            rows = rows.Where(product => product.Stats!.MaxDiscountPercent >= minDiscount);
        }

        if (query.NewArrivalsOnly)
        {
            var now = filters.UtcNow;
            rows = rows.Where(product => product.NewUntilUtc != null && product.NewUntilUtc > now);
        }

        return rows;
    }

    /// <summary>Doc 08 §7.3. Every sort ends with the product ID, so paging is stable.</summary>
    private static IOrderedQueryable<Product> ApplySort(
        IQueryable<Product> rows,
        ProductListQuery query,
        int? categoryId)
    {
        switch (query.Sort)
        {
            case ProductSortOption.Newest:
                return rows
                    .OrderByDescending(product => product.FirstPublishedAtUtc)
                    .ThenBy(product => product.Id);

            case ProductSortOption.BestSelling:
                return rows
                    .OrderByDescending(product => product.Stats!.SoldQty30d)
                    .ThenByDescending(product => product.Stats!.SoldQtyTotal)
                    .ThenBy(product => product.Id);

            case ProductSortOption.PriceAscending:
                return rows
                    .OrderBy(product => product.Stats!.MinSalePrice)
                    .ThenBy(product => product.Id);

            case ProductSortOption.PriceDescending:
                return rows
                    .OrderByDescending(product => product.Stats!.MinSalePrice)
                    .ThenBy(product => product.Id);

            case ProductSortOption.HighestRated:
                // NULLs last: an unrated product must not outrank a five-star one.
                return rows
                    .OrderBy(product => product.Stats!.RatingAverage == null)
                    .ThenByDescending(product => product.Stats!.RatingAverage)
                    .ThenByDescending(product => product.Stats!.RatingCount)
                    .ThenBy(product => product.Id);

            case ProductSortOption.BiggestDiscount:
                return rows
                    .OrderByDescending(product => product.Stats!.MaxDiscountPercent)
                    .ThenBy(product => product.Id);

            case ProductSortOption.Recommended:
            default:
                if (categoryId is { } selectedCategory)
                {
                    // Category page: the merchandiser's manual order inside this category wins,
                    // NULLs last, then the global pin, then sales and recency (doc 08 §7.3).
                    return rows
                        .OrderBy(product => product.CategoryLinks
                            .Where(link => link.CategoryId == selectedCategory)
                            .Select(link => link.SortOrder)
                            .FirstOrDefault() == null)
                        .ThenBy(product => product.CategoryLinks
                            .Where(link => link.CategoryId == selectedCategory)
                            .Select(link => link.SortOrder)
                            .FirstOrDefault())
                        .ThenBy(product => product.RecommendedRank == null)
                        .ThenBy(product => product.RecommendedRank)
                        .ThenByDescending(product => product.Stats!.SoldQty30d)
                        .ThenByDescending(product => product.FirstPublishedAtUtc)
                        .ThenBy(product => product.Id);
                }

                return rows
                    .OrderBy(product => product.RecommendedRank == null)
                    .ThenBy(product => product.RecommendedRank)
                    .ThenByDescending(product => product.Stats!.SoldQty30d)
                    .ThenByDescending(product => product.FirstPublishedAtUtc)
                    .ThenBy(product => product.Id);
        }
    }

    /// <summary>
    /// Doc 08 §7.6 relevance: name prefix 100, a word prefix 50, name contains 20, every match 10,
    /// plus <c>SoldQty30d</c>. Scored on the first token, which is the one a shopper leads with.
    /// </summary>
    private static IOrderedQueryable<Product> ApplyRelevanceSort(IQueryable<Product> rows, string leadToken)
    {
        var prefix = SearchTerms.ToPrefixPattern(leadToken);
        var wordPrefix = SearchTerms.ToWordPrefixPattern(leadToken);
        var contains = SearchTerms.ToContainsPattern(leadToken);

        return rows
            .OrderByDescending(product =>
                (EF.Functions.Like(product.SearchIndex!.NameText, prefix, EscapeString) ? 100 : 0)
                + (EF.Functions.Like(product.SearchIndex.NameText, wordPrefix, EscapeString) ? 50 : 0)
                + (EF.Functions.Like(product.SearchIndex.NameText, contains, EscapeString) ? 20 : 0)
                + 10
                + product.Stats!.SoldQty30d)
            .ThenBy(product => product.Id);
    }

    // ---------------------------------------------------------------- projections and mapping

    private static System.Linq.Expressions.Expression<Func<Product, ProductCardRow>> CardProjection(
        int defaultLowStockThreshold) =>
        product => new ProductCardRow(
            product.Id,
            product.ProductCode,
            product.Name,
            product.Slug,
            product.DepartmentId,
            product.Department!.Name,
            product.Department.Slug,
            product.PrimaryCategory!.Name,
            product.PrimaryCategory.Slug,
            product.CardImageAsset != null ? product.CardImageAsset.StorageKey : null,
            product.CardImageAsset != null ? product.CardImageAsset.AltText : null,
            product.HoverImageAsset != null ? product.HoverImageAsset.StorageKey : null,
            product.Stats!.MinSalePrice,
            product.Stats!.MaxOriginalPrice,
            product.Stats!.MaxDiscountPercent,
            product.Stats!.HasDiscount,
            product.Stats!.RatingAverage,
            product.Stats!.RatingCount,
            product.Stats!.SoldQty30d,
            product.BadgeOverride,
            product.NewUntilUtc,
            product.Variants
                .Where(variant => variant.IsActive && variant.StockLevel != null)
                .Sum(variant => variant.StockLevel!.Available),
            product.Variants
                .Where(variant => variant.IsActive && variant.StockLevel != null)
                .Sum(variant => variant.StockLevel!.LowStockThreshold ?? defaultLowStockThreshold),
            product.Colours
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
                .Where(product => product.IsPublished && product.Stats != null)
                .Where(product => product.DepartmentId == departmentId)
                .Select(product => product.Stats!.SoldQty30d)
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
                .Where(product => curatedIds.Contains(product.Id))
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
                .Where(product => product.PrimaryCategoryId == categoryId && !excluded.Contains(product.Id))
                .OrderByDescending(product => product.Stats!.SoldQty30d)
                .ThenBy(product => product.Id)
                .Take(wanted - rows.Count)
                .Select(CardProjection(_options.DefaultLowStockThreshold))
                .ToListAsync(cancellationToken);

            rows.AddRange(fallback);
        }

        return await ToCardsAsync(rows, cancellationToken);
    }

    /// <summary>The <c>ESCAPE</c> clause value for every generated <c>LIKE</c> (doc 08 §7.6).</summary>
    private const string EscapeString = "\\";

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
