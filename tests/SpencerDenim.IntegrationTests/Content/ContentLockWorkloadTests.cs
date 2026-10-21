using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.Tests.MediaFixtures;

namespace SpencerDenim.IntegrationTests.Content;

/// <summary>
/// Tester pass, #215 round 2: the lock order the content save now takes (department row, then media asset rows by
/// ascending id, then writes) under a mixed concurrent workload of every writer that locks or references the same
/// rows — a mega-menu card moving between two departments and swapping its picture, a homepage item, a size guide,
/// a sale campaign banner, a department's hero picture, and media-library deletes (one refused, one allowed) — all
/// at once, round after round. No answer may be a 500 (a deadlock victim, error 1205, surfaces as one), and every
/// write must end in a success, a 409 or a 400 the caller can act on.
/// <para>
/// Rounds default to 25; set <c>T215_WORKLOAD_ROUNDS</c> for a longer soak.
/// </para>
/// </summary>
public sealed class ContentLockWorkloadTests(SqlServerDatabaseFixture sqlServer)
{
    private const string CardsUrl = "/api/admin/content/mega-menu/cards";
    private const string HomeUrl = "/api/admin/content/home";
    private const string SizeGuidesUrl = "/api/admin/content/size-guides";
    private const string CampaignsUrl = "/api/admin/marketing/sale-campaigns";
    private const string DepartmentsUrl = "/api/admin/catalog/departments";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] Everything =
    [
        Permissions.Content.View,
        Permissions.Content.Create,
        Permissions.Content.Edit,
        Permissions.Content.Delete,
        Permissions.Media.Create,
        Permissions.Media.View,
        Permissions.Media.Delete,
        Permissions.Promotions.View,
        Permissions.Promotions.Create,
        Permissions.Promotions.Edit,
        Permissions.Promotions.Delete,
        Permissions.Categories.View,
        Permissions.Categories.Edit,
    ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_content_writer_at_once_never_deadlocks_or_fails()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);
        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var client = await host.SignInAsync(Everything);

        var rounds = int.TryParse(Environment.GetEnvironmentVariable("T215_WORKLOAD_ROUNDS"), CultureInfo.InvariantCulture, out var asked)
            ? asked
            : 25;

        var a = await UploadImageAsync(client);
        var b = await UploadImageAsync(client);
        var (aId, bId) = (await AssetIdAsync(host, a), await AssetIdAsync(host, b));
        var kids = await DepartmentIdAsync(host, "kids");
        var men = await DepartmentIdAsync(host, "men");
        var shirts = await ShirtsAsync(host);

        var original = await DepartmentRowAsync(host, kids);
        int cardId = 0, itemId = 0, guideId = 0, campaignId = 0, pinnedCardId = 0;
        var orphaned = new ConcurrentBag<string>();
        var failures = new ConcurrentBag<string>();
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
            // ---- arrange one of each owner
            using (var created = await SendAsync(client, "POST", CardsUrl, CardBody(kids, a, null)))
            {
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                cardId = (await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("id").GetInt32();
            }

            var social = await SocialSectionAsync(host);
            using (var created = await SendAsync(client, "POST", $"{HomeUrl}/sections/{social}/items", ItemBody(a, await SectionVersionAsync(host, social))))
            {
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                itemId = (await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("id").GetInt32();
            }

            using (var created = await SendAsync(client, "POST", SizeGuidesUrl, GuideBody(a, null)))
            {
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                guideId = (await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("id").GetInt32();
            }

            using (var created = await SendAsync(client, "POST", CampaignsUrl, CampaignBody(a, null, shirts)))
            {
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                campaignId = (await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("campaign").GetProperty("id").GetInt32();
            }

            // A second card holds picture B for the whole run, so a delete of B must always be refused.
            using (var created = await SendAsync(client, "POST", CardsUrl, CardBody(men, b, null)))
            {
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                pinnedCardId = (await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("id").GetInt32();
            }

            // ---- the workload: every round each owner moves to a fresh picture C while the library deletes C
            // (either may win, never both), deletes the pinned B (always refused) and deletes an unused upload.
            for (var round = 0; round < rounds; round++)
            {
                var picture = await UploadImageAsync(client);
                var pictureId = await AssetIdAsync(host, picture);
                var spare = await UploadImageAsync(client);
                roundCodes.Clear();

                var card = Task.Run(async () =>
                {
                    var body = CardBody(round % 2 == 0 ? men : kids, picture, await VersionAsync(host, "card", cardId));
                    using var answer = await SendAsync(client, "PUT", $"{CardsUrl}/{cardId}", body);
                    Note("card", answer, await answer.Content.ReadAsStringAsync(Token));
                }, Token);

                var item = Task.Run(async () =>
                {
                    var body = ItemBody(picture, await SectionVersionAsync(host, social));
                    using var answer = await SendAsync(client, "PUT", $"{HomeUrl}/items/{itemId}", body);
                    Note("item", answer, await answer.Content.ReadAsStringAsync(Token));
                }, Token);

                var guide = Task.Run(async () =>
                {
                    using var answer = await SendAsync(client, "PUT", $"{SizeGuidesUrl}/{guideId}", GuideBody(picture, await VersionAsync(host, "guide", guideId)));
                    Note("guide", answer, await answer.Content.ReadAsStringAsync(Token));
                }, Token);

                var campaign = Task.Run(async () =>
                {
                    using var answer = await SendAsync(client, "PUT", $"{CampaignsUrl}/{campaignId}", CampaignBody(picture, await VersionAsync(host, "campaign", campaignId), shirts));
                    Note("campaign", answer, await answer.Content.ReadAsStringAsync(Token));
                }, Token);

                var department = Task.Run(async () =>
                {
                    var body = new Dictionary<string, object?>
                    {
                        ["description"] = original.Description,
                        ["heroImageAssetId"] = pictureId,
                        ["seoTitle"] = original.SeoTitle,
                        ["seoDescription"] = original.SeoDescription,
                        ["rowVersion"] = await VersionAsync(host, "department", kids),
                    };
                    using var answer = await SendAsync(client, "PUT", $"{DepartmentsUrl}/{kids}", body);
                    Note("department", answer, await answer.Content.ReadAsStringAsync(Token));
                }, Token);

                var refusedDelete = Task.Run(async () =>
                {
                    using var answer = await client.DeleteAsync(new Uri($"/api/admin/media/{b}", UriKind.Relative), Token);
                    Note("delete-in-use", answer, await answer.Content.ReadAsStringAsync(Token));
                }, Token);

                var contestedDelete = Task.Run(async () =>
                {
                    // Jittered so the saves win some rounds and the delete others.
                    await Task.Delay(round % 7 * 8, Token);
                    using var answer = await client.DeleteAsync(new Uri($"/api/admin/media/{picture}", UriKind.Relative), Token);
                    Note("delete-contested", answer, await answer.Content.ReadAsStringAsync(Token));
                }, Token);

                var freeDelete = Task.Run(async () =>
                {
                    using var answer = await client.DeleteAsync(new Uri($"/api/admin/media/{spare}", UriKind.Relative), Token);
                    Note("delete-free", answer, await answer.Content.ReadAsStringAsync(Token));
                }, Token);

                await Task.WhenAll(card, item, guide, campaign, department, refusedDelete, contestedDelete, freeDelete);

                // When the delete won, every content save that chose the picture was refused — not answered
                // 200 with nothing written.
                if (roundCodes.TryGetValue("delete-contested", out var deleteCode) && deleteCode == 204)
                {
                    foreach (var actor in new[] { "card", "item", "guide", "campaign", "department" })
                    {
                        if (roundCodes.TryGetValue(actor, out var saveCode) && saveCode is >= 200 and < 300)
                        {
                            orphaned.Add(string.Create(CultureInfo.InvariantCulture, $"round {round}: {actor} answered {saveCode} though its picture was deleted"));
                        }
                    }
                }

                // Whoever won, no owner may be left on a deleted picture — the department included since #223, which
                // gave the admin department edit its usage row and its lock.
                if (await DeletedAsync(host, pictureId))
                {
                    var holders = await WithDbAsync(host, async db => new[]
                    {
                        await db.Departments.CountAsync(row => row.HeroImageAssetId == pictureId, Token),
                        await db.MegaMenuPromoCards.CountAsync(row => row.ImageAssetId == pictureId, Token),
                        await db.HomeSectionItems.CountAsync(row => row.ImageAssetId == pictureId, Token),
                        await db.SizeGuides.CountAsync(row => row.ImageAssetId == pictureId, Token),
                        await db.SaleCampaigns.CountAsync(row => row.BannerAssetId == pictureId, Token),
                        await db.MediaUsages.CountAsync(row => row.MediaAssetId == pictureId, Token),
                    });

                    if (holders.Any(count => count > 0))
                    {
                        orphaned.Add(string.Create(CultureInfo.InvariantCulture, $"round {round}: department/card/item/guide/campaign/usages = {string.Join('/', holders)}"));
                    }
                }
            }

            var summary = string.Join(", ", statuses.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}×{pair.Value}"));
            TestContext.Current.SendDiagnosticMessage("T215 workload: " + summary);
            if (Environment.GetEnvironmentVariable("T215_WORKLOAD_LOG") is { Length: > 0 } log)
            {
                await File.AppendAllTextAsync(log, string.Create(CultureInfo.InvariantCulture, $"{rounds} rounds: {summary}{Environment.NewLine}"), Token);
            }
            Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures.Take(10)) + Environment.NewLine + summary);

            // The two pictures stayed in use, and therefore undeleted, throughout.
            Assert.True(orphaned.IsEmpty, string.Join(Environment.NewLine, orphaned) + Environment.NewLine + summary);
            Assert.False(await DeletedAsync(host, bId), "The pinned picture was deleted. " + summary);
            Assert.DoesNotContain(statuses.Keys, key => key.StartsWith("delete-in-use 2", StringComparison.Ordinal));
        }
        finally
        {
            await CleanUpAsync(host, cardId, itemId, guideId, campaignId, kids, original);
            await CleanUpAsync(host, pinnedCardId, 0, 0, 0, kids, original);
        }
    }

    // ================================================================== bodies

    private static Dictionary<string, object?> CardBody(int departmentId, Guid image, string? rowVersion) => new()
    {
        ["departmentId"] = departmentId,
        ["title"] = "Workload card",
        ["subtitle"] = null,
        ["buttonLabel"] = "Shop now",
        ["linkUrl"] = "/new-arrivals",
        ["imagePublicId"] = image,
        ["sortOrder"] = 60,
        ["isActive"] = false,
        ["startsAt"] = null,
        ["endsAt"] = null,
        ["rowVersion"] = rowVersion,
    };

    private static Dictionary<string, object?> ItemBody(Guid image, string? rowVersion) => new()
    {
        ["title"] = "Workload item",
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
        ["sortOrder"] = 95,
        ["isEnabled"] = false,
        ["startsAt"] = null,
        ["endsAt"] = null,
        ["rowVersion"] = rowVersion,
    };

    private static Dictionary<string, object?> GuideBody(Guid image, string? rowVersion) => new()
    {
        ["name"] = "Workload guide",
        ["introHtml"] = "<p>Measure over light clothing.</p>",
        ["tableJson"] = """{"columns":["Size","Chest (cm)"],"rows":[["S","86-91"]]}""",
        ["measuringTipsHtml"] = "<ul><li>Keep the tape level.</li></ul>",
        ["imagePublicId"] = image,
        ["rowVersion"] = rowVersion,
    };

    private static Dictionary<string, object?> CampaignBody(Guid banner, string? rowVersion, int categoryId) => new(StringComparer.Ordinal)
    {
        ["name"] = "Workload campaign",
        ["description"] = "Arranged by the #215 lock workload.",
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

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string url, object body)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(url, UriKind.Relative))
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        return await client.SendAsync(request, Token);
    }

    private static async Task<Guid> UploadImageAsync(HttpClient client)
    {
        using var form = MediaTestHost.FilePart("file", TestImages.UniqueJpeg(), "workload.jpg", "image/jpeg");
        form.Add(new StringContent("A workload picture"), "altText");

        using var response = await client.PostAsync(new Uri("/api/admin/media/images", UriKind.Relative), form, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("publicId").GetGuid();
    }

    private static async Task<T> WithDbAsync<T>(MediaTestHost host, Func<AppDbContext, Task<T>> read)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static Task<int> AssetIdAsync(MediaTestHost host, Guid publicId) =>
        WithDbAsync(host, db => db.MediaAssets.Where(asset => asset.PublicId == publicId).Select(asset => asset.Id).SingleAsync(Token));

    private static Task<bool> DeletedAsync(MediaTestHost host, int assetId) =>
        WithDbAsync(host, db => db.MediaAssets.IgnoreQueryFilters().Where(asset => asset.Id == assetId).Select(asset => asset.IsDeleted).SingleAsync(Token));

    private static Task<int> DepartmentIdAsync(MediaTestHost host, string slug) =>
        WithDbAsync(host, db => db.Departments.Where(department => department.Slug == slug).Select(department => department.Id).SingleAsync(Token));

    private static Task<int> ShirtsAsync(MediaTestHost host) =>
        WithDbAsync(host, db => db.Categories
            .Where(category => category.Slug == "shirts-blouses" && category.Department!.Slug == "women")
            .Select(category => category.Id)
            .SingleAsync(Token));

    private static Task<int> SocialSectionAsync(MediaTestHost host) =>
        WithDbAsync(host, db => db.HomeSections.Where(section => section.SectionType == SpencerDenim.Domain.Content.HomeSectionType.SocialGallery).Select(section => section.Id).SingleAsync(Token));

    private static Task<string?> SectionVersionAsync(MediaTestHost host, int sectionId) =>
        WithDbAsync(host, async db => Base64(await db.HomeSections.Where(section => section.Id == sectionId).Select(section => section.RowVersion).SingleAsync(Token)));

    private static Task<string?> VersionAsync(MediaTestHost host, string kind, int id) =>
        WithDbAsync(host, async db => Base64(kind switch
        {
            "card" => await db.MegaMenuPromoCards.Where(row => row.Id == id).Select(row => row.RowVersion).SingleAsync(Token),
            "guide" => await db.SizeGuides.Where(row => row.Id == id).Select(row => row.RowVersion).SingleAsync(Token),
            "campaign" => await db.SaleCampaigns.Where(row => row.Id == id).Select(row => row.RowVersion).SingleAsync(Token),
            "department" => await db.Departments.Where(row => row.Id == id).Select(row => row.RowVersion).SingleAsync(Token),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        }));

    private static string? Base64(byte[]? version) => version is null ? null : Convert.ToBase64String(version);

    private sealed record DepartmentRow(string? Description, int? HeroImageAssetId, string? SeoTitle, string? SeoDescription)
    {
        /// <summary>The department's own usage rows when the test began (#223 writes them), to put back afterwards.</summary>
        public List<(int AssetId, string Role)> Usages { get; init; } = [];
    }

    private static Task<DepartmentRow> DepartmentRowAsync(MediaTestHost host, int id) =>
        WithDbAsync(host, async db =>
        {
            var owner = id.ToString(CultureInfo.InvariantCulture);
            var usages = await db.MediaUsages.AsNoTracking()
                .Where(usage => usage.OwnerType == MediaUsageOwnerTypes.Department && usage.OwnerId == owner)
                .Select(usage => new { usage.MediaAssetId, usage.UsageRole })
                .ToListAsync(Token);

            return await db.Departments.AsNoTracking().Where(row => row.Id == id)
                .Select(row => new DepartmentRow(row.Description, row.HeroImageAssetId, row.SeoTitle, row.SeoDescription))
                .SingleAsync(Token) with
            {
                Usages = usages.Select(usage => (usage.MediaAssetId, usage.UsageRole)).ToList(),
            };
        });

    /// <summary>Leaves the shared database as found: owners and their usage rows removed, Kids' hero put back.</summary>
    private static async Task CleanUpAsync(MediaTestHost host, int cardId, int itemId, int guideId, int campaignId, int kids, DepartmentRow original)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        async Task Usages(string ownerType, int id)
        {
            var owner = id.ToString(CultureInfo.InvariantCulture);
            await db.MediaUsages.Where(usage => usage.OwnerType == ownerType && usage.OwnerId == owner).ExecuteDeleteAsync(Token);
        }

        await Usages(MediaUsageOwnerTypes.MegaMenuPromoCard, cardId);
        await db.MegaMenuPromoCards.Where(row => row.Id == cardId).ExecuteDeleteAsync(Token);
        await Usages(MediaUsageOwnerTypes.HomeSectionItem, itemId);
        await db.HomeSectionItems.Where(row => row.Id == itemId).ExecuteDeleteAsync(Token);
        await Usages(MediaUsageOwnerTypes.SizeGuide, guideId);
        await db.SizeGuides.Where(row => row.Id == guideId).ExecuteDeleteAsync(Token);

        if (campaignId != 0)
        {
            await Usages(MediaUsageOwnerTypes.SaleCampaign, campaignId);
            await db.Database.ExecuteSqlAsync($"DELETE FROM marketing.SaleCampaignTargets WHERE SaleCampaignId = {campaignId}", Token);
            await db.SaleCampaigns.Where(row => row.Id == campaignId).ExecuteDeleteAsync(Token);
        }

        // Since #223 the admin department edit rewrites its usage row with its hero, so both go back as they were.
        await Usages(MediaUsageOwnerTypes.Department, kids);
        await db.Departments.Where(row => row.Id == kids)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.HeroImageAssetId, original.HeroImageAssetId), Token);

        foreach (var (assetId, role) in original.Usages)
        {
            db.MediaUsages.Add(new MediaUsage
            {
                MediaAssetId = assetId,
                OwnerType = MediaUsageOwnerTypes.Department,
                OwnerId = kids.ToString(CultureInfo.InvariantCulture),
                UsageRole = role,
            });
        }

        await db.SaveChangesAsync(Token);
    }
}
