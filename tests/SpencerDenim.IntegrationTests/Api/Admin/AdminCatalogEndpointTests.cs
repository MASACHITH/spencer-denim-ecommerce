using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The ADM-11 admin catalogue endpoints (W1-12), end to end against a real SQL Server (doc 06 §4).
/// <para>
/// Everything a test creates it removes again in a <c>finally</c>: the fixture's database is shared by the
/// whole assembly and a dozen catalogue tests assert against the seeded tree, so a stray category or a
/// category left deactivated would fail someone else's test with no clue where it came from (the assembly
/// runs its classes sequentially — see <c>AssemblyInfo.cs</c>).
/// </para>
/// </summary>
public sealed class AdminCatalogEndpointTests(SqlServerDatabaseFixture sqlServer) : IAsyncDisposable
{
    private const string DepartmentsUrl = "/api/admin/catalog/departments";
    private const string CategoriesUrl = "/api/admin/catalog/categories";

    private static readonly string[] FullCatalogueRights =
    [
        Permissions.Categories.View,
        Permissions.Categories.Create,
        Permissions.Categories.Edit,
        Permissions.Categories.Delete,
    ];

    /// <summary>The whole department list, for the reorder round-trips.</summary>
    private static readonly int[] SeededDepartmentIds = [1, 2, 3];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Hosts created by this class, kept so every one of them is disposed — including the extra
    /// <c>Development</c> host the issue #47 theory needs.
    /// </summary>
    private readonly Dictionary<string, ApiFactory> _factories = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- authentication and authorization -------------------------------------------------------

    [Fact]
    public async Task Every_route_refuses_an_unauthenticated_caller()
    {
        var factory = await HostAsync();
        using var client = AdminCatalogApiClient.Anonymous(factory);

        foreach (var (method, path) in Routes())
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
            using var response = await client.SendAsync(request, Token);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    /// <summary>
    /// A signed-in user with no catalogue rights at all. The role exists and grants nothing, so this proves
    /// the check is "holds the permission", not "is signed in".
    /// </summary>
    [Fact]
    public async Task Every_route_refuses_a_signed_in_user_without_any_catalogue_permission()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "nothing", [], Token);

        foreach (var (method, path) in Routes())
        {
            var status = method == HttpMethod.Get
                ? (await client.GetAsync(path, Token)).Status
                : (await Send(client, method, path)).Status;

            Assert.Equal(HttpStatusCode.Forbidden, status);
        }
    }

    /// <summary>
    /// <c>Categories.View</c> reads and nothing else: the grid of doc 08 §3.1 is per action, and a viewer
    /// who could reorder the mega menu would make the Create and Delete permissions decorative.
    /// </summary>
    [Fact]
    public async Task A_viewer_can_read_the_tree_and_change_nothing()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(
            factory, "viewer", [Permissions.Categories.View], Token);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(DepartmentsUrl, Token)).Status);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsync(
            $"{DepartmentsUrl}/order", new { orderedDepartmentIds = SeededDepartmentIds }, Token)).Status);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(
            CategoriesUrl, new { departmentId = 1, name = "X", slug = "x" }, Token)).Status);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.DeleteAsync($"{CategoriesUrl}/1", null, Token)).Status);
    }

    /// <summary>
    /// Creating and removing are separate permissions in doc 11 P1's own table, so a user who may add a
    /// category must not thereby be able to take one away.
    /// </summary>
    [Fact]
    public async Task Create_and_delete_are_separate_permissions()
    {
        var factory = await HostAsync();
        var creator = await AdminCatalogApiClient.SignInAsync(
            factory, "creator", [Permissions.Categories.View, Permissions.Categories.Create], Token);

        var (status, body) = await creator.PostAsync(
            CategoriesUrl,
            new { departmentId = 1, name = "W1-12 create rights", slug = "w112-create-rights" },
            Token);
        Assert.Equal(HttpStatusCode.Created, status);
        var categoryId = body.GetProperty("id").GetInt32();

        try
        {
            var rowVersion = await RowVersionAsync(factory, categoryId);

            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await creator.DeleteAsync($"{CategoriesUrl}/{categoryId}", rowVersion, Token)).Status);
        }
        finally
        {
            await HardDeleteAsync(factory, categoryId);
        }
    }

    /// <summary>
    /// All eight write routes, not one of them. The first version covered a single route and the matrix is
    /// exactly the thing that catches the route somebody adds later and forgets to guard — a check that
    /// samples one row of it is a check that will miss that.
    /// </summary>
    [Fact]
    public async Task No_write_route_accepts_a_request_without_the_antiforgery_token()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "noxsrf", FullCatalogueRights, Token);

        var checked_ = 0;

        foreach (var (method, path) in Routes().Where(route => route.Method != HttpMethod.Get))
        {
            var status = await client.SendWithoutAntiforgeryAsync(method, path, BodyFor(method), Token);

            Assert.True(
                status == HttpStatusCode.BadRequest,
                $"{method} {path} answered {(int)status} without an antiforgery token.");
            checked_++;
        }

        Assert.Equal(8, checked_);
    }

    /// <summary>
    /// The other half of the matrix: a user who may edit must not thereby be able to create or to remove.
    /// Those are separate permissions in doc 11 P1's own table, and nothing tested the pair in that
    /// direction.
    /// </summary>
    [Fact]
    public async Task An_edit_only_user_can_neither_create_nor_remove()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(
            factory, "editor", [Permissions.Categories.View, Permissions.Categories.Edit], Token);

        // The thing they may do, so the refusals below are about the permission and not about the request.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(DepartmentsUrl, Token)).Status);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsync(
                CategoriesUrl, new { departmentId = 1, name = "X", slug = "x" }, Token)).Status);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.DeleteAsync($"{CategoriesUrl}/1", "AAAAAAAAAAA=", Token)).Status);
    }

    /// <summary>
    /// And a user holding only Create may not edit, reorder, hide or remove — the same question from the
    /// other side, so neither permission can be quietly standing in for the other.
    /// </summary>
    [Fact]
    public async Task A_create_only_user_can_neither_edit_nor_reorder_nor_remove()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(
            factory, "creator-only", [Permissions.Categories.View, Permissions.Categories.Create], Token);

        foreach (var (method, path) in Routes().Where(route =>
            route.Method != HttpMethod.Get && route.Path != CategoriesUrl))
        {
            var status = method == HttpMethod.Delete
                ? (await client.DeleteAsync(path, "AAAAAAAAAAA=", Token)).Status
                : (await client.PutAsync(path, BodyFor(method)!, Token)).Status;

            Assert.True(
                status == HttpStatusCode.Forbidden,
                $"{method} {path} answered {(int)status} for a user holding only Categories.Create.");
        }
    }

    // ---- the read model -------------------------------------------------------------------------

    /// <summary>
    /// The whole reason this read model exists: the storefront's reader drops a deactivated row, so an admin
    /// screen built on it could never switch one back on.
    /// </summary>
    [Fact]
    public async Task The_admin_read_model_shows_a_deactivated_department_the_storefront_hides()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "reader", FullCatalogueRights, Token);
        using var storefront = AdminCatalogApiClient.Anonymous(factory);

        var kids = await DepartmentIdAsync(factory, "kids");
        await SetDepartmentActiveAsync(client, factory, kids, isActive: false);

        try
        {
            var (_, admin) = await client.GetAsync(DepartmentsUrl, Token);
            var row = admin.EnumerateArray().Single(department => department.GetProperty("id").GetInt32() == kids);

            Assert.False(row.GetProperty("isActive").GetBoolean());
            Assert.NotEqual(string.Empty, row.GetProperty("rowVersion").GetString());

            // A department that is off still reports what it would put in the shop, so the number reads the
            // same whichever way the switch is about to move.
            Assert.True(row.GetProperty("storefrontProductCount").GetInt32() > 0);

            using var shop = await storefront.GetAsync(
                new Uri("/api/catalog/departments", UriKind.Relative), Token);
            var shopBody = await shop.Content.ReadAsStringAsync(Token);
            Assert.DoesNotContain("\"slug\":\"kids\"", shopBody, StringComparison.Ordinal);
        }
        finally
        {
            await SetDepartmentActiveAsync(client, factory, kids, isActive: true);
        }
    }

    [Fact]
    public async Task The_category_tree_carries_the_path_the_counts_and_a_row_version()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "tree", FullCatalogueRights, Token);

        var kids = await DepartmentIdAsync(factory, "kids");
        var (status, body) = await client.GetAsync($"{DepartmentsUrl}/{kids}/categories", Token);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Kids", body.GetProperty("departmentName").GetString());

        var girls = body.GetProperty("categories")
            .EnumerateArray()
            .Single(node => node.GetProperty("slug").GetString() == "girls");

        Assert.Equal((byte)1, girls.GetProperty("depth").GetByte());
        Assert.Equal($"/{girls.GetProperty("id").GetInt32()}/", girls.GetProperty("path").GetString());
        Assert.NotEqual(string.Empty, girls.GetProperty("rowVersion").GetString());

        // Three levels deep is what CK_Categories_Depth allows and what the Kids seed uses.
        var dresses = girls.GetProperty("children")
            .EnumerateArray()
            .Single(node => node.GetProperty("slug").GetString() == "girls-dresses");

        Assert.Equal((byte)2, dresses.GetProperty("depth").GetByte());
        Assert.Equal(
            $"/{girls.GetProperty("id").GetInt32()}/{dresses.GetProperty("id").GetInt32()}/",
            dresses.GetProperty("path").GetString());
        Assert.True(dresses.GetProperty("productCount").GetInt32() > 0);
        Assert.True(dresses.GetProperty("primaryProductCount").GetInt32() > 0);
    }

    [Fact]
    public async Task The_tree_of_a_department_that_does_not_exist_is_a_404()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "missing", FullCatalogueRights, Token);

        var (status, body) = await client.GetAsync($"{DepartmentsUrl}/987654/categories", Token);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not_found", body.GetProperty("code").GetString());
    }

    // ---- malformed input (issue #47) ------------------------------------------------------------

    /// <summary>
    /// Issue <b>#47</b>: a malformed body on the login endpoints answers <b>500</b>. These routes answer a
    /// 400 problem document with a code, and the assertion is run in <c>Development</c> as well as the
    /// default test environment, because #47's own cause is that minimal APIs throw on a bad request in
    /// Development and short-circuit outside it — an environment-dependent status is what made the defect
    /// hard to see.
    /// </summary>
    [Theory]
    [InlineData("Testing")]
    [InlineData("Development")]
    public async Task A_malformed_json_body_is_a_400_rather_than_a_500_in_every_environment(string environment)
    {
        var factory = await HostAsync(environment);
        var client = await AdminCatalogApiClient.SignInAsync(factory, "malformed", FullCatalogueRights, Token);

        foreach (var payload in new[] { "{bad", "{\"name\":", "[]", "\"just a string\"" })
        {
            var (status, body) = await client.PostRawAsync(CategoriesUrl, payload, Token);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("malformed_json", body.GetProperty("code").GetString());
        }

        // A field of the wrong JSON type is the other half of #47's repro ({"email":123}).
        var (typeStatus, typeBody) = await client.PostRawAsync(
            CategoriesUrl, "{\"departmentId\":\"one\"}", Token);
        Assert.Equal(HttpStatusCode.BadRequest, typeStatus);
        Assert.Equal("malformed_json", typeBody.GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_empty_body_and_a_non_json_body_are_both_named_400s()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "nobody", FullCatalogueRights, Token);

        var (nullStatus, nullBody) = await client.PostRawAsync(CategoriesUrl, "null", Token);
        Assert.Equal(HttpStatusCode.BadRequest, nullStatus);
        Assert.Equal("empty_body", nullBody.GetProperty("code").GetString());

        var (textStatus, textBody) = await client.PostRawAsync(
            CategoriesUrl, "name=Jeans", Token, contentType: "text/plain");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, textStatus);
        Assert.Equal("not_json", textBody.GetProperty("code").GetString());
    }

    // ---- create ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_created_category_appears_in_the_tree_with_a_correct_path()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "create", FullCatalogueRights, Token);

        var women = await DepartmentIdAsync(factory, "women");
        var (status, body) = await client.PostAsync(
            CategoriesUrl,
            new
            {
                departmentId = women,
                name = "W1-12 Knitwear",
                slug = "w112-knitwear",
                description = "  Soft knits.  ",
                showInMegaMenu = true,
                megaMenuColumn = 2,
                seoTitle = "Knitwear",
            },
            Token);

        Assert.Equal(HttpStatusCode.Created, status);
        var categoryId = body.GetProperty("id").GetInt32();

        try
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var category = await dbContext.Categories.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == categoryId, Token);

            Assert.Equal($"/{categoryId}/", category.Path);
            Assert.Equal((byte)1, category.Depth);
            Assert.True(category.IsActive);
            Assert.Equal((byte)2, category.MegaMenuColumn);

            // A trimmed value, not the spaces an admin typed.
            Assert.Equal("Soft knits.", category.Description);

            // Audit columns of doc 08 §1.5 [A] are stamped with the acting user, not left NULL.
            Assert.Equal(client.UserId, category.CreatedByUserId);
        }
        finally
        {
            await HardDeleteAsync(factory, categoryId);
        }
    }

    [Fact]
    public async Task A_bad_slug_is_a_400_naming_the_field_rather_than_a_500()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "badslug", FullCatalogueRights, Token);
        var women = await DepartmentIdAsync(factory, "women");

        foreach (var slug in new[] { "Knit Wear", "knit_wear", "-knitwear", "knitwear-", "knit--wear" })
        {
            var (status, body) = await client.PostAsync(
                CategoriesUrl, new { departmentId = women, name = "Knitwear", slug }, Token);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.True(
                body.GetProperty("errors").TryGetProperty("slug", out var messages),
                $"'{slug}' was refused without naming the slug field: {body}");
            Assert.Contains("lower-case", messages[0].GetString()!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_sibling_slug_that_is_already_taken_is_a_400_and_a_cousin_s_is_not()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "dupslug", FullCatalogueRights, Token);
        var women = await DepartmentIdAsync(factory, "women");
        var men = await DepartmentIdAsync(factory, "men");

        var (status, body) = await client.PostAsync(
            CategoriesUrl, new { departmentId = women, name = "Jeans again", slug = "jeans" }, Token);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("slug", out _));

        // UX_Categories_Dept_Parent_Slug is per department and parent: "jeans" exists in both Women and Men
        // in the seeded tree, which is the proof that the index is not global.
        var (menStatus, _) = await client.PostAsync(
            CategoriesUrl, new { departmentId = men, name = "Jeans", slug = "jeans" }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, menStatus);

        var created = await client.PostAsync(
            CategoriesUrl,
            new { departmentId = women, parentCategoryId = await CategoryIdAsync(factory, women, "jeans"), name = "Jeans", slug = "jeans" },
            Token);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        await HardDeleteAsync(factory, created.Body.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task A_fourth_level_is_refused_with_the_depth_named()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "depth", FullCatalogueRights, Token);

        var women = await DepartmentIdAsync(factory, "women");

        // The seeded tree is only two category levels deep (the "Kids is three levels deep" comment in
        // CatalogReferenceData counts the department), so the third level has to be built here before the
        // fourth can be refused.
        var level1 = (await client.PostAsync(
            CategoriesUrl,
            new { departmentId = women, name = "W1-12 L1", slug = "w112-l1" },
            Token)).Body.GetProperty("id").GetInt32();

        var level2 = (await client.PostAsync(
            CategoriesUrl,
            new { departmentId = women, parentCategoryId = level1, name = "W1-12 L2", slug = "w112-l2" },
            Token)).Body.GetProperty("id").GetInt32();

        var level3 = (await client.PostAsync(
            CategoriesUrl,
            new { departmentId = women, parentCategoryId = level2, name = "W1-12 L3", slug = "w112-l3" },
            Token)).Body.GetProperty("id").GetInt32();

        try
        {
            Assert.Equal((byte)3, (await SnapshotAsync(factory, level3)).Depth);

            var (status, body) = await client.PostAsync(
                CategoriesUrl,
                new { departmentId = women, parentCategoryId = level3, name = "W1-12 L4", slug = "w112-l4" },
                Token);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.True(body.GetProperty("errors").TryGetProperty("parentCategoryId", out var messages));
            Assert.Contains("3 levels", messages[0].GetString()!, StringComparison.Ordinal);
        }
        finally
        {
            await HardDeleteAsync(factory, level3);
            await HardDeleteAsync(factory, level2);
            await HardDeleteAsync(factory, level1);
        }
    }

    [Fact]
    public async Task A_banner_link_that_is_not_a_safe_url_is_refused()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "banner", FullCatalogueRights, Token);
        var women = await DepartmentIdAsync(factory, "women");

        var (status, body) = await client.PostAsync(
            CategoriesUrl,
            new
            {
                departmentId = women,
                name = "W1-12 banner",
                slug = "w112-banner",
                bannerLinkUrl = "javascript:alert(1)",
            },
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("bannerLinkUrl", out _));
    }

    // ---- edit, move and concurrency -------------------------------------------------------------

    [Fact]
    public async Task Editing_with_a_stale_row_version_is_a_409_and_changes_nothing()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "stale", FullCatalogueRights, Token);

        var women = await DepartmentIdAsync(factory, "women");
        var skirts = await CategoryIdAsync(factory, women, "skirts");
        var original = await SnapshotAsync(factory, skirts);

        // The first edit succeeds and moves the row version.
        var first = await client.PutAsync(
            $"{CategoriesUrl}/{skirts}",
            Edit(original, name: "W1-12 Skirts"),
            Token);
        Assert.Equal(HttpStatusCode.NoContent, first.Status);

        try
        {
            // The second uses the token the first one invalidated: exactly two admins with the same page open.
            var (status, body) = await client.PutAsync(
                $"{CategoriesUrl}/{skirts}", Edit(original, name: "Someone else's name"), Token);

            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal("stale_row_version", body.GetProperty("code").GetString());

            var now = await SnapshotAsync(factory, skirts);
            Assert.Equal("W1-12 Skirts", now.Name);
        }
        finally
        {
            var current = await SnapshotAsync(factory, skirts);
            await client.PutAsync($"{CategoriesUrl}/{skirts}", Edit(current, name: original.Name), Token);
        }
    }

    [Fact]
    public async Task A_request_with_no_row_version_at_all_is_refused()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "norv", FullCatalogueRights, Token);

        var women = await DepartmentIdAsync(factory, "women");
        var skirts = await CategoryIdAsync(factory, women, "skirts");
        var original = await SnapshotAsync(factory, skirts);

        var (status, body) = await client.PutAsync(
            $"{CategoriesUrl}/{skirts}",
            new { name = "No token", slug = original.Slug, parentCategoryId = original.ParentCategoryId },
            Token);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("stale_row_version", body.GetProperty("code").GetString());
        Assert.Equal(original.Name, (await SnapshotAsync(factory, skirts)).Name);
    }

    [Fact]
    public async Task Saving_a_form_nobody_edited_writes_no_audit_row()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "nochange", FullCatalogueRights, Token);

        var women = await DepartmentIdAsync(factory, "women");
        var skirts = await CategoryIdAsync(factory, women, "skirts");
        var before = await SnapshotAsync(factory, skirts);
        var auditRowsBefore = await AuditRowCountAsync(factory, "Category", skirts);

        var (status, _) = await client.PutAsync($"{CategoriesUrl}/{skirts}", Edit(before), Token);

        Assert.Equal(HttpStatusCode.NoContent, status);

        var after = await SnapshotAsync(factory, skirts);
        Assert.Equal(before.RowVersion, after.RowVersion);
        Assert.Equal(auditRowsBefore, await AuditRowCountAsync(factory, "Category", skirts));
    }

    /// <summary>
    /// Doc 11 P1 step 4 and doc 08 §10.1: the move updates the path of the whole subtree in one transaction.
    /// The assertion reads the paths out of the database afterwards, not the response — a move that reported
    /// success and left a child pointing at the old parent is the defect this is for.
    /// </summary>
    [Fact]
    public async Task Moving_a_category_re_paths_its_whole_subtree_in_the_database()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "move", FullCatalogueRights, Token);
        var women = await DepartmentIdAsync(factory, "women");

        var parentId = (await client.PostAsync(
            CategoriesUrl,
            new { departmentId = women, name = "W1-12 Outerwear", slug = "w112-outerwear" },
            Token)).Body.GetProperty("id").GetInt32();

        var childId = (await client.PostAsync(
            CategoriesUrl,
            new { departmentId = women, parentCategoryId = parentId, name = "W1-12 Coats", slug = "w112-coats" },
            Token)).Body.GetProperty("id").GetInt32();

        var grandchildId = (await client.PostAsync(
            CategoriesUrl,
            new { departmentId = women, parentCategoryId = childId, name = "W1-12 Trench", slug = "w112-trench" },
            Token)).Body.GetProperty("id").GetInt32();

        try
        {
            Assert.Equal($"/{parentId}/{childId}/{grandchildId}/", (await SnapshotAsync(factory, grandchildId)).Path);

            // Coats (with Trench under it) moves up to the department's top level.
            var coats = await SnapshotAsync(factory, childId);
            var (status, _) = await client.PutAsync(
                $"{CategoriesUrl}/{childId}", Edit(coats) with { ParentCategoryId = null }, Token);
            Assert.Equal(HttpStatusCode.NoContent, status);

            var movedChild = await SnapshotAsync(factory, childId);
            var movedGrandchild = await SnapshotAsync(factory, grandchildId);

            Assert.Null(movedChild.ParentCategoryId);
            Assert.Equal($"/{childId}/", movedChild.Path);
            Assert.Equal((byte)1, movedChild.Depth);

            Assert.Equal($"/{childId}/{grandchildId}/", movedGrandchild.Path);
            Assert.Equal((byte)2, movedGrandchild.Depth);

            // And the move is one audit row naming the new path, not three.
            var row = await LastAuditRowAsync(factory, "Category", childId);
            Assert.Equal("Update", row.GetProperty("action").GetString());
            Assert.Contains($"\"path\":\"/{childId}/\"", row.GetProperty("afterJson").GetString()!, StringComparison.Ordinal);
        }
        finally
        {
            await HardDeleteAsync(factory, grandchildId);
            await HardDeleteAsync(factory, childId);
            await HardDeleteAsync(factory, parentId);
        }
    }

    [Fact]
    public async Task A_move_under_a_descendant_or_into_another_department_is_refused()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "badmove", FullCatalogueRights, Token);
        var women = await DepartmentIdAsync(factory, "women");
        var men = await DepartmentIdAsync(factory, "men");

        var parentId = (await client.PostAsync(
            CategoriesUrl,
            new { departmentId = women, name = "W1-12 Loop parent", slug = "w112-loop-parent" },
            Token)).Body.GetProperty("id").GetInt32();

        var childId = (await client.PostAsync(
            CategoriesUrl,
            new { departmentId = women, parentCategoryId = parentId, name = "W1-12 Loop child", slug = "w112-loop-child" },
            Token)).Body.GetProperty("id").GetInt32();

        try
        {
            var parent = await SnapshotAsync(factory, parentId);

            var loop = await client.PutAsync(
                $"{CategoriesUrl}/{parentId}", Edit(parent) with { ParentCategoryId = childId }, Token);
            Assert.Equal(HttpStatusCode.BadRequest, loop.Status);
            Assert.True(loop.Body.GetProperty("errors").TryGetProperty("parentCategoryId", out _));

            var crossDepartment = await client.PutAsync(
                $"{CategoriesUrl}/{parentId}",
                Edit(parent) with { ParentCategoryId = await CategoryIdAsync(factory, men, "shirts") },
                Token);
            Assert.Equal(HttpStatusCode.BadRequest, crossDepartment.Status);

            // Nothing moved.
            Assert.Equal($"/{parentId}/", (await SnapshotAsync(factory, parentId)).Path);
            Assert.Equal($"/{parentId}/{childId}/", (await SnapshotAsync(factory, childId)).Path);
        }
        finally
        {
            await HardDeleteAsync(factory, childId);
            await HardDeleteAsync(factory, parentId);
        }
    }

    // ---- reorder --------------------------------------------------------------------------------

    [Fact]
    public async Task A_reorder_applies_the_whole_list_and_a_stale_one_is_refused()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "reorder", FullCatalogueRights, Token);
        var women = await DepartmentIdAsync(factory, "women");

        var topLevel = await TopLevelIdsAsync(factory, women);
        var original = topLevel.ToArray();

        try
        {
            var reversed = original.Reverse().ToArray();

            var (status, _) = await client.PutAsync(
                $"{CategoriesUrl}/order",
                new { departmentId = women, parentCategoryId = (int?)null, orderedCategoryIds = reversed },
                Token);
            Assert.Equal(HttpStatusCode.NoContent, status);
            Assert.Equal(reversed, await TopLevelIdsAsync(factory, women));

            // A list made before someone else's change: it leaves one out, so it is refused outright rather
            // than applied to the part of the list it does name.
            var (staleStatus, staleBody) = await client.PutAsync(
                $"{CategoriesUrl}/order",
                new { departmentId = women, parentCategoryId = (int?)null, orderedCategoryIds = reversed.Take(reversed.Length - 1).ToArray() },
                Token);

            Assert.Equal(HttpStatusCode.Conflict, staleStatus);
            Assert.Equal("stale_order", staleBody.GetProperty("code").GetString());
            Assert.Single(staleBody.GetProperty("missingIds").EnumerateArray());

            // And the order is still the one the successful call left behind.
            Assert.Equal(reversed, await TopLevelIdsAsync(factory, women));
        }
        finally
        {
            await client.PutAsync(
                $"{CategoriesUrl}/order",
                new { departmentId = women, parentCategoryId = (int?)null, orderedCategoryIds = original },
                Token);
        }
    }

    /// <summary>
    /// Two admins reordering the same list at once. The second request carries a list built before the first
    /// landed; because a reorder is the whole list and every row carries a <c>RowVersion</c>, the result is
    /// one admin's order rather than an interleaving of both.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_reorders_cannot_interleave_into_a_broken_order()
    {
        var factory = await HostAsync();
        var first = await AdminCatalogApiClient.SignInAsync(factory, "race-a", FullCatalogueRights, Token);
        var second = await AdminCatalogApiClient.SignInAsync(factory, "race-b", FullCatalogueRights, Token);
        var women = await DepartmentIdAsync(factory, "women");

        var original = (await TopLevelIdsAsync(factory, women)).ToArray();

        try
        {
            var byFirst = original.Reverse().ToArray();
            var bySecond = original.Skip(1).Append(original[0]).ToArray();

            var a = first.PutAsync(
                $"{CategoriesUrl}/order",
                new { departmentId = women, parentCategoryId = (int?)null, orderedCategoryIds = byFirst },
                Token);
            var b = second.PutAsync(
                $"{CategoriesUrl}/order",
                new { departmentId = women, parentCategoryId = (int?)null, orderedCategoryIds = bySecond },
                Token);

            var results = await Task.WhenAll(a, b);

            // Whatever the interleaving, the list is exactly one of the two requested orders — never a mix.
            var landed = await TopLevelIdsAsync(factory, women);
            Assert.True(
                landed.SequenceEqual(byFirst) || landed.SequenceEqual(bySecond),
                $"the order ended up as [{string.Join(',', landed)}], which is neither " +
                $"[{string.Join(',', byFirst)}] nor [{string.Join(',', bySecond)}]");

            // Both may succeed if they happen to serialise; a loser is told to reload rather than ignored.
            foreach (var (status, body) in results.Where(result => result.Status != HttpStatusCode.NoContent))
            {
                Assert.Equal(HttpStatusCode.Conflict, status);
                Assert.Equal("stale_row_version", body.GetProperty("code").GetString());
            }
        }
        finally
        {
            await first.PutAsync(
                $"{CategoriesUrl}/order",
                new { departmentId = women, parentCategoryId = (int?)null, orderedCategoryIds = original },
                Token);
        }
    }

    [Fact]
    public async Task Reordering_the_departments_applies_and_restores()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "deptorder", FullCatalogueRights, Token);

        var original = (await DepartmentIdsInOrderAsync(factory)).ToArray();

        try
        {
            var reversed = original.Reverse().ToArray();
            var (status, _) = await client.PutAsync(
                $"{DepartmentsUrl}/order", new { orderedDepartmentIds = reversed }, Token);

            Assert.Equal(HttpStatusCode.NoContent, status);
            Assert.Equal(reversed, await DepartmentIdsInOrderAsync(factory));

            var (badStatus, badBody) = await client.PutAsync(
                $"{DepartmentsUrl}/order", new { orderedDepartmentIds = new[] { reversed[0] } }, Token);
            Assert.Equal(HttpStatusCode.Conflict, badStatus);
            Assert.Equal("stale_order", badBody.GetProperty("code").GetString());
        }
        finally
        {
            await client.PutAsync(
                $"{DepartmentsUrl}/order", new { orderedDepartmentIds = original }, Token);
        }
    }

    // ---- remove ---------------------------------------------------------------------------------

    [Fact]
    public async Task Removing_a_category_with_products_or_children_is_refused_with_the_counts()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "remove", FullCatalogueRights, Token);

        var women = await DepartmentIdAsync(factory, "women");
        var skirts = await CategoryIdAsync(factory, women, "skirts");

        var (status, body) = await client.DeleteAsync(
            $"{CategoriesUrl}/{skirts}", await RowVersionAsync(factory, skirts), Token);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("category_in_use", body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("linkedProducts").GetInt32() > 0);
        Assert.Equal(0, body.GetProperty("childCategories").GetInt32());

        // Nothing was deleted.
        Assert.False((await SnapshotAsync(factory, skirts)).IsDeleted);

        var kids = await DepartmentIdAsync(factory, "kids");
        var girls = await CategoryIdAsync(factory, kids, "girls");
        var (childStatus, childBody) = await client.DeleteAsync(
            $"{CategoriesUrl}/{girls}", await RowVersionAsync(factory, girls), Token);

        Assert.Equal(HttpStatusCode.Conflict, childStatus);
        Assert.True(childBody.GetProperty("childCategories").GetInt32() > 0);
    }

    [Fact]
    public async Task Removing_an_empty_category_soft_deletes_it_and_it_leaves_the_tree()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "removeok", FullCatalogueRights, Token);
        var women = await DepartmentIdAsync(factory, "women");

        var categoryId = (await client.PostAsync(
            CategoriesUrl,
            new { departmentId = women, name = "W1-12 Scrap", slug = "w112-scrap" },
            Token)).Body.GetProperty("id").GetInt32();

        var (status, _) = await client.DeleteAsync(
            $"{CategoriesUrl}/{categoryId}", await RowVersionAsync(factory, categoryId), Token);
        Assert.Equal(HttpStatusCode.NoContent, status);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The row is still there, soft-deleted, with who and when (doc 08 §1.5 [SD]).
        var row = await dbContext.Categories
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == categoryId, Token);

        Assert.True(row.IsDeleted);
        Assert.Equal(client.UserId, row.DeletedByUserId);
        Assert.NotNull(row.DeletedAtUtc);

        // And it is gone from the admin tree, which has no restore: removed is not hidden.
        var (_, tree) = await client.GetAsync($"{DepartmentsUrl}/{women}/categories", Token);
        Assert.DoesNotContain(
            tree.GetProperty("categories").EnumerateArray(),
            node => node.GetProperty("id").GetInt32() == categoryId);

        // The slug is free again, because UX_Categories_Dept_Parent_Slug is filtered on IsDeleted = 0.
        var again = await client.PostAsync(
            CategoriesUrl, new { departmentId = women, name = "W1-12 Scrap", slug = "w112-scrap" }, Token);
        Assert.Equal(HttpStatusCode.Created, again.Status);

        await HardDeleteAsync(factory, again.Body.GetProperty("id").GetInt32());
        await HardDeleteAsync(factory, categoryId);
    }

    [Fact]
    public async Task Removing_a_category_that_is_already_gone_is_a_404()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "gone", FullCatalogueRights, Token);

        var (status, body) = await client.DeleteAsync($"{CategoriesUrl}/987654", "AAAAAAAAAAA=", Token);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not_found", body.GetProperty("code").GetString());
    }

    /// <summary>
    /// m13: a category whose parent has been soft-deleted under it. The remove guard makes that unreachable
    /// through the API — it refuses while any child exists — but a support script or a restore could do it,
    /// and the old behaviour was the worst available: the row existed, held its slug against a new one, and
    /// appeared on no screen at all.
    /// </summary>
    [Fact]
    public async Task A_category_whose_parent_was_deleted_under_it_is_still_shown_rather_than_vanishing()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "orphan", FullCatalogueRights, Token);
        var women = await DepartmentIdAsync(factory, "women");

        var parentId = (await client.PostAsync(
            CategoriesUrl,
            new { departmentId = women, name = "W1-12 Orphan parent", slug = "w112-orphan-parent" },
            Token)).Body.GetProperty("id").GetInt32();

        var childId = (await client.PostAsync(
            CategoriesUrl,
            new { departmentId = women, parentCategoryId = parentId, name = "W1-12 Orphan child", slug = "w112-orphan-child" },
            Token)).Body.GetProperty("id").GetInt32();

        try
        {
            // Straight to the database, because the API deliberately refuses this.
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await dbContext.Categories
                    .Where(category => category.Id == parentId)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(category => category.IsDeleted, true),
                        Token);
            }

            var (_, tree) = await client.GetAsync($"{DepartmentsUrl}/{women}/categories", Token);

            var ids = tree.GetProperty("categories")
                .EnumerateArray()
                .Select(node => node.GetProperty("id").GetInt32())
                .ToList();

            Assert.DoesNotContain(parentId, ids);
            Assert.Contains(childId, ids);
        }
        finally
        {
            await HardDeleteAsync(factory, childId);
            await HardDeleteAsync(factory, parentId);
        }
    }

    // ---- the audit trail (issue #59) ------------------------------------------------------------

    /// <summary>
    /// Issue <b>#59</b>: before this task the whole table held sign-ins and nothing else, so the viewer
    /// W1-09 shipped showed a client nothing. A catalogue edit by a named staff member now leaves a row.
    /// </summary>
    [Fact]
    public async Task Create_edit_status_change_and_remove_each_leave_an_audit_row()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "audit", FullCatalogueRights, Token);
        var women = await DepartmentIdAsync(factory, "women");

        var categoryId = (await client.PostAsync(
            CategoriesUrl,
            new { departmentId = women, name = "W1-12 Audited", slug = "w112-audited" },
            Token)).Body.GetProperty("id").GetInt32();

        try
        {
            var snapshot = await SnapshotAsync(factory, categoryId);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync(
                $"{CategoriesUrl}/{categoryId}", Edit(snapshot, name: "W1-12 Audited twice"), Token)).Status);

            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync(
                $"{CategoriesUrl}/{categoryId}/active",
                new { isActive = false, rowVersion = await RowVersionAsync(factory, categoryId) },
                Token)).Status);

            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(
                $"{CategoriesUrl}/{categoryId}", await RowVersionAsync(factory, categoryId), Token)).Status);

            await using var scope = factory.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var entityId = categoryId.ToString(CultureInfo.InvariantCulture);

            var rows = await dbContext.AuditLogs
                .AsNoTracking()
                .Where(log => log.EntityType == "Category" && log.EntityId == entityId)
                .OrderBy(log => log.Id)
                .ToListAsync(Token);

            Assert.Equal(["Create", "Update", "StatusChange", "SoftDelete"], rows.Select(row => row.Action));

            foreach (var row in rows)
            {
                Assert.Equal(client.UserId, row.UserId);
                Assert.Equal(client.Email, row.UserEmail);
                Assert.False(string.IsNullOrWhiteSpace(row.CorrelationId));

                // IpAddress is deliberately not asserted: WebApplicationFactory's in-memory server has no
                // remote endpoint, so the column is NULL here however the endpoint reads it. The endpoint
                // takes it from Connection.RemoteIpAddress, which doc 08 §3.8 allows to be NULL, and it is
                // checked against a real socket in the browser pass rather than claimed by a test that
                // cannot see it.
            }

            // The diff is the one field that moved, not the whole row (doc 08 §3.8).
            var update = rows.Single(row => row.Action == "Update");
            using var after = JsonDocument.Parse(update.AfterJson!);
            Assert.Equal(["name"], after.RootElement.EnumerateObject().Select(property => property.Name));
        }
        finally
        {
            await HardDeleteAsync(factory, categoryId);
        }
    }

    /// <summary>
    /// The audit row and the change commit together (<c>IAuditLogWriter.Enlist</c>). A refused write leaves
    /// no trail of a change that never happened.
    /// </summary>
    [Fact]
    public async Task A_refused_write_leaves_no_audit_row_behind()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "norow", FullCatalogueRights, Token);

        var women = await DepartmentIdAsync(factory, "women");
        var skirts = await CategoryIdAsync(factory, women, "skirts");
        var before = await AuditRowCountAsync(factory, "Category", skirts);
        var snapshot = await SnapshotAsync(factory, skirts);

        // A bad slug: refused at validation.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync(
            $"{CategoriesUrl}/{skirts}", Edit(snapshot) with { Slug = "Not A Slug" }, Token)).Status);

        // A stale token: refused at the concurrency check.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsync(
            $"{CategoriesUrl}/{skirts}",
            Edit(snapshot, name: "x") with { RowVersion = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }) },
            Token)).Status);

        Assert.Equal(before, await AuditRowCountAsync(factory, "Category", skirts));
    }

    [Fact]
    public async Task A_reorder_is_one_audit_row_naming_the_list_rather_than_one_per_row()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "orderaudit", FullCatalogueRights, Token);
        var women = await DepartmentIdAsync(factory, "women");

        var original = (await TopLevelIdsAsync(factory, women)).ToArray();
        var scope = $"{women}/root";
        var before = await AuditRowCountAsync(factory, "CategoryOrder", scope);

        try
        {
            await client.PutAsync(
                $"{CategoriesUrl}/order",
                new { departmentId = women, parentCategoryId = (int?)null, orderedCategoryIds = original.Reverse().ToArray() },
                Token);

            Assert.Equal(before + 1, await AuditRowCountAsync(factory, "CategoryOrder", scope));

            var row = await LastAuditRowAsync(factory, "CategoryOrder", scope);
            Assert.Contains(
                string.Join(',', original),
                row.GetProperty("beforeJson").GetString()!,
                StringComparison.Ordinal);
        }
        finally
        {
            await client.PutAsync(
                $"{CategoriesUrl}/order",
                new { departmentId = women, parentCategoryId = (int?)null, orderedCategoryIds = original },
                Token);
        }
    }

    // ---- the database constraints ---------------------------------------------------------------

    /// <summary>
    /// The migration's half of the W1-02 follow-up. The domain guard only covers writes that go through the
    /// application; a support script or a data fix does not, and a slug that is not a slug is a row the
    /// listing links to and whose own URL answers 400.
    /// </summary>
    [Theory]
    [InlineData("Not-A-Slug")]
    [InlineData("not a slug")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--hyphen")]
    [InlineData("")]
    public async Task A_direct_sql_write_cannot_put_a_bad_slug_in_a_category(string slug)
    {
        sqlServer.SkipIfUnavailable();
        var factory = await HostAsync();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var women = await dbContext.Departments.AsNoTracking()
            .Where(department => department.Slug == "women")
            .Select(department => department.Id)
            .SingleAsync(Token);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO catalog.Categories (DepartmentId, ParentCategoryId, Path, Depth, Name, Slug, SortOrder, ShowInMegaMenu, IsActive, IsDeleted, CreatedAtUtc)
            VALUES ({0}, NULL, '/0/', 1, N'Direct write', {1}, 99, 0, 1, 0, SYSUTCDATETIME());
            """,
            [women, slug],
            Token));

        Assert.Contains("CK_Categories_Slug", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_direct_sql_write_cannot_put_a_bad_slug_in_a_department()
    {
        sqlServer.SkipIfUnavailable();
        var factory = await HostAsync();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE catalog.Departments SET Slug = 'Women Wear' WHERE Slug = 'women';",
            Token));

        Assert.Contains("CK_Departments_Slug", exception.ToString(), StringComparison.Ordinal);

        // And the row is untouched, because the statement never applied.
        Assert.True(await dbContext.Departments.AsNoTracking().AnyAsync(d => d.Slug == "women", Token));
    }

    /// <summary>
    /// A good slug has to still go in, or the constraint would be proving nothing except that inserts fail.
    /// </summary>
    [Fact]
    public async Task A_good_slug_still_goes_in_through_direct_sql()
    {
        sqlServer.SkipIfUnavailable();
        var factory = await HostAsync();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var women = await dbContext.Departments.AsNoTracking()
            .Where(department => department.Slug == "women")
            .Select(department => department.Id)
            .SingleAsync(Token);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO catalog.Categories (DepartmentId, ParentCategoryId, Path, Depth, Name, Slug, SortOrder, ShowInMegaMenu, IsActive, IsDeleted, CreatedAtUtc)
            VALUES ({0}, NULL, '/0/', 1, N'Direct write', 'w112-direct-write', 99, 0, 1, 0, SYSUTCDATETIME());
            """,
            [women],
            Token);

        var inserted = await dbContext.Categories.AsNoTracking()
            .SingleAsync(category => category.Slug == "w112-direct-write", Token);

        await HardDeleteAsync(factory, inserted.Id);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static IEnumerable<(HttpMethod Method, string Path)> Routes()
    {
        yield return (HttpMethod.Get, DepartmentsUrl);
        yield return (HttpMethod.Get, $"{DepartmentsUrl}/1/categories");
        yield return (HttpMethod.Get, $"{CategoriesUrl}/1/deactivation-impact");
        yield return (HttpMethod.Get, "/api/admin/catalog/category-form-options");
        yield return (HttpMethod.Put, $"{DepartmentsUrl}/order");
        yield return (HttpMethod.Put, $"{DepartmentsUrl}/1");
        yield return (HttpMethod.Put, $"{DepartmentsUrl}/1/active");
        yield return (HttpMethod.Post, CategoriesUrl);
        yield return (HttpMethod.Put, $"{CategoriesUrl}/order");
        yield return (HttpMethod.Put, $"{CategoriesUrl}/1");
        yield return (HttpMethod.Put, $"{CategoriesUrl}/1/active");
        yield return (HttpMethod.Delete, $"{CategoriesUrl}/1");
    }

    /// <summary>A body shaped for the route, so a refusal is about the guard rather than about the JSON.</summary>
    private static object? BodyFor(HttpMethod method) => method == HttpMethod.Delete
        ? null
        : new
        {
            departmentId = 1,
            name = "X",
            slug = "x",
            isActive = true,
            orderedDepartmentIds = SeededDepartmentIds,
            orderedCategoryIds = SeededDepartmentIds,
        };

    private static Task<(HttpStatusCode Status, JsonElement Body)> Send(
        AdminCatalogApiClient client,
        HttpMethod method,
        string path)
    {
        if (method == HttpMethod.Post)
        {
            return client.PostAsync(path, new { departmentId = 1, name = "X", slug = "x" }, Token);
        }

        if (method == HttpMethod.Delete)
        {
            return client.DeleteAsync(path, "AAAAAAAAAAA=", Token);
        }

        return client.PutAsync(path, new { name = "X", slug = "x", isActive = true }, Token);
    }

    private async Task<ApiFactory> HostAsync(string environment = "Testing")
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        if (!_factories.TryGetValue(environment, out var factory))
        {
            factory = new ApiFactory(sqlServer.ConnectionString, environment);
            _factories[environment] = factory;
        }

        return factory;
    }

    private static async Task SetDepartmentActiveAsync(
        AdminCatalogApiClient client,
        ApiFactory factory,
        int departmentId,
        bool isActive)
    {
        var rowVersion = await DepartmentRowVersionAsync(factory, departmentId);
        var (status, body) = await client.PutAsync(
            $"{DepartmentsUrl}/{departmentId}/active", new { isActive, rowVersion }, Token);

        Assert.True(
            status == HttpStatusCode.NoContent,
            $"switching department {departmentId} to isActive={isActive} returned {(int)status}: {body}");
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

    private static async Task<int[]> DepartmentIdsInOrderAsync(ApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.Departments.AsNoTracking()
            .OrderBy(department => department.SortOrder)
            .ThenBy(department => department.Id)
            .Select(department => department.Id)
            .ToArrayAsync(Token);
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

    private static async Task<int[]> TopLevelIdsAsync(ApiFactory factory, int departmentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.Categories.AsNoTracking()
            .Where(category => category.DepartmentId == departmentId && category.ParentCategoryId == null)
            .OrderBy(category => category.SortOrder)
            .ThenBy(category => category.Id)
            .Select(category => category.Id)
            .ToArrayAsync(Token);
    }

    private static async Task<CategorySnapshot> SnapshotAsync(ApiFactory factory, int categoryId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var category = await dbContext.Categories
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == categoryId, Token);

        return new CategorySnapshot(
            category.Id,
            category.ParentCategoryId,
            category.Name,
            category.Slug,
            category.Description,
            category.ImageAssetId,
            category.BannerAssetId,
            category.BannerLinkUrl,
            category.AgeGroupId,
            category.SizeGuideId,
            category.ShowInMegaMenu,
            category.MegaMenuColumn,
            category.SeoTitle,
            category.SeoDescription,
            category.Path,
            category.Depth,
            category.IsActive,
            category.IsDeleted,
            Convert.ToBase64String(category.RowVersion!));
    }

    private static async Task<string> RowVersionAsync(ApiFactory factory, int categoryId) =>
        (await SnapshotAsync(factory, categoryId)).RowVersion;

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

    private static async Task<int> AuditRowCountAsync(ApiFactory factory, string entityType, object entityId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Convert.ToString(entityId, CultureInfo.InvariantCulture);

        return await dbContext.AuditLogs.AsNoTracking()
            .CountAsync(log => log.EntityType == entityType && log.EntityId == id, Token);
    }

    private static async Task<JsonElement> LastAuditRowAsync(ApiFactory factory, string entityType, object entityId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Convert.ToString(entityId, CultureInfo.InvariantCulture);

        var row = await dbContext.AuditLogs.AsNoTracking()
            .Where(log => log.EntityType == entityType && log.EntityId == id)
            .OrderByDescending(log => log.Id)
            .Select(log => new { log.Action, log.BeforeJson, log.AfterJson })
            .FirstAsync(Token);

        return JsonSerializer.SerializeToElement(row, JsonOptions);
    }

    /// <summary>
    /// Takes a test's own row out of the shared database for good — including a soft-deleted one, which the
    /// admin API deliberately cannot restore or purge. <c>ExecuteDeleteAsync</c> is the documented hard-delete
    /// path (doc 08 N12) and never reaches the interceptor that refuses <c>Remove</c>.
    /// </summary>
    private static async Task HardDeleteAsync(ApiFactory factory, int categoryId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.Categories
            .IgnoreQueryFilters()
            .Where(category => category.Id == categoryId)
            .ExecuteDeleteAsync(CancellationToken.None);
    }

    /// <summary>
    /// An edit body built from the row's own values, so a test states only the field it is about. Writing
    /// fifteen literals per test is how a "nothing else changed" assertion quietly stops being true.
    /// </summary>
    private static CategoryEditBody Edit(CategorySnapshot snapshot, string? name = null) => new(
        snapshot.ParentCategoryId,
        name ?? snapshot.Name,
        snapshot.Slug,
        snapshot.Description,
        snapshot.ImageAssetId,
        snapshot.BannerAssetId,
        snapshot.BannerLinkUrl,
        snapshot.AgeGroupId,
        snapshot.SizeGuideId,
        snapshot.ShowInMegaMenu,
        snapshot.MegaMenuColumn,
        snapshot.SeoTitle,
        snapshot.SeoDescription,
        snapshot.RowVersion);

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in _factories.Values)
        {
            await factory.DisposeAsync();
        }

        _factories.Clear();
        GC.SuppressFinalize(this);
    }

    private sealed record CategorySnapshot(
        int Id,
        int? ParentCategoryId,
        string Name,
        string Slug,
        string? Description,
        int? ImageAssetId,
        int? BannerAssetId,
        string? BannerLinkUrl,
        int? AgeGroupId,
        int? SizeGuideId,
        bool ShowInMegaMenu,
        byte? MegaMenuColumn,
        string? SeoTitle,
        string? SeoDescription,
        string Path,
        byte Depth,
        bool IsActive,
        bool IsDeleted,
        string RowVersion);

    private sealed record CategoryEditBody(
        int? ParentCategoryId,
        string Name,
        string Slug,
        string? Description,
        int? ImageAssetId,
        int? BannerAssetId,
        string? BannerLinkUrl,
        int? AgeGroupId,
        int? SizeGuideId,
        bool ShowInMegaMenu,
        byte? MegaMenuColumn,
        string? SeoTitle,
        string? SeoDescription,
        string? RowVersion);
}
