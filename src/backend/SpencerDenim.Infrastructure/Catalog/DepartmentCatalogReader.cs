using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Catalog.Abstractions;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Catalog;

/// <summary>
/// Reads the navigation tree (GLB-05, DEPT-01) in two queries — departments, then every active
/// category — and assembles the hierarchy in memory. Doc 08 §3.2 sizes the tree at about 200 rows,
/// so fetching it whole beats a recursive query or one round trip per level.
/// </summary>
internal sealed class DepartmentCatalogReader(AppDbContext dbContext, IOptions<CatalogOptions> options)
    : IDepartmentCatalogReader
{
    private readonly CatalogOptions _options = options.Value;

    public async Task<IReadOnlyList<DepartmentDto>> GetDepartmentsAsync(CancellationToken cancellationToken)
    {
        var departments = await dbContext.Departments
            .AsNoTracking()
            .Where(department => department.IsActive)
            .OrderBy(department => department.SortOrder)
            .ThenBy(department => department.Id)
            .Select(department => new DepartmentRow(
                department.Id,
                department.Name,
                department.Slug,
                department.Description,
                department.HeroImageAsset != null
                    && department.HeroImageAsset.Status == MediaAssetStatus.Ready
                    ? new MediaRef(
                        department.HeroImageAsset.StorageKey,
                        department.HeroImageAsset.PublicId,
                        department.HeroImageAsset.RenditionSetVersion,
                        department.HeroImageAsset.Width)
                    : null,
                department.SeoTitle,
                department.SeoDescription))
            .ToListAsync(cancellationToken);

        var categories = await dbContext.Categories
            .AsNoTracking()
            .Where(category => category.IsActive)
            .OrderBy(category => category.SortOrder)
            .ThenBy(category => category.Id)
            .Select(category => new CategoryRow(
                category.Id,
                category.DepartmentId,
                category.ParentCategoryId,
                category.Depth,
                category.Name,
                category.Slug,
                category.Description,
                category.ImageAsset != null && category.ImageAsset.Status == MediaAssetStatus.Ready
                    ? new MediaRef(
                        category.ImageAsset.StorageKey,
                        category.ImageAsset.PublicId,
                        category.ImageAsset.RenditionSetVersion,
                        category.ImageAsset.Width)
                    : null,
                category.ShowInMegaMenu,
                category.MegaMenuColumn,
                category.AgeGroup != null ? category.AgeGroup.Slug : null))
            .ToListAsync(cancellationToken);

        var childrenByParent = categories
            .Where(category => category.ParentCategoryId is not null)
            .GroupBy(category => category.ParentCategoryId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        // A department banner is drawn full-bleed, so it is the one storefront slot whose candidates
        // are allowed to run all the way to `zoom`.
        var heroImages = departments.ToDictionary(
            department => department.Id,
            department => MediaUrlBuilder.ToImageSources(
                _options.MediaBaseUrl,
                department.HeroImage,
                MediaRenditions.Large,
                MediaRenditions.Zoom));

        return departments
            .Select(department => new DepartmentDto(
                department.Id,
                department.Name,
                department.Slug,
                department.Description,
                heroImages[department.Id].Url,
                heroImages[department.Id].SrcSet,
                department.SeoTitle,
                department.SeoDescription,
                categories
                    .Where(category => category.DepartmentId == department.Id && category.ParentCategoryId is null)
                    .Select(category => BuildNode(category, childrenByParent, _options.MediaBaseUrl))
                    .ToList()))
            .ToList()
            .AsReadOnly();
    }

    private static CategoryNodeDto BuildNode(
        CategoryRow category,
        Dictionary<int, List<CategoryRow>> childrenByParent,
        string mediaBaseUrl)
    {
        var children = childrenByParent.TryGetValue(category.Id, out var rows)
            ? rows.Select(child => BuildNode(child, childrenByParent, mediaBaseUrl)).ToList()
            : [];

        // A category tile is a grid cell — a mega-menu promo or a department-page tile — so it takes
        // the same sizes a product card does.
        var image = MediaUrlBuilder.ToImageSources(
            mediaBaseUrl,
            category.Image,
            MediaRenditions.Card,
            MediaRenditions.Large);

        return new CategoryNodeDto(
            category.Id,
            category.Name,
            category.Slug,
            category.Depth,
            category.Description,
            image.Url,
            image.SrcSet,
            category.ShowInMegaMenu,
            category.MegaMenuColumn,
            category.AgeGroupSlug,
            children);
    }

    private sealed record DepartmentRow(
        int Id,
        string Name,
        string Slug,
        string? Description,
        MediaRef? HeroImage,
        string? SeoTitle,
        string? SeoDescription);

    private sealed record CategoryRow(
        int Id,
        int DepartmentId,
        int? ParentCategoryId,
        byte Depth,
        string Name,
        string Slug,
        string? Description,
        MediaRef? Image,
        bool ShowInMegaMenu,
        byte? MegaMenuColumn,
        string? AgeGroupSlug);
}
