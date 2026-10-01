using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Catalog.Abstractions;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Catalog;

/// <summary>
/// Reads every address a crawler may be told about (GLB-11), in three bounded queries.
/// <para>
/// **Why it is not a loop over the listing endpoint.** <c>sitemap.xml</c> is a public URL that
/// enumerates the shop, so the cost of one request is the thing to design. Paging the listing API
/// would be one request per 96 products, each carrying card images, colourways, ratings and derived
/// stock for rows the sitemap throws away. This asks for slugs and timestamps: three projections, no
/// media, no variants, no stock, one <c>Take</c> that the catalogue cannot grow past.
/// </para>
/// <para>
/// **Why it does not decide what is visible.** It composes <see cref="CatalogVisibility"/>, the same
/// predicates the mega menu and the listing use, so the sitemap's idea of "live" cannot drift from the
/// shop's. That matters as of W1-12: the client can now deactivate and soft-delete categories, and a
/// sitemap built from its own rule would keep offering a crawler listings that answer 404.
/// </para>
/// <para>
/// **Freshness is not cached here.** One read per call, and the caller decides how often to call: the
/// storefront's <c>sitemap.xml</c> route holds the rendered document for a few minutes and keeps the
/// last good one through an outage. Caching in both places would mean two ages for one answer.
/// </para>
/// </summary>
internal sealed class SitemapCatalogReader(
    AppDbContext dbContext,
    IOptions<CatalogOptions> options,
    IClock clock)
    : ISitemapCatalogReader
{
    private readonly CatalogOptions _options = options.Value;

    public async Task<CatalogSitemapDto> GetSitemapAsync(CancellationToken cancellationToken)
    {
        var departments = await dbContext.Departments
            .AsNoTracking()
            .Where(CatalogVisibility.VisibleDepartment)
            .OrderBy(department => department.SortOrder)
            .ThenBy(department => department.Id)
            .Select(department => new SitemapDepartmentRow(
                department.Id,
                department.Slug,
                department.UpdatedAtUtc ?? department.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        var categories = await dbContext.Categories
            .AsNoTracking()
            .Where(CatalogVisibility.VisibleCategory)
            .OrderBy(category => category.SortOrder)
            .ThenBy(category => category.Id)
            .Select(category => new SitemapCategoryRow(
                category.Id,
                category.DepartmentId,
                category.ParentCategoryId,
                category.Slug,
                category.UpdatedAtUtc ?? category.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        // One more than the cap, so "there are more than we will publish" is a fact read off the
        // database rather than a guess from a full page.
        var limit = _options.SitemapMaxProducts;
        var products = await dbContext.Products
            .AsNoTracking()
            .Where(CatalogVisibility.VisibleProduct)
            // Most recently changed first, because that is the half of a truncated catalogue a crawler
            // most needs to hear about. The Id breaks ties so paging and repeat reads are stable
            // (doc 08 §7.1).
            .OrderByDescending(product => product.UpdatedAtUtc ?? product.CreatedAtUtc)
            .ThenByDescending(product => product.Id)
            .Take(limit + 1)
            .Select(product => new SitemapProductDto(
                product.Slug,
                product.UpdatedAtUtc ?? product.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        var truncated = products.Count > limit;
        if (truncated)
        {
            products.RemoveAt(products.Count - 1);
        }

        return new CatalogSitemapDto(
            clock.UtcNow.UtcDateTime,
            BuildDepartments(departments, categories),
            products,
            truncated);
    }

    /// <summary>
    /// Turns the two flat lists into a department-with-trails shape, descending only through active
    /// parents.
    /// <para>
    /// A category whose parent is inactive is left out even though its own row is active, and that is
    /// the point: its address is <c>/kids/girls/girls-dresses</c>, the listing refuses the middle
    /// segment, and offering a crawler a URL the storefront will not serve is worse than offering none.
    /// It is also exactly what the mega menu does, because it only ever renders children of a node it
    /// has already drawn.
    /// </para>
    /// </summary>
    private static List<SitemapDepartmentDto> BuildDepartments(
        List<SitemapDepartmentRow> departments,
        List<SitemapCategoryRow> categories)
    {
        var childrenByParent = categories
            .Where(category => category.ParentCategoryId is not null)
            .GroupBy(category => category.ParentCategoryId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        var result = new List<SitemapDepartmentDto>(departments.Count);

        foreach (var department in departments)
        {
            var trails = new List<SitemapCategoryDto>();
            var roots = categories.Where(category =>
                category.DepartmentId == department.Id && category.ParentCategoryId is null);

            foreach (var root in roots)
            {
                Walk(root, [], trails, childrenByParent);
            }

            result.Add(new SitemapDepartmentDto(department.Slug, department.LastModifiedUtc, trails));
        }

        return result;
    }

    private static void Walk(
        SitemapCategoryRow category,
        IReadOnlyList<string> ancestors,
        List<SitemapCategoryDto> into,
        Dictionary<int, List<SitemapCategoryRow>> childrenByParent)
    {
        string[] trail = [.. ancestors, category.Slug];
        into.Add(new SitemapCategoryDto(trail, category.LastModifiedUtc));

        if (!childrenByParent.TryGetValue(category.Id, out var children))
        {
            return;
        }

        foreach (var child in children)
        {
            Walk(child, trail, into, childrenByParent);
        }
    }

    private sealed record SitemapDepartmentRow(int Id, string Slug, DateTime? LastModifiedUtc);

    private sealed record SitemapCategoryRow(
        int Id,
        int DepartmentId,
        int? ParentCategoryId,
        string Slug,
        DateTime? LastModifiedUtc);
}
