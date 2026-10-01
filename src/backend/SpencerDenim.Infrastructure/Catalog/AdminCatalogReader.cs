using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Catalog.Admin.Abstractions;
using SpencerDenim.Application.Catalog.Admin.Contracts;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Catalog;

/// <summary>
/// The admin catalogue-structure read model (ADM-11, W1-12).
/// <para>
/// <b>Every query here is deliberately missing the <c>IsActive</c> predicate</b> that
/// <see cref="DepartmentCatalogReader"/> and <see cref="ProductCatalogReader"/> carry. That predicate is
/// what doc 08 §7.1 means by the admin's switch, and an admin screen that honoured it could show only the
/// rows that are already on. Soft-deleted rows <i>are</i> excluded, by the global query filter, because
/// removed is not the same as hidden and ADM-11 offers no restore.
/// </para>
/// <para>
/// Counts are gathered as grouped queries over the whole department and joined in memory, not asked per
/// node. A tree of about 200 rows (doc 08 §3.2) asked one count at a time is 200 round trips for one
/// screen, which is the N+1 the listing reader was already corrected for once.
/// </para>
/// </summary>
internal sealed class AdminCatalogReader(AppDbContext dbContext) : IAdminCatalogReader
{
    /// <summary>How many product names a deactivation warning names before it stops listing them.</summary>
    private const int ImpactExampleLimit = 5;

    public async Task<IReadOnlyList<AdminDepartmentDto>> GetDepartmentsAsync(CancellationToken cancellationToken)
    {
        var departments = await dbContext.Departments
            .AsNoTracking()
            .OrderBy(department => department.SortOrder)
            .ThenBy(department => department.Id)
            .Select(department => new
            {
                department.Id,
                department.Name,
                department.Slug,
                department.Description,
                department.HeroImageAssetId,
                department.SortOrder,
                department.IsActive,
                department.SeoTitle,
                department.SeoDescription,
                department.RowVersion,
                CategoryCount = department.Categories.Count,

                // Published products this department would put in the shop: everything except the
                // department's own switch, so the number reads the same whichever way the switch is
                // about to move. The primary-category predicate is doc 08 §7.1's, and leaving it out
                // would promise an admin products the listing already hides.
                StorefrontProductCount = dbContext.Products.Count(product =>
                    product.DepartmentId == department.Id
                    && product.IsPublished
                    && product.PrimaryCategory!.IsActive),
            })
            .ToListAsync(cancellationToken);

        return departments
            .Select(row => new AdminDepartmentDto(
                row.Id,
                row.Name,
                row.Slug,
                row.Description,
                row.HeroImageAssetId,
                row.SortOrder,
                row.IsActive,
                row.SeoTitle,
                row.SeoDescription,
                RowVersionToken.ToBase64(row.RowVersion),
                row.CategoryCount,
                row.StorefrontProductCount))
            .ToList()
            .AsReadOnly();
    }

    public async Task<AdminCategoryTreeDto?> GetCategoryTreeAsync(int departmentId, CancellationToken cancellationToken)
    {
        var department = await dbContext.Departments
            .AsNoTracking()
            .Where(candidate => candidate.Id == departmentId)
            .Select(candidate => new { candidate.Id, candidate.Name, candidate.Slug, candidate.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        if (department is null)
        {
            return null;
        }

        var categories = await dbContext.Categories
            .AsNoTracking()
            .Where(category => category.DepartmentId == departmentId)
            .OrderBy(category => category.SortOrder)
            .ThenBy(category => category.Id)
            .Select(category => new CategoryAdminRow(
                category.Id,
                category.DepartmentId,
                category.ParentCategoryId,
                category.Path,
                category.Depth,
                category.Name,
                category.Slug,
                category.Description,
                category.ImageAssetId,
                category.BannerAssetId,
                category.BannerLinkUrl,
                category.AgeGroupId,
                category.SizeGuideId,
                category.SortOrder,
                category.ShowInMegaMenu,
                category.MegaMenuColumn,
                category.IsActive,
                category.SeoTitle,
                category.SeoDescription,
                category.RowVersion,

                // Every non-deleted product linked here, published or not — the number the remove guard
                // uses, so the screen can disable the button for the reason the API would refuse it.
                //
                // Counted through Products rather than as `category.ProductLinks.Count`, and the difference
                // is not cosmetic: catalog.ProductCategories has no soft-delete filter of its own, so the
                // navigation's count includes links belonging to deleted products. The guard would then
                // refuse a removal because of a product nobody can see, with no way for the admin to clear
                // it. Reaching through the Products set applies that set's query filter (doc 08 §10.2).
                dbContext.ProductCategories.Count(link =>
                    link.CategoryId == category.Id
                    && dbContext.Products.Any(product => product.Id == link.ProductId)),
                dbContext.Products.Count(product => product.PrimaryCategoryId == category.Id)))
            .ToListAsync(cancellationToken);

        var childrenByParent = categories
            .Where(category => category.ParentCategoryId is not null)
            .GroupBy(category => category.ParentCategoryId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        var roots = categories
            .Where(category => category.ParentCategoryId is null)
            .Select(category => ToNode(category, childrenByParent))
            .ToList();

        return new AdminCategoryTreeDto(
            department.Id,
            department.Name,
            department.Slug,
            department.IsActive,
            roots.AsReadOnly());
    }

    public async Task<CategoryDeactivationImpactDto?> GetDeactivationImpactAsync(
        int categoryId,
        CancellationToken cancellationToken)
    {
        var category = await dbContext.Categories
            .AsNoTracking()
            .Where(candidate => candidate.Id == categoryId)
            .Select(candidate => new { candidate.Id, candidate.Name, candidate.Path, candidate.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        if (category is null)
        {
            return null;
        }

        // Published products whose own primary category this is: exactly what ProductCatalogReader.BaseQuery
        // drops when the flag goes off (doc 08 §7.1).
        var leavingQuery = dbContext.Products
            .AsNoTracking()
            .Where(product => product.PrimaryCategoryId == categoryId && product.IsPublished);

        var leaving = await leavingQuery.CountAsync(cancellationToken);

        var examples = await leavingQuery
            .OrderBy(product => product.Name)
            .Select(product => product.Name)
            .Take(ImpactExampleLimit)
            .ToListAsync(cancellationToken);

        // The subtree, excluding the category itself. `LIKE path + '%'` over IX_Categories_Path.
        var descendantPrefix = category.Path;
        var descendants = dbContext.Categories
            .AsNoTracking()
            .Where(candidate =>
                candidate.Id != categoryId
                && candidate.Path.StartsWith(descendantPrefix));

        var descendantCount = await descendants.CountAsync(cancellationToken);

        // The number an admin would never predict: these products keep working URLs and stay in every
        // listing, and nothing in the navigation leads to them any more, because
        // DepartmentCatalogReader builds the tree only out of rows whose parent is itself in the tree.
        var strandedProducts = await dbContext.Products
            .AsNoTracking()
            .Where(product =>
                product.IsPublished
                && product.PrimaryCategoryId != categoryId
                && product.PrimaryCategory!.IsActive
                && product.PrimaryCategory.Path.StartsWith(descendantPrefix))
            .CountAsync(cancellationToken);

        return new CategoryDeactivationImpactDto(
            category.Id,
            category.Name,
            category.IsActive,
            leaving,
            descendantCount,
            strandedProducts,
            examples.AsReadOnly());
    }

    private static AdminCategoryNodeDto ToNode(
        CategoryAdminRow row,
        Dictionary<int, List<CategoryAdminRow>> childrenByParent)
    {
        var children = childrenByParent.TryGetValue(row.Id, out var rows)
            ? rows.Select(child => ToNode(child, childrenByParent)).ToList()
            : [];

        return new AdminCategoryNodeDto(
            row.Id,
            row.DepartmentId,
            row.ParentCategoryId,
            row.Path,
            row.Depth,
            row.Name,
            row.Slug,
            row.Description,
            row.ImageAssetId,
            row.BannerAssetId,
            row.BannerLinkUrl,
            row.AgeGroupId,
            row.SizeGuideId,
            row.SortOrder,
            row.ShowInMegaMenu,
            row.MegaMenuColumn,
            row.IsActive,
            row.SeoTitle,
            row.SeoDescription,
            RowVersionToken.ToBase64(row.RowVersion),
            row.ProductCount,
            row.PrimaryProductCount,
            children.AsReadOnly());
    }

    /// <summary>The flat projection, before the tree is assembled in memory.</summary>
    private sealed record CategoryAdminRow(
        int Id,
        int DepartmentId,
        int? ParentCategoryId,
        string Path,
        byte Depth,
        string Name,
        string Slug,
        string? Description,
        int? ImageAssetId,
        int? BannerAssetId,
        string? BannerLinkUrl,
        int? AgeGroupId,
        int? SizeGuideId,
        int SortOrder,
        bool ShowInMegaMenu,
        byte? MegaMenuColumn,
        bool IsActive,
        string? SeoTitle,
        string? SeoDescription,
        byte[]? RowVersion,
        int ProductCount,
        int PrimaryProductCount);
}

/// <summary>
/// The one place a <c>rowversion</c> becomes the token a client sends back, so the read model and the
/// command's comparison cannot disagree about the encoding.
/// </summary>
internal static class RowVersionToken
{
    public static string ToBase64(byte[]? rowVersion) =>
        rowVersion is null or { Length: 0 } ? string.Empty : Convert.ToBase64String(rowVersion);
}
