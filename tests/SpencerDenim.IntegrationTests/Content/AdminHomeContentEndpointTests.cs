using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.Tests.MediaFixtures;

namespace SpencerDenim.IntegrationTests.Content;

/// <summary>
/// The ADM-17 homepage, announcement and size-guide routes (#192) against the real API and SQL Server
/// (#126: every new route has an endpoint test, not only a stub). Permissions, the whole-form rule, audit
/// rows, MediaUsages, the cache eviction, and the schedule precision of #37 — each a property of the whole
/// write path that a handler test cannot prove.
/// </summary>
public sealed class AdminHomeContentEndpointTests(SqlServerDatabaseFixture sqlServer)
{
    private const string HomeUrl = "/api/admin/content/home";
    private const string AnnouncementsUrl = "/api/admin/content/announcements";
    private const string SizeGuidesUrl = "/api/admin/content/size-guides";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] Everything =
    [
        Permissions.Content.View,
        Permissions.Content.Create,
        Permissions.Content.Edit,
        Permissions.Content.Delete,
        Permissions.Media.Create,
        Permissions.Media.View,
    ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ================================================================== permissions

    public static TheoryData<string, string> Routes => new()
    {
        { "GET", HomeUrl },
        { "PUT", HomeUrl + "/sections/10" },
        { "POST", HomeUrl + "/sections/8/items" },
        { "PUT", HomeUrl + "/items/1" },
        { "DELETE", HomeUrl + "/items/1" },
        { "GET", AnnouncementsUrl },
        { "POST", AnnouncementsUrl },
        { "PUT", AnnouncementsUrl + "/1" },
        { "DELETE", AnnouncementsUrl + "/1" },
        { "GET", SizeGuidesUrl },
        { "GET", SizeGuidesUrl + "/1" },
        { "POST", SizeGuidesUrl },
        { "PUT", SizeGuidesUrl + "/1" },
        { "DELETE", SizeGuidesUrl + "/1" },
    };

    /// <summary>A signed-in staff member with no Content permission is refused on every route, before any body is read.</summary>
    [Theory]
    [MemberData(nameof(Routes))]
    public async Task A_role_without_content_permissions_is_refused_everywhere(string method, string url)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Customers.View);

        using var response = await SendAsync(client, method, url, new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task An_anonymous_caller_is_refused_everywhere(string method, string url)
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        using var response = await SendAsync(client, method, url, new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Each write needs its own permission: View alone reads, and Create, Edit and Delete do not stand in for each other.</summary>
    [Fact]
    public async Task Each_write_needs_its_own_permission()
    {
        await using var host = await StartAsync();
        using var viewer = await host.SignInAsync(Permissions.Content.View);
        using var editor = await host.SignInAsync(Permissions.Content.View, Permissions.Content.Edit);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(new Uri(HomeUrl, UriKind.Relative), Token)).StatusCode);

        using var viewerWrite = await SendAsync(viewer, "PUT", HomeUrl + "/sections/10", new { });
        Assert.Equal(HttpStatusCode.Forbidden, viewerWrite.StatusCode);

        using var editorCreate = await SendAsync(editor, "POST", AnnouncementsUrl, new { });
        Assert.Equal(HttpStatusCode.Forbidden, editorCreate.StatusCode);

        using var editorDelete = await SendAsync(editor, "DELETE", SizeGuidesUrl + "/1", null);
        Assert.Equal(HttpStatusCode.Forbidden, editorDelete.StatusCode);
    }

    [Fact]
    public async Task A_write_without_the_antiforgery_token_is_refused()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);
        client.DefaultRequestHeaders.Remove(MediaTestHost.AntiforgeryHeader);

        using var response = await SendAsync(client, "POST", AnnouncementsUrl, AnnouncementBody("Hello"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync(Token), StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================== homepage

    [Fact]
    public async Task The_editor_sees_all_ten_blocks_with_their_rules_and_a_fresh_shop_shows_none_of_the_artwork_blocks()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);
        using var visitor = host.CreateAnonymousClient();

        var home = await GetJsonAsync(client, HomeUrl);
        var sections = home.GetProperty("sections").EnumerateArray().ToList();

        Assert.Equal(10, sections.Count);
        Assert.Equal(9, home.GetProperty("benefitIcons").GetArrayLength());

        var trending = sections.Single(section => section.GetProperty("type").GetString() == "TrendingItems");
        Assert.Equal(12, trending.GetProperty("rules").GetProperty("maxItems").GetInt32());
        Assert.Equal(4, trending.GetProperty("items").GetArrayLength());
        Assert.All(trending.GetProperty("items").EnumerateArray(), item => Assert.False(item.GetProperty("isEnabled").GetBoolean()));

        // #63: the artwork rows exist and wait, switched off, so the storefront is exactly as it was.
        Cache(host).Evict();
        var storefront = await GetJsonAsync(visitor, "/api/content/home");
        var shown = storefront.GetProperty("sections").EnumerateArray().Select(section => section.GetProperty("type").GetString()).ToList();
        Assert.DoesNotContain("HeroVideo", shown);
        Assert.DoesNotContain("TrendingItems", shown);
        Assert.DoesNotContain("CampaignBanner", shown);
        Assert.DoesNotContain("ActivewearFeature", shown);
        Assert.DoesNotContain("SocialGallery", shown);
    }

    /// <summary>
    /// Saving a block is visible to the very next visitor: the cache was warm, the save evicted it. An audit
    /// row records the diff, and an untouched resave writes none.
    /// </summary>
    [Fact]
    public async Task A_block_save_is_audited_and_reaches_the_next_visitor_at_once()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);
        using var visitor = host.CreateAnonymousClient();

        var newsletter = await SectionAsync(client, "Newsletter");
        var originalTitle = newsletter.GetProperty("title").GetString();
        var marker = "Letters " + Guid.NewGuid().ToString("N")[..8];

        await GetJsonAsync(visitor, "/api/content/home"); // warm the cache

        try
        {
            using var saved = await SendAsync(client, "PUT", $"{HomeUrl}/sections/{newsletter.GetProperty("id").GetInt32()}", SectionBody(newsletter, title: marker));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

            var page = await GetJsonAsync(visitor, "/api/content/home");
            Assert.Contains(
                page.GetProperty("sections").EnumerateArray(),
                section => section.GetProperty("title").GetString() == marker);

            await using var scope = host.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var audit = await db.AuditLogs.AsNoTracking()
                .Where(row => row.EntityType == "HomeSection" && row.AfterJson!.Contains(marker))
                .ToListAsync(Token);
            Assert.Single(audit);

            // Untouched resave: 200, no second audit row.
            var current = await SectionAsync(client, "Newsletter");
            using var again = await SendAsync(client, "PUT", $"{HomeUrl}/sections/{current.GetProperty("id").GetInt32()}", SectionBody(current, title: marker));
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Single(await db.AuditLogs.AsNoTracking()
                .Where(row => row.EntityType == "HomeSection" && row.AfterJson!.Contains(marker))
                .ToListAsync(Token));
        }
        finally
        {
            var current = await SectionAsync(client, "Newsletter");
            using var restore = await SendAsync(client, "PUT", $"{HomeUrl}/sections/{current.GetProperty("id").GetInt32()}", SectionBody(current, title: originalTitle));
        }
    }

    [Fact]
    public async Task A_block_save_missing_a_key_or_with_a_stale_version_is_refused()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);

        var sale = await SectionAsync(client, "SaleSection");
        var id = sale.GetProperty("id").GetInt32();

        using var missing = await SendAsync(client, "PUT", $"{HomeUrl}/sections/{id}", new { title = "Sale", rowVersion = sale.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("subtitle", await missing.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);

        var body = SectionBody(sale);
        body["rowVersion"] = "AAAAAAAAAAE=";
        using var stale = await SendAsync(client, "PUT", $"{HomeUrl}/sections/{id}", body);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("stale_row_version", await stale.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);

        var offSite = SectionBody(sale);
        offSite["listingUrl"] = "https://evil.example/sale";
        using var badLink = await SendAsync(client, "PUT", $"{HomeUrl}/sections/{id}", offSite);
        Assert.Equal(HttpStatusCode.BadRequest, badLink.StatusCode);
        Assert.Contains("listingUrl", await badLink.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);

        var unused = SectionBody(sale);
        unused["overlay"] = "dark";
        using var unusedField = await SendAsync(client, "PUT", $"{HomeUrl}/sections/{id}", unused);
        Assert.Equal(HttpStatusCode.BadRequest, unusedField.StatusCode);
    }

    /// <summary>
    /// An item's picture is a media-library public id; the save records the usage row the delete guard
    /// reads, a removed picture's row goes, and a deleted item takes its rows with it.
    /// </summary>
    [Fact]
    public async Task An_items_pictures_are_recorded_in_media_usages_for_its_whole_life()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);

        var image = await UploadImageAsync(client);
        var social = await SectionAsync(client, "SocialGallery");
        var sectionId = social.GetProperty("id").GetInt32();

        var item = ItemBody(social.GetProperty("rowVersion").GetString(), title: "Instagram", linkUrl: "https://instagram.com/colombowardrobe");
        item["imagePublicId"] = image;
        item["sortOrder"] = 99;

        using var created = await SendAsync(client, "POST", $"{HomeUrl}/sections/{sectionId}/items", item);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = (await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("id").GetInt32();

        Assert.Equal([MediaUsageRoles.Image], await UsageRolesAsync(host, itemId));

        // A stale section version: the item write is guarded by the block's token.
        var stale = ItemBody("AAAAAAAAAAE=", title: "Instagram");
        stale["imagePublicId"] = image;
        using var staleSave = await SendAsync(client, "PUT", $"{HomeUrl}/items/{itemId}", stale);
        Assert.Equal(HttpStatusCode.Conflict, staleSave.StatusCode);

        // A dangerous link is refused by name.
        var hostile = ItemBody((await SectionAsync(client, "SocialGallery")).GetProperty("rowVersion").GetString(), title: "x", linkUrl: "javascript:alert(1)");
        hostile["imagePublicId"] = image;
        using var refused = await SendAsync(client, "PUT", $"{HomeUrl}/items/{itemId}", hostile);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("linkUrl", await refused.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);

        // The picture cannot be removed from a social tile (it is required), but it can be swapped.
        var other = await UploadImageAsync(client);
        var swap = ItemBody((await SectionAsync(client, "SocialGallery")).GetProperty("rowVersion").GetString(), title: "Instagram");
        swap["imagePublicId"] = other;
        swap["sortOrder"] = 99;
        using var swapped = await SendAsync(client, "PUT", $"{HomeUrl}/items/{itemId}", swap);
        Assert.Equal(HttpStatusCode.OK, swapped.StatusCode);
        Assert.Equal(other, await UsageAssetPublicIdAsync(host, itemId));

        var version = (await SectionAsync(client, "SocialGallery")).GetProperty("rowVersion").GetString();
        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, new Uri($"{HomeUrl}/items/{itemId}", UriKind.Relative));
        deleteRequest.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        using var deleted = await client.SendAsync(deleteRequest, Token);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Empty(await UsageRolesAsync(host, itemId));

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.AuditLogs.AnyAsync(row => row.EntityType == "HomeSectionItem" && row.EntityId == itemId.ToString(System.Globalization.CultureInfo.InvariantCulture) && row.Action == "Delete", Token));
    }

    [Fact]
    public async Task A_block_with_no_items_refuses_one_and_a_full_block_refuses_another()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);

        var sale = await SectionAsync(client, "SaleSection");
        using var notSupported = await SendAsync(client, "POST", $"{HomeUrl}/sections/{sale.GetProperty("id").GetInt32()}/items", ItemBody(sale.GetProperty("rowVersion").GetString(), title: "x"));
        Assert.Equal(HttpStatusCode.Conflict, notSupported.StatusCode);
        Assert.Contains("items_not_supported", await notSupported.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);

        var banner = await SectionAsync(client, "CampaignBanner");
        using var full = await SendAsync(client, "POST", $"{HomeUrl}/sections/{banner.GetProperty("id").GetInt32()}/items", ItemBody(banner.GetProperty("rowVersion").GetString(), title: "Second"));
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        Assert.Contains("section_full", await full.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    // ================================================================== #37 schedule precision

    /// <summary>
    /// Issue #37, end to end: a block whose window ends a few seconds into a warm cache entry is gone the
    /// moment it ends, and one whose window starts a few seconds in appears the moment it starts — not up to
    /// five minutes later. Before the fix the W2-01 tester measured ~250 s.
    /// <para>
    /// Relative to the real clock the API uses, never a calendar date. The allowance doc 08 §7.4 records is
    /// the time one request takes; the test allows two seconds.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_block_appears_and_disappears_on_its_schedule_through_a_warm_cache()
    {
        await using var host = await StartAsync();
        using var visitor = host.CreateAnonymousClient();

        var start = DateTime.UtcNow;
        var endsAt = start.AddSeconds(4);

        await SetNewsletterWindowAsync(host, startsAtUtc: null, endsAtUtc: endsAt);

        try
        {
            Assert.True(await ShowsNewsletterAsync(visitor), "The block was not shown inside its window.");

            // Warm, and still inside the window: served from the cache, and correct.
            Assert.True(await ShowsNewsletterAsync(visitor));

            await WaitUntilAsync(endsAt);
            Assert.True(
                await EventuallyAsync(async () => !await ShowsNewsletterAsync(visitor), TimeSpan.FromSeconds(2)),
                "The block outlived its end by more than the allowance.");

            var startsAt = DateTime.UtcNow.AddSeconds(4);
            await SetNewsletterWindowAsync(host, startsAtUtc: startsAt, endsAtUtc: null);

            Assert.False(await ShowsNewsletterAsync(visitor), "The block was shown before its start.");
            Assert.False(await ShowsNewsletterAsync(visitor));

            await WaitUntilAsync(startsAt);
            Assert.True(
                await EventuallyAsync(() => ShowsNewsletterAsync(visitor), TimeSpan.FromSeconds(2)),
                "The block appeared later than the allowance after its start.");
        }
        finally
        {
            await SetNewsletterWindowAsync(host, startsAtUtc: null, endsAtUtc: null);
        }
    }

    // ================================================================== announcements

    [Fact]
    public async Task An_announcement_goes_on_the_strip_at_once_and_comes_off_it_with_a_version_guard()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);
        using var visitor = host.CreateAnonymousClient();

        var text = "Weekend offer " + Guid.NewGuid().ToString("N")[..6];

        using var created = await SendAsync(client, "POST", AnnouncementsUrl, AnnouncementBody(text, "/sale"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var saved = await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
        var id = saved.GetProperty("id").GetInt32();

        var strip = await GetJsonAsync(visitor, "/api/content/announcements");
        Assert.Contains(strip.EnumerateArray(), row => row.GetProperty("message").GetString() == text);

        var listed = (await GetJsonAsync(client, AnnouncementsUrl)).EnumerateArray().Single(row => row.GetProperty("id").GetInt32() == id);
        Assert.Equal(saved.GetProperty("version").GetString(), listed.GetProperty("version").GetString());

        var stale = AnnouncementBody(text + "!", "/sale");
        stale["version"] = "1";
        using var staleSave = await SendAsync(client, "PUT", $"{AnnouncementsUrl}/{id}", stale);
        Assert.Equal(HttpStatusCode.Conflict, staleSave.StatusCode);

        using var tooLong = await SendAsync(client, "POST", AnnouncementsUrl, AnnouncementBody(new string('x', 201)));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, new Uri($"{AnnouncementsUrl}/{id}", UriKind.Relative));
        deleteRequest.Headers.TryAddWithoutValidation("If-Match", $"\"{listed.GetProperty("version").GetString()}\"");
        using var deleted = await client.SendAsync(deleteRequest, Token);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var after = await GetJsonAsync(visitor, "/api/content/announcements");
        Assert.DoesNotContain(after.EnumerateArray(), row => row.GetProperty("message").GetString() == text);
    }

    // ================================================================== size guides (#70)

    [Fact]
    public async Task A_size_guide_is_sanitised_bounded_and_cannot_be_removed_while_in_use()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);

        var body = new Dictionary<string, object?>
        {
            ["name"] = "IT guide " + Guid.NewGuid().ToString("N")[..6],
            ["introHtml"] = "<p onmouseover=\"x()\">Measure <script>alert(1)</script>over light clothing.</p>",
            ["tableJson"] = """{"columns":["Size","Chest (cm)"],"rows":[["S","86-91"],["M",91]]}""",
            ["measuringTipsHtml"] = "<ul><li>Keep the tape level.</li></ul><iframe src=\"https://evil.example\"></iframe>",
            ["imagePublicId"] = null,
        };

        using var created = await SendAsync(client, "POST", SizeGuidesUrl, body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var saved = await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
        var id = saved.GetProperty("id").GetInt32();
        Assert.NotEqual(0, saved.GetProperty("removedFromIntro").GetArrayLength());
        Assert.NotEqual(0, saved.GetProperty("removedFromTips").GetArrayLength());

        var detail = await GetJsonAsync(client, $"{SizeGuidesUrl}/{id}");
        Assert.DoesNotContain("script", detail.GetProperty("introHtml").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iframe", detail.GetProperty("measuringTipsHtml").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("91", detail.GetProperty("table").GetProperty("rows")[1][1].GetString());
        Assert.Equal(1000, detail.GetProperty("limits").GetProperty("maxCells").GetInt32());

        // Over the cell bound: 20 columns × 51 rows. Refused on tableJson, nothing stored.
        var header = string.Join(',', Enumerable.Range(1, 20).Select(index => $"\"C{index}\""));
        var row = "[" + string.Join(',', Enumerable.Repeat("\"1\"", 20)) + "]";
        var huge = new Dictionary<string, object?>(body)
        {
            ["tableJson"] = "{\"columns\":[" + header + "],\"rows\":[" + string.Join(',', Enumerable.Repeat(row, 51)) + "]}",
        };
        using var tooBig = await SendAsync(client, "POST", SizeGuidesUrl, huge);
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);
        Assert.Contains("tableJson", await tooBig.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);

        // In use by a category: refused with the counts.
        int categoryId;
        int? previousGuide;
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var category = await db.Categories.OrderBy(candidate => candidate.Id).FirstAsync(Token);
            categoryId = category.Id;
            previousGuide = category.SizeGuideId;
            category.SizeGuideId = id;
            await db.SaveChangesAsync(Token);
        }

        try
        {
            using var inUse = await DeleteAsync(client, $"{SizeGuidesUrl}/{id}", detail.GetProperty("rowVersion").GetString());
            Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
            var problem = await inUse.Content.ReadAsStringAsync(Token);
            Assert.Contains("size_guide_in_use", problem, StringComparison.Ordinal);
            Assert.Contains("categoryCount", problem, StringComparison.Ordinal);
        }
        finally
        {
            await using var scope = host.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var category = await db.Categories.SingleAsync(candidate => candidate.Id == categoryId, Token);
            category.SizeGuideId = previousGuide;
            await db.SaveChangesAsync(Token);
        }

        using var removed = await DeleteAsync(client, $"{SizeGuidesUrl}/{id}", detail.GetProperty("rowVersion").GetString());
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        using var gone = await client.GetAsync(new Uri($"{SizeGuidesUrl}/{id}", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    // ================================================================== helpers

    private async Task<MediaTestHost> StartAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        return MediaTestHost.Create(sqlServer.ConnectionString);
    }

    private static IHomePageCache Cache(MediaTestHost host) => host.Factory.Services.GetRequiredService<IHomePageCache>();

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string url, object? body)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(url, UriKind.Relative));
        if (body is not null && method is not "GET" and not "DELETE")
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        return await client.SendAsync(request, Token);
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, string url, string? version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(url, UriKind.Relative));
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return await client.SendAsync(request, Token);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
    }

    private static async Task<JsonElement> SectionAsync(HttpClient client, string type) =>
        (await GetJsonAsync(client, HomeUrl)).GetProperty("sections").EnumerateArray()
            .Single(section => section.GetProperty("type").GetString() == type);

    private static Dictionary<string, object?> SectionBody(JsonElement section, string? title = null)
    {
        static object? Read(JsonElement element, string name) => element.GetProperty(name).ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number => element.GetProperty(name).GetInt32(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => element.GetProperty(name).GetString(),
        };

        return new Dictionary<string, object?>
        {
            ["title"] = title ?? Read(section, "title"),
            ["subtitle"] = Read(section, "subtitle"),
            ["isEnabled"] = Read(section, "isEnabled"),
            ["sortOrder"] = Read(section, "sortOrder"),
            ["startsAt"] = Read(section, "startsAt"),
            ["endsAt"] = Read(section, "endsAt"),
            ["mode"] = Read(section, "mode"),
            ["count"] = Read(section, "count"),
            ["listingUrl"] = Read(section, "listingUrl"),
            ["linkLabel"] = Read(section, "linkLabel"),
            ["overlay"] = Read(section, "overlay"),
            ["rowVersion"] = Read(section, "rowVersion"),
        };
    }

    private static Dictionary<string, object?> ItemBody(string? rowVersion, string? title = null, string? linkUrl = null) => new()
    {
        ["title"] = title,
        ["subtitle"] = null,
        ["body"] = null,
        ["buttonLabel"] = null,
        ["linkUrl"] = linkUrl,
        ["imagePublicId"] = null,
        ["mobileImagePublicId"] = null,
        ["videoPublicId"] = null,
        ["departmentId"] = null,
        ["productSlug"] = null,
        ["iconKey"] = null,
        ["sortOrder"] = 50,
        ["isEnabled"] = false,
        ["startsAt"] = null,
        ["endsAt"] = null,
        ["rowVersion"] = rowVersion,
    };

    private static Dictionary<string, object?> AnnouncementBody(string message, string? linkUrl = null) => new()
    {
        ["message"] = message,
        ["linkUrl"] = linkUrl,
        ["sortOrder"] = 900,
        ["isActive"] = true,
        ["startsAt"] = null,
        ["endsAt"] = null,
    };

    private static async Task<Guid> UploadImageAsync(HttpClient client)
    {
        using var form = MediaTestHost.FilePart("file", TestImages.UniqueJpeg(), "tile.jpg", "image/jpeg");
        form.Add(new StringContent("A linen shirt on a hanger"), "altText");

        using var response = await client.PostAsync(new Uri("/api/admin/media/images", UriKind.Relative), form, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("publicId").GetGuid();
    }

    private static async Task<List<string>> UsageRolesAsync(MediaTestHost host, int itemId)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = itemId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return await db.MediaUsages.AsNoTracking()
            .Where(usage => usage.OwnerType == MediaUsageOwnerTypes.HomeSectionItem && usage.OwnerId == owner)
            .Select(usage => usage.UsageRole)
            .ToListAsync(Token);
    }

    private static async Task<Guid> UsageAssetPublicIdAsync(MediaTestHost host, int itemId)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = itemId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return await db.MediaUsages.AsNoTracking()
            .Where(usage => usage.OwnerType == MediaUsageOwnerTypes.HomeSectionItem && usage.OwnerId == owner)
            .Select(usage => usage.MediaAsset!.PublicId)
            .SingleAsync(Token);
    }

    /// <summary>Behind the API's back, as a scheduled change would be, then the cache dropped once.</summary>
    private static async Task SetNewsletterWindowAsync(MediaTestHost host, DateTime? startsAtUtc, DateTime? endsAtUtc)
    {
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.HomeSections
                .Where(section => section.SectionType == HomeSectionType.Newsletter)
                .ExecuteUpdateAsync(set => set.SetProperty(section => section.StartsAtUtc, startsAtUtc).SetProperty(section => section.EndsAtUtc, endsAtUtc), Token);
        }

        Cache(host).Evict();
    }

    private static async Task<bool> ShowsNewsletterAsync(HttpClient visitor)
    {
        var page = await GetJsonAsync(visitor, "/api/content/home");
        return page.GetProperty("sections").EnumerateArray().Any(section => section.GetProperty("type").GetString() == "Newsletter");
    }

    private static async Task WaitUntilAsync(DateTime utc)
    {
        var wait = utc - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, Token);
        }
    }

    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        do
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(100, Token);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }
}
