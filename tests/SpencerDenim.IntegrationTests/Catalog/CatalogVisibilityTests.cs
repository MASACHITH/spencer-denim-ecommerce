using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Inventory;
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

    /// <summary>
    /// Issue #25: <c>LoadBestSellerThresholdsAsync</c> queried <c>dbContext.Products</c> directly rather
    /// than through <c>BaseQuery()</c>, so a product in a deactivated category still counted towards its
    /// department's top N by <c>SoldQty30d</c>. The threshold then came out too high and the Best Seller
    /// badge was withheld from a product that had earned it.
    /// <para>
    /// The arithmetic, with the seeded women's department and <c>Catalog:BestSellerTopN</c> at its default
    /// of 10. Sold in 30 days, descending: 88, 71, 64, 63, 57, 52, 45, 40, 34, <b>31</b>, 27, 22, … so the
    /// threshold is 31 and <c>belted-shirt-dress</c> at 27 shows no badge. Deactivating women's
    /// <c>jeans</c> withdraws the 88 and the 45; the tenth is then 22, the dress clears it, and because
    /// the dress has no discount, no override, healthy stock and was published 175 days ago, Best Seller
    /// is the badge the resolver reaches (doc 08 §3.2 orders them Sale → Low Stock → Best Seller → New).
    /// </para>
    /// <para>
    /// <b>This test is red without the fix</b>, with the dress still carrying no badge: the two withdrawn
    /// products keep counting and the threshold stays at 31.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Deactivating_a_category_stops_its_products_setting_the_best_seller_threshold()
    {
        var api = CreateApi();

        var before = await FindAsync(api, "belted-shirt-dress");
        Assert.Null(before.Badge);
        Assert.False(before.Price.HasDiscount);
        Assert.Equal(StockStatus.InStock, before.StockStatus);

        // The two products that are about to leave are in the top ten today, which is what makes the
        // threshold move at all.
        var jeans = await api.ListAsync("?department=women&category=jeans&pageSize=96", Token);
        Assert.Equal(2, jeans.Items.Count);

        await SetCategoryActiveAsync("jeans", isActive: false, departmentSlug: "women");
        try
        {
            var after = await FindAsync(api, "belted-shirt-dress");

            Assert.Equal(ProductBadge.BestSeller, after.Badge);
        }
        finally
        {
            await SetCategoryActiveAsync("jeans", isActive: true, departmentSlug: "women");
        }

        var restored = await FindAsync(api, "belted-shirt-dress");
        Assert.Null(restored.Badge);
    }

    /// <summary>
    /// The same correction at the boundary rather than inside it, on a different top N so it does not
    /// rest on where the tenth place happens to fall.
    /// <para>
    /// Women's three best sellers are 88 (<c>jeans</c>), 71 and 64, so at <c>BestSellerTopN</c> 3 the
    /// threshold is 64 and <c>seamless-studio-leggings</c> misses it by one at 63. Withdrawing
    /// <c>jeans</c> leaves 71, 64, 63 — the threshold becomes 63, which the leggings <i>equal</i>, and
    /// the comparison is <c>&gt;=</c>. Red without <c>BaseQuery()</c>: the withdrawn 88 keeps third place
    /// at 64 and the badge never appears.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_products_badge_turns_on_the_moment_a_deactivated_category_stops_counting()
    {
        SqlServer.SkipIfUnavailable();

        await using var factory = new ApiFactory(
            SqlServer.ConnectionString,
            additionalConfiguration: new Dictionary<string, string?> { ["Catalog:BestSellerTopN"] = "3" });
        using var client = factory.CreateClient();
        var api = new CatalogApiClient(client);

        Assert.Null((await FindAsync(api, "seamless-studio-leggings")).Badge);

        await SetCategoryActiveAsync("jeans", isActive: false, departmentSlug: "women");
        try
        {
            Assert.Equal(ProductBadge.BestSeller, (await FindAsync(api, "seamless-studio-leggings")).Badge);
        }
        finally
        {
            await SetCategoryActiveAsync("jeans", isActive: true, departmentSlug: "women");
        }

        Assert.Null((await FindAsync(api, "seamless-studio-leggings")).Badge);
    }

    /// <summary>One card off the women's listing, by slug.</summary>
    private static async Task<ProductCardDto> FindAsync(CatalogApiClient api, string slug)
    {
        var listing = await api.ListAsync("?department=women&pageSize=96", Token);
        var card = listing.Items.SingleOrDefault(candidate => candidate.Slug == slug);

        Assert.NotNull(card);
        return card;
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
