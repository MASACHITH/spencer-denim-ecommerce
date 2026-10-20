using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Catalog;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Content;

/// <summary>
/// Tester pass for #215 (ADM-17 mega-menu cards): the selection rule at its exact boundaries with an injected
/// clock, ties, pictures that stop being drawable (soft-deleted, wrong type), and the API under concurrency
/// (two editors on one row version, parallel creates against the limit of six).
/// </summary>
public sealed class MegaMenuCardTesterTests(SqlServerDatabaseFixture sqlServer)
{
    private const string CardsUrl = "/api/admin/content/mega-menu/cards";

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
    ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ================================================================== the selection rule, exactly

    /// <summary>
    /// A window opens at its start (inclusive) and closes at its end (exclusive) to the millisecond, as the feed
    /// reads it: one millisecond either side flips the card.
    /// </summary>
    [Fact]
    public async Task A_card_appears_exactly_at_its_start_and_disappears_exactly_at_its_end()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);
        var boundary = new DateTime(2026, 11, 1, 18, 30, 0, 0, DateTimeKind.Utc);

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var picture = await AddImageAsync(db);
            var opening = await AddDepartmentAsync(db, "opening", 1);
            var closing = await AddDepartmentAsync(db, "closing", 2);

            db.MegaMenuPromoCards.Add(Card(opening, picture, "Opens", startsAt: boundary));
            db.MegaMenuPromoCards.Add(Card(closing, picture, "Closes", endsAt: boundary));
            await db.SaveChangesAsync(Token);
        }

        Assert.Equal((null, "Closes"), await DrawnAsync(deployment, boundary.AddMilliseconds(-1)));
        Assert.Equal(("Opens", null), await DrawnAsync(deployment, boundary));
    }

    /// <summary>Equal positions are broken by id, oldest first, in the feed and in the order it lists the rest.</summary>
    [Fact]
    public async Task Cards_at_the_same_position_are_drawn_oldest_first()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var picture = await AddImageAsync(db);
            var department = await AddDepartmentAsync(db, "ties", 1);

            foreach (var title in new[] { "A", "B", "C" })
            {
                db.MegaMenuPromoCards.Add(Card(department, picture, title, sortOrder: 4));
                await db.SaveChangesAsync(Token);
            }

            db.MegaMenuPromoCards.Add(Card(department, picture, "Lower", sortOrder: 5));
            await db.SaveChangesAsync(Token);
        }

        var feed = await FeedAsync(deployment, DateTime.UtcNow);
        Assert.Equal(["A", "B", "C", "Lower"], feed.Single().PromoCards.Select(card => card.Title).ToList());
    }

    /// <summary>
    /// A picture soft-deleted behind the card's back (the save/delete race the developer recorded ends here), or a
    /// row pointing at a video, is never drawn; the next card is.
    /// </summary>
    [Fact]
    public async Task A_card_whose_picture_was_deleted_or_is_a_video_is_skipped_for_the_next()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var good = await AddImageAsync(db);
            var deleted = await AddImageAsync(db);
            var video = await AddImageAsync(db, MediaAssetType.Video, posterAssetId: good);
            var department = await AddDepartmentAsync(db, "skips", 1);

            db.MegaMenuPromoCards.Add(Card(department, deleted, "Deleted", sortOrder: 0));
            db.MegaMenuPromoCards.Add(Card(department, video, "Video", sortOrder: 1));
            db.MegaMenuPromoCards.Add(Card(department, good, "Good", sortOrder: 2));
            await db.SaveChangesAsync(Token);

            var asset = await db.MediaAssets.SingleAsync(candidate => candidate.Id == deleted, Token);
            asset.SoftDelete(DateTime.UtcNow, null);
            await db.SaveChangesAsync(Token);
        }

        var feed = await FeedAsync(deployment, DateTime.UtcNow);
        Assert.Equal(["Good"], feed.Single().PromoCards.Select(card => card.Title).ToList());
    }

    // ================================================================== concurrency, through the API

    /// <summary>Two editors holding the same row version, saving at once: exactly one wins, the other is a 409.</summary>
    [Fact]
    public async Task Two_editors_saving_the_same_version_at_once_cannot_both_win()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);
        var men = await DepartmentIdAsync(host, "men");

        using var created = await SendAsync(client, "POST", CardsUrl, Body(men, "Race " + Suffix()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var saved = await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
        var cardId = saved.GetProperty("id").GetInt32();
        var version = saved.GetProperty("rowVersion").GetString();

        try
        {
            var edits = Enumerable.Range(0, 6).Select(index =>
            {
                var body = Body(men, $"Editor {index} {Suffix()}");
                body["rowVersion"] = version;
                return SendAsync(client, "PUT", $"{CardsUrl}/{cardId}", body);
            }).ToList();

            var answers = await Task.WhenAll(edits);
            var codes = answers.Select(answer => answer.StatusCode).ToList();
            foreach (var answer in answers)
            {
                answer.Dispose();
            }

            Assert.Equal(1, codes.Count(code => code == HttpStatusCode.OK));
            Assert.All(codes.Where(code => code != HttpStatusCode.OK), code => Assert.Equal(HttpStatusCode.Conflict, code));
        }
        finally
        {
            await RemoveCardAsync(host, cardId);
        }
    }

    /// <summary>
    /// The limit of six per department holds when creates arrive at once. The handler counts and then inserts with
    /// no lock, so under read-committed snapshot every parallel request counts the same number.
    /// </summary>
    [Fact]
    public async Task Parallel_creates_cannot_take_a_department_past_the_limit()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);
        var kids = await DepartmentIdAsync(host, "kids");
        var before = await CardIdsAsync(host, kids);

        try
        {
            var creates = Enumerable.Range(0, 20)
                .Select(index => SendAsync(client, "POST", CardsUrl, Body(kids, $"Burst {index} {Suffix()}")))
                .ToList();

            foreach (var answer in await Task.WhenAll(creates))
            {
                answer.Dispose();
            }

            var after = await CardIdsAsync(host, kids);
            Assert.True(
                after.Count <= MegaMenuPromoCardLimits.MaxCardsPerDepartment,
                string.Create(CultureInfo.InvariantCulture, $"Kids holds {after.Count} cards after a burst of creates."));
        }
        finally
        {
            foreach (var id in (await CardIdsAsync(host, kids)).Except(before))
            {
                await RemoveCardAsync(host, id);
            }
        }
    }

    /// <summary>
    /// A card save choosing a picture and a media-library delete of that picture, sent at the same moment, must not
    /// both succeed: either the delete is refused (the card holds the picture) or the save is refused (the picture
    /// is gone). Both succeeding leaves a card pointing at a deleted asset with a usage row on it — the gap the
    /// developer recorded, which #191's lock order (now on main) closes for campaigns.
    /// </summary>
    [Fact]
    public async Task A_card_save_racing_a_delete_of_its_picture_never_leaves_it_on_a_deleted_asset()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);
        var women = await DepartmentIdAsync(host, "women");
        var broken = 0;
        var cards = new List<int>();

        try
        {
            for (var round = 0; round < 15; round++)
            {
                using var form = MediaTestHost.FilePart("file", SpencerDenim.Tests.MediaFixtures.TestImages.UniqueJpeg(), "race.jpg", "image/jpeg");
                form.Add(new StringContent("A race picture"), "altText");
                using var uploaded = await client.PostAsync(new Uri("/api/admin/media/images", UriKind.Relative), form, Token);
                Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
                var image = (await uploaded.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("publicId").GetGuid();

                using var created = await SendAsync(client, "POST", CardsUrl, Body(women, "Race card " + Suffix()));
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                var saved = await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
                var cardId = saved.GetProperty("id").GetInt32();
                cards.Add(cardId);

                var edit = Body(women, "Race card " + Suffix());
                edit["imagePublicId"] = image;
                edit["rowVersion"] = saved.GetProperty("rowVersion").GetString();

                var save = SendAsync(client, "PUT", $"{CardsUrl}/{cardId}", edit);
                var delete = client.DeleteAsync(new Uri($"/api/admin/media/{image}", UriKind.Relative), Token);
                using var saveAnswer = await save;
                using var deleteAnswer = await delete;

                if (saveAnswer.StatusCode == HttpStatusCode.OK && deleteAnswer.IsSuccessStatusCode)
                {
                    broken++;
                }

                // The department's limit is six; each round leaves the shared database as it found it.
                await RemoveCardAsync(host, cardId);
                cards.Remove(cardId);
            }

            Assert.True(broken == 0, string.Create(CultureInfo.InvariantCulture, $"{broken} of 15 rounds left a card on a deleted picture."));
        }
        finally
        {
            foreach (var id in cards)
            {
                await RemoveCardAsync(host, id);
            }
        }
    }

    // ================================================================== helpers

    private static async Task<(string? Opening, string? Closing)> DrawnAsync(FreshDeployment deployment, DateTime utcNow)
    {
        var feed = await FeedAsync(deployment, utcNow);
        return (
            feed.Single(department => department.Slug == "opening").PromoCards is { Count: > 0 } o1 ? o1[0].Title : null,
            feed.Single(department => department.Slug == "closing").PromoCards is { Count: > 0 } o2 ? o2[0].Title : null);
    }

    private static async Task<IReadOnlyList<SpencerDenim.Application.Catalog.Contracts.DepartmentDto>> FeedAsync(
        FreshDeployment deployment,
        DateTime utcNow)
    {
        await using var scope = deployment.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reader = new DepartmentCatalogReader(db, Options.Create(new CatalogOptions()), new FixedClock(utcNow));
        return await reader.GetDepartmentsAsync(Token);
    }

    private static MegaMenuPromoCard Card(
        int departmentId,
        int imageAssetId,
        string title,
        short sortOrder = 0,
        DateTime? startsAt = null,
        DateTime? endsAt = null) => new()
    {
        DepartmentId = departmentId,
        Title = title,
        ButtonLabel = "Shop now",
        LinkUrl = "/new-arrivals",
        ImageAssetId = imageAssetId,
        SortOrder = sortOrder,
        IsActive = true,
        StartsAtUtc = startsAt,
        EndsAtUtc = endsAt,
    };

    private static async Task<int> AddDepartmentAsync(AppDbContext db, string slug, short sortOrder)
    {
        var department = new Department { Name = slug, Slug = slug, SortOrder = sortOrder, IsActive = true };
        db.Departments.Add(department);
        await db.SaveChangesAsync(Token);
        return department.Id;
    }

    private static async Task<int> AddImageAsync(
        AppDbContext db,
        MediaAssetType type = MediaAssetType.Image,
        int? posterAssetId = null)
    {
        var asset = new MediaAsset
        {
            MediaType = type,
            Visibility = MediaVisibility.Public,
            Status = MediaAssetStatus.Ready,
            OriginalFileName = type == MediaAssetType.Image ? "card.jpg" : "card.mp4",
            StorageKey = $"public/originals/{Guid.NewGuid():n}" + (type == MediaAssetType.Image ? ".jpg" : ".mp4"),
            ContentType = type == MediaAssetType.Image ? "image/jpeg" : "video/mp4",
            SizeBytes = 2048,
            Width = 1200,
            Height = 1500,
            RenditionSetVersion = 1,
            Checksum = [.. Guid.NewGuid().ToByteArray(), .. Guid.NewGuid().ToByteArray()],
            AltText = "A card picture",
            PosterAssetId = posterAssetId,
        };

        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync(Token);
        return asset.Id;
    }

    private async Task<MediaTestHost> StartAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);
        return MediaTestHost.Create(sqlServer.ConnectionString);
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static Dictionary<string, object?> Body(int departmentId, string title) => new()
    {
        ["departmentId"] = departmentId,
        ["title"] = title,
        ["subtitle"] = null,
        ["buttonLabel"] = "Shop now",
        ["linkUrl"] = "/new-arrivals",
        ["imagePublicId"] = null,
        ["sortOrder"] = 50,
        ["isActive"] = false,
        ["startsAt"] = null,
        ["endsAt"] = null,
    };

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string url, object body)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(url, UriKind.Relative))
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        return await client.SendAsync(request, Token);
    }

    private static async Task<int> DepartmentIdAsync(MediaTestHost host, string slug)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Departments.Where(department => department.Slug == slug).Select(department => department.Id).SingleAsync(Token);
    }

    private static async Task<List<int>> CardIdsAsync(MediaTestHost host, int departmentId)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.MegaMenuPromoCards.Where(card => card.DepartmentId == departmentId).Select(card => card.Id).ToListAsync(Token);
    }

    private static async Task RemoveCardAsync(MediaTestHost host, int cardId)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = cardId.ToString(CultureInfo.InvariantCulture);

        await db.MediaUsages
            .Where(usage => usage.OwnerType == MediaUsageOwnerTypes.MegaMenuPromoCard && usage.OwnerId == owner)
            .ExecuteDeleteAsync(Token);
        await db.MegaMenuPromoCards.Where(card => card.Id == cardId).ExecuteDeleteAsync(Token);
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTimeOffset UtcNow => new(utcNow, TimeSpan.Zero);
    }
}
