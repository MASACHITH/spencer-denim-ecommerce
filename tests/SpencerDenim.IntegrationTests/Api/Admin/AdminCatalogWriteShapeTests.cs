using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The <b>write</b> side of the ADM-11 contract: every field the admin screens send survives the round trip.
/// <para>
/// <b>This is not the test that catches fix round 1's blocker, and an earlier version of this comment
/// claimed it was.</b> It cannot be: the blocker was a field the <i>TypeScript</i> form model had dropped,
/// and no C# test can see TypeScript. The key-set assertion below compares the request body against a list
/// written out in this same file — useful as a statement of the contract, worthless as a detector of the
/// client drifting from it. The two tests that do catch it are
/// <c>catalog-page.spec.ts</c> → "a department save keeps the hero image it cannot yet show a picker for"
/// and <c>category-tree.spec.ts</c> → "a category save keeps every field it cannot yet show a control for",
/// which assert the outgoing request body and were watched failing first.
/// </para>
/// <para>
/// What this suite is worth is the other half: it proves the <b>API</b> stores every field it is handed and
/// clears nothing it was not asked to, which is what makes "send what you loaded" a sufficient fix on the
/// client. <c>AdminCatalogPayloadShapeTests</c> does the same for the read payloads.
/// </para>
/// <para>
/// <b>The fixtures set every nullable column to something, and that is the other half of why nothing caught
/// it.</b> With the image columns null in every fixture in four suites, a dropped field round-tripped null
/// to null and every assertion still passed.
/// </para>
/// </summary>
public sealed class AdminCatalogWriteShapeTests(SqlServerDatabaseFixture sqlServer) : IAsyncDisposable
{
    /// <summary>
    /// Every field the admin screen's own write model sends for a category: <c>CategoryFormValues</c> plus
    /// the two the service adds. Asserted against the body below, so this cannot pin a contract nothing uses.
    /// </summary>
    private static readonly string[] CategoryWriteProperties =
    [
        "parentCategoryId",
        "name",
        "slug",
        "description",
        "imageAssetId",
        "bannerAssetId",
        "bannerLinkUrl",
        "ageGroupId",
        "sizeGuideId",
        "showInMegaMenu",
        "megaMenuColumn",
        "seoTitle",
        "seoDescription",
        "rowVersion",
    ];

    /// <summary>
    /// Every field the department form sends. No name and no slug: the owner made both read-only on
    /// 2026-10-02, confirming doc 11 P1 step 1.
    /// </summary>
    private static readonly string[] DepartmentWriteProperties =
    [
        "description",
        "heroImageAssetId",
        "seoTitle",
        "seoDescription",
        "rowVersion",
    ];

    private static readonly string[] CatalogueRights =
    [
        Permissions.Categories.View,
        Permissions.Categories.Create,
        Permissions.Categories.Edit,
        Permissions.Categories.Delete,
    ];

    private ApiFactory? _factory;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_category_write_round_trips_every_field_the_admin_screen_sends()
    {
        var client = await SignInAsync("write-shape");
        var women = await DepartmentIdAsync("women");
        var imageId = await ReadyImageAssetIdAsync();
        var ageGroupId = await FirstAgeGroupIdAsync();
        var sizeGuideId = await FirstSizeGuideIdAsync();

        var created = await client.PostAsync(
            "/api/admin/catalog/categories",
            new { departmentId = women, name = "W1-12 round trip", slug = "w112-round-trip" },
            Token);

        Assert.Equal(HttpStatusCode.Created, created.Status);
        var categoryId = created.Body.GetProperty("id").GetInt32();

        try
        {
            var body = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["parentCategoryId"] = null,
                ["name"] = "W1-12 round trip",
                ["slug"] = "w112-round-trip",
                ["description"] = "Soft knits.",
                ["imageAssetId"] = imageId,
                ["bannerAssetId"] = imageId,
                ["bannerLinkUrl"] = "/women/jeans",
                ["ageGroupId"] = ageGroupId,
                ["sizeGuideId"] = sizeGuideId,
                ["showInMegaMenu"] = false,
                ["megaMenuColumn"] = 3,
                ["seoTitle"] = "Round trip",
                ["seoDescription"] = "Round trip description.",
                ["rowVersion"] = await RowVersionAsync(categoryId),
            };

            Assert.Equal(
                CategoryWriteProperties.Order(StringComparer.Ordinal),
                body.Keys.Order(StringComparer.Ordinal));

            var (status, problem) = await client.PutAsync(
                $"/api/admin/catalog/categories/{categoryId}", body, Token);
            Assert.True(status == HttpStatusCode.NoContent, $"{(int)status}: {problem}");

            await using var scope = _factory!.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await dbContext.Categories.AsNoTracking()
                .SingleAsync(category => category.Id == categoryId, Token);

            Assert.Equal("Soft knits.", row.Description);
            Assert.Equal(imageId, row.ImageAssetId);
            Assert.Equal(imageId, row.BannerAssetId);
            Assert.Equal("/women/jeans", row.BannerLinkUrl);
            Assert.Equal(ageGroupId, row.AgeGroupId);
            Assert.Equal(sizeGuideId, row.SizeGuideId);
            Assert.False(row.ShowInMegaMenu);
            Assert.Equal((byte)3, row.MegaMenuColumn);
            Assert.Equal("Round trip", row.SeoTitle);
            Assert.Equal("Round trip description.", row.SeoDescription);
        }
        finally
        {
            await HardDeleteAsync(categoryId);
        }
    }

    /// <inheritdoc cref="A_category_write_round_trips_every_field_the_admin_screen_sends"/>
    [Fact]
    public async Task A_department_write_round_trips_every_field_the_admin_screen_sends()
    {
        var client = await SignInAsync("write-shape-dept");
        var departmentId = await DepartmentIdAsync("men");
        var imageId = await ReadyImageAssetIdAsync();
        var before = await DepartmentStateAsync(departmentId);

        try
        {
            var body = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["description"] = "W1-12 round trip.",
                ["heroImageAssetId"] = imageId,
                ["seoTitle"] = "Round trip",
                ["seoDescription"] = "Round trip description.",
                ["rowVersion"] = before.RowVersion,
            };

            Assert.Equal(
                DepartmentWriteProperties.Order(StringComparer.Ordinal),
                body.Keys.Order(StringComparer.Ordinal));

            var (status, problem) = await client.PutAsync(
                $"/api/admin/catalog/departments/{departmentId}", body, Token);
            Assert.True(status == HttpStatusCode.NoContent, $"{(int)status}: {problem}");

            var after = await DepartmentStateAsync(departmentId);

            Assert.Equal("W1-12 round trip.", after.Description);
            Assert.Equal(imageId, after.HeroImageAssetId);
            Assert.Equal("Round trip", after.SeoTitle);
            Assert.Equal("Round trip description.", after.SeoDescription);

            // And the two the owner made read-only are exactly as they were, through a full save.
            Assert.Equal(before.Name, after.Name);
            Assert.Equal(before.Slug, after.Slug);
        }
        finally
        {
            var current = await DepartmentStateAsync(departmentId);
            await client.PutAsync(
                $"/api/admin/catalog/departments/{departmentId}",
                new
                {
                    description = before.Description,
                    heroImageAssetId = before.HeroImageAssetId,
                    seoTitle = before.SeoTitle,
                    seoDescription = before.SeoDescription,
                    rowVersion = current.RowVersion,
                },
                Token);
        }
    }

    /// <summary>
    /// The department form has four fields and the hero image is one of them, so this is the narrowest
    /// version of the blocker: a save that carries the other three must not take the image with it.
    /// </summary>
    [Fact]
    public async Task A_department_save_that_carries_the_hero_image_keeps_it()
    {
        var client = await SignInAsync("hero-keeps");
        var departmentId = await DepartmentIdAsync("kids");
        var imageId = await ReadyImageAssetIdAsync();
        var before = await DepartmentStateAsync(departmentId);

        try
        {
            await client.PutAsync(
                $"/api/admin/catalog/departments/{departmentId}",
                new { description = before.Description, heroImageAssetId = imageId, rowVersion = before.RowVersion },
                Token);

            var withImage = await DepartmentStateAsync(departmentId);
            Assert.Equal(imageId, withImage.HeroImageAssetId);

            // A second save, as the screen makes it: the image travels with it and survives.
            var (status, _) = await client.PutAsync(
                $"/api/admin/catalog/departments/{departmentId}",
                new
                {
                    description = "Changed copy.",
                    heroImageAssetId = withImage.HeroImageAssetId,
                    rowVersion = withImage.RowVersion,
                },
                Token);

            Assert.Equal(HttpStatusCode.NoContent, status);
            Assert.Equal(imageId, (await DepartmentStateAsync(departmentId)).HeroImageAssetId);
        }
        finally
        {
            var current = await DepartmentStateAsync(departmentId);
            await client.PutAsync(
                $"/api/admin/catalog/departments/{departmentId}",
                new
                {
                    description = before.Description,
                    heroImageAssetId = before.HeroImageAssetId,
                    seoTitle = before.SeoTitle,
                    seoDescription = before.SeoDescription,
                    rowVersion = current.RowVersion,
                },
                Token);
        }
    }

    // ---- helpers --------------------------------------------------------------------------------

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

    private async Task<DepartmentState> DepartmentStateAsync(int departmentId)
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await dbContext.Departments.AsNoTracking()
            .SingleAsync(department => department.Id == departmentId, Token);

        return new DepartmentState(
            row.Name,
            row.Slug,
            row.Description,
            row.HeroImageAssetId,
            row.SeoTitle,
            row.SeoDescription,
            Convert.ToBase64String(row.RowVersion!));
    }

    /// <summary>
    /// Any asset the write path will accept: an image, <c>Ready</c> and public, which is what
    /// <c>ReadyImageAssetExistsAsync</c> asks for. The seeded demo placeholders are all three.
    /// </summary>
    private async Task<int> ReadyImageAssetIdAsync()
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.MediaAssets.AsNoTracking()
            .Where(asset =>
                asset.MediaType == MediaAssetType.Image
                && asset.Status == MediaAssetStatus.Ready
                && asset.Visibility == MediaVisibility.Public)
            .Select(asset => asset.Id)
            .OrderBy(id => id)
            .FirstAsync(Token);
    }

    private async Task<int> FirstAgeGroupIdAsync()
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.AgeGroups.AsNoTracking()
            .OrderBy(ageGroup => ageGroup.Id)
            .Select(ageGroup => ageGroup.Id)
            .FirstAsync(Token);
    }

    private async Task<int> FirstSizeGuideIdAsync()
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.SizeGuides.AsNoTracking()
            .OrderBy(sizeGuide => sizeGuide.Id)
            .Select(sizeGuide => sizeGuide.Id)
            .FirstAsync(Token);
    }

    private async Task<string> RowVersionAsync(int categoryId)
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rowVersion = await dbContext.Categories.AsNoTracking()
            .Where(category => category.Id == categoryId)
            .Select(category => category.RowVersion)
            .SingleAsync(Token);

        return Convert.ToBase64String(rowVersion!);
    }

    private async Task HardDeleteAsync(int categoryId)
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
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

    private sealed record DepartmentState(
        string Name,
        string Slug,
        string? Description,
        int? HeroImageAssetId,
        string? SeoTitle,
        string? SeoDescription,
        string RowVersion);
}
