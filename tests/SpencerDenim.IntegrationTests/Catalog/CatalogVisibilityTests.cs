using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Catalog;

/// <summary>
/// What <c>IsActive</c> does to the storefront (doc 08 §7.1), and what an ambiguous category slug does
/// (doc 08 §3.2). Both need the seeded catalogue changed, so every test here restores what it changed
/// in a <c>finally</c>; the assembly runs its test classes sequentially, so nothing else observes the
/// half-applied state (see <c>AssemblyInfo.cs</c>).
/// </summary>
public sealed class CatalogVisibilityTests(SqlServerDatabaseFixture sqlServer) : CatalogTestBase(sqlServer)
{
    /// <summary>
    /// Deactivating a category used to take it out of the menu and make <c>?category=…</c> answer
    /// nothing, while its products stayed in the listing and in search, with a product page whose
    /// breadcrumb pointed at the now-empty category — a dead end reachable from a live page.
    /// </summary>
    [Fact]
    public async Task Deactivating_a_category_takes_its_products_off_the_storefront()
    {
        var api = CreateApi();

        var before = await api.ListAsync("?pageSize=96", Token);
        var skirts = await api.ListAsync("?department=women&category=skirts&pageSize=96", Token);
        var slugs = skirts.Items.Select(card => card.Slug).ToList();

        Assert.Equal(2, slugs.Count);
        Assert.Contains("denim-wrap-mini-skirt", slugs);

        await SetCategoryActiveAsync("skirts", isActive: false);
        try
        {
            var listing = await api.ListAsync("?pageSize=96", Token);
            Assert.Equal(before.TotalCount - slugs.Count, listing.TotalCount);
            Assert.DoesNotContain(listing.Items, card => slugs.Contains(card.Slug));

            var search = await api.SearchAsync("?q=denim&pageSize=96", Token);
            Assert.DoesNotContain(search.Results.Items, card => card.Slug == "denim-wrap-mini-skirt");

            var (status, _) = await api.GetRawAsync("/api/catalog/products/denim-wrap-mini-skirt", Token);
            Assert.Equal(HttpStatusCode.NotFound, status);
        }
        finally
        {
            await SetCategoryActiveAsync("skirts", isActive: true);
        }

        var after = await api.ListAsync("?pageSize=96", Token);
        Assert.Equal(before.TotalCount, after.TotalCount);
    }

    [Fact]
    public async Task Deactivating_a_department_takes_its_products_off_the_storefront()
    {
        var api = CreateApi();

        var before = await api.ListAsync("?pageSize=96", Token);
        var kids = await api.ListAsync("?department=kids&pageSize=96", Token);

        await SetDepartmentActiveAsync("kids", isActive: false);
        try
        {
            var listing = await api.ListAsync("?pageSize=96", Token);

            Assert.Equal(before.TotalCount - kids.TotalCount, listing.TotalCount);
            Assert.DoesNotContain(listing.Items, card => card.DepartmentSlug == "kids");

            var (status, _) = await api.GetRawAsync("/api/catalog/products/girls-printed-tee", Token);
            Assert.Equal(HttpStatusCode.NotFound, status);
        }
        finally
        {
            await SetDepartmentActiveAsync("kids", isActive: true);
        }

        var after = await api.ListAsync("?pageSize=96", Token);
        Assert.Equal(before.TotalCount, after.TotalCount);
    }

    /// <summary>
    /// A product keeps its place while only a secondary category link is deactivated: doc 08 §7.1 ties
    /// visibility to the department and the primary category, nothing else.
    /// </summary>
    [Fact]
    public async Task A_product_stays_visible_when_only_a_secondary_category_is_deactivated()
    {
        var api = CreateApi();

        var before = await api.ListAsync("?pageSize=96", Token);
        await SetSecondaryCategoryLinkAsync("denim-wrap-mini-skirt", "accessories", "women", linked: true);
        try
        {
            await SetCategoryActiveAsync("accessories", isActive: false, departmentSlug: "women");

            var listing = await api.ListAsync("?pageSize=96", Token);
            Assert.Contains(listing.Items, card => card.Slug == "denim-wrap-mini-skirt");
        }
        finally
        {
            await SetCategoryActiveAsync("accessories", isActive: true, departmentSlug: "women");
            await SetSecondaryCategoryLinkAsync("denim-wrap-mini-skirt", "accessories", "women", linked: false);
        }

        var after = await api.ListAsync("?pageSize=96", Token);
        Assert.Equal(before.TotalCount, after.TotalCount);
    }

    /// <summary>
    /// Doc 08 §3.2 makes a slug unique per (department, parent), so two categories under different
    /// parents of one department can share one. Naming the department does not tell them apart, and
    /// the guard used to stand down as soon as a department was given.
    /// </summary>
    [Fact]
    public async Task An_ambiguous_category_slug_inside_one_department_is_a_400_too()
    {
        var api = CreateApi();

        await SetCategorySlugAsync("boys-bottoms", "girls-bottoms");
        try
        {
            foreach (var query in new[] { "?category=girls-bottoms", "?category=girls-bottoms&department=kids" })
            {
                var (status, body) = await api.GetRawAsync("/api/catalog/products" + query, Token);

                Assert.Equal(HttpStatusCode.BadRequest, status);
                var message = body.GetProperty("errors").GetProperty("category")[0].GetString()!;

                Assert.Contains("2 active categories in department=kids", message, StringComparison.Ordinal);
                Assert.DoesNotContain("Add department=", message, StringComparison.Ordinal);
            }
        }
        finally
        {
            await SetCategorySlugAsync("girls-bottoms", "boys-bottoms", parentSlug: "boys");
        }

        var restored = await api.ListAsync("?category=girls-bottoms&pageSize=96", Token);
        Assert.NotEmpty(restored.Items);
    }

    private async Task SetCategoryActiveAsync(string slug, bool isActive, string? departmentSlug = null)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var category = await dbContext.Categories
            .FirstAsync(
                candidate => candidate.Slug == slug
                    && (departmentSlug == null || candidate.Department!.Slug == departmentSlug),
                Token);

        category.IsActive = isActive;
        await dbContext.SaveChangesAsync(Token);
    }

    private async Task SetDepartmentActiveAsync(string slug, bool isActive)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var department = await dbContext.Departments.FirstAsync(candidate => candidate.Slug == slug, Token);
        department.IsActive = isActive;
        await dbContext.SaveChangesAsync(Token);
    }

    /// <summary>Renames a category, identified by its slug and optionally by its parent's slug.</summary>
    private async Task SetCategorySlugAsync(string slug, string newSlug, string? parentSlug = null)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var category = await dbContext.Categories
            .FirstAsync(
                candidate => candidate.Slug == slug
                    && (parentSlug == null || candidate.ParentCategory!.Slug == parentSlug),
                Token);

        category.Slug = newSlug;
        await dbContext.SaveChangesAsync(Token);
    }

    /// <summary>Adds or removes a <c>ProductCategories</c> row, which is a link and never a primary category.</summary>
    private async Task SetSecondaryCategoryLinkAsync(
        string productSlug,
        string categorySlug,
        string departmentSlug,
        bool linked)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var productId = await dbContext.Products
            .Where(product => product.Slug == productSlug)
            .Select(product => product.Id)
            .FirstAsync(Token);

        var categoryId = await dbContext.Categories
            .Where(category => category.Slug == categorySlug && category.Department!.Slug == departmentSlug)
            .Select(category => category.Id)
            .FirstAsync(Token);

        if (linked)
        {
            dbContext.ProductCategories.Add(new ProductCategory { ProductId = productId, CategoryId = categoryId });
            await dbContext.SaveChangesAsync(Token);
            return;
        }

        await dbContext.ProductCategories
            .Where(link => link.ProductId == productId && link.CategoryId == categoryId)
            .ExecuteDeleteAsync(Token);
    }
}
