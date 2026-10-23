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
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Media;

/// <summary>
/// #223: doc 08 §5.3's media lock order under a mixed concurrent workload of <b>every</b> owner that now locks
/// its pictures — a product, a category, a department's hero, a homepage item, a mega-menu card moving between
/// two departments, a sale campaign's banner — and the media library's deletes (one contested, one always
/// refused, one free), all at once, round after round. No answer may be a 500 (a deadlock victim, error 1205,
/// surfaces as one), and no owner may be left on a deleted picture whoever wins.
/// <para>
/// Rounds default to 25 so the suite stays quick; set <c>T223_WORKLOAD_ROUNDS</c> for the soak (#223 was signed
/// off on 250).
/// </para>
/// </summary>
public sealed class MediaOwnerLockWorkloadTests(SqlServerDatabaseFixture sqlServer)
{
    private const string ProductsUrl = "/api/admin/products";
    private const string CategoriesUrl = "/api/admin/catalog/categories";
    private const string DepartmentsUrl = "/api/admin/catalog/departments";
    private const string CardsUrl = "/api/admin/content/mega-menu/cards";
    private const string HomeUrl = "/api/admin/content/home";
    private const string CampaignsUrl = "/api/admin/marketing/sale-campaigns";

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
        Permissions.Content.View,
        Permissions.Content.Create,
        Permissions.Content.Edit,
        Permissions.Content.Delete,
        Permissions.Promotions.View,
        Permissions.Promotions.Create,
        Permissions.Promotions.Edit,
        Permissions.Promotions.Delete,
        Permissions.Media.View,
        Permissions.Media.Create,
        Permissions.Media.Delete,
    ];

    private static readonly string[] Savers = ["product", "category", "department", "item", "card", "campaign"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_media_owner_and_the_library_at_once_never_deadlock_fail_or_orphan_a_picture()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);
        // A deadlock victim (1205) is transient to the execution strategy, so it is retried and never surfaces as a
        // 500. Counted here instead: the lock order's promise is that there are none to retry.
        var deadlocks = new DeadlockCounter();
        await using var host = MediaTestHost.Create(
            sqlServer.ConnectionString,
            configureServices: services => services.AddSingleton<IInterceptor>(deadlocks));
        using var client = await host.SignInAsync(Everything);

        var rounds = int.TryParse(Environment.GetEnvironmentVariable("T223_WORKLOAD_ROUNDS"), CultureInfo.InvariantCulture, out var asked)
            ? asked
            : 25;

        var assets = new List<int>();
        var a = await AddImageAsync(host, assets);
        var b = await AddImageAsync(host, assets);

        var women = await ScalarAsync(host, $"SELECT Id FROM catalog.Departments WHERE Slug = 'women'");
        var kids = await ScalarAsync(host, $"SELECT Id FROM catalog.Departments WHERE Slug = 'kids'");
        var men = await ScalarAsync(host, $"SELECT Id FROM catalog.Departments WHERE Slug = 'men'");
        var shirts = await ScalarAsync(host, $"SELECT c.Id FROM catalog.Categories c JOIN catalog.Departments d ON d.Id = c.DepartmentId WHERE c.Slug = 'shirts-blouses' AND d.Slug = 'women'");
        var original = await DepartmentAsync(host, kids);

        int productId = 0, categoryId = 0, cardId = 0, pinnedCardId = 0, itemId = 0, campaignId = 0;
        var failures = new ConcurrentBag<string>();
        var orphaned = new ConcurrentBag<string>();
        var statuses = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var roundCodes = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        void Note(string actor, HttpResponseMessage answer, string body)
        {
            var code = (int)answer.StatusCode;
            roundCodes[actor] = code;
            statuses.AddOrUpdate(actor + " " + code.ToString(CultureInfo.InvariantCulture), 1, (_, n) => n + 1);
            if (code >= 500)
            {
                failures.Add($"{actor} {code}: {body[..Math.Min(body.Length, 300)]}");
            }
        }

        try
        {
            // ---- arrange one of each owner, on picture A
            productId = await CreatedIdAsync(await SendAsync(client, HttpMethod.Post, ProductsUrl, await ProductBodyAsync(host, a.PublicId, null)));
            categoryId = await CreatedIdAsync(await SendAsync(client, HttpMethod.Post, CategoriesUrl, new Dictionary<string, object?>
            {
                ["departmentId"] = women,
                ["parentCategoryId"] = null,
                ["name"] = "T223 workload",
                ["slug"] = "t223-workload",
                ["imageAssetId"] = a.Id,
                ["showInMegaMenu"] = false,
            }));
            cardId = await CreatedIdAsync(await SendAsync(client, HttpMethod.Post, CardsUrl, CardBody(kids, a.PublicId, null)));

            var social = await ScalarAsync(host, $"SELECT Id FROM content.HomeSections WHERE SectionType = {(byte)SpencerDenim.Domain.Content.HomeSectionType.SocialGallery}");
            itemId = await CreatedIdAsync(await SendAsync(client, HttpMethod.Post, $"{HomeUrl}/sections/{social}/items", ItemBody(a.PublicId, await VersionAsync(host, $"SELECT RowVersion FROM content.HomeSections WHERE Id = {social}"))));

            using (var created = await SendAsync(client, HttpMethod.Post, CampaignsUrl, CampaignBody(a.PublicId, null, shirts)))
            {
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                campaignId = (await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("campaign").GetProperty("id").GetInt32();
            }

            // A second card holds picture B for the whole run, so a delete of B must always be refused.
            pinnedCardId = await CreatedIdAsync(await SendAsync(client, HttpMethod.Post, CardsUrl, CardBody(men, b.PublicId, null)));

            // ---- the workload: every round each owner moves to a fresh picture C while the library deletes C
            // (either may win, never both), deletes the pinned B (always refused) and an unused picture (allowed).
            for (var round = 0; round < rounds; round++)
            {
                var picture = await AddImageAsync(host, assets);
                var spare = await AddImageAsync(host, assets);
                roundCodes.Clear();

                var versions = new
                {
                    Product = await VersionAsync(host, $"SELECT RowVersion FROM catalog.Products WHERE Id = {productId}"),
                    Category = await VersionAsync(host, $"SELECT RowVersion FROM catalog.Categories WHERE Id = {categoryId}"),
                    Department = await VersionAsync(host, $"SELECT RowVersion FROM catalog.Departments WHERE Id = {kids}"),
                    Card = await VersionAsync(host, $"SELECT RowVersion FROM content.MegaMenuPromoCards WHERE Id = {cardId}"),
                    Section = await VersionAsync(host, $"SELECT RowVersion FROM content.HomeSections WHERE Id = {social}"),
                    Campaign = await VersionAsync(host, $"SELECT RowVersion FROM marketing.SaleCampaigns WHERE Id = {campaignId}"),
                };
                var productBody = await ProductBodyAsync(host, picture.PublicId, versions.Product);
                var current = round;

                async Task Run(string actor, Func<Task<HttpResponseMessage>> send)
                {
                    using var answer = await send();
                    Note(actor, answer, await answer.Content.ReadAsStringAsync(Token));
                }

                await Task.WhenAll(
                    Task.Run(() => Run("product", () => SendAsync(client, HttpMethod.Put, $"{ProductsUrl}/{productId}", productBody)), Token),
                    Task.Run(() => Run("category", () => SendAsync(client, HttpMethod.Put, $"{CategoriesUrl}/{categoryId}", CategoryBody(picture.Id, versions.Category))), Token),
                    Task.Run(() => Run("department", () => SendAsync(client, HttpMethod.Put, $"{DepartmentsUrl}/{kids}", new Dictionary<string, object?>
                    {
                        ["description"] = original.Description,
                        ["heroImageAssetId"] = picture.Id,
                        ["seoTitle"] = original.SeoTitle,
                        ["seoDescription"] = original.SeoDescription,
                        ["rowVersion"] = versions.Department,
                    })), Token),
                    Task.Run(() => Run("item", () => SendAsync(client, HttpMethod.Put, $"{HomeUrl}/items/{itemId}", ItemBody(picture.PublicId, versions.Section))), Token),
                    Task.Run(() => Run("card", () => SendAsync(client, HttpMethod.Put, $"{CardsUrl}/{cardId}", CardBody(current % 2 == 0 ? men : kids, picture.PublicId, versions.Card))), Token),
                    Task.Run(() => Run("campaign", () => SendAsync(client, HttpMethod.Put, $"{CampaignsUrl}/{campaignId}", CampaignBody(picture.PublicId, versions.Campaign, shirts))), Token),
                    Task.Run(() => Run("delete-in-use", () => client.DeleteAsync(new Uri($"/api/admin/media/{b.PublicId}", UriKind.Relative), Token)), Token),
                    Task.Run(
                        async () =>
                        {
                            // Jittered so the saves win some rounds and the delete others.
                            await Task.Delay(current % 7 * 6, Token);
                            await Run("delete-contested", () => client.DeleteAsync(new Uri($"/api/admin/media/{picture.PublicId}", UriKind.Relative), Token));
                        },
                        Token),
                    Task.Run(() => Run("delete-free", () => client.DeleteAsync(new Uri($"/api/admin/media/{spare.PublicId}", UriKind.Relative), Token)), Token));

                // When the delete won, every save that chose the picture was refused — not answered 2xx.
                if (roundCodes.TryGetValue("delete-contested", out var deleteCode) && deleteCode == 204)
                {
                    foreach (var actor in Savers)
                    {
                        if (roundCodes.TryGetValue(actor, out var saveCode) && saveCode is >= 200 and < 300)
                        {
                            orphaned.Add(string.Create(CultureInfo.InvariantCulture, $"round {round}: {actor} answered {saveCode} though its picture was deleted"));
                        }
                    }
                }

                // Whoever won, no owner may be left on a deleted picture.
                if (await ScalarAsync(host, $"SELECT CAST(IsDeleted AS int) FROM media.MediaAssets WHERE Id = {picture.Id}") == 1)
                {
                    var holders = new[]
                    {
                        await ScalarAsync(host, $"SELECT COUNT(*) FROM catalog.ProductMedia WHERE MediaAssetId = {picture.Id}"),
                        await ScalarAsync(host, $"SELECT COUNT(*) FROM catalog.Products WHERE CardImageAssetId = {picture.Id}"),
                        await ScalarAsync(host, $"SELECT COUNT(*) FROM catalog.Categories WHERE ImageAssetId = {picture.Id}"),
                        await ScalarAsync(host, $"SELECT COUNT(*) FROM catalog.Departments WHERE HeroImageAssetId = {picture.Id}"),
                        await ScalarAsync(host, $"SELECT COUNT(*) FROM content.HomeSectionItems WHERE ImageAssetId = {picture.Id}"),
                        await ScalarAsync(host, $"SELECT COUNT(*) FROM content.MegaMenuPromoCards WHERE ImageAssetId = {picture.Id}"),
                        await ScalarAsync(host, $"SELECT COUNT(*) FROM marketing.SaleCampaigns WHERE BannerAssetId = {picture.Id}"),
                        await ScalarAsync(host, $"SELECT COUNT(*) FROM media.MediaUsages WHERE MediaAssetId = {picture.Id}"),
                    };

                    if (holders.Any(count => count > 0))
                    {
                        orphaned.Add(string.Create(CultureInfo.InvariantCulture, $"round {round}: productMedia/card/category/department/item/card/campaign/usages = {string.Join('/', holders)}"));
                    }
                }
            }

            var summary = string.Join(", ", statuses.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}×{pair.Value}"));
            TestContext.Current.SendDiagnosticMessage("T223 workload: " + summary);
            if (Environment.GetEnvironmentVariable("T223_WORKLOAD_LOG") is { Length: > 0 } log)
            {
                await File.AppendAllTextAsync(log, string.Create(CultureInfo.InvariantCulture, $"{rounds} rounds: {summary}{Environment.NewLine}"), Token);
            }

            Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures.Take(10)) + Environment.NewLine + summary);
            Assert.True(deadlocks.Count == 0, string.Create(CultureInfo.InvariantCulture, $"{deadlocks.Count} deadlock victim(s) (1205), retried by the execution strategy. {summary}"));
            Assert.True(orphaned.IsEmpty, string.Join(Environment.NewLine, orphaned.Take(20)) + Environment.NewLine + summary);
            Assert.Equal(0, await ScalarAsync(host, $"SELECT CAST(IsDeleted AS int) FROM media.MediaAssets WHERE Id = {b.Id}"));
            Assert.DoesNotContain(statuses.Keys, key => key.StartsWith("delete-in-use 2", StringComparison.Ordinal));

            // The contested delete only ever won (204) or was refused because a save got there first (409).
            Assert.All(
                statuses.Keys.Where(key => key.StartsWith("delete-contested ", StringComparison.Ordinal)),
                key => Assert.True(key is "delete-contested 204" or "delete-contested 409", key + Environment.NewLine + summary));
        }
        finally
        {
            await CleanUpAsync(host, productId, categoryId, cardId, pinnedCardId, itemId, campaignId, original, assets);
        }
    }

    // ================================================================== bodies

    private static async Task<Dictionary<string, object?>> ProductBodyAsync(MediaTestHost host, Guid picture, string? rowVersion)
    {
        var women = await ScalarAsync(host, $"SELECT Id FROM catalog.Departments WHERE Slug = 'women'");
        var category = await ScalarAsync(host, $"SELECT MIN(Id) FROM catalog.Categories WHERE DepartmentId = {women} AND IsDeleted = 0 AND Slug <> 't223-workload'");
        var material = await ScalarAsync(host, $"SELECT MIN(Id) FROM catalog.AttributeOptions WHERE AttributeType = {(byte)AttributeOptionType.Material}");

        return new Dictionary<string, object?>
        {
            ["productCode"] = "T223-WORKLOAD",
            ["name"] = "T223 workload",
            ["slug"] = "t223-workload",
            ["departmentId"] = women,
            ["primaryCategoryId"] = category,
            ["materialId"] = material,
            ["basePrice"] = 5490m,
            ["colours"] = Array.Empty<object>(),
            ["variants"] = Array.Empty<object>(),
            ["relatedProducts"] = Array.Empty<object>(),
            ["media"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["assetPublicId"] = picture,
                    ["colourName"] = null,
                    ["mediaRole"] = "Gallery",
                    ["sortOrder"] = 0,
                    ["altTextOverride"] = "Workload",
                },
            },
            ["rowVersion"] = rowVersion,
        };
    }

    private static Dictionary<string, object?> CategoryBody(int imageAssetId, string? rowVersion) => new()
    {
        ["parentCategoryId"] = null,
        ["name"] = "T223 workload",
        ["slug"] = "t223-workload",
        ["description"] = null,
        ["imageAssetId"] = imageAssetId,
        ["bannerAssetId"] = null,
        ["bannerLinkUrl"] = null,
        ["ageGroupId"] = null,
        ["sizeGuideId"] = null,
        ["showInMegaMenu"] = false,
        ["megaMenuColumn"] = null,
        ["seoTitle"] = null,
        ["seoDescription"] = null,
        ["rowVersion"] = rowVersion,
    };

    private static Dictionary<string, object?> CardBody(int departmentId, Guid image, string? rowVersion) => new()
    {
        ["departmentId"] = departmentId,
        ["title"] = "T223 workload card",
        ["subtitle"] = null,
        ["buttonLabel"] = "Shop now",
        ["linkUrl"] = "/new-arrivals",
        ["imagePublicId"] = image,
        ["sortOrder"] = 61,
        ["isActive"] = false,
        ["startsAt"] = null,
        ["endsAt"] = null,
        ["rowVersion"] = rowVersion,
    };

    private static Dictionary<string, object?> ItemBody(Guid image, string? rowVersion) => new()
    {
        ["title"] = "T223 workload item",
        ["subtitle"] = null,
        ["body"] = null,
        ["buttonLabel"] = null,
        ["linkUrl"] = "https://instagram.com/colombowardrobe",
        ["imagePublicId"] = image,
        ["mobileImagePublicId"] = null,
        ["videoPublicId"] = null,
        ["departmentId"] = null,
        ["productSlug"] = null,
        ["iconKey"] = null,
        ["sortOrder"] = 96,
        ["isEnabled"] = false,
        ["startsAt"] = null,
        ["endsAt"] = null,
        ["rowVersion"] = rowVersion,
    };

    private static Dictionary<string, object?> CampaignBody(Guid banner, string? rowVersion, int categoryId) => new(StringComparer.Ordinal)
    {
        ["name"] = "T223 workload campaign",
        ["description"] = "Arranged by the #223 lock workload.",
        ["discountType"] = "Percent",
        ["discountValue"] = 10m,
        ["startsAt"] = ShopTime.Midnight(0),
        ["endsAt"] = ShopTime.Midnight(7),
        ["isActive"] = false,
        ["bannerPublicId"] = banner.ToString(),
        ["targets"] = new[]
        {
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["targetType"] = "Category",
                ["departmentId"] = null,
                ["categoryId"] = categoryId,
                ["productId"] = null,
                ["includeSubcategories"] = true,
            },
        },
        ["rowVersion"] = rowVersion,
    };

    // ================================================================== helpers

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

    private sealed record Picture(int Id, Guid PublicId);

    private sealed record DepartmentRow(int Id, string? Description, int? HeroImageAssetId, string? SeoTitle, string? SeoDescription, List<(int AssetId, string Role)> Usages);

    private static async Task<Picture> AddImageAsync(MediaTestHost host, List<int> created)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var publicId = Guid.NewGuid();

        var asset = new MediaAsset
        {
            PublicId = publicId,
            MediaType = MediaAssetType.Image,
            Visibility = MediaVisibility.Public,
            Status = MediaAssetStatus.Ready,
            OriginalFileName = "t223-workload.png",
            StorageKey = $"public/images/{publicId:N}/original.png",
            ContentType = "image/png",
            SizeBytes = 1024,
            Width = 1600,
            Height = 900,
            RenditionSetVersion = MediaRenditions.CurrentSetVersion,
            Checksum = System.Security.Cryptography.SHA256.HashData(publicId.ToByteArray()),
            AltText = "A #223 workload picture",
        };

        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync(Token);
        created.Add(asset.Id);

        return new Picture(asset.Id, publicId);
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

    private static async Task<DepartmentRow> DepartmentAsync(MediaTestHost host, int id)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Departments.AsNoTracking().SingleAsync(department => department.Id == id, Token);
        var owner = id.ToString(CultureInfo.InvariantCulture);
        var usages = await db.MediaUsages.AsNoTracking()
            .Where(usage => usage.OwnerType == MediaUsageOwnerTypes.Department && usage.OwnerId == owner)
            .Select(usage => new { usage.MediaAssetId, usage.UsageRole })
            .ToListAsync(Token);

        return new DepartmentRow(id, row.Description, row.HeroImageAssetId, row.SeoTitle, row.SeoDescription, usages.Select(usage => (usage.MediaAssetId, usage.UsageRole)).ToList());
    }

    /// <summary>Leaves the shared database as found: every owner and usage row removed, Kids' hero put back.</summary>
    private static async Task CleanUpAsync(
        MediaTestHost host,
        int productId,
        int categoryId,
        int cardId,
        int pinnedCardId,
        int itemId,
        int campaignId,
        DepartmentRow original,
        List<int> assets)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var none = CancellationToken.None;

        async Task Raw(string sql, params object[] parameters) => await db.Database.ExecuteSqlRawAsync(sql, parameters, none);

        if (productId != 0)
        {
            await Raw(
                """
                DELETE FROM ops.AuditLogs WHERE EntityType = 'Product' AND EntityId = CAST({0} AS varchar(100));
                DELETE FROM media.MediaUsages WHERE OwnerType = 'Product' AND OwnerId = CAST({0} AS varchar(100));
                DELETE FROM catalog.ProductMedia WHERE ProductId = {0};
                DELETE FROM catalog.ProductCategories WHERE ProductId = {0};
                DELETE FROM catalog.ProductStats WHERE ProductId = {0};
                DELETE FROM catalog.ProductSearchIndex WHERE ProductId = {0};
                DELETE FROM catalog.Products WHERE Id = {0};
                """,
                productId);
        }

        if (categoryId != 0)
        {
            await Raw(
                """
                DELETE FROM ops.AuditLogs WHERE EntityType = 'Category' AND EntityId = CAST({0} AS varchar(100));
                DELETE FROM media.MediaUsages WHERE OwnerType = 'Category' AND OwnerId = CAST({0} AS varchar(100));
                DELETE FROM catalog.Categories WHERE Id = {0};
                """,
                categoryId);
        }

        foreach (var card in new[] { cardId, pinnedCardId }.Where(id => id != 0))
        {
            await Raw(
                """
                DELETE FROM media.MediaUsages WHERE OwnerType = 'MegaMenuPromoCard' AND OwnerId = CAST({0} AS varchar(100));
                DELETE FROM content.MegaMenuPromoCards WHERE Id = {0};
                """,
                card);
        }

        if (itemId != 0)
        {
            await Raw(
                """
                DELETE FROM media.MediaUsages WHERE OwnerType = 'HomeSectionItem' AND OwnerId = CAST({0} AS varchar(100));
                DELETE FROM content.HomeSectionItems WHERE Id = {0};
                """,
                itemId);
        }

        if (campaignId != 0)
        {
            await Raw(
                """
                DELETE FROM media.MediaUsages WHERE OwnerType = 'SaleCampaign' AND OwnerId = CAST({0} AS varchar(100));
                DELETE FROM marketing.SaleCampaignTargets WHERE SaleCampaignId = {0};
                DELETE FROM marketing.SaleCampaigns WHERE Id = {0};
                """,
                campaignId);
        }

        await Raw("DELETE FROM media.MediaUsages WHERE OwnerType = 'Department' AND OwnerId = CAST({0} AS varchar(100));", original.Id);
        await db.Departments.Where(row => row.Id == original.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.HeroImageAssetId, original.HeroImageAssetId), none);

        foreach (var (assetId, role) in original.Usages)
        {
            await Raw(
                "INSERT INTO media.MediaUsages (MediaAssetId, OwnerType, OwnerId, UsageRole) VALUES ({0}, 'Department', CAST({1} AS varchar(100)), {2});",
                assetId,
                original.Id,
                role);
        }

        foreach (var assetId in assets)
        {
            await Raw(
                """
                DELETE FROM media.MediaUsages WHERE MediaAssetId = {0};
                DELETE FROM media.MediaAssets WHERE Id = {0};
                """,
                assetId);
        }
    }
}
