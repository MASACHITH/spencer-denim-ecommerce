using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Catalog;

/// <summary>
/// What an admin's own click does to the shop (ADM-11, doc 08 §7.1).
/// <para>
/// <b>Why this is a separate suite from the endpoint tests.</b> Asserting that <c>IsActive</c> is now 0 is
/// asserting that a flag was set; it would pass with the storefront reader broken, missing, or ignoring the
/// column. <c>CatalogVisibilityTests</c> already proves the reader honours the flag when the flag is changed
/// with raw SQL. What was never proved is the whole chain: an admin signs in, presses the button in the API
/// that ADM-11 actually gives them, and the products leave the public listing, the search results and their
/// own product page. That is the claim a client would make, so it is the one tested here.
/// </para>
/// <para>
/// Everything is put back in a <c>finally</c>: the fixture's database is shared and the assembly runs its
/// classes in sequence, so a category left off would fail a listing assertion somewhere else.
/// </para>
/// </summary>
public sealed class AdminDeactivationEffectTests(SqlServerDatabaseFixture sqlServer) : IAsyncDisposable
{
    private const string CategoriesUrl = "/api/admin/catalog/categories";
    private const string DepartmentsUrl = "/api/admin/catalog/departments";

    private static readonly string[] CatalogueRights =
    [
        Permissions.Categories.View,
        Permissions.Categories.Edit,
    ];

    private ApiFactory? _factory;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_admin_deactivating_a_category_takes_its_products_off_the_storefront()
    {
        var factory = await HostAsync();
        var admin = await AdminCatalogApiClient.SignInAsync(factory, "deactivate", CatalogueRights, Token);
        using var shopperClient = AdminCatalogApiClient.Anonymous(factory);
        var shop = new CatalogApiClient(shopperClient);

        var women = await DepartmentIdAsync(factory, "women");
        var skirts = await CategoryIdAsync(factory, women, "skirts");

        var everything = await shop.ListAsync("?pageSize=96", Token);
        var inSkirts = await shop.ListAsync("?department=women&category=skirts&pageSize=96", Token);
        var slugs = inSkirts.Items.Select(card => card.Slug).ToList();

        Assert.NotEmpty(slugs);

        // The warning the screen shows before the button is pressed has to agree with what happens.
        var (impactStatus, impact) = await admin.GetAsync($"{CategoriesUrl}/{skirts}/deactivation-impact", Token);
        Assert.Equal(HttpStatusCode.OK, impactStatus);
        Assert.Equal(slugs.Count, impact.GetProperty("productsLeavingTheStorefront").GetInt32());
        Assert.NotEmpty(impact.GetProperty("examples").EnumerateArray());

        await SetCategoryActiveAsync(admin, factory, skirts, isActive: false);

        try
        {
            var listing = await shop.ListAsync("?pageSize=96", Token);
            Assert.Equal(everything.TotalCount - slugs.Count, listing.TotalCount);
            Assert.DoesNotContain(listing.Items, card => slugs.Contains(card.Slug));

            // The category's own listing answers the PLP-06 "no products found" state rather than an error:
            // an inactive slug resolves to nothing, which the reader treats as a filter that matches no rows
            // (ProductCatalogReader.MatchesNothing). A 200 with products in it would be the defect.
            var narrowed = await shop.ListAsync("?department=women&category=skirts&pageSize=96", Token);
            Assert.Equal(0, narrowed.TotalCount);
            Assert.Empty(narrowed.Items);

            var search = await shop.SearchAsync("?q=skirt&pageSize=96", Token);
            Assert.DoesNotContain(search.Results.Items, card => slugs.Contains(card.Slug));

            var product = await shop.GetRawAsync($"/api/catalog/products/{slugs[0]}", Token);
            Assert.Equal(HttpStatusCode.NotFound, product.Status);

            // And it is out of the mega menu, while the admin tree still shows it so it can be switched back.
            using var menu = await shopperClient.GetAsync(
                new Uri("/api/catalog/departments", UriKind.Relative), Token);
            var menuBody = await menu.Content.ReadAsStringAsync(Token);
            Assert.DoesNotContain("\"slug\":\"skirts\"", menuBody, StringComparison.Ordinal);

            var (_, tree) = await admin.GetAsync($"{DepartmentsUrl}/{women}/categories", Token);
            var node = tree.GetProperty("categories").EnumerateArray()
                .Single(candidate => candidate.GetProperty("id").GetInt32() == skirts);
            Assert.False(node.GetProperty("isActive").GetBoolean());
        }
        finally
        {
            await SetCategoryActiveAsync(admin, factory, skirts, isActive: true);
        }

        // Switching it back on restores exactly what left — proving the first half was reversible, which is
        // the property that makes IsActive a switch rather than a deletion.
        var restored = await shop.ListAsync("?pageSize=96", Token);
        Assert.Equal(everything.TotalCount, restored.TotalCount);

        var skirtsAgain = await shop.ListAsync("?department=women&category=skirts&pageSize=96", Token);
        Assert.Equal(slugs.Order(StringComparer.Ordinal), skirtsAgain.Items.Select(card => card.Slug).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The number an admin would never predict, and the reason the impact payload has three of them.
    /// <c>IsActive</c> is per row and <c>ProductCatalogReader.BaseQuery</c> checks only a product's own
    /// primary category, so deactivating a <i>parent</i> leaves its children's products on sale with no menu
    /// route to them.
    /// </summary>
    [Fact]
    public async Task Deactivating_a_parent_strands_its_children_s_products_rather_than_hiding_them()
    {
        var factory = await HostAsync();
        var admin = await AdminCatalogApiClient.SignInAsync(factory, "strand", CatalogueRights, Token);
        using var shopperClient = AdminCatalogApiClient.Anonymous(factory);
        var shop = new CatalogApiClient(shopperClient);

        var kids = await DepartmentIdAsync(factory, "kids");
        var girls = await CategoryIdAsync(factory, kids, "girls");

        var (_, impact) = await admin.GetAsync($"{CategoriesUrl}/{girls}/deactivation-impact", Token);

        var leaving = impact.GetProperty("productsLeavingTheStorefront").GetInt32();
        var descendants = impact.GetProperty("descendantCategories").GetInt32();
        var stranded = impact.GetProperty("descendantProductsLosingTheirMenuRoute").GetInt32();

        // "Girls" is a level-1 grouping with no products of its own and three child categories that have them.
        Assert.Equal(0, leaving);
        Assert.Equal(3, descendants);
        Assert.True(stranded > 0, "the seeded Girls subtree should hold published products");

        var before = await shop.ListAsync("?pageSize=96", Token);
        await SetCategoryActiveAsync(admin, factory, girls, isActive: false);

        try
        {
            // Exactly what the payload said: nothing leaves the listing…
            var after = await shop.ListAsync("?pageSize=96", Token);
            Assert.Equal(before.TotalCount, after.TotalCount);

            // …and the whole subtree is out of the navigation, children included, even though their own
            // IsActive is still 1.
            using var menu = await shopperClient.GetAsync(
                new Uri("/api/catalog/departments", UriKind.Relative), Token);
            var menuBody = await menu.Content.ReadAsStringAsync(Token);

            Assert.DoesNotContain("\"slug\":\"girls\"", menuBody, StringComparison.Ordinal);
            Assert.DoesNotContain("\"slug\":\"girls-dresses\"", menuBody, StringComparison.Ordinal);

            // The children are still active rows, which is why the count above is the honest one to show.
            await using var scope = factory.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await dbContext.Categories.AsNoTracking()
                .AnyAsync(category => category.Slug == "girls-dresses" && category.IsActive, Token));
        }
        finally
        {
            await SetCategoryActiveAsync(admin, factory, girls, isActive: true);
        }
    }

    [Fact]
    public async Task An_admin_deactivating_a_department_empties_it_on_the_storefront()
    {
        var factory = await HostAsync();
        var admin = await AdminCatalogApiClient.SignInAsync(factory, "deptoff", CatalogueRights, Token);
        using var shopperClient = AdminCatalogApiClient.Anonymous(factory);
        var shop = new CatalogApiClient(shopperClient);

        var kids = await DepartmentIdAsync(factory, "kids");
        var everything = await shop.ListAsync("?pageSize=96", Token);
        var inKids = await shop.ListAsync("?department=kids&pageSize=96", Token);

        Assert.True(inKids.TotalCount > 0);

        var rowVersion = await DepartmentRowVersionAsync(factory, kids);
        var (status, body) = await admin.PutAsync(
            $"{DepartmentsUrl}/{kids}/active", new { isActive = false, rowVersion }, Token);
        Assert.True(status == HttpStatusCode.NoContent, $"{(int)status}: {body}");

        try
        {
            var listing = await shop.ListAsync("?pageSize=96", Token);

            Assert.Equal(everything.TotalCount - inKids.TotalCount, listing.TotalCount);
            Assert.DoesNotContain(listing.Items, card => card.DepartmentSlug == "kids");
        }
        finally
        {
            var current = await DepartmentRowVersionAsync(factory, kids);
            await admin.PutAsync(
                $"{DepartmentsUrl}/{kids}/active", new { isActive = true, rowVersion = current }, Token);
        }

        Assert.Equal(everything.TotalCount, (await shop.ListAsync("?pageSize=96", Token)).TotalCount);
    }

    /// <summary>
    /// A fresh category an admin creates through ADM-11 reaches the shop — the other direction of the same
    /// chain, and the one that shows the write path is wired to the reader the storefront really uses.
    /// </summary>
    [Fact]
    public async Task A_category_an_admin_creates_appears_in_the_storefront_navigation()
    {
        var factory = await HostAsync();
        var admin = await AdminCatalogApiClient.SignInAsync(
            factory, "newcat", [Permissions.Categories.View, Permissions.Categories.Create, Permissions.Categories.Edit], Token);
        using var shopperClient = AdminCatalogApiClient.Anonymous(factory);

        var women = await DepartmentIdAsync(factory, "women");

        var (createStatus, created) = await admin.PostAsync(
            CategoriesUrl,
            new
            {
                departmentId = women,
                name = "W1-12 Linen Shirting",
                slug = "w112-linen-shirting",
                description = "Everything in linen.",
                showInMegaMenu = true,
                megaMenuColumn = 1,
            },
            Token);

        Assert.Equal(HttpStatusCode.Created, createStatus);
        var categoryId = created.GetProperty("id").GetInt32();

        try
        {
            using var menu = await shopperClient.GetAsync(
                new Uri("/api/catalog/departments", UriKind.Relative), Token);
            var departments = await menu.Content.ReadFromJsonAsync<JsonElement>(
                CatalogApiClient.JsonOptions, Token);

            var node = departments.EnumerateArray()
                .Single(department => department.GetProperty("slug").GetString() == "women")
                .GetProperty("categories")
                .EnumerateArray()
                .Single(category => category.GetProperty("slug").GetString() == "w112-linen-shirting");

            Assert.Equal("W1-12 Linen Shirting", node.GetProperty("name").GetString());
            Assert.Equal("Everything in linen.", node.GetProperty("description").GetString());
            Assert.Equal((byte)1, node.GetProperty("depth").GetByte());

            // Empty, so its listing answers an honest empty page rather than a 400 or a 500.
            var shop = new CatalogApiClient(shopperClient);
            var listing = await shop.ListAsync("?department=women&category=w112-linen-shirting", Token);
            Assert.Equal(0, listing.TotalCount);
        }
        finally
        {
            await HardDeleteAsync(factory, categoryId);
        }
    }

    private async Task<ApiFactory> HostAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        return _factory ??= new ApiFactory(sqlServer.ConnectionString);
    }

    private static async Task SetCategoryActiveAsync(
        AdminCatalogApiClient admin,
        ApiFactory factory,
        int categoryId,
        bool isActive)
    {
        var rowVersion = await CategoryRowVersionAsync(factory, categoryId);
        var (status, body) = await admin.PutAsync(
            $"{CategoriesUrl}/{categoryId}/active", new { isActive, rowVersion }, Token);

        Assert.True(
            status == HttpStatusCode.NoContent,
            $"switching category {categoryId} to isActive={isActive} returned {(int)status}: {body}");
    }

    private static async Task<int> DepartmentIdAsync(ApiFactory factory, string slug)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.Departments.AsNoTracking()
            .Where(department => department.Slug == slug)
            .Select(department => department.Id)
            .SingleAsync(Token);
    }

    private static async Task<int> CategoryIdAsync(ApiFactory factory, int departmentId, string slug)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.Categories.AsNoTracking()
            .Where(category => category.DepartmentId == departmentId && category.Slug == slug)
            .Select(category => category.Id)
            .SingleAsync(Token);
    }

    private static async Task<string> CategoryRowVersionAsync(ApiFactory factory, int categoryId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rowVersion = await dbContext.Categories.AsNoTracking()
            .Where(category => category.Id == categoryId)
            .Select(category => category.RowVersion)
            .SingleAsync(Token);

        return Convert.ToBase64String(rowVersion!);
    }

    private static async Task<string> DepartmentRowVersionAsync(ApiFactory factory, int departmentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rowVersion = await dbContext.Departments.AsNoTracking()
            .Where(department => department.Id == departmentId)
            .Select(department => department.RowVersion)
            .SingleAsync(Token);

        return Convert.ToBase64String(rowVersion!);
    }

    private static async Task HardDeleteAsync(ApiFactory factory, int categoryId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.Categories
            .IgnoreQueryFilters()
            .Where(category => category.Id == categoryId)
            .ExecuteDeleteAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
