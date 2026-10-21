using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Media.Abstractions;
using SpencerDenim.Application.Media.Contracts;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Media;

/// <summary>
/// #223 tester pass: the cases the developer's tests did not reach.
/// <list type="bullet">
/// <item>A product save with many pictures, given in <i>descending</i> id order and with one asset in several
/// slots (gallery, card, two colourways' swatch), takes exactly one lock per distinct asset, in ascending id
/// order. The developer's tests only ever save one picture per owner, so nothing checked the order.</item>
/// <item>Many products and categories saving the same set of pictures in different orders, at once, with
/// library deletes of those pictures: no deadlock victim (1205), no 5xx. Without the ascending order this
/// deadlocks; the workload is what proves the order matters in practice.</item>
/// <item>The delete's own-stamp recognition is not fooled by somebody else's delete landing between a failed
/// commit attempt and its retry: that answers 404, not 204.</item>
/// </list>
/// </summary>
public sealed class MediaLockTesterTests(SqlServerDatabaseFixture sqlServer)
{
    private const string ProductsUrl = "/api/admin/products";
    private const string CategoriesUrl = "/api/admin/catalog/categories";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] Everything =
    [
        Permissions.Products.View,
        Permissions.Products.Create,
        Permissions.Products.Edit,
        Permissions.Products.Delete,
        Permissions.Categories.View,
        Permissions.Categories.Create,
        Permissions.Categories.Edit,
        Permissions.Categories.Delete,
        Permissions.Media.View,
        Permissions.Media.Delete,
    ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ================================================================== lock order on a big save

    [Fact]
    public async Task A_product_with_twenty_pictures_locks_each_distinct_asset_once_in_ascending_id_order()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        var recorder = new AssetLockRecorder();
        await using var host = MediaTestHost.Create(
            sqlServer.ConnectionString,
            configureServices: services => services.AddSingleton<IInterceptor>(recorder));
        using var client = await host.SignInAsync(Everything);

        var assets = new List<int>();
        var productId = 0;

        try
        {
            var pictures = new List<Picture>();
            for (var i = 0; i < 20; i++)
            {
                pictures.Add(await AddImageAsync(host, assets));
            }

            var swatch = pictures[5];
            var descending = pictures.OrderByDescending(picture => picture.Id).ToList();

            // Created with no pictures, so the edit below is the save being measured.
            productId = await CreatedIdAsync(await SendAsync(client, HttpMethod.Post, ProductsUrl, await ProductBodyAsync(host, "T223T-TWENTY", [], null, null)));

            var body = await ProductBodyAsync(
                host,
                "T223T-TWENTY",
                descending,
                swatch,
                await VersionAsync(host, $"SELECT RowVersion FROM catalog.Products WHERE Id = {productId}"));

            recorder.Arm();
            using var saved = await SendAsync(client, HttpMethod.Put, $"{ProductsUrl}/{productId}", body);
            recorder.Disarm();

            Assert.True(saved.StatusCode == HttpStatusCode.NoContent, $"{(int)saved.StatusCode}: {await saved.Content.ReadAsStringAsync(Token)}");

            var locked = recorder.Locked;
            var expected = pictures.Select(picture => picture.Id).OrderBy(id => id).ToList();

            // One lock per distinct asset (the swatch shared by two colours and also a gallery picture, and the
            // card, are the same asset: one lock), in ascending id order although the request listed them
            // descending.
            Assert.Equal(expected, locked);

            Assert.Equal(20, await ScalarAsync(host, $"SELECT COUNT(*) FROM catalog.ProductMedia WHERE ProductId = {productId}"));
            Assert.Equal(2, await ScalarAsync(host, $"SELECT COUNT(*) FROM catalog.ProductColours WHERE ProductId = {productId} AND SwatchAssetId = {swatch.Id}"));
            Assert.Equal(1, await ScalarAsync(host, $"SELECT COUNT(*) FROM media.MediaUsages WHERE OwnerType = 'Product' AND OwnerId = CAST({productId} AS varchar(100)) AND MediaAssetId = {swatch.Id} AND UsageRole = 'Swatch'"));

            // Removing every picture again: no asset to lock, the save still goes through, and every usage row goes.
            body = await ProductBodyAsync(host, "T223T-TWENTY", [], null, await VersionAsync(host, $"SELECT RowVersion FROM catalog.Products WHERE Id = {productId}"));
            recorder.Arm();
            using var cleared = await SendAsync(client, HttpMethod.Put, $"{ProductsUrl}/{productId}", body);
            recorder.Disarm();

            Assert.True(cleared.StatusCode == HttpStatusCode.NoContent, $"{(int)cleared.StatusCode}: {await cleared.Content.ReadAsStringAsync(Token)}");
            Assert.Empty(recorder.Locked);
            Assert.Equal(0, await ScalarAsync(host, $"SELECT COUNT(*) FROM media.MediaUsages WHERE OwnerType = 'Product' AND OwnerId = CAST({productId} AS varchar(100))"));
        }
        finally
        {
            await CleanUpAsync(host, [productId], [], assets);
        }
    }

    // ================================================================== many savers, shared pictures

    [Fact]
    public async Task Many_products_and_categories_saving_the_same_pictures_in_different_orders_never_deadlock()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        var deadlocks = new DeadlockCounter();
        await using var host = MediaTestHost.Create(
            sqlServer.ConnectionString,
            configureServices: services => services.AddSingleton<IInterceptor>(deadlocks));
        using var client = await host.SignInAsync(Everything);

        var rounds = int.TryParse(Environment.GetEnvironmentVariable("T223T_MULTI_ROUNDS"), CultureInfo.InvariantCulture, out var asked)
            ? asked
            : 12;
        var productCount = int.TryParse(Environment.GetEnvironmentVariable("T223T_MULTI_PRODUCTS"), CultureInfo.InvariantCulture, out var askedProducts)
            ? askedProducts
            : 6;

        var assets = new List<int>();
        var products = new List<int>();
        var categories = new List<int>();
        var failures = new ConcurrentBag<string>();
        var orphaned = new List<string>();
        var statuses = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        try
        {
            var shared = new List<Picture>();
            for (var i = 0; i < 4; i++)
            {
                shared.Add(await AddImageAsync(host, assets));
            }

            for (var i = 0; i < productCount; i++)
            {
                var code = string.Create(CultureInfo.InvariantCulture, $"T223T-MULTI-{i}");
                products.Add(await CreatedIdAsync(await SendAsync(client, HttpMethod.Post, ProductsUrl, await ProductBodyAsync(host, code, shared, null, null))));
            }

            var women = await ScalarAsync(host, $"SELECT Id FROM catalog.Departments WHERE Slug = 'women'");
            for (var i = 0; i < 2; i++)
            {
                categories.Add(await CreatedIdAsync(await SendAsync(client, HttpMethod.Post, CategoriesUrl, new Dictionary<string, object?>
                {
                    ["departmentId"] = women,
                    ["parentCategoryId"] = null,
                    ["name"] = string.Create(CultureInfo.InvariantCulture, $"T223T multi {i}"),
                    ["slug"] = string.Create(CultureInfo.InvariantCulture, $"t223t-multi-{i}"),
                    ["imageAssetId"] = shared[0].Id,
                    ["bannerAssetId"] = shared[3].Id,
                    ["showInMegaMenu"] = false,
                })));
            }

            void Note(string actor, HttpResponseMessage answer, string body)
            {
                var code = (int)answer.StatusCode;
                statuses.AddOrUpdate(actor + " " + code.ToString(CultureInfo.InvariantCulture), 1, (_, n) => n + 1);
                if (code >= 500)
                {
                    failures.Add($"{actor} {code}: {body[..Math.Min(body.Length, 300)]}");
                }
            }

            async Task Run(string actor, Func<Task<HttpResponseMessage>> send)
            {
                using var answer = await send();
                Note(actor, answer, await answer.Content.ReadAsStringAsync(Token));
            }

            for (var round = 0; round < rounds; round++)
            {
                var contested = await AddImageAsync(host, assets);
                var work = new List<Func<Task>>();

                for (var i = 0; i < products.Count; i++)
                {
                    // Every product lists the shared pictures in a different rotation (and half of them reversed),
                    // so without the ascending lock order two saves would take the same rows in opposite orders.
                    var rotation = shared.Skip((i + round) % shared.Count).Concat(shared.Take((i + round) % shared.Count)).ToList();
                    if (i % 2 == 1)
                    {
                        rotation.Reverse();
                    }

                    if (i % 2 == 0)
                    {
                        rotation.Insert(i % rotation.Count, contested);
                    }

                    var productId = products[i];
                    var code = string.Create(CultureInfo.InvariantCulture, $"T223T-MULTI-{i}");
                    var body = await ProductBodyAsync(host, code, rotation, null, await VersionAsync(host, $"SELECT RowVersion FROM catalog.Products WHERE Id = {productId}"));
                    work.Add(() => Run("product", () => SendAsync(client, HttpMethod.Put, $"{ProductsUrl}/{productId}", body)));
                }

                for (var i = 0; i < categories.Count; i++)
                {
                    var categoryId = categories[i];
                    var (image, banner) = (i + round) % 2 == 0 ? (shared[0], shared[3]) : (shared[3], shared[0]);
                    var body = await CategoryBodyAsync(host, categoryId, image.Id, banner.Id);
                    work.Add(() => Run("category", () => SendAsync(client, HttpMethod.Put, $"{CategoriesUrl}/{categoryId}", body)));
                }

                var inUse = shared[(round % shared.Count)];
                work.Add(() => Run("delete-in-use", () => client.DeleteAsync(new Uri($"/api/admin/media/{inUse.PublicId}", UriKind.Relative), Token)));

                var jitter = round % 5 * 5;
                work.Add(async () =>
                {
                    await Task.Delay(jitter, Token);
                    await Run("delete-contested", () => client.DeleteAsync(new Uri($"/api/admin/media/{contested.PublicId}", UriKind.Relative), Token));
                });

                await Task.WhenAll(work.Select(item => Task.Run(item, Token)));

                if (await ScalarAsync(host, $"SELECT CAST(IsDeleted AS int) FROM media.MediaAssets WHERE Id = {contested.Id}") == 1)
                {
                    var held = await ScalarAsync(host, $"SELECT COUNT(*) FROM catalog.ProductMedia WHERE MediaAssetId = {contested.Id}")
                        + await ScalarAsync(host, $"SELECT COUNT(*) FROM media.MediaUsages WHERE MediaAssetId = {contested.Id}");
                    if (held > 0)
                    {
                        orphaned.Add(string.Create(CultureInfo.InvariantCulture, $"round {round}: {held} row(s) still hold the deleted contested picture"));
                    }
                }
            }

            var summary = string.Join(", ", statuses.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}x{pair.Value}"));
            TestContext.Current.SendDiagnosticMessage($"T223 tester multi-asset workload ({rounds} rounds, {products.Count} products): {summary}; deadlocks {deadlocks.Count}");
            if (Environment.GetEnvironmentVariable("T223_WORKLOAD_LOG") is { Length: > 0 } log)
            {
                await File.AppendAllTextAsync(log, string.Create(CultureInfo.InvariantCulture, $"multi {rounds} rounds x {products.Count} products: {summary}; deadlocks {deadlocks.Count}{Environment.NewLine}"), Token);
            }

            Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures.Take(10)) + Environment.NewLine + summary);
            Assert.True(deadlocks.Count == 0, string.Create(CultureInfo.InvariantCulture, $"{deadlocks.Count} deadlock victim(s) (1205). {summary}"));
            Assert.True(orphaned.Count == 0, string.Join(Environment.NewLine, orphaned));
            Assert.DoesNotContain(statuses.Keys, key => key.StartsWith("delete-in-use 2", StringComparison.Ordinal));
            foreach (var picture in shared)
            {
                Assert.Equal(0, await ScalarAsync(host, $"SELECT CAST(IsDeleted AS int) FROM media.MediaAssets WHERE Id = {picture.Id}"));
            }
        }
        finally
        {
            await CleanUpAsync(host, products, categories, assets);
        }
    }

    // ================================================================== the retry's own-stamp check

    /// <summary>
    /// The first attempt reaches its commit, which fails (so nothing is committed), and before the retry runs a
    /// different user deletes the asset. The retry then finds the asset deleted, but not with this request's
    /// stamp, so the answer is 404 (somebody else deleted it), never a 204 that claims this request did.
    /// </summary>
    [Theory]
    [InlineData("other-user-other-time")]
    [InlineData("same-user-other-time")]
    [InlineData("other-user-same-time")]
    public async Task A_retried_delete_that_finds_somebody_elses_delete_answers_not_found(string interloperKind)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        var interloper = new CommitFailsThenSomebodyDeletes(sqlServer.ConnectionString);
        await using var host = MediaTestHost.Create(
            sqlServer.ConnectionString,
            configureServices: services =>
            {
                services.AddSingleton<IInterceptor>(interloper);
                services.AddSingleton<IInterceptor>(interloper.StampSpy);
            });
        var factory = host.Factory;

        // Two real staff users: the requester and (for the first case) somebody else.
        using var first = await host.SignInAsync(Everything);
        using var second = await host.SignInAsync(Everything);

        int assetId;
        Guid publicId;
        int? requester;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var asset = NewImage("t223t-stamp.png");
            db.MediaAssets.Add(asset);
            await db.SaveChangesAsync(Token);
            (assetId, publicId) = (asset.Id, asset.PublicId);

            var users = await db.Database.SqlQuery<int>($"SELECT TOP (2) Id AS Value FROM [identity].Users WHERE Email LIKE 'media-%' ORDER BY Id DESC").ToListAsync(Token);
            Assert.True(users.Count == 2, "the test database needs two users");
            requester = users[0];
            interloper.AssetId = assetId;
            interloper.DeletedByUserId = interloperKind.StartsWith("same-user", StringComparison.Ordinal) ? users[0] : users[1];

            // "same-time": the interloper writes exactly the DeletedAtUtc the request's own (rolled back) attempt
            // sent, so only the user id tells the two deletes apart.
            interloper.CopyRequestStamp = interloperKind.EndsWith("same-time", StringComparison.Ordinal);
        }

        try
        {
            MediaDeleteOutcome outcome;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                interloper.Arm();
                outcome = await scope.ServiceProvider.GetRequiredService<IMediaService>().DeleteAsync(publicId, requester, Token);
            }

            Assert.Equal(1, interloper.CommitFailures);
            Assert.Equal(1, interloper.Interventions);
            Assert.IsType<MediaDeleteOutcome.NotFound>(outcome);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var stamp = await db.MediaAssets.IgnoreQueryFilters().Where(asset => asset.Id == assetId)
                    .Select(asset => new { asset.IsDeleted, asset.DeletedByUserId, asset.DeletedAtUtc })
                    .SingleAsync(Token);

                // The interloper's stamp stands; the retry wrote nothing over it.
                Assert.True(stamp.IsDeleted);
                Assert.Equal(interloper.DeletedByUserId, stamp.DeletedByUserId);
                Assert.Equal(interloper.WrittenStamp, stamp.DeletedAtUtc);
                if (interloper.CopyRequestStamp)
                {
                    Assert.Equal(interloper.StampSpy.Stamp, stamp.DeletedAtUtc);
                }
            }
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync(
                "DELETE FROM media.MediaUsages WHERE MediaAssetId = {0}; DELETE FROM media.MediaAssets WHERE Id = {0};",
                [assetId],
                CancellationToken.None);
        }
    }

    // ================================================================== a save retried after a failed commit

    /// <summary>
    /// The execution strategy re-runs a save whose commit attempt failed with a transient error. The retried
    /// attempt still has the first attempt's tracked usage rows (<c>Added</c>, never written): it must write each
    /// once, not trip <c>UX_MediaUsages_Asset_Owner_Role</c> or lose them. Category, department and product.
    /// </summary>
    [Fact]
    public async Task Saves_retried_after_a_failed_commit_attempt_write_their_pictures_and_usage_rows_once()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        var failing = new FailNextCommit();
        await using var host = MediaTestHost.Create(
            sqlServer.ConnectionString,
            configureServices: services => services.AddSingleton<IInterceptor>(failing));
        using var client = await host.SignInAsync(Everything);

        var assets = new List<int>();
        var categories = new List<int>();
        var products = new List<int>();
        var kids = await ScalarAsync(host, $"SELECT Id FROM catalog.Departments WHERE Slug = 'kids'");
        var originalHero = await ScalarAsync(host, $"SELECT HeroImageAssetId FROM catalog.Departments WHERE Id = {kids}");
        string? originalDescription;
        List<(int AssetId, string Role)> originalUsages;
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var owner = kids.ToString(CultureInfo.InvariantCulture);
            originalDescription = await db.Departments.Where(row => row.Id == kids).Select(row => row.Description).SingleAsync(Token);
            originalUsages = (await db.MediaUsages.AsNoTracking()
                    .Where(usage => usage.OwnerType == MediaUsageOwnerTypes.Department && usage.OwnerId == owner)
                    .Select(usage => new { usage.MediaAssetId, usage.UsageRole })
                    .ToListAsync(Token))
                .Select(usage => (usage.MediaAssetId, usage.UsageRole))
                .ToList();
        }

        try
        {
            var image = await AddImageAsync(host, assets);
            var banner = await AddImageAsync(host, assets);
            var hero = await AddImageAsync(host, assets);
            var gallery = await AddImageAsync(host, assets);

            // ---- category
            var women = await ScalarAsync(host, $"SELECT Id FROM catalog.Departments WHERE Slug = 'women'");
            var categoryId = await CreatedIdAsync(await SendAsync(client, HttpMethod.Post, CategoriesUrl, new Dictionary<string, object?>
            {
                ["departmentId"] = women,
                ["parentCategoryId"] = null,
                ["name"] = "T223T retry",
                ["slug"] = "t223t-retry",
                ["showInMegaMenu"] = false,
            }));
            categories.Add(categoryId);

            var categoryBody = await CategoryBodyAsync(host, categoryId, image.Id, banner.Id);
            failing.Arm();
            using (var saved = await SendAsync(client, HttpMethod.Put, $"{CategoriesUrl}/{categoryId}", categoryBody))
            {
                Assert.True(saved.StatusCode == HttpStatusCode.NoContent, $"category {(int)saved.StatusCode}: {await saved.Content.ReadAsStringAsync(Token)}");
            }

            Assert.Equal(1, failing.Fired);
            Assert.Equal(2, await ScalarAsync(host, $"SELECT COUNT(*) FROM media.MediaUsages WHERE OwnerType = 'Category' AND OwnerId = CAST({categoryId} AS varchar(100))"));
            Assert.Equal(image.Id, await ScalarAsync(host, $"SELECT ImageAssetId FROM catalog.Categories WHERE Id = {categoryId}"));

            // ---- department
            failing.Arm();
            using (var saved = await SendAsync(client, HttpMethod.Put, $"/api/admin/catalog/departments/{kids}", new Dictionary<string, object?>
            {
                ["description"] = "T223T retry",
                ["heroImageAssetId"] = hero.Id,
                ["seoTitle"] = null,
                ["seoDescription"] = null,
                ["rowVersion"] = await VersionAsync(host, $"SELECT RowVersion FROM catalog.Departments WHERE Id = {kids}"),
            }))
            {
                Assert.True(saved.StatusCode == HttpStatusCode.NoContent, $"department {(int)saved.StatusCode}: {await saved.Content.ReadAsStringAsync(Token)}");
            }

            Assert.Equal(2, failing.Fired);
            Assert.Equal(1, await ScalarAsync(host, $"SELECT COUNT(*) FROM media.MediaUsages WHERE OwnerType = 'Department' AND OwnerId = CAST({kids} AS varchar(100))"));
            Assert.Equal(hero.Id, await ScalarAsync(host, $"SELECT HeroImageAssetId FROM catalog.Departments WHERE Id = {kids}"));

            // ---- product
            var productId = await CreatedIdAsync(await SendAsync(client, HttpMethod.Post, ProductsUrl, await ProductBodyAsync(host, "T223T-RETRY", [], null, null)));
            products.Add(productId);

            var productBody = await ProductBodyAsync(host, "T223T-RETRY", [gallery], null, await VersionAsync(host, $"SELECT RowVersion FROM catalog.Products WHERE Id = {productId}"));
            failing.Arm();
            using (var saved = await SendAsync(client, HttpMethod.Put, $"{ProductsUrl}/{productId}", productBody))
            {
                Assert.True(saved.StatusCode == HttpStatusCode.NoContent, $"product {(int)saved.StatusCode}: {await saved.Content.ReadAsStringAsync(Token)}");
            }

            Assert.Equal(3, failing.Fired);
            Assert.Equal(1, await ScalarAsync(host, $"SELECT COUNT(*) FROM catalog.ProductMedia WHERE ProductId = {productId}"));
            Assert.Equal(2, await ScalarAsync(host, $"SELECT COUNT(*) FROM media.MediaUsages WHERE OwnerType = 'Product' AND OwnerId = CAST({productId} AS varchar(100))"));
        }
        finally
        {
            failing.Disarm();
            await using (var scope = host.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM media.MediaUsages WHERE OwnerType = 'Department' AND OwnerId = CAST({0} AS varchar(100));",
                    [kids],
                    CancellationToken.None);
                await db.Departments.Where(row => row.Id == kids)
                    .ExecuteUpdateAsync(
                        set => set
                            .SetProperty(row => row.HeroImageAssetId, originalHero == 0 ? null : originalHero)
                            .SetProperty(row => row.Description, originalDescription),
                        CancellationToken.None);
                foreach (var (assetId, role) in originalUsages)
                {
                    await db.Database.ExecuteSqlRawAsync(
                        "INSERT INTO media.MediaUsages (MediaAssetId, OwnerType, OwnerId, UsageRole) VALUES ({0}, 'Department', CAST({1} AS varchar(100)), {2});",
                        [assetId, kids, role],
                        CancellationToken.None);
                }
            }

            await CleanUpAsync(host, products, categories, assets);
        }
    }

    // ================================================================== helpers

    private sealed record Picture(int Id, Guid PublicId);

    private static MediaAsset NewImage(string name)
    {
        var publicId = Guid.NewGuid();
        return new MediaAsset
        {
            PublicId = publicId,
            MediaType = MediaAssetType.Image,
            Visibility = MediaVisibility.Public,
            Status = MediaAssetStatus.Ready,
            OriginalFileName = name,
            StorageKey = $"public/images/{publicId:N}/original.png",
            ContentType = "image/png",
            SizeBytes = 1024,
            Width = 1600,
            Height = 900,
            RenditionSetVersion = MediaRenditions.CurrentSetVersion,
            Checksum = System.Security.Cryptography.SHA256.HashData(publicId.ToByteArray()),
            AltText = "A #223 tester picture",
        };
    }

    private static async Task<Picture> AddImageAsync(MediaTestHost host, List<int> created)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asset = NewImage("t223t.png");
        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync(Token);
        created.Add(asset.Id);
        return new Picture(asset.Id, asset.PublicId);
    }

    private static async Task<Dictionary<string, object?>> ProductBodyAsync(
        MediaTestHost host,
        string code,
        IReadOnlyList<Picture> gallery,
        Picture? sharedSwatch,
        string? rowVersion)
    {
        var women = await ScalarAsync(host, $"SELECT Id FROM catalog.Departments WHERE Slug = 'women'");
        var category = await ScalarAsync(host, $"SELECT MIN(Id) FROM catalog.Categories WHERE DepartmentId = {women} AND IsDeleted = 0 AND Slug NOT LIKE 't223%'");
        var material = await ScalarAsync(host, $"SELECT MIN(Id) FROM catalog.AttributeOptions WHERE AttributeType = {(byte)AttributeOptionType.Material}");
        var family = await ScalarAsync(host, $"SELECT MIN(Id) FROM catalog.ColourFamilies");

        var colours = sharedSwatch is null
            ? Array.Empty<object>()
            : new object[]
            {
                Colour("Indigo", family, sharedSwatch.PublicId, 0),
                Colour("Stone", family, sharedSwatch.PublicId, 1),
            };

        return new Dictionary<string, object?>
        {
            ["productCode"] = code,
            ["name"] = code,
            ["slug"] = code.ToLowerInvariant(),
            ["departmentId"] = women,
            ["primaryCategoryId"] = category,
            ["materialId"] = material,
            ["basePrice"] = 5490m,
            ["colours"] = colours,
            ["variants"] = Array.Empty<object>(),
            ["relatedProducts"] = Array.Empty<object>(),
            ["media"] = gallery
                .Select((picture, index) => (object)new Dictionary<string, object?>
                {
                    ["assetPublicId"] = picture.PublicId,
                    ["colourName"] = null,
                    ["mediaRole"] = "Gallery",
                    ["sortOrder"] = index,
                    ["altTextOverride"] = "Tester",
                })
                .ToArray(),
            ["rowVersion"] = rowVersion,
        };
    }

    private static Dictionary<string, object?> Colour(string name, int family, Guid swatch, short sortOrder) => new()
    {
        ["id"] = null,
        ["name"] = name,
        ["colourFamilyId"] = family,
        ["swatchHex"] = null,
        ["swatchAssetPublicId"] = swatch,
        ["sortOrder"] = sortOrder,
    };

    private static async Task<Dictionary<string, object?>> CategoryBodyAsync(MediaTestHost host, int categoryId, int imageAssetId, int bannerAssetId)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var category = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Categories.AsNoTracking()
            .SingleAsync(row => row.Id == categoryId, Token);

        return new Dictionary<string, object?>
        {
            ["parentCategoryId"] = category.ParentCategoryId,
            ["name"] = category.Name,
            ["slug"] = category.Slug,
            ["description"] = category.Description,
            ["imageAssetId"] = imageAssetId,
            ["bannerAssetId"] = bannerAssetId,
            ["bannerLinkUrl"] = category.BannerLinkUrl,
            ["ageGroupId"] = category.AgeGroupId,
            ["sizeGuideId"] = category.SizeGuideId,
            ["showInMegaMenu"] = category.ShowInMegaMenu,
            ["megaMenuColumn"] = category.MegaMenuColumn,
            ["seoTitle"] = category.SeoTitle,
            ["seoDescription"] = category.SeoDescription,
            ["rowVersion"] = Convert.ToBase64String(category.RowVersion!),
        };
    }

    private static async Task<int> CreatedIdAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Token);
            Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {body}");
            return JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt32();
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, object body)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative))
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        return await client.SendAsync(request, Token);
    }

    private static async Task<int> ScalarAsync(MediaTestHost host, FormattableString sql)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var values = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .SqlQuery<int?>(Wrap(sql))
            .ToListAsync(Token);
        return values.SingleOrDefault() ?? 0;
    }

    private static async Task<string?> VersionAsync(MediaTestHost host, FormattableString sql)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var values = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .SqlQuery<byte[]>(Wrap(sql))
            .ToListAsync(Token);
        return Convert.ToBase64String(values.Single());
    }

    private static FormattableString Wrap(FormattableString sql) =>
        System.Runtime.CompilerServices.FormattableStringFactory.Create("SELECT (" + sql.Format + ") AS Value", sql.GetArguments());

    private static async Task CleanUpAsync(MediaTestHost host, IEnumerable<int> products, IEnumerable<int> categories, IEnumerable<int> assets)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var none = CancellationToken.None;

        foreach (var productId in products.Where(id => id != 0))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                DELETE FROM ops.AuditLogs WHERE EntityType = 'Product' AND EntityId = CAST({0} AS varchar(100));
                DELETE FROM media.MediaUsages WHERE OwnerType = 'Product' AND OwnerId = CAST({0} AS varchar(100));
                DELETE FROM inventory.StockLevels WHERE VariantId IN (SELECT Id FROM catalog.ProductVariants WHERE ProductId = {0});
                DELETE FROM catalog.ProductVariants WHERE ProductId = {0};
                DELETE FROM catalog.RelatedProducts WHERE ProductId = {0} OR RelatedProductId = {0};
                DELETE FROM catalog.ProductMedia WHERE ProductId = {0};
                DELETE FROM catalog.ProductColours WHERE ProductId = {0};
                DELETE FROM catalog.ProductCategories WHERE ProductId = {0};
                DELETE FROM catalog.ProductStats WHERE ProductId = {0};
                DELETE FROM catalog.ProductSearchIndex WHERE ProductId = {0};
                DELETE FROM catalog.Products WHERE Id = {0};
                """,
                [productId],
                none);
        }

        foreach (var categoryId in categories.Where(id => id != 0))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                DELETE FROM ops.AuditLogs WHERE EntityType = 'Category' AND EntityId = CAST({0} AS varchar(100));
                DELETE FROM media.MediaUsages WHERE OwnerType = 'Category' AND OwnerId = CAST({0} AS varchar(100));
                DELETE FROM catalog.Categories WHERE Id = {0};
                """,
                [categoryId],
                none);
        }

        foreach (var assetId in assets)
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM media.MediaUsages WHERE MediaAssetId = {0}; DELETE FROM media.MediaAssets WHERE Id = {0};",
                [assetId],
                none);
        }
    }

    /// <summary>Records the asset id of every per-asset lock statement <c>MediaAssetRowLocks</c> sends while armed.</summary>
    private sealed class AssetLockRecorder : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<int> _locked = new();
        private volatile bool _armed;

        public List<int> Locked => [.. _locked];

        public void Arm()
        {
            _locked.Clear();
            _armed = true;
        }

        public void Disarm() => _armed = false;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        private void Record(DbCommand command)
        {
            if (_armed
                && command.CommandText.Contains("FROM media.MediaAssets a WITH (UPDLOCK", StringComparison.Ordinal)
                && command.Parameters.Count == 1)
            {
                _locked.Enqueue(Convert.ToInt32(command.Parameters[0].Value, CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>Counts commands that failed as a deadlock victim (SQL Server error 1205).</summary>
    private sealed class DeadlockCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        {
            Note(eventData.Exception);
            base.CommandFailed(command, eventData);
        }

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Note(eventData.Exception);
            return base.CommandFailedAsync(command, eventData, cancellationToken);
        }

        private void Note(Exception exception)
        {
            if (exception is SqlException { Number: 1205 })
            {
                Interlocked.Increment(ref _count);
            }
        }
    }

    /// <summary>
    /// Fails the first commit attempt <b>before</b> it commits (a transient <see cref="TimeoutException"/>, so the
    /// transaction rolls back and the execution strategy retries), and as the retry's transaction starts, soft
    /// deletes the asset from another connection with a stamp that is not the request's.
    /// </summary>
    private sealed class CommitFailsThenSomebodyDeletes(string connectionString) : DbTransactionInterceptor
    {
        public static readonly DateTime InterloperStamp = new(2026, 10, 21, 1, 2, 3, 456, DateTimeKind.Utc);

        private int _armed;
        private int _commitFailures;
        private int _interventions;

        public int AssetId { get; set; }

        public int? DeletedByUserId { get; set; }

        public bool CopyRequestStamp { get; set; }

        public DateTime? WrittenStamp { get; private set; }

        public RequestStampSpy StampSpy { get; } = new();

        public int CommitFailures => Volatile.Read(ref _commitFailures);

        public int Interventions => Volatile.Read(ref _interventions);

        public void Arm() => Volatile.Write(ref _armed, 1);

        public override InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
        {
            FailFirstCommit();
            return base.TransactionCommitting(transaction, eventData, result);
        }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            FailFirstCommit();
            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }

        public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
        {
            Intervene();
            return base.TransactionStarted(connection, eventData, result);
        }

        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
        {
            Intervene();
            return base.TransactionStartedAsync(connection, eventData, result, cancellationToken);
        }

        private void FailFirstCommit()
        {
            if (Volatile.Read(ref _armed) == 1 && Interlocked.CompareExchange(ref _commitFailures, 1, 0) == 0)
            {
                throw new TimeoutException("Injected: the commit attempt failed before committing (#223 tester).");
            }
        }

        private void Intervene()
        {
            // Only the retry's transaction: the first commit has already failed (and rolled back) by then.
            if (Volatile.Read(ref _armed) == 1
                && Volatile.Read(ref _commitFailures) == 1
                && Interlocked.CompareExchange(ref _interventions, 1, 0) == 0)
            {
                using var connection = new SqlConnection(connectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE media.MediaAssets SET IsDeleted = 1, DeletedAtUtc = @at, DeletedByUserId = @by WHERE Id = @id";
                WrittenStamp = CopyRequestStamp
                    ? StampSpy.Stamp ?? throw new InvalidOperationException("The request's soft-delete UPDATE was never seen.")
                    : InterloperStamp;
                command.Parameters.Add(new SqlParameter("@at", System.Data.SqlDbType.DateTime2) { Value = WrittenStamp, Scale = 3 });
                command.Parameters.Add(new SqlParameter("@by", System.Data.SqlDbType.Int) { Value = (object?)DeletedByUserId ?? DBNull.Value });
                command.Parameters.AddWithValue("@id", AssetId);
                command.ExecuteNonQuery();
            }
        }
    }

    /// <summary>Fails the next commit attempt before it commits (transient), once per <see cref="Arm"/>.</summary>
    private sealed class FailNextCommit : DbTransactionInterceptor
    {
        private int _armed;
        private int _fired;

        public int Fired => Volatile.Read(ref _fired);

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Disarm() => Volatile.Write(ref _armed, 0);

        public override InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
        {
            Fail();
            return base.TransactionCommitting(transaction, eventData, result);
        }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Fail();
            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }

        private void Fail()
        {
            if (Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            {
                Interlocked.Increment(ref _fired);
                throw new TimeoutException("Injected: the commit attempt failed before committing (#223 tester).");
            }
        }
    }

    /// <summary>Captures the <c>DeletedAtUtc</c> the request's soft-delete <c>UPDATE</c> sends.</summary>
    private sealed class RequestStampSpy : DbCommandInterceptor
    {
        public DateTime? Stamp { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Capture(DbCommand command)
        {
            if (Stamp is null
                && command.CommandText.Contains("UPDATE [media].[MediaAssets]", StringComparison.Ordinal)
                && command.CommandText.Contains("[DeletedAtUtc]", StringComparison.Ordinal))
            {
                Stamp = command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value).OfType<DateTime>().FirstOrDefault();
            }
        }
    }
}
