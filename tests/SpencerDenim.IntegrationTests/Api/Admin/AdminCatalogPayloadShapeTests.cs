using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The exact property names the ADM-11 read payloads carry on the wire (W1-12).
/// <para>
/// <b>Why name-for-name and not "it contains the fields I happened to assert".</b> The admin screens' only
/// contact with the server is this JSON, and their TypeScript interfaces
/// (<c>projects/admin/src/app/core/catalog/admin-catalog.model.ts</c>) are a separate hand-written copy of
/// it. A renamed or dropped property is a field that silently reads <c>undefined</c> in the browser — a
/// count that shows as blank, a row version that is never sent back and so turns every save into a
/// "reload the page" conflict. The browser suite cannot catch it either, because it runs against a stub
/// that would be renamed in the same commit.
/// </para>
/// <para>
/// So this asserts the whole set, through the API's real serializer and its real
/// <c>ConfigureHttpJsonOptions</c>. A field added on purpose fails it once, in one place, with the list to
/// update — which is the point: the frontend model has to be updated with it.
/// </para>
/// </summary>
public sealed class AdminCatalogPayloadShapeTests(SqlServerDatabaseFixture sqlServer) : IAsyncDisposable
{
    private static readonly string[] DepartmentProperties =
    [
        "id",
        "name",
        "slug",
        "description",
        "heroImageAssetId",
        "sortOrder",
        "isActive",
        "seoTitle",
        "seoDescription",
        "rowVersion",
        "categoryCount",
        "storefrontProductCount",
    ];

    private static readonly string[] TreeProperties =
    [
        "departmentId",
        "departmentName",
        "departmentSlug",
        "departmentIsActive",
        "categories",
    ];

    private static readonly string[] CategoryProperties =
    [
        "id",
        "departmentId",
        "parentCategoryId",
        "path",
        "depth",
        "name",
        "slug",
        "description",
        "imageAssetId",
        "bannerAssetId",
        "bannerLinkUrl",
        "ageGroupId",
        "sizeGuideId",
        "sortOrder",
        "showInMegaMenu",
        "megaMenuColumn",
        "isActive",
        "seoTitle",
        "seoDescription",
        "rowVersion",
        "productCount",
        "primaryProductCount",
        "children",
    ];

    private static readonly string[] ImpactProperties =
    [
        "categoryId",
        "name",
        "isActive",
        "productsLeavingTheStorefront",
        "descendantCategories",
        "descendantProductsLosingTheirMenuRoute",
        "examples",
    ];

    private static readonly string[] FormOptionProperties = ["ageGroups", "sizeGuides"];

    private static readonly string[] OptionProperties = ["id", "name"];

    private static readonly string[] CatalogueRights =
    [
        Permissions.Categories.View,
        Permissions.Categories.Edit,
    ];

    private ApiFactory? _factory;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_department_list_carries_exactly_the_fields_the_admin_model_declares()
    {
        var client = await SignInAsync("shape-depts");

        var (status, body) = await client.GetAsync("/api/admin/catalog/departments", Token);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(DepartmentProperties.Order(StringComparer.Ordinal), Names(body[0]));
    }

    [Fact]
    public async Task The_category_tree_carries_exactly_the_fields_the_admin_model_declares()
    {
        var client = await SignInAsync("shape-tree");
        var departmentId = await DepartmentIdAsync("kids");

        var (status, body) = await client.GetAsync(
            $"/api/admin/catalog/departments/{departmentId}/categories", Token);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(TreeProperties.Order(StringComparer.Ordinal), Names(body));

        var root = body.GetProperty("categories")[0];
        Assert.Equal(CategoryProperties.Order(StringComparer.Ordinal), Names(root));

        // And a child node is the same shape, because the screen walks the tree with one type.
        var child = root.GetProperty("children")[0];
        Assert.Equal(CategoryProperties.Order(StringComparer.Ordinal), Names(child));
    }

    [Fact]
    public async Task The_deactivation_impact_carries_exactly_the_fields_the_admin_model_declares()
    {
        var client = await SignInAsync("shape-impact");
        var departmentId = await DepartmentIdAsync("women");
        var categoryId = await CategoryIdAsync(departmentId, "skirts");

        var (status, body) = await client.GetAsync(
            $"/api/admin/catalog/categories/{categoryId}/deactivation-impact", Token);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(ImpactProperties.Order(StringComparer.Ordinal), Names(body));
    }

    /// <summary>
    /// The token has to be a string the screen can hand straight back. A byte array would serialise as
    /// base64 too, but as a JSON string of its own shape that <c>Convert.TryFromBase64String</c> on the way
    /// in would then refuse.
    /// </summary>
    [Fact]
    public async Task A_row_version_is_a_base64_string_the_screen_can_return_unchanged()
    {
        var client = await SignInAsync("shape-rv");

        var (_, body) = await client.GetAsync("/api/admin/catalog/departments", Token);
        var token = body[0].GetProperty("rowVersion");

        Assert.Equal(JsonValueKind.String, token.ValueKind);

        var bytes = Convert.FromBase64String(token.GetString()!);
        Assert.Equal(8, bytes.Length);
    }

    [Fact]
    public async Task The_category_form_options_carry_exactly_the_fields_the_admin_model_declares()
    {
        var client = await SignInAsync("shape-options");

        var (status, body) = await client.GetAsync("/api/admin/catalog/category-form-options", Token);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(FormOptionProperties.Order(StringComparer.Ordinal), Names(body));

        // Both lists are seeded, so an empty one is a defect rather than an empty database.
        var ageGroup = body.GetProperty("ageGroups").EnumerateArray().First();
        Assert.Equal(OptionProperties.Order(StringComparer.Ordinal), Names(ageGroup));
        Assert.NotEmpty(body.GetProperty("sizeGuides").EnumerateArray());
    }

    private async Task<AdminCatalogApiClient> SignInAsync(string label)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        _factory ??= new ApiFactory(sqlServer.ConnectionString);
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

    private async Task<int> CategoryIdAsync(int departmentId, string slug)
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.Categories.AsNoTracking()
            .Where(category => category.DepartmentId == departmentId && category.Slug == slug)
            .Select(category => category.Id)
            .SingleAsync(Token);
    }

    private static IEnumerable<string> Names(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal);

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
