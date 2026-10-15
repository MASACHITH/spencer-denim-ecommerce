using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Analytics;
using SpencerDenim.Domain.Analytics;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Analytics;

/// <summary>
/// Writes one batch of page views: resolves what only the catalogue knows, then inserts.
/// <para>
/// <b>Resolution happens here, once per batch, not per view on the request path.</b> Whether <c>/women</c>
/// is a department and <c>/about</c> a content page, which product <c>/product/{slug}</c> is and which
/// category <c>/kids/girls/girls-dresses</c> is, all need a database read — which a recorder that must
/// never slow a page cannot make. A batch of up to 200 views costs at most four small reads here.
/// </para>
/// <para>
/// A slug that resolves to nothing is kept as <see cref="PageType.Other"/> (single segments and listings)
/// or as a product view with no <see cref="PageView.ProductId"/>: a page the server rendered is never
/// thrown away for want of a catalogue match, and a mistyped address a browser reported is not credited to
/// a real page.
/// </para>
/// <para>
/// <b>No isolation level is set and no transaction is opened</b> beyond the one <c>SaveChanges</c> makes: the
/// connection goes back to the pool exactly as it came out (the <c>CartStore</c> lesson).
/// </para>
/// </summary>
internal sealed class PageViewStore(AppDbContext dbContext)
{
    public async Task InsertAsync(IReadOnlyList<PageView> views, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(views);

        if (views.Count == 0)
        {
            return;
        }

        await ResolveAsync(views, cancellationToken);

        dbContext.PageViews.AddRange(views);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
    }

    private async Task ResolveAsync(IReadOnlyList<PageView> views, CancellationToken cancellationToken)
    {
        var parsed = views.Select(view => (View: view, Segments: SegmentsOf(view.Path))).ToList();

        var productSlugs = parsed
            .Where(item => item.View.PageType == PageType.Product && item.Segments.Count == 2
                && item.Segments[1] != PagePath.Redacted)
            .Select(item => item.Segments[1])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var needsDepartments = parsed.Any(item => item.View.PageType is PageType.Department or PageType.Category);

        var products = productSlugs.Count == 0
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : await dbContext.Products
                .AsNoTracking()
                .Where(product => productSlugs.Contains(product.Slug))
                .Select(product => new { product.Slug, product.Id })
                .ToDictionaryAsync(row => row.Slug, row => row.Id, StringComparer.Ordinal, cancellationToken);

        var departments = !needsDepartments
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : await dbContext.Departments
                .AsNoTracking()
                .Select(department => new { department.Slug, department.Id })
                .ToDictionaryAsync(row => row.Slug, row => row.Id, StringComparer.Ordinal, cancellationToken);

        var contentSlugs = parsed
            .Where(item => item.View.PageType == PageType.Department && item.Segments.Count == 1
                && !departments.ContainsKey(item.Segments[0]))
            .Select(item => item.Segments[0])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var contentPages = contentSlugs.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await dbContext.ContentPages
                .AsNoTracking()
                .Where(page => page.IsPublished && contentSlugs.Contains(page.Slug))
                .Select(page => page.Slug)
                .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);

        var listingDepartments = parsed
            .Where(item => item.View.PageType == PageType.Category && departments.ContainsKey(item.Segments[0]))
            .Select(item => departments[item.Segments[0]])
            .Distinct()
            .ToList();

        var categories = listingDepartments.Count == 0
            ? []
            : await dbContext.Categories
                .AsNoTracking()
                .Where(category => listingDepartments.Contains(category.DepartmentId))
                .Select(category => new CategoryRow(category.Id, category.DepartmentId, category.ParentCategoryId, category.Slug, category.Depth))
                .ToListAsync(cancellationToken);

        foreach (var (view, segments) in parsed)
        {
            switch (view.PageType)
            {
                case PageType.Product:
                    view.ProductId = segments.Count == 2 && products.TryGetValue(segments[1], out var productId)
                        ? productId
                        : null;
                    break;

                case PageType.Department:
                    view.PageType = departments.ContainsKey(segments[0])
                        ? PageType.Department
                        : contentPages.Contains(segments[0]) ? PageType.Content : PageType.Other;
                    break;

                case PageType.Category:
                    view.CategoryId = departments.TryGetValue(segments[0], out var departmentId)
                        ? FindCategory(categories, departmentId, segments)
                        : null;
                    view.PageType = view.CategoryId is null ? PageType.Other : PageType.Category;
                    break;
            }
        }
    }

    /// <summary>
    /// The storefront's own rule (<c>categoryVerdict</c> in <c>catalog-route.guards.ts</c>): the department, and
    /// the <i>deepest</i> segment found anywhere in its tree. When two categories in one department share that
    /// slug under different parents, the one whose parent is the segment before it wins.
    /// </summary>
    private static int? FindCategory(List<CategoryRow> categories, int departmentId, List<string> segments)
    {
        var slug = segments[^1];

        var candidates = categories
            .Where(category => category.DepartmentId == departmentId && category.Slug == slug)
            .ToList();

        if (candidates.Count <= 1 || segments.Count < 3)
        {
            return candidates.OrderBy(category => category.Depth).FirstOrDefault()?.Id;
        }

        var parent = categories.FirstOrDefault(category =>
            category.DepartmentId == departmentId && category.Slug == segments[^2]);

        return (candidates.FirstOrDefault(category => category.ParentCategoryId == parent?.Id)
            ?? candidates.OrderBy(category => category.Depth).First()).Id;
    }

    private static List<string> SegmentsOf(string path)
    {
        var queryAt = path.IndexOf('?', StringComparison.Ordinal);

        return (queryAt >= 0 ? path[..queryAt] : path)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    private sealed record CategoryRow(int Id, int DepartmentId, int? ParentCategoryId, string Slug, byte Depth);
}
