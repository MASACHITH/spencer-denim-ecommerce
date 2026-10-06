using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Catalog.Admin.Contracts;
using SpencerDenim.Application.Catalog.Admin.Products;
using SpencerDenim.Application.Catalog.Admin.Products.Abstractions;
using SpencerDenim.Application.Catalog.Admin.Products.Contracts;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Catalog;

/// <summary>
/// The admin product read model (ADM-10, W1-03).
/// <para>
/// <b>Every query here is deliberately missing the <c>IsPublished</c> and <c>IsActive</c> predicates</b>
/// that <see cref="ProductCatalogReader"/> carries. Those are what put a product in the shop, and a screen
/// that honoured them could list only the products that are already live — so it could never reach the one
/// an admin has just created in order to publish it. Soft-deleted products <i>are</i> excluded, by the
/// global query filter, because removed is not the same as hidden and ADM-10 offers no restore.
/// </para>
/// <para>
/// <b>The aggregate is read as five queries, not as one join.</b> A product with 6 colours, 48 variants, 12
/// photographs and 3 related picks joined into one result set is 6 × 48 × 12 × 3 rows of duplicated product
/// columns; split queries keep it linear. <c>AsSplitQuery</c> is not used because the collections are
/// projected rather than loaded as entities — each one is its own <c>SELECT</c> by construction, which is
/// also what keeps the count visible to <c>AdminProductQueryPlanTests</c>.
/// </para>
/// </summary>
internal sealed class AdminProductReader(AppDbContext dbContext, CatalogOptions options) : IAdminProductReader
{
    public async Task<PagedResult<AdminProductListItemDto>> ListAsync(
        AdminProductListCriteria criteria,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var query = dbContext.Products.AsNoTracking();

        if (criteria.DepartmentId is { } departmentId)
        {
            query = query.Where(product => product.DepartmentId == departmentId);
        }

        if (criteria.CategoryId is { } categoryId)
        {
            // Through the link table, so a product filed into a category as an extra is found there too —
            // the primary category is stored in ProductCategories as well (doc 08 §3.2).
            query = query.Where(product => product.CategoryLinks.Any(link => link.CategoryId == categoryId));
        }

        if (criteria.Published is { } published)
        {
            query = query.Where(product => product.IsPublished == published);
        }

        if (Clean(criteria.Term) is { } term)
        {
            // Name, code and SKU: the three things a staff member has in front of them. The product code
            // and the SKU are the ones that come off a label or a packing list, and they are matched by
            // prefix as well as anywhere, so typing the start of a code is enough.
            var pattern = '%' + Escape(term) + '%';

            query = query.Where(product =>
                EF.Functions.Like(product.Name, pattern, "\\")
                || EF.Functions.Like(product.ProductCode, pattern, "\\")
                || EF.Functions.Like(product.Slug, pattern, "\\")
                || product.Variants.Any(variant => EF.Functions.Like(variant.Sku, pattern, "\\")));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        if (totalCount == 0)
        {
            return PagedResult.Empty<AdminProductListItemDto>(criteria.Page, criteria.PageSize);
        }

        query = criteria.Sort switch
        {
            AdminProductSort.Name => query.OrderBy(product => product.Name).ThenBy(product => product.Id),

            AdminProductSort.ProductCode =>
                query.OrderBy(product => product.ProductCode).ThenBy(product => product.Id),

            AdminProductSort.PriceAscending =>
                query.OrderBy(product => product.BasePrice).ThenBy(product => product.Id),

            AdminProductSort.PriceDescending =>
                query.OrderByDescending(product => product.BasePrice).ThenBy(product => product.Id),

            // The default, and the list an admin at work wants: what was touched last, at the top. A
            // product that has never been edited falls back to when it was created, which is why this is
            // not a plain ORDER BY on a nullable column.
            _ => query
                .OrderByDescending(product => product.UpdatedAtUtc ?? product.CreatedAtUtc)
                .ThenByDescending(product => product.Id),
        };

        var rows = await query
            .Skip((criteria.Page - 1) * criteria.PageSize)
            .Take(criteria.PageSize)
            .Select(product => new
            {
                product.Id,
                product.ProductCode,
                product.Name,
                product.Slug,
                product.DepartmentId,
                DepartmentName = product.Department!.Name,
                product.PrimaryCategoryId,
                PrimaryCategoryName = product.PrimaryCategory!.Name,
                product.BasePrice,
                product.CompareAtPrice,
                product.BadgeOverride,
                product.IsPublished,
                product.FirstPublishedAtUtc,
                product.UpdatedAtUtc,
                VariantCount = product.Variants.Count(variant => variant.IsActive),
                ColourCount = product.Colours.Count,
                HasVideo = product.Media.Any(media => media.MediaRole == ProductMediaRole.Video),
                product.RowVersion,
                CardImage = product.CardImageAsset == null
                    || product.CardImageAsset.Status != MediaAssetStatus.Ready
                        ? null
                        : new MediaRef(
                            product.CardImageAsset.StorageKey,
                            product.CardImageAsset.PublicId,
                            product.CardImageAsset.RenditionSetVersion,
                            product.CardImageAsset.Width),
            })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(row => new AdminProductListItemDto(
                row.Id,
                row.ProductCode,
                row.Name,
                row.Slug,
                row.DepartmentId,
                row.DepartmentName,
                row.PrimaryCategoryId,
                row.PrimaryCategoryName,
                row.BasePrice,
                row.CompareAtPrice,
                row.BadgeOverride,
                row.IsPublished,
                row.FirstPublishedAtUtc,
                row.UpdatedAtUtc,
                row.VariantCount,
                row.ColourCount,
                row.HasVideo,
                MediaUrlBuilder.ToRenditionUrl(options.MediaBaseUrl, row.CardImage, MediaRenditions.Thumb),
                RowVersionToken.ToBase64(row.RowVersion)))
            .ToList();

        return new PagedResult<AdminProductListItemDto>(items, criteria.Page, criteria.PageSize, totalCount);
    }

    public async Task<AdminProductDetailDto?> GetAsync(int productId, CancellationToken cancellationToken)
    {
        // Loaded as a tracked graph rather than projected, because the publish checklist is a function over
        // the aggregate (ProductPublishChecklist) and has to be the *same* function the API refuses with —
        // two implementations would drift, and the one that drifted would be the screen's, which is the
        // version the admin believes.
        var product = await dbContext.Products
            .AsNoTracking()
            .Include(candidate => candidate.Colours)
                .ThenInclude(colour => colour.SwatchAsset)
            .Include(candidate => candidate.Colours)
                .ThenInclude(colour => colour.ColourFamily)
            .Include(candidate => candidate.Variants)
                .ThenInclude(variant => variant.Size)
            .Include(candidate => candidate.Media)
                .ThenInclude(media => media.MediaAsset)
                    .ThenInclude(asset => asset!.PosterAsset)
            .Include(candidate => candidate.Media)
                .ThenInclude(media => media.ProductColour)
            .Include(candidate => candidate.CategoryLinks)
            .Include(candidate => candidate.RelatedProducts)
                .ThenInclude(related => related.Related)
            .AsSplitQuery()
            .FirstOrDefaultAsync(candidate => candidate.Id == productId, cancellationToken);

        if (product is null)
        {
            return null;
        }

        var onHand = await dbContext.StockLevels
            .AsNoTracking()
            .Where(stock => stock.Variant!.ProductId == productId)
            .ToDictionaryAsync(stock => stock.VariantId, stock => stock.OnHand, cancellationToken);

        var variantIdsWithOrders = (await dbContext.OrderLines
                .AsNoTracking()
                .Where(line => line.Variant!.ProductId == productId)
                .Select(line => line.VariantId)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var blockers = ProductPublishChecklist.Blockers(product);

        return new AdminProductDetailDto(
            product.Id,
            product.ProductCode,
            product.Name,
            product.Slug,
            product.DepartmentId,
            product.PrimaryCategoryId,
            product.CategoryLinks
                .Select(link => link.CategoryId)
                .Where(id => id != product.PrimaryCategoryId)
                .Order()
                .ToList(),
            product.AgeGroupId,
            product.ShortDescription,
            product.Description,
            product.CareInstructions,
            product.MaterialId,
            product.FitId,
            product.OccasionId,
            product.BasePrice,
            product.CompareAtPrice,
            product.BadgeOverride,
            product.NewUntilUtc,
            product.RecommendedRank,
            product.SizeGuideId,
            product.SeoTitle,
            product.SeoDescription,
            product.IsPublished,
            product.FirstPublishedAtUtc,
            AssetUrl(product.CardImageAsset, MediaRenditions.Card),
            AssetUrl(product.HoverImageAsset, MediaRenditions.Card),
            product.Colours
                .OrderBy(colour => colour.SortOrder)
                .ThenBy(colour => colour.Id)
                .Select(colour => new AdminProductColourDto(
                    colour.Id,
                    colour.Name,
                    colour.ColourFamilyId,
                    colour.ColourFamily?.Name,
                    colour.SwatchHex,
                    colour.SwatchAsset?.PublicId,
                    AssetUrl(colour.SwatchAsset, MediaRenditions.Thumb),
                    colour.SortOrder))
                .ToList(),
            product.Variants
                .OrderBy(variant => variant.SortOrder)
                .ThenBy(variant => variant.Id)
                .Select(variant => new AdminProductVariantDto(
                    variant.Id,
                    product.Colours.FirstOrDefault(colour => colour.Id == variant.ProductColourId)?.Name
                        ?? string.Empty,
                    variant.SizeId,
                    variant.Size?.Label,
                    variant.Size?.ScaleName,
                    variant.Sku,
                    variant.Barcode,
                    variant.PriceOverride,
                    variant.CompareAtPriceOverride,
                    variant.IsActive,
                    variant.SortOrder,
                    variantIdsWithOrders.Contains(variant.Id),
                    onHand.TryGetValue(variant.Id, out var quantity) ? quantity : 0))
                .ToList(),
            product.Media
                .OrderBy(media => media.MediaRole)
                .ThenBy(media => media.SortOrder)
                .ThenBy(media => media.Id)
                .Select(media => new AdminProductMediaDto(
                    media.Id,
                    media.MediaAsset!.PublicId,
                    media.MediaAsset.MediaType,
                    media.MediaAsset.MediaType == MediaAssetType.Video
                        ? MediaUrlBuilder.ToPublicUrl(options.MediaBaseUrl, media.MediaAsset.StorageKey)
                        : AssetUrl(media.MediaAsset, MediaRenditions.Card),
                    AssetUrl(media.MediaAsset.PosterAsset, MediaRenditions.Card),
                    media.ProductColour?.Name,
                    media.MediaRole,
                    media.SortOrder,
                    media.AltTextOverride,
                    media.MediaAsset.AltText))
                .ToList(),
            product.RelatedProducts
                .OrderBy(related => related.SortOrder)
                .ThenBy(related => related.RelatedProductId)
                .Where(related => related.Related is not null)
                .Select(related => new AdminRelatedProductDto(
                    related.RelatedProductId,
                    related.Related!.Name,
                    related.Related.ProductCode,
                    related.Related.IsPublished,
                    related.SortOrder))
                .ToList(),
            blockers.Count == 0,
            blockers,
            RowVersionToken.ToBase64(product.RowVersion));
    }

    public async Task<ProductFormOptionsDto> GetFormOptionsAsync(CancellationToken cancellationToken)
    {
        var departments = await dbContext.Departments
            .AsNoTracking()
            .OrderBy(department => department.SortOrder)
            .ThenBy(department => department.Id)
            .Select(department => new CatalogOptionDto(department.Id, department.Name))
            .ToListAsync(cancellationToken);

        var categories = await dbContext.Categories
            .AsNoTracking()
            .OrderBy(category => category.DepartmentId)
            .ThenBy(category => category.Path)
            .Select(category => new
            {
                category.Id,
                category.DepartmentId,
                category.ParentCategoryId,
                category.Depth,
                category.Name,
                category.Path,
                category.IsActive,
            })
            .ToListAsync(cancellationToken);

        var categoryNames = categories.ToDictionary(category => category.Id, category => category.Name);

        var categoryOptions = categories
            .Select(category => new ProductCategoryOptionDto(
                category.Id,
                category.DepartmentId,
                category.ParentCategoryId,
                category.Depth,
                category.Name,
                string.Join(
                    " › ",
                    CategoryPath.IdsIn(category.Path)
                        .Select(id => categoryNames.TryGetValue(id, out var name) ? name : null)
                        .Where(name => name is not null)),
                category.IsActive))
            .ToList();

        var attributes = await dbContext.AttributeOptions
            .AsNoTracking()
            .Where(option => option.IsActive)
            .OrderBy(option => option.AttributeType)
            .ThenBy(option => option.SortOrder)
            .ThenBy(option => option.Id)
            .Select(option => new AttributeRow(option.Id, option.AttributeType, option.Name))
            .ToListAsync(cancellationToken);

        var colourFamilies = await dbContext.ColourFamilies
            .AsNoTracking()
            .OrderBy(family => family.SortOrder)
            .ThenBy(family => family.Id)
            .Select(family => new ColourFamilyOptionDto(family.Id, family.Name, family.HexCode))
            .ToListAsync(cancellationToken);

        var sizes = await dbContext.Sizes
            .AsNoTracking()
            .OrderBy(size => size.ScaleName)
            .ThenBy(size => size.SortOrder)
            .ThenBy(size => size.Id)
            .Select(size => new ProductSizeOptionDto(size.Id, size.ScaleName, size.Label, size.SortOrder))
            .ToListAsync(cancellationToken);

        var ageGroups = await dbContext.AgeGroups
            .AsNoTracking()
            .Where(group => group.IsActive)
            .OrderBy(group => group.SortOrder)
            .ThenBy(group => group.Id)
            .Select(group => new CatalogOptionDto(group.Id, group.Name))
            .ToListAsync(cancellationToken);

        var sizeGuides = await dbContext.SizeGuides
            .AsNoTracking()
            .OrderBy(guide => guide.Name)
            .ThenBy(guide => guide.Id)
            .Select(guide => new CatalogOptionDto(guide.Id, guide.Name))
            .ToListAsync(cancellationToken);

        return new ProductFormOptionsDto(
            departments,
            categoryOptions,
            ageGroups,
            Options(attributes, AttributeOptionType.Material),
            Options(attributes, AttributeOptionType.Fit),
            Options(attributes, AttributeOptionType.Occasion),
            colourFamilies,
            sizes,
            sizeGuides);

        static List<CatalogOptionDto> Options(IEnumerable<AttributeRow> rows, AttributeOptionType type) =>
            rows
                .Where(row => row.AttributeType == type)
                .Select(row => new CatalogOptionDto(row.Id, row.Name))
                .ToList();
    }

    public async Task<IReadOnlyList<AdminRelatedProductDto>> SearchForRelatedAsync(
        string? term,
        int? excludeProductId,
        int take,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Products.AsNoTracking();

        if (excludeProductId is { } exclude)
        {
            query = query.Where(product => product.Id != exclude);
        }

        if (Clean(term) is { } cleaned)
        {
            var pattern = '%' + Escape(cleaned) + '%';

            query = query.Where(product =>
                EF.Functions.Like(product.Name, pattern, "\\")
                || EF.Functions.Like(product.ProductCode, pattern, "\\"));
        }

        return await query
            .OrderBy(product => product.Name)
            .ThenBy(product => product.Id)
            .Take(Math.Max(take, 1))
            .Select(product => new AdminRelatedProductDto(
                product.Id, product.Name, product.ProductCode, product.IsPublished, (short)0))
            .ToListAsync(cancellationToken);
    }

    private string? AssetUrl(MediaAsset? asset, string code) =>
        asset is null || asset.Status != MediaAssetStatus.Ready
            ? null
            : MediaUrlBuilder.ToRenditionUrl(
                options.MediaBaseUrl,
                new MediaRef(asset.StorageKey, asset.PublicId, asset.RenditionSetVersion, asset.Width),
                code);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Escapes the three characters <c>LIKE</c> treats as wildcards, so a staff member searching for
    /// "50%" finds the product called "50% Linen" instead of every product in the catalogue.
    /// </summary>
    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal);
}

/// <summary>
/// One row of <c>catalog.AttributeOptions</c> on its way to three separate pickers. A named record rather
/// than an anonymous type, because the three pickers are filled by a local function and an anonymous type
/// cannot be its parameter.
/// </summary>
internal sealed record AttributeRow(int Id, AttributeOptionType AttributeType, string Name);
