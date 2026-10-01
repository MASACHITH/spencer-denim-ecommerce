using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Catalog;

/// <summary>
/// <c>GET /api/catalog/sitemap</c> against the real database (GLB-11).
///
/// <para>
/// **What has to be true, and why a unit test cannot say it.** The sitemap is a public document that
/// tells a crawler which addresses exist, so the expensive mistake is offering one that answers 404 —
/// a listing an admin deactivated, a category they deleted, a product they unpublished. W1-12 made
/// that reachable by the client rather than only by a migration, which is why this is tested by
/// changing the catalogue and reading the sitemap back rather than by asserting a predicate.
/// </para>
/// <para>
/// Every test restores what it changed in a <c>finally</c>; the assembly runs its test classes
/// sequentially, so nothing else observes the half-applied state (see <c>AssemblyInfo.cs</c>).
/// </para>
/// </summary>
public sealed class CatalogSitemapTests(SqlServerDatabaseFixture sqlServer) : CatalogTestBase(sqlServer)
{
    [Fact]
    public async Task It_offers_every_department_category_and_product_the_storefront_serves()
    {
        var api = CreateApi();

        var sitemap = await GetSitemapAsync();
        var departments = await api.GetDepartmentsAsync(Token);
        var listing = await api.ListAsync("?pageSize=96", Token);

        // Exactly the departments the menu is drawn from, in the same order.
        Assert.Equal(
            departments.Select(department => department.Slug),
            sitemap.Departments.Select(department => department.Slug));

        // And a category the menu offers is a category the sitemap offers, at the same address.
        var menuTrails = departments
            .SelectMany(department => Trails(department.Slug, department.Categories))
            .ToHashSet(StringComparer.Ordinal);
        var sitemapTrails = sitemap.Departments
            .SelectMany(department =>
                department.Categories.Select(category => $"{department.Slug}/{string.Join('/', category.SlugTrail)}"))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(menuTrails, sitemapTrails);

        // The listing's first page is a subset of the products offered, which is the cheapest check
        // that the two read the same rows. The exact total is asserted below.
        var offered = sitemap.Products.Select(product => product.Slug).ToHashSet(StringComparer.Ordinal);
        Assert.All(listing.Items, card => Assert.Contains(card.Slug, offered));
        Assert.Equal(listing.TotalCount, sitemap.Products.Count);
        Assert.False(sitemap.ProductsTruncated);
    }

    /// <summary>
    /// The one that matters: a category the client switches off leaves the sitemap at the moment it
    /// leaves the shop. Before <c>CatalogVisibility</c>, "visible" was written out in each reader, and
    /// a third copy in the sitemap would have been free to drift from the first two.
    /// </summary>
    [Fact]
    public async Task Deactivating_a_category_takes_it_and_its_products_out_of_the_sitemap()
    {
        var api = CreateApi();

        var before = await GetSitemapAsync();
        var skirts = await api.ListAsync("?department=women&category=skirts&pageSize=96", Token);
        var skirtSlugs = skirts.Items.Select(card => card.Slug).ToList();

        Assert.NotEmpty(skirtSlugs);
        Assert.Contains(before.Departments.Single(d => d.Slug == "women").Categories, category => category.SlugTrail is ["skirts"]);

        await SetCategoryActiveAsync("skirts", isActive: false);
        try
        {
            var after = await GetSitemapAsync();

            Assert.DoesNotContain(
                after.Departments.Single(department => department.Slug == "women").Categories,
                category => category.SlugTrail is ["skirts"]);

            var offered = after.Products.Select(product => product.Slug).ToHashSet(StringComparer.Ordinal);
            Assert.All(skirtSlugs, slug => Assert.DoesNotContain(slug, offered));
            Assert.Equal(before.Products.Count - skirtSlugs.Count, after.Products.Count);
        }
        finally
        {
            await SetCategoryActiveAsync("skirts", isActive: true);
        }

        Assert.Equal(before.Products.Count, (await GetSitemapAsync()).Products.Count);
    }

    [Fact]
    public async Task Deactivating_a_department_takes_its_whole_subtree_out_of_the_sitemap()
    {
        var api = CreateApi();

        var before = await GetSitemapAsync();
        var kids = await api.ListAsync("?department=kids&pageSize=96", Token);

        await SetDepartmentActiveAsync("kids", isActive: false);
        try
        {
            var after = await GetSitemapAsync();

            Assert.DoesNotContain(after.Departments, department => department.Slug == "kids");
            Assert.Equal(before.Products.Count - kids.TotalCount, after.Products.Count);
        }
        finally
        {
            await SetDepartmentActiveAsync("kids", isActive: true);
        }
    }

    /// <summary>
    /// A category whose own row is active but whose parent is not has no reachable address — the
    /// listing refuses the middle segment — so offering it would be handing a crawler a 404 on
    /// purpose. The mega menu behaves the same way, because it only draws children of a node it has
    /// already drawn.
    /// </summary>
    [Fact]
    public async Task A_category_under_a_deactivated_parent_is_not_offered()
    {
        CreateApi();

        var before = await GetSitemapAsync();
        var kidsBefore = before.Departments.Single(department => department.Slug == "kids").Categories;

        Assert.Contains(kidsBefore, category => category.SlugTrail is ["girls"]);
        var underGirls = kidsBefore.Count(category => category.SlugTrail.Count > 1 && category.SlugTrail[0] == "girls");
        Assert.True(underGirls > 0, "the seeded Kids tree should have categories under Girls");

        await SetCategoryActiveAsync("girls", isActive: false);
        try
        {
            var after = await GetSitemapAsync();
            var kidsAfter = after.Departments.Single(department => department.Slug == "kids").Categories;

            Assert.DoesNotContain(kidsAfter, category => category.SlugTrail is ["girls"]);
            Assert.DoesNotContain(kidsAfter, category => category.SlugTrail[0] == "girls");
        }
        finally
        {
            await SetCategoryActiveAsync("girls", isActive: true);
        }
    }

    [Fact]
    public async Task Unpublishing_a_product_takes_it_out_of_the_sitemap()
    {
        CreateApi();

        var before = await GetSitemapAsync();
        var slug = before.Products[0].Slug;

        await SetProductPublishedAsync(slug, isPublished: false);
        try
        {
            var after = await GetSitemapAsync();

            Assert.DoesNotContain(after.Products, product => product.Slug == slug);
            Assert.Equal(before.Products.Count - 1, after.Products.Count);
        }
        finally
        {
            await SetProductPublishedAsync(slug, isPublished: true);
        }
    }

    /// <summary>
    /// A public endpoint that enumerates the catalogue must be bounded, and it must say when the bound
    /// was reached rather than answering a quietly short document. One is used as the cap so that the
    /// seeded catalogue is comfortably past it.
    /// </summary>
    [Fact]
    public async Task It_caps_the_product_list_and_reports_that_it_did()
    {
        SqlServer.SkipIfUnavailable();

        await using var factory = new ApiFactory(
            SqlServer.ConnectionString,
            additionalConfiguration: new Dictionary<string, string?> { ["Catalog:SitemapMaxProducts"] = "1" });
        using var client = factory.CreateClient();

        var sitemap = await client.GetFromJsonAsync<CatalogSitemapDto>(
            "/api/catalog/sitemap",
            CatalogApiClient.JsonOptions,
            Token);

        Assert.NotNull(sitemap);
        Assert.Single(sitemap.Products);
        Assert.True(sitemap.ProductsTruncated);
        // The departments are not capped: the tree is about 200 rows whole (doc 08 §3.2).
        Assert.NotEmpty(sitemap.Departments);
    }

    /// <summary>
    /// Three queries, whatever the catalogue holds: departments, categories, products. It is the one
    /// read in the system whose result set grows with the whole shop, so an N+1 here is a crawler
    /// hitting the database once per product.
    /// </summary>
    [Fact]
    public async Task It_reads_the_whole_catalogue_in_three_queries()
    {
        CreateApi();
        CommandCounter.Reset();

        await GetSitemapAsync();

        Assert.Equal(3, CommandCounter.Count);
    }

    [Fact]
    public async Task It_answers_HEAD_as_well_as_GET()
    {
        var api = CreateApi();

        using var response = await api.HeadAsync("/api/catalog/sitemap", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Every slug in the document is one the storefront's own route pattern accepts.</summary>
    [Fact]
    public async Task Every_slug_it_offers_is_a_slug()
    {
        CreateApi();

        var sitemap = await GetSitemapAsync();

        foreach (var department in sitemap.Departments)
        {
            Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", department.Slug);
            foreach (var segment in department.Categories.SelectMany(category => category.SlugTrail))
            {
                Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", segment);
            }
        }

        foreach (var product in sitemap.Products)
        {
            Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", product.Slug);
        }
    }

    private async Task<CatalogSitemapDto> GetSitemapAsync()
    {
        var api = CreateApi();
        var (status, body) = await api.GetRawAsync("/api/catalog/sitemap", Token);

        Assert.Equal(HttpStatusCode.OK, status);

        var sitemap = JsonSerializer.Deserialize<CatalogSitemapDto>(body, CatalogApiClient.JsonOptions);
        Assert.NotNull(sitemap);

        return sitemap;
    }

    /// <summary>Every category address under a department, as "department/trail", from the menu tree.</summary>
    private static IEnumerable<string> Trails(string departmentSlug, IEnumerable<CategoryNodeDto> nodes)
    {
        foreach (var node in nodes)
        {
            yield return $"{departmentSlug}/{node.Slug}";

            foreach (var child in Trails($"{departmentSlug}/{node.Slug}", node.Children))
            {
                yield return child;
            }
        }
    }

    private async Task SetCategoryActiveAsync(string slug, bool isActive)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var category = await dbContext.Categories.FirstAsync(candidate => candidate.Slug == slug, Token);
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

    private async Task SetProductPublishedAsync(string slug, bool isPublished)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var product = await dbContext.Products.FirstAsync(candidate => candidate.Slug == slug, Token);
        product.IsPublished = isPublished;
        await dbContext.SaveChangesAsync(Token);
    }
}
