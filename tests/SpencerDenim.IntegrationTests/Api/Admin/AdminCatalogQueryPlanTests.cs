using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// What the ADM-11 read model costs, measured rather than asserted in a comment.
/// <para>
/// The counts beside each category are correlated sub-selects inside one projection, so the tree is two
/// queries however many categories a department holds. That is a claim about a query plan, and a claim
/// about a query plan is one refactor away from being false — a stray <c>Include</c>, a count moved into a
/// <c>Select</c> over a navigation, and the screen quietly becomes one query per node on a tree doc 08 §3.2
/// sizes at about 200 rows. <c>CatalogQueryPlanTests</c> guards the storefront listing the same way for the
/// same reason.
/// </para>
/// <para>
/// The bound is deliberately loose — it is there to catch an N+1, not to pin an exact number that a
/// framework upgrade would move.
/// </para>
/// </summary>
public sealed class AdminCatalogQueryPlanTests(SqlServerDatabaseFixture sqlServer) : IAsyncDisposable
{
    /// <summary>Enough categories that one query per node would be unmistakable against any fixed bound.</summary>
    private const int ExtraCategories = 120;

    private static readonly string[] CatalogueRights =
    [
        Permissions.Categories.View,
        Permissions.Categories.Create,
        Permissions.Categories.Edit,
        Permissions.Categories.Delete,
    ];

    private readonly CommandCountingInterceptor _commands = new();
    private readonly List<int> _created = [];

    private ApiFactory? _factory;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_category_tree_costs_the_same_two_queries_however_many_categories_it_holds()
    {
        var client = await SignInAsync("plan-tree");
        var departmentId = await DepartmentIdAsync("women");

        await SeedCategoriesAsync(departmentId, ExtraCategories);

        // Warm up, so the count excludes the model build, the session read and any first-request work.
        await client.GetAsync($"/api/admin/catalog/departments/{departmentId}/categories", Token);

        _commands.Reset();
        var (status, body) = await client.GetAsync(
            $"/api/admin/catalog/departments/{departmentId}/categories", Token);
        var commands = _commands.Count;

        Assert.Equal(HttpStatusCode.OK, status);

        var nodes = body.GetProperty("categories").EnumerateArray().Count();
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"{nodes} top-level categories returned with {commands} SQL commands.");

        Assert.True(nodes >= ExtraCategories, $"only {nodes} categories came back; the seed did not take.");

        // One query per node would be at least 120 more commands. Ten leaves room for the authentication
        // and permission reads the request also makes.
        Assert.True(
            commands <= 10,
            $"the admin tree issued {commands} SQL commands for {nodes} categories, which is per-node work.");
    }

    [Fact]
    public async Task The_department_list_costs_the_same_whatever_the_catalogue_holds()
    {
        var client = await SignInAsync("plan-list");

        await client.GetAsync("/api/admin/catalog/departments", Token);

        _commands.Reset();
        var (status, body) = await client.GetAsync("/api/admin/catalog/departments", Token);
        var commands = _commands.Count;

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3, body.EnumerateArray().Count());

        TestContext.Current.TestOutputHelper!.WriteLine($"3 departments returned with {commands} SQL commands.");
        Assert.True(commands <= 8, $"the department list issued {commands} SQL commands for 3 rows.");
    }

    // ---- helpers --------------------------------------------------------------------------------

    private async Task<AdminCatalogApiClient> SignInAsync(string label)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        _factory ??= new ApiFactory(sqlServer.ConnectionString, commandInterceptor: _commands);
        return await AdminCatalogApiClient.SignInAsync(_factory, label, CatalogueRights, Token);
    }

    private async Task<int> DepartmentIdAsync(string slug)
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.Departments.AsNoTracking()
            .Where(department => department.Slug == slug)
            .Select(department => department.Id)
            .SingleAsync(Token);
    }

    /// <summary>
    /// Adds categories straight through the context rather than through the API: this is about the read
    /// plan, and 120 round trips through the write path would be the slowest part of the suite.
    /// </summary>
    private async Task SeedCategoriesAsync(int departmentId, int count)
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var categories = new List<Category>(count);
        for (var i = 0; i < count; i++)
        {
            categories.Add(new Category
            {
                DepartmentId = departmentId,
                Name = $"W1-12 plan {i}",
                Slug = $"w112-plan-{i}",
                Depth = 1,
                SortOrder = 1000 + i,
                Path = "/0/",
            });
        }

        dbContext.Categories.AddRange(categories);
        await dbContext.SaveChangesAsync(Token);

        foreach (var category in categories)
        {
            category.Path = CategoryPath.Build(null, category.Id);
            _created.Add(category.Id);
        }

        await dbContext.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            // The fixture's database is shared by the whole assembly, so this suite's 120 rows cannot be
            // left in it: every other catalogue test counts categories.
            if (_created.Count > 0)
            {
                await using var scope = _factory.Services.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                await dbContext.Categories
                    .IgnoreQueryFilters()
                    .Where(category => _created.Contains(category.Id))
                    .ExecuteDeleteAsync(CancellationToken.None);
            }

            await _factory.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
