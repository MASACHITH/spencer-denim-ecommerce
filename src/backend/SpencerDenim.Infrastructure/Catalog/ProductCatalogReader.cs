using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Catalog.Abstractions;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Application.Catalog.Products;
using SpencerDenim.Application.Catalog.Search;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Inventory;
using SpencerDenim.Domain.Media;
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

    public async Task<Result<PagedResult<ProductCardDto>>> ListAsync(
        ProductListQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var filters = await ResolveFiltersAsync(query, cancellationToken);
        if (filters.Rejection is { } rejection)
        {
            return Result.Failure<PagedResult<ProductCardDto>>(rejection.Field, rejection.Message);
        }

        if (filters.MatchesNothing)
        {
            // A filter named a slug or size that does not exist: the correct answer is the PLP-06
            // "No products found" state, not an error and not an unfiltered listing.
            return Result.Success(PagedResult.Empty<ProductCardDto>(query.Page, query.PageSize));
        }

        var rows = ApplyFilters(BaseQuery(), query, filters);

        var totalCount = await rows.CountAsync(cancellationToken);
        if (totalCount == 0)
        {
            return Result.Success(PagedResult.Empty<ProductCardDto>(query.Page, query.PageSize));
        }

        var page = await ApplySort(rows, query, filters.CategoryId)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(CardProjection(_options.DefaultLowStockThreshold))
            .ToListAsync(cancellationToken);

        var cards = await ToCardsAsync(page, cancellationToken);
        return Result.Success(new PagedResult<ProductCardDto>(cards, query.Page, query.PageSize, totalCount));
    }

    public async Task<Result<PagedResult<ProductCardDto>>> SearchAsync(
        ProductSearchQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var listQuery = query.ListQuery;
        var filters = await ResolveFiltersAsync(listQuery, cancellationToken);
        if (filters.Rejection is { } rejection)
        {
            return Result.Failure<PagedResult<ProductCardDto>>(rejection.Field, rejection.Message);
        }

        if (filters.MatchesNothing)
        {
            return Result.Success(PagedResult.Empty<ProductCardDto>(listQuery.Page, listQuery.PageSize));
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
            return Result.Success(PagedResult.Empty<ProductCardDto>(listQuery.Page, listQuery.PageSize));
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
        return Result.Success(new PagedResult<ProductCardDto>(cards, listQuery.Page, listQuery.PageSize, totalCount));
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
                // PDP-04, twice over because the product may override its category's guide. Written out
                // rather than factored into a helper: a method call in an expression tree is not
                // something EF can translate, and an `Expression<Func<SizeGuide, SizeGuideRow>>` cannot
                // be invoked from inside another one without a visitor, which would cost more clarity
                // than the duplication does.
                //
                // Each branch takes id, name and content from the *same* row. The pair they replaced
                // took the id from `Products.SizeGuideId` and the name from the navigation, so a product
                // whose own guide had been soft-deleted while its category still had one answered with
                // the deleted guide's id beside the live guide's name — and would now have answered with
                // a third thing's content.
                product.SizeGuide != null
                    ? new SizeGuideRow(
                        product.SizeGuide.Id,
                        product.SizeGuide.Name,
                        product.SizeGuide.IntroHtml,
                        product.SizeGuide.TableJson,
                        product.SizeGuide.MeasuringTipsHtml,
                        product.SizeGuide.ImageAsset != null
                            && product.SizeGuide.ImageAsset.Status == MediaAssetStatus.Ready
                            ? new MediaRef(
                                product.SizeGuide.ImageAsset.StorageKey,
                                product.SizeGuide.ImageAsset.PublicId,
                                product.SizeGuide.ImageAsset.RenditionSetVersion,
                                product.SizeGuide.ImageAsset.Width)
                            : null)
                    : null,
                product.PrimaryCategory.SizeGuide != null
                    ? new SizeGuideRow(
                        product.PrimaryCategory.SizeGuide.Id,
                        product.PrimaryCategory.SizeGuide.Name,
                        product.PrimaryCategory.SizeGuide.IntroHtml,
                        product.PrimaryCategory.SizeGuide.TableJson,
                        product.PrimaryCategory.SizeGuide.MeasuringTipsHtml,
                        product.PrimaryCategory.SizeGuide.ImageAsset != null
                            && product.PrimaryCategory.SizeGuide.ImageAsset.Status == MediaAssetStatus.Ready
                            ? new MediaRef(
                                product.PrimaryCategory.SizeGuide.ImageAsset.StorageKey,
                                product.PrimaryCategory.SizeGuide.ImageAsset.PublicId,
                                product.PrimaryCategory.SizeGuide.ImageAsset.RenditionSetVersion,
                                product.PrimaryCategory.SizeGuide.ImageAsset.Width)
                            : null)
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
                        colour.SwatchAsset != null && colour.SwatchAsset.Status == MediaAssetStatus.Ready
                            ? new MediaRef(
                                colour.SwatchAsset.StorageKey,
                                colour.SwatchAsset.PublicId,
                                colour.SwatchAsset.RenditionSetVersion,
                                colour.SwatchAsset.Width)
                            : null,
                        colour.SortOrder))
                    .ToList(),
                product.Media
                    // A slide whose file is not on disk is not a slide. Unlike a card image, which can
                    // fall back to an empty frame, a gallery item with nothing to show would still take
                    // a thumbnail in the rail and a stop in the keyboard order (PDP-01).
                    .Where(media => media.MediaAsset!.Status == MediaAssetStatus.Ready)
                    .OrderBy(media => media.SortOrder)
                    .ThenBy(media => media.Id)
                    .Select(media => new MediaRow(
                        media.Id,
                        new MediaRef(
                            media.MediaAsset!.StorageKey,
                            media.MediaAsset.PublicId,
                            media.MediaAsset.RenditionSetVersion,
                            media.MediaAsset.Width),
                        media.AltTextOverride ?? media.MediaAsset.AltText,
                        media.MediaRole,
                        media.ProductColourId,
                        media.MediaAsset.Width,
                        media.MediaAsset.Height,
                        media.MediaAsset.PosterAsset != null
                            && media.MediaAsset.PosterAsset.Status == MediaAssetStatus.Ready
                            ? new MediaRef(
                                media.MediaAsset.PosterAsset.StorageKey,
                                media.MediaAsset.PosterAsset.PublicId,
                                media.MediaAsset.PosterAsset.RenditionSetVersion,
                                media.MediaAsset.PosterAsset.Width)
                            : null,
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
            .Where(product => product.Stats != null)
            // Doc 08 §7.1: IsActive is the only switch an admin has for taking a section off the
            // storefront, so a product leaves with its department or its primary category. Its other
            // category links do not keep it visible — a product whose primary category is gone has a
            // breadcrumb into nothing, which is a dead end reachable from a live page.
            .Where(product => product.Department!.IsActive && product.PrimaryCategory!.IsActive);

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
    /// Doc 08 §7.6 relevance, exactly as the section words it: name prefix 100, name contains 50,
    /// category or colour 20, any other match 10, then <c>SoldQty30d</c> to order the products that
    /// scored the same. The section describes one <c>CASE</c> score, applied here to the first token,
    /// which is the word a shopper leads with; the later tokens have already narrowed the result set.
    /// <para>
    /// Popularity is a separate sort key rather than a term added to the score, which is what stops it
    /// reaching past a band however high it climbs. Nothing caps it: a cap would only flatten the
    /// products it is there to tell apart.
    /// </para>
    /// </summary>
    private static IOrderedQueryable<Product> ApplyRelevanceSort(IQueryable<Product> rows, string leadToken)
    {
        var prefix = SearchTerms.ToPrefixPattern(leadToken);
        var contains = SearchTerms.ToContainsPattern(leadToken);

        return rows
            .OrderByDescending(product =>
                EF.Functions.Like(product.SearchIndex!.NameText, prefix, EscapeString) ? 100
                : EF.Functions.Like(product.SearchIndex.NameText, contains, EscapeString) ? 50
                : product.CategoryLinks.Any(link =>
                        EF.Functions.Like(link.Category!.Name, contains, EscapeString))
                    || product.Colours.Any(colour =>
                        EF.Functions.Like(colour.Name, contains, EscapeString)
                        || EF.Functions.Like(colour.ColourFamily!.Name, contains, EscapeString)) ? 20
                : 10)
            .ThenByDescending(product => product.Stats!.SoldQty30d)
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
            product.CardImageAsset != null && product.CardImageAsset.Status == MediaAssetStatus.Ready
                ? new MediaRef(
                    product.CardImageAsset.StorageKey,
                    product.CardImageAsset.PublicId,
                    product.CardImageAsset.RenditionSetVersion,
                    product.CardImageAsset.Width)
                : null,
            // Gated with the image, not separately: alt text is a description of a picture, so a payload
            // carrying it for an asset that has no file to serve is describing something that is not
            // there.
            product.CardImageAsset != null && product.CardImageAsset.Status == MediaAssetStatus.Ready
                ? product.CardImageAsset.AltText
                : null,
            product.HoverImageAsset != null && product.HoverImageAsset.Status == MediaAssetStatus.Ready
                ? new MediaRef(
                    product.HoverImageAsset.StorageKey,
                    product.HoverImageAsset.PublicId,
                    product.HoverImageAsset.RenditionSetVersion,
                    product.HoverImageAsset.Width)
                : null,
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
                    colour.SwatchAsset != null && colour.SwatchAsset.Status == MediaAssetStatus.Ready
                        ? new MediaRef(
                            colour.SwatchAsset.StorageKey,
                            colour.SwatchAsset.PublicId,
                            colour.SwatchAsset.RenditionSetVersion,
                            colour.SwatchAsset.Width)
                        : null,
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

        // CARD-01 draws both photos in the same fixed 3:4 frame, so they take the same sizes. The cap
        // is `large`: a card is a column of a grid, never the full width of a screen, so there is no
        // layout in which `zoom` could be the right answer and every reason not to offer it.
        var cardImage = MediaUrlBuilder.ToImageSources(
            _options.MediaBaseUrl,
            row.CardImage,
            MediaRenditions.Card,
            MediaRenditions.Large);

        var hoverImage = MediaUrlBuilder.ToImageSources(
            _options.MediaBaseUrl,
            row.HoverImage,
            MediaRenditions.Card,
            MediaRenditions.Large);

        return new ProductCardDto(
            row.Id,
            row.ProductCode,
            row.Name,
            row.Slug,
            row.DepartmentName,
            row.DepartmentSlug,
            row.CategoryName,
            row.CategorySlug,
            cardImage.Url,
            cardImage.SrcSet,
            hoverImage.Url,
            hoverImage.SrcSet,
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

    /// <summary>
    /// PDP-04: the guide, with its measurements, or <see langword="null"/> when the product has none.
    /// <para>
    /// <see cref="SizeGuideRefDto.Content"/> is <see langword="null"/> rather than four empty fields
    /// when the guide holds nothing a shopper could read. That is the shape the storefront's
    /// <c>hasSizeGuideContent</c> was written against — it is the one condition that unhides the
    /// trigger, and a guide with a name and no measurements is deliberately not worth a button — so
    /// "there is nothing to show" has to be one check rather than four. It goes on the wire as
    /// <c>"content": null</c>: a present key with a null value, not an omitted one.
    /// </para>
    /// </summary>
    private SizeGuideRefDto? ToSizeGuide(SizeGuideRow? guide)
    {
        if (guide is null)
        {
            return null;
        }

        var intro = Trimmed(guide.IntroHtml);
        var table = SizeGuideTable.Parse(guide.TableJson);
        var tips = Trimmed(guide.MeasuringTipsHtml);

        // `large` rather than `zoom`: a measuring diagram is drawn inside a dialog that is narrower than
        // the page, never full-screen, so zoom would only spend a shopper's bytes. The row is already
        // guarded on `Ready` in the projection, so there is a file behind whatever this names.
        var imageUrl = MediaUrlBuilder.ToRenditionUrl(_options.MediaBaseUrl, guide.Image, MediaRenditions.Large);

        var content = intro is null && table is null && tips is null && imageUrl is null
            ? null
            : new SizeGuideContentDto(intro, table, tips, imageUrl);

        return new SizeGuideRefDto(guide.Id, guide.Name, content);
    }

    /// <summary>
    /// The value, or <see langword="null"/> when it is blank. Whitespace-only rich text is an empty
    /// field an admin has saved over, not content, and sending it would make
    /// <c>hasSizeGuideContent</c> answer on a string with nothing in it.
    /// </summary>
    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private ColourSwatchDto ToSwatch(ColourRow colour) => new(
        colour.Id,
        colour.Name,
        colour.FamilyName,
        colour.FamilySlug,
        colour.SwatchHex,
        MediaUrlBuilder.ToRenditionUrl(_options.MediaBaseUrl, colour.SwatchImage, MediaRenditions.Thumb));

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

        // PDP-01 shows one asset in three sizes at once: the rail, the stage and — once the shopper
        // opens it — the full-screen viewer. The stage is capped at `zoom` rather than `large` because
        // it is full-width below 768 px, where a retina phone can want more than 1200 px.
        var images = row.Media
            .Select(media =>
            {
                var stage = MediaUrlBuilder.ToImageSources(
                    _options.MediaBaseUrl,
                    media.Media,
                    MediaRenditions.Large,
                    MediaRenditions.Zoom);

                return new ProductImageDto(
                    media.Id,
                    stage.Url ?? string.Empty,
                    stage.SrcSet,
                    MediaUrlBuilder.ToRenditionUrl(_options.MediaBaseUrl, media.Media, MediaRenditions.Thumb),
                    MediaUrlBuilder.ToRenditionUrl(_options.MediaBaseUrl, media.Media, MediaRenditions.Zoom),
                    media.AltText,
                    media.Role,
                    media.ColourId,
                    media.Width,
                    media.Height,
                    MediaUrlBuilder.ToRenditionUrl(_options.MediaBaseUrl, media.Poster, MediaRenditions.Large),
                    media.SortOrder);
            })
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
            ToSizeGuide(row.OwnSizeGuide ?? row.CategorySizeGuide),
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
            var matches = await dbContext.Categories
                .AsNoTracking()
                .Where(candidate => candidate.Slug == categorySlug && candidate.IsActive)
                .Where(candidate => departmentId == null || candidate.DepartmentId == departmentId)
                .OrderBy(candidate => candidate.Depth)
                .ThenBy(candidate => candidate.Id)
                .Select(candidate => new
                {
                    candidate.Id,
                    candidate.Path,
                    candidate.DepartmentId,
                    DepartmentSlug = candidate.Department!.Slug,
                })
                .ToListAsync(cancellationToken);

            if (matches.Count == 0)
            {
                filters.MatchesNothing = true;
                return filters;
            }

            // Doc 08 §3.2 makes a category slug unique per (department, parent), not globally, so
            // "jeans" names one category in each department — and two categories under different
            // parents of one department may share a slug too. Serving whichever sorts first would
            // quietly hide the rest, so an ambiguous slug is refused however it came about.
            if (matches.Count > 1)
            {
                var departmentSlugs = matches
                    .OrderBy(match => match.DepartmentId)
                    .Select(match => match.DepartmentSlug)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                filters.Rejection = new ValidationError(
                    "category",
                    departmentSlugs.Count > 1
                        ? $"The category '{categorySlug}' exists in more than one department. " +
                          $"Add {DescribeChoices(departmentSlugs)}."
                        : $"The category '{categorySlug}' names {matches.Count} active categories in " +
                          $"department={departmentSlugs[0]}, so naming the department does not tell them " +
                          "apart. The duplicate slugs have to be corrected in the catalogue.");
                return filters;
            }

            var category = matches[0];
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

    /// <summary>Renders the choices as "department=women, department=men or department=kids".</summary>
    private static string DescribeChoices(IReadOnlyList<string> departmentSlugs)
    {
        var choices = departmentSlugs.Select(slug => $"department={slug}").ToList();

        return choices.Count == 1
            ? choices[0]
            : string.Join(", ", choices.Take(choices.Count - 1)) + " or " + choices[^1];
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
    /// <para>
    /// It counts over <see cref="BaseQuery"/> and not over <c>dbContext.Products</c>, which is the
    /// correction in issue #25. Spelling the published and stats predicates out here left out the one
    /// <see cref="BaseQuery"/> adds on top of them — a product leaves the storefront with its department
    /// or its primary category — so a product in a deactivated category still counted towards its
    /// department's top N. The threshold then came out too high and the badge was withheld from a
    /// product that had earned it. Cosmetic, and the same invariant the rest of this class exists to
    /// enforce, so it is enforced in one place rather than restated in two.
    /// </para>
    /// </summary>
    private async Task<Dictionary<int, int>> LoadBestSellerThresholdsAsync(
        List<int> departmentIds,
        CancellationToken cancellationToken)
    {
        var topN = Math.Max(_options.BestSellerTopN, 1);
        var thresholds = new Dictionary<int, int>(departmentIds.Count);

        foreach (var departmentId in departmentIds)
        {
            var threshold = await BaseQuery()
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

        /// <summary>Set when a filter cannot be answered at all, rather than simply matching nothing.</summary>
        public ValidationError? Rejection { get; set; }

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
