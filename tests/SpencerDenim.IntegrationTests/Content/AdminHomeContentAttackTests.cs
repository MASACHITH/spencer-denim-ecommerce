using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.Tests.MediaFixtures;

namespace SpencerDenim.IntegrationTests.Content;

/// <summary>
/// Independent tester's additions for #192 (ADM-17) against the real API and SQL Server: the whole
/// permission and antiforgery matrix of the ten write routes, switches that must stay off, concurrent saves,
/// media that is gone or still in use, and hostile size-guide HTML. Every row a test creates it removes.
/// </summary>
public sealed class AdminHomeContentAttackTests(SqlServerDatabaseFixture sqlServer)
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
        Permissions.Media.Delete,
    ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The ten write routes and the one permission each must demand.</summary>
    private static readonly (string Method, string Url, string Permission)[] Writes =
    [
        ("PUT", HomeUrl + "/sections/10", Permissions.Content.Edit),
        ("POST", HomeUrl + "/sections/4/items", Permissions.Content.Create),
        ("PUT", HomeUrl + "/items/1", Permissions.Content.Edit),
        ("DELETE", HomeUrl + "/items/1", Permissions.Content.Delete),
        ("POST", AnnouncementsUrl, Permissions.Content.Create),
        ("PUT", AnnouncementsUrl + "/1", Permissions.Content.Edit),
        ("DELETE", AnnouncementsUrl + "/1", Permissions.Content.Delete),
        ("POST", SizeGuidesUrl, Permissions.Content.Create),
        ("PUT", SizeGuidesUrl + "/1", Permissions.Content.Edit),
        ("DELETE", SizeGuidesUrl + "/1", Permissions.Content.Delete),
    ];

    // ================================================================== permissions and antiforgery

    /// <summary>
    /// Every write route against View plus each single write permission: only its own lets it through, and
    /// none of the others stands in for it (the role matrix: Store Manager and Content Editor hold all four,
    /// Order Staff none).
    /// </summary>
    [Fact]
    public async Task Each_write_route_takes_exactly_its_own_permission()
    {
        await using var host = await StartAsync();

        var clients = new Dictionary<string, HttpClient>(StringComparer.Ordinal);
        foreach (var permission in new[] { Permissions.Content.Create, Permissions.Content.Edit, Permissions.Content.Delete })
        {
            clients[permission] = await host.SignInAsync(Permissions.Content.View, permission);
        }

        try
        {
            foreach (var (method, url, required) in Writes)
            {
                foreach (var (permission, client) in clients)
                {
                    using var response = await SendAsync(client, method, url, new { });

                    if (permission == required)
                    {
                        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
                    }
                    else
                    {
                        Assert.True(
                            response.StatusCode == HttpStatusCode.Forbidden,
                            $"{method} {url} answered {(int)response.StatusCode} to a caller holding only {permission}.");
                    }
                }
            }
        }
        finally
        {
            foreach (var client in clients.Values)
            {
                client.Dispose();
            }
        }
    }

    [Fact]
    public async Task Every_write_route_refuses_a_request_without_the_antiforgery_token()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);
        client.DefaultRequestHeaders.Remove(MediaTestHost.AntiforgeryHeader);

        foreach (var (method, url, _) in Writes)
        {
            using var response = await SendAsync(client, method, url, new { });

            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{method} {url} answered {(int)response.StatusCode}.");
            Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync(Token), StringComparison.OrdinalIgnoreCase);
        }
    }

    // ================================================================== switches stay where they were put

    /// <summary>
    /// <c>IsEnabled</c> and <c>IsActive</c> carry a database default of <c>true</c>. A row created switched
    /// off must be stored switched off — not silently take the column default.
    /// </summary>
    [Fact]
    public async Task An_item_and_an_announcement_created_switched_off_are_stored_switched_off()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);
        using var visitor = host.CreateAnonymousClient();

        var trending = await SectionAsync(client, "TrendingItems");
        var item = ItemBody(trending.GetProperty("rowVersion").GetString(), "Off on purpose " + Suffix());
        item["isEnabled"] = false;
        using var created = await SendAsync(client, "POST", $"{HomeUrl}/sections/{trending.GetProperty("id").GetInt32()}/items", item);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = (await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("id").GetInt32();

        var text = "Hidden offer " + Suffix();
        var announcement = AnnouncementBody(text);
        announcement["isActive"] = false;
        using var added = await SendAsync(client, "POST", AnnouncementsUrl, announcement);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var announcementId = (await added.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("id").GetInt32();

        try
        {
            await using var scope = host.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Assert.False((await db.HomeSectionItems.AsNoTracking().SingleAsync(row => row.Id == itemId, Token)).IsEnabled);
            Assert.False((await db.AnnouncementMessages.AsNoTracking().SingleAsync(row => row.Id == announcementId, Token)).IsActive);

            var strip = await GetJsonAsync(visitor, "/api/content/announcements");
            Assert.DoesNotContain(strip.EnumerateArray(), row => row.GetProperty("message").GetString() == text);
        }
        finally
        {
            await DeleteItemAsync(host, itemId);
            await DeleteAnnouncementAsync(host, announcementId);
        }
    }

    // ================================================================== concurrency

    /// <summary>
    /// The block is the aggregate: an item form opened before somebody edited the block is refused, and the
    /// fresh token works. Then two editors saving the same block at the same moment: exactly one wins, every
    /// round.
    /// </summary>
    [Fact]
    public async Task A_stale_block_version_refuses_an_item_and_two_simultaneous_saves_have_one_winner()
    {
        await using var host = await StartAsync();
        using var first = await host.SignInAsync(Everything);
        using var second = await host.SignInAsync(Everything);

        var trending = await SectionAsync(first, "TrendingItems");
        var sectionId = trending.GetProperty("id").GetInt32();
        var originalTitle = trending.GetProperty("title").GetString();
        var opened = trending.GetProperty("rowVersion").GetString();

        var createdIds = new List<int>();

        try
        {
            using (var edit = await SendAsync(first, "PUT", $"{HomeUrl}/sections/{sectionId}", SectionBody(trending, "Edited " + Suffix())))
            {
                Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
            }

            using (var staleItem = await SendAsync(second, "POST", $"{HomeUrl}/sections/{sectionId}/items", ItemBody(opened, "Late " + Suffix())))
            {
                Assert.Equal(HttpStatusCode.Conflict, staleItem.StatusCode);
                Assert.Contains("stale_row_version", await staleItem.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
            }

            for (var round = 0; round < 6; round++)
            {
                var current = await SectionAsync(first, "TrendingItems");

                var saves = await Task.WhenAll(
                    SendAsync(first, "PUT", $"{HomeUrl}/sections/{sectionId}", SectionBody(current, $"A{round} {Suffix()}")),
                    SendAsync(second, "PUT", $"{HomeUrl}/sections/{sectionId}", SectionBody(current, $"B{round} {Suffix()}")));

                Assert.Equal(1, saves.Count(response => response.StatusCode == HttpStatusCode.OK));
                Assert.Equal(1, saves.Count(response => response.StatusCode == HttpStatusCode.Conflict));
                foreach (var response in saves)
                {
                    response.Dispose();
                }

                // And two item adds against the same token: one lands, one is told to reload.
                var token = (await SectionAsync(first, "TrendingItems")).GetProperty("rowVersion").GetString();
                var adds = await Task.WhenAll(
                    SendAsync(first, "POST", $"{HomeUrl}/sections/{sectionId}/items", ItemBody(token, $"Race A{round}")),
                    SendAsync(second, "POST", $"{HomeUrl}/sections/{sectionId}/items", ItemBody(token, $"Race B{round}")));

                Assert.Equal(1, adds.Count(response => response.StatusCode == HttpStatusCode.Created));
                Assert.Equal(1, adds.Count(response => response.StatusCode == HttpStatusCode.Conflict));
                foreach (var response in adds)
                {
                    if (response.StatusCode == HttpStatusCode.Created)
                    {
                        createdIds.Add((await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("id").GetInt32());
                    }

                    response.Dispose();
                }
            }
        }
        finally
        {
            foreach (var id in createdIds)
            {
                await DeleteItemAsync(host, id);
            }

            await using var scope = host.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.HomeSections.Where(section => section.Id == sectionId)
                .ExecuteUpdateAsync(set => set.SetProperty(section => section.Title, originalTitle), Token);
        }
    }

    /// <summary>
    /// The announcement token is the last-updated time, checked in the handler and not in the UPDATE. Two
    /// editors saving the same message from the same version at the same moment must not both be told
    /// "saved" — one of the two edits would be lost without anybody hearing about it.
    /// <para>
    /// DEFECT (tester, #192): both saves were accepted in 9 of 10 rounds on LocalDB under RCSI, because each request reads
    /// the row before the other commits. Making <c>UpdatedAtUtc</c> an EF concurrency token
    /// (<c>IsConcurrencyToken()</c>, a model-only change, no migration) puts the predicate in the UPDATE.
    /// </para>
    /// </summary>
    [Fact(Skip = "Tester #192 defect: simultaneous announcement saves from one version are both accepted (lost update; 9 of 10 rounds on LocalDB). Remove Skip once fixed.")]
    public async Task Two_simultaneous_announcement_saves_from_one_version_have_one_winner()
    {
        await using var host = await StartAsync();
        using var first = await host.SignInAsync(Everything);
        using var second = await host.SignInAsync(Everything);

        var lostUpdates = 0;

        for (var round = 0; round < 10; round++)
        {
            using var created = await SendAsync(first, "POST", AnnouncementsUrl, AnnouncementBody("Race " + Suffix()));
            var saved = await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
            var id = saved.GetProperty("id").GetInt32();
            var version = saved.GetProperty("version").GetString();

            try
            {
                var a = AnnouncementBody("Editor A " + Suffix());
                a["version"] = version;
                var b = AnnouncementBody("Editor B " + Suffix());
                b["version"] = version;

                var saves = await Task.WhenAll(
                    SendAsync(first, "PUT", $"{AnnouncementsUrl}/{id}", a),
                    SendAsync(second, "PUT", $"{AnnouncementsUrl}/{id}", b));

                if (saves.All(response => response.StatusCode == HttpStatusCode.OK))
                {
                    lostUpdates++;
                }

                foreach (var response in saves)
                {
                    response.Dispose();
                }
            }
            finally
            {
                await DeleteAnnouncementAsync(host, id);
            }
        }

        Assert.Equal(0, lostUpdates);
    }

    // ================================================================== media

    /// <summary>
    /// A picture removed from the library cannot be attached; a picture in use on the homepage cannot be
    /// removed from the library; and an item's token must be its own block's, not another block's.
    /// </summary>
    [Fact]
    public async Task Deleted_media_is_refused_media_in_use_is_kept_and_another_blocks_token_is_refused()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);

        var gone = await UploadImageAsync(client);
        using (var deleted = await client.DeleteAsync(new Uri($"/api/admin/media/{gone}", UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        var trending = await SectionAsync(client, "TrendingItems");
        var sectionId = trending.GetProperty("id").GetInt32();
        var withGone = ItemBody(trending.GetProperty("rowVersion").GetString(), "Gone " + Suffix());
        withGone["imagePublicId"] = gone;
        using (var refused = await SendAsync(client, "POST", $"{HomeUrl}/sections/{sectionId}/items", withGone))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("imagePublicId", await refused.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        }

        var kept = await UploadImageAsync(client);
        var withKept = ItemBody(trending.GetProperty("rowVersion").GetString(), "Kept " + Suffix());
        withKept["imagePublicId"] = kept;
        using var created = await SendAsync(client, "POST", $"{HomeUrl}/sections/{sectionId}/items", withKept);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = (await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("id").GetInt32();

        try
        {
            using (var inUse = await client.DeleteAsync(new Uri($"/api/admin/media/{kept}", UriKind.Relative), Token))
            {
                Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
            }

            // The item belongs to the trending block; the social block's token does not open it.
            var social = await SectionAsync(client, "SocialGallery");
            var foreign = ItemBody(social.GetProperty("rowVersion").GetString(), "Hijack");
            using var hijack = await SendAsync(client, "PUT", $"{HomeUrl}/items/{itemId}", foreign);
            Assert.Equal(HttpStatusCode.Conflict, hijack.StatusCode);

            using var missing = await SendAsync(client, "PUT", $"{HomeUrl}/items/{int.MaxValue}", foreign);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            await DeleteItemAsync(host, itemId);
        }
    }

    // ================================================================== size-guide HTML and bounds

    public static TheoryData<string> HostileHtml => new()
    {
        "<img src=x alt=\"a\" onerror=\"alert(1)\">",
        "<a href=\"javascript:alert(1)\">x</a>",
        "<a href=\"jav&#x09;ascript:alert(1)\">x</a>",
        "<a href=\" javascript:alert(1)\">x</a>",
        "<svg><style><img src=x onerror=alert(1)></style></svg>",
        "<math><mtext><table><mglyph><style><img src=x onerror=alert(1)>",
        "<p style=\"background:url(javascript:alert(1))\">x</p>",
        "<iframe srcdoc=\"<script>alert(1)</script>\"></iframe>",
        "<noscript><p title=\"</noscript><img src=x onerror=alert(1)>\"></noscript>",
        "<form action=\"https://evil.example\"><button>Go</button></form>",
        "<a href=\"data:text/html,<script>alert(1)</script>\">x</a>",
    };

    [Theory]
    [MemberData(nameof(HostileHtml))]
    public async Task Hostile_size_guide_html_is_stored_harmless(string html)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);

        var body = SizeGuideBody("Hostile " + Suffix());
        body["introHtml"] = "<p>Measure.</p>" + html;
        body["measuringTipsHtml"] = html;

        using var created = await SendAsync(client, "POST", SizeGuidesUrl, body);

        if (created.StatusCode == HttpStatusCode.BadRequest)
        {
            return; // Refused outright is as good as cleaned.
        }

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("id").GetInt32();

        try
        {
            var detail = await GetJsonAsync(client, $"{SizeGuidesUrl}/{id}");
            foreach (var field in new[] { "introHtml", "measuringTipsHtml" })
            {
                var stored = detail.GetProperty(field).ValueKind == JsonValueKind.Null ? string.Empty : detail.GetProperty(field).GetString()!;

                foreach (var forbidden in new[] { "onerror", "javascript:", "<script", "<style", "<svg", "<math", "<iframe", "<form", "srcdoc", "data:text" })
                {
                    Assert.DoesNotContain(forbidden, stored, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        finally
        {
            await using var scope = host.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.SizeGuides.Where(guide => guide.Id == id).ExecuteDeleteAsync(Token);
        }
    }

    /// <summary>
    /// A table far past the byte cap, and a lone surrogate written as an escape, are both a 400 naming
    /// <c>tableJson</c> — never a 500 — and a deleted guide cannot be edited back to life.
    /// </summary>
    [Fact]
    public async Task An_oversized_or_broken_table_is_a_400_and_a_deleted_guide_is_not_found()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);

        var huge = SizeGuideBody("Huge " + Suffix());
        huge["tableJson"] = "{\"columns\":[\"Size\"],\"rows\":[[\"" + new string('x', 2 * 1024 * 1024) + "\"]]}";
        using (var tooBig = await SendAsync(client, "POST", SizeGuidesUrl, huge))
        {
            Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);
            Assert.Contains("tableJson", await tooBig.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(SizeGuidesUrl, UriKind.Relative)))
        {
            // Raw JSON so the escape reaches the server exactly as written.
            request.Content = new StringContent(
                """{"name":"Surrogate","introHtml":null,"tableJson":"{\"columns\":[\"Size\"],\"rows\":[[\"\\ud800\"]]}","measuringTipsHtml":null,"imagePublicId":null}""",
                System.Text.Encoding.UTF8,
                "application/json");
            using var surrogate = await client.SendAsync(request, Token);
            Assert.Equal(HttpStatusCode.BadRequest, surrogate.StatusCode);
        }

        using var created = await SendAsync(client, "POST", SizeGuidesUrl, SizeGuideBody("Doomed " + Suffix()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("id").GetInt32();
        var version = (await GetJsonAsync(client, $"{SizeGuidesUrl}/{id}")).GetProperty("rowVersion").GetString();

        try
        {
            using (var delete = new HttpRequestMessage(HttpMethod.Delete, new Uri($"{SizeGuidesUrl}/{id}", UriKind.Relative)))
            {
                delete.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
                using var removed = await client.SendAsync(delete, Token);
                Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
            }

            var revive = SizeGuideBody("Revived");
            revive["rowVersion"] = version;
            using var edited = await SendAsync(client, "PUT", $"{SizeGuidesUrl}/{id}", revive);
            Assert.Equal(HttpStatusCode.NotFound, edited.StatusCode);
        }
        finally
        {
            await using var scope = host.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.SizeGuides.IgnoreQueryFilters().Where(guide => guide.Id == id).ExecuteDeleteAsync(Token);
        }
    }

    // ================================================================== helpers

    private async Task<MediaTestHost> StartAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        return MediaTestHost.Create(sqlServer.ConnectionString);
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string url, object? body)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(url, UriKind.Relative));
        if (body is not null && method is not "GET" and not "DELETE")
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

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

    private static Dictionary<string, object?> SectionBody(JsonElement section, string title)
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
            ["title"] = title,
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

    private static Dictionary<string, object?> ItemBody(string? rowVersion, string title) => new()
    {
        ["title"] = title,
        ["subtitle"] = null,
        ["body"] = null,
        ["buttonLabel"] = null,
        ["linkUrl"] = null,
        ["imagePublicId"] = null,
        ["mobileImagePublicId"] = null,
        ["videoPublicId"] = null,
        ["departmentId"] = null,
        ["productSlug"] = null,
        ["iconKey"] = null,
        ["sortOrder"] = 77,
        ["isEnabled"] = false,
        ["startsAt"] = null,
        ["endsAt"] = null,
        ["rowVersion"] = rowVersion,
    };

    private static Dictionary<string, object?> AnnouncementBody(string message) => new()
    {
        ["message"] = message,
        ["linkUrl"] = null,
        ["sortOrder"] = 950,
        ["isActive"] = true,
        ["startsAt"] = null,
        ["endsAt"] = null,
    };

    private static Dictionary<string, object?> SizeGuideBody(string name) => new()
    {
        ["name"] = name,
        ["introHtml"] = null,
        ["tableJson"] = """{"columns":["Size","Chest (cm)"],"rows":[["S","86-91"]]}""",
        ["measuringTipsHtml"] = null,
        ["imagePublicId"] = null,
    };

    private static async Task<Guid> UploadImageAsync(HttpClient client)
    {
        using var form = MediaTestHost.FilePart("file", TestImages.UniqueJpeg(), "tile.jpg", "image/jpeg");
        form.Add(new StringContent("A folded linen shirt"), "altText");

        using var response = await client.PostAsync(new Uri("/api/admin/media/images", UriKind.Relative), form, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("publicId").GetGuid();
    }

    private static async Task DeleteItemAsync(MediaTestHost host, int itemId)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = itemId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        await db.MediaUsages.Where(usage => usage.OwnerType == MediaUsageOwnerTypes.HomeSectionItem && usage.OwnerId == owner).ExecuteDeleteAsync(Token);
        await db.HomeSectionItems.Where(item => item.Id == itemId).ExecuteDeleteAsync(Token);
    }

    private static async Task DeleteAnnouncementAsync(MediaTestHost host, int id)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.AnnouncementMessages.Where(message => message.Id == id).ExecuteDeleteAsync(Token);
    }
}
