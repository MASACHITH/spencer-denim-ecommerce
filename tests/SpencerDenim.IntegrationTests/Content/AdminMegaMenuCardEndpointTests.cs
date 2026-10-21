using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Contracts;
using SpencerDenim.Domain.Common;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.Tests.MediaFixtures;

namespace SpencerDenim.IntegrationTests.Content;

/// <summary>
/// The mega-menu card routes (#215, GLB-05, ADM-17) against the real API and SQL Server (#126: every route has an
/// endpoint test). Permissions per role and per action, antiforgery, the whole-form rule, validation, row
/// versions, audit rows, <c>MediaUsages</c> and the media library's delete refusal, the cache, the storefront's
/// departments feed (the fallback, the schedule, the order) and the limit per department.
/// <para>
/// The suite shares one database and runs one class at a time, so every test that adds a card removes it again,
/// and the seeded switched-off cards are left exactly as they were.
/// </para>
/// </summary>
public sealed class AdminMegaMenuCardEndpointTests(SqlServerDatabaseFixture sqlServer)
{
    private const string MenuUrl = "/api/admin/content/mega-menu";
    private const string CardsUrl = MenuUrl + "/cards";
    private const string DepartmentsUrl = "/api/catalog/departments";

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

    // ================================================================== permissions

    public static TheoryData<string, string> Routes => new()
    {
        { "GET", MenuUrl },
        { "POST", CardsUrl },
        { "PUT", CardsUrl + "/1" },
        { "DELETE", CardsUrl + "/1" },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task A_role_without_content_permissions_is_refused_everywhere(string method, string url)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Customers.View, Permissions.Media.Create);

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

    /// <summary>View reads; Create, Edit and Delete each need their own permission and do not stand in for each other.</summary>
    [Fact]
    public async Task Each_action_needs_its_own_permission()
    {
        await using var host = await StartAsync();
        using var viewer = await host.SignInAsync(Permissions.Content.View);
        using var creator = await host.SignInAsync(Permissions.Content.View, Permissions.Content.Create);
        using var editor = await host.SignInAsync(Permissions.Content.View, Permissions.Content.Edit);
        using var remover = await host.SignInAsync(Permissions.Content.View, Permissions.Content.Delete);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(new Uri(MenuUrl, UriKind.Relative), Token)).StatusCode);

        using (var response = await SendAsync(viewer, "POST", CardsUrl, new { }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var response = await SendAsync(editor, "POST", CardsUrl, new { }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var response = await SendAsync(creator, "PUT", CardsUrl + "/1", new { }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var response = await SendAsync(remover, "PUT", CardsUrl + "/1", new { }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var response = await SendAsync(editor, "DELETE", CardsUrl + "/1", null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // Each permission does open its own route (a 400 for the empty body, not a 403).
        using (var response = await SendAsync(creator, "POST", CardsUrl, new { }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using (var response = await SendAsync(editor, "PUT", CardsUrl + "/" + int.MaxValue, new { }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using (var response = await DeleteAsync(remover, CardsUrl + "/" + int.MaxValue, "AAAAAAAAAAE="))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task A_write_without_the_antiforgery_token_is_refused(string method)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);
        client.DefaultRequestHeaders.Remove(MediaTestHost.AntiforgeryHeader);

        using var response = await SendAsync(client, method, method == "POST" ? CardsUrl : CardsUrl + "/1", CardBody(1, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync(Token), StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================== a fresh shop: the fallback

    /// <summary>
    /// The seeder offers every department today's card, switched off; so the editor lists one card each, none of
    /// them on the menu, and the storefront's departments feed carries no cards at all — the menu keeps drawing
    /// the card it always has, from the department. A fresh database looks exactly as before.
    /// </summary>
    [Fact]
    public async Task A_seeded_shop_offers_todays_card_switched_off_and_the_storefront_keeps_its_fallback()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);
        using var visitor = host.CreateAnonymousClient();

        var menu = await GetJsonAsync(client, MenuUrl);
        Assert.Equal(MegaMenuPromoCardLimits.MaxCardsPerDepartment, menu.GetProperty("maxCardsPerDepartment").GetInt32());

        var departments = menu.GetProperty("departments").EnumerateArray().ToList();
        Assert.NotEmpty(departments);

        foreach (var department in departments)
        {
            var name = department.GetProperty("name").GetString();
            var card = Assert.Single(department.GetProperty("cards").EnumerateArray());
            Assert.Equal("New in " + name, card.GetProperty("title").GetString());
            Assert.Equal("Shop now", card.GetProperty("buttonLabel").GetString());
            Assert.Equal("/new-arrivals", card.GetProperty("linkUrl").GetString());
            Assert.False(card.GetProperty("isActive").GetBoolean());
            Assert.False(card.GetProperty("isShownNow").GetBoolean());
        }

        var feed = await GetJsonAsync(visitor, DepartmentsUrl);
        Assert.All(feed.EnumerateArray(), department => Assert.Equal(0, department.GetProperty("promoCards").GetArrayLength()));
    }

    // ================================================================== the whole life of a card

    /// <summary>
    /// Add a card: 201, on the storefront's very next request (the feed has no cache), audited, its picture held
    /// in <c>MediaUsages</c> so the library refuses to delete it. Change its picture: the old one is free again.
    /// A stale version is refused. Remove it: its usage row goes, the storefront falls back, and the picture can
    /// finally be deleted.
    /// </summary>
    [Fact]
    public async Task A_card_reaches_the_menu_at_once_holds_its_picture_and_lets_go_when_removed()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);
        using var visitor = host.CreateAnonymousClient();

        var women = await DepartmentIdAsync(host, "women");
        var first = await UploadImageAsync(client, "A model in a linen dress");
        var second = await UploadImageAsync(client, "Denim on a rail");
        var title = "Linen days " + Suffix();

        var body = CardBody(women, first, title: title);
        await GetJsonAsync(visitor, DepartmentsUrl);

        using var created = await SendAsync(client, "POST", CardsUrl, body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var saved = await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
        var cardId = saved.GetProperty("id").GetInt32();
        var version = saved.GetProperty("rowVersion").GetString();

        try
        {
            // The storefront: on the next request, with its alt text and responsive sources.
            var card = await StorefrontCardAsync(visitor, "women");
            Assert.NotNull(card);
            Assert.Equal(title, card.Value.GetProperty("title").GetString());
            Assert.Equal("A model in a linen dress", card.Value.GetProperty("imageAlt").GetString());
            Assert.Contains(first.ToString(), card.Value.GetProperty("imageUrl").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("w,", card.Value.GetProperty("imageSrcSet").GetString(), StringComparison.Ordinal);

            // The editor marks it as the one on the menu.
            var listed = await EditorCardAsync(client, cardId);
            Assert.True(listed.GetProperty("isShownNow").GetBoolean());
            Assert.True(listed.GetProperty("isLiveNow").GetBoolean());

            // MediaUsages, and the library's refusal naming the card.
            Assert.Equal([(first, MediaUsageRoles.Image)], await UsagesAsync(host, cardId));
            using (var inUse = await client.DeleteAsync(new Uri($"/api/admin/media/{first}", UriKind.Relative), Token))
            {
                Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
                var problem = await inUse.Content.ReadAsStringAsync(Token);
                Assert.Contains(MediaUsageOwnerTypes.MegaMenuPromoCard, problem, StringComparison.Ordinal);
                Assert.Contains(title, problem, StringComparison.Ordinal);
            }

            // A stale version is refused and changes nothing.
            var stale = CardBody(women, second, title: "Stale " + Suffix());
            stale["rowVersion"] = "AAAAAAAAAAE=";
            using (var refused = await SendAsync(client, "PUT", $"{CardsUrl}/{cardId}", stale))
            {
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                Assert.Contains("stale_row_version", await refused.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
            }

            // Change the picture: the usage row follows, the first picture is free again.
            var edit = CardBody(women, second, title: title);
            edit["rowVersion"] = version;
            using (var updated = await SendAsync(client, "PUT", $"{CardsUrl}/{cardId}", edit))
            {
                Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
                version = (await updated.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("rowVersion").GetString();
            }

            Assert.Equal([(second, MediaUsageRoles.Image)], await UsagesAsync(host, cardId));
            using (var freed = await client.DeleteAsync(new Uri($"/api/admin/media/{first}", UriKind.Relative), Token))
            {
                Assert.Equal(HttpStatusCode.NoContent, freed.StatusCode);
            }

            // The trail: a create and an update, field by field.
            var audit = await AuditAsync(host, cardId, title);
            Assert.Contains(audit, row => row.Action == "Update" && row.AfterJson!.Contains("imageAssetId", StringComparison.Ordinal));

            // Remove it with a stale If-Match: refused. With the current one: gone, with its usage rows.
            using (var staleDelete = await DeleteAsync(client, $"{CardsUrl}/{cardId}", "AAAAAAAAAAE="))
            {
                Assert.Equal(HttpStatusCode.Conflict, staleDelete.StatusCode);
            }

            using (var removed = await DeleteAsync(client, $"{CardsUrl}/{cardId}", version))
            {
                Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
            }

            cardId = 0;
            Assert.Null(await StorefrontCardAsync(visitor, "women"));
            Assert.Empty(await UsagesAsync(host, saved.GetProperty("id").GetInt32()));
            Assert.Contains(await AuditAsync(host, saved.GetProperty("id").GetInt32(), title), row => row.Action == "Delete");

            using (var freed = await client.DeleteAsync(new Uri($"/api/admin/media/{second}", UriKind.Relative), Token))
            {
                Assert.Equal(HttpStatusCode.NoContent, freed.StatusCode);
            }
        }
        finally
        {
            await RemoveCardAsync(host, cardId);
        }
    }

    [Fact]
    public async Task A_create_is_audited_and_an_untouched_resave_writes_no_second_row()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);

        var men = await DepartmentIdAsync(host, "men");
        var body = CardBody(men, null, title: "Quiet " + Suffix(), isActive: false);

        using var created = await SendAsync(client, "POST", CardsUrl, body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var saved = await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
        var cardId = saved.GetProperty("id").GetInt32();

        try
        {
            var title = (string)body["title"]!;
            var createdRow = Assert.Single(await AuditAsync(host, cardId, title));
            Assert.Equal("Create", createdRow.Action);

            body["rowVersion"] = saved.GetProperty("rowVersion").GetString();
            using var again = await SendAsync(client, "PUT", $"{CardsUrl}/{cardId}", body);
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Single(await AuditAsync(host, cardId, title));

            // Switched off and without a picture: it owns no usage row, and the storefront has nothing to draw.
            Assert.Empty(await UsagesAsync(host, cardId));
        }
        finally
        {
            await RemoveCardAsync(host, cardId);
        }
    }

    // ================================================================== validation

    [Fact]
    public async Task A_body_missing_a_key_is_refused_by_name()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);

        var body = CardBody(await DepartmentIdAsync(host, "women"), null, isActive: false);
        body.Remove("subtitle");

        using var response = await SendAsync(client, "POST", CardsUrl, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("subtitle", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    public static TheoryData<string, object?> BadValues => new()
    {
        { "title", null },
        { "title", new string('t', MegaMenuPromoCardLimits.TitleMaxLength + 1) },
        { "title", "Two\nlines" },
        { "subtitle", new string('s', MegaMenuPromoCardLimits.SubtitleMaxLength + 1) },
        { "buttonLabel", " " },
        { "buttonLabel", new string('b', ContentFieldLimits.ButtonLabelMaxLength + 1) },
        { "linkUrl", null },
        { "linkUrl", "javascript:alert(1)" },
        { "linkUrl", "//evil.example/x" },
        { "linkUrl", "/\\evil.example" },
        { "linkUrl", "https://user:pass@evil.example" },
        { "linkUrl", "/women dresses" },
        { "sortOrder", 1000 },
        { "departmentId", int.MaxValue },
        { "imagePublicId", "77777777-7777-7777-7777-777777777777" },
    };

    [Theory]
    [MemberData(nameof(BadValues))]
    public async Task A_bad_value_is_refused_on_its_field_and_nothing_is_written(string field, object? value)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);

        var before = await CardCountAsync(host);
        var body = CardBody(await DepartmentIdAsync(host, "women"), null, isActive: false);
        body[field] = value;

        using var response = await SendAsync(client, "POST", CardsUrl, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.Equal(before, await CardCountAsync(host));
    }

    [Fact]
    public async Task A_card_cannot_be_switched_on_without_a_picture_or_with_a_window_that_never_opens()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);
        var women = await DepartmentIdAsync(host, "women");

        using (var noPicture = await SendAsync(client, "POST", CardsUrl, CardBody(women, null, isActive: true)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noPicture.StatusCode);
            Assert.Contains("imagePublicId", await noPicture.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        }

        var backwards = CardBody(women, null, isActive: false);
        backwards["startsAt"] = "2030-01-02T10:00";
        backwards["endsAt"] = "2030-01-02T10:00";
        using (var never = await SendAsync(client, "POST", CardsUrl, backwards))
        {
            Assert.Equal(HttpStatusCode.BadRequest, never.StatusCode);
            Assert.Contains("endsAt", await never.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        }

        var zoned = CardBody(women, null, isActive: false);
        zoned["startsAt"] = "2030-01-02T10:00+05:30";
        using (var offset = await SendAsync(client, "POST", CardsUrl, zoned))
        {
            Assert.Equal(HttpStatusCode.BadRequest, offset.StatusCode);
        }
    }

    /// <summary>
    /// Text is stored as typed — markup included — because the storefront renders it by interpolation and never
    /// as HTML. The JSON the feed sends is the raw string; escaping it is the renderer's job (proved in the
    /// storefront's SSR e2e test).
    /// </summary>
    [Fact]
    public async Task Markup_in_the_text_is_stored_and_served_as_text()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);
        using var visitor = host.CreateAnonymousClient();

        var image = await UploadImageAsync(client, "Shirts");
        var hostile = "<script>alert(1)</script><img src=x onerror=alert(2)> " + Suffix();
        var body = CardBody(await DepartmentIdAsync(host, "kids"), image, title: hostile[..Math.Min(hostile.Length, 100)]);
        body["subtitle"] = "\"><svg onload=alert(3)>";

        using var created = await SendAsync(client, "POST", CardsUrl, body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var cardId = (await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("id").GetInt32();

        try
        {
            var card = await StorefrontCardAsync(visitor, "kids");
            Assert.NotNull(card);
            Assert.Equal(body["title"], card.Value.GetProperty("title").GetString());
            Assert.Equal("\"><svg onload=alert(3)>", card.Value.GetProperty("subtitle").GetString());
        }
        finally
        {
            await RemoveCardAsync(host, cardId);
        }
    }

    // ================================================================== the storefront's choice

    /// <summary>
    /// Only a card that is switched on, inside its window and carries a ready picture is drawn; of those, the
    /// lowest position first. Times are relative to now, so this cannot go stale on a calendar date.
    /// </summary>
    [Fact]
    public async Task The_feed_sends_only_live_cards_in_position_order()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);
        using var visitor = host.CreateAnonymousClient();

        var men = await DepartmentIdAsync(host, "men");
        var image = await UploadImageAsync(client, "A shirt");
        var colomboNow = ColomboTime.ToLocal(DateTimeOffset.UtcNow);

        var later = CardBody(men, image, title: "Second " + Suffix(), sortOrder: 20);
        var sooner = CardBody(men, image, title: "First " + Suffix(), sortOrder: 10);
        var future = CardBody(men, image, title: "Future " + Suffix(), sortOrder: 0);
        future["startsAt"] = WallClock(colomboNow.AddDays(1));
        var past = CardBody(men, image, title: "Past " + Suffix(), sortOrder: 1);
        past["startsAt"] = WallClock(colomboNow.AddDays(-2));
        past["endsAt"] = WallClock(colomboNow.AddDays(-1));
        var window = CardBody(men, image, title: "Window " + Suffix(), sortOrder: 15);
        window["startsAt"] = WallClock(colomboNow.AddHours(-1));
        window["endsAt"] = WallClock(colomboNow.AddHours(1));

        var ids = new List<int>();
        try
        {
            foreach (var body in new[] { later, sooner, future, past, window })
            {
                using var created = await SendAsync(client, "POST", CardsUrl, body);
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                ids.Add((await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("id").GetInt32());
            }

            var feed = await GetJsonAsync(visitor, DepartmentsUrl);
            var cards = feed.EnumerateArray().Single(department => department.GetProperty("slug").GetString() == "men")
                .GetProperty("promoCards").EnumerateArray()
                .Select(card => card.GetProperty("title").GetString())
                .ToList();

            Assert.Equal([sooner["title"], window["title"], later["title"]], cards.Cast<object?>().ToList());

            var shown = (await GetJsonAsync(client, MenuUrl)).GetProperty("departments").EnumerateArray()
                .Single(department => department.GetProperty("slug").GetString() == "men")
                .GetProperty("cards").EnumerateArray()
                .Where(card => card.GetProperty("isShownNow").GetBoolean())
                .Select(card => card.GetProperty("title").GetString())
                .ToList();
            Assert.Equal([(string?)sooner["title"]], shown);
        }
        finally
        {
            foreach (var id in ids)
            {
                await RemoveCardAsync(host, id);
            }
        }
    }

    /// <summary>
    /// A live card whose picture stops being showable (made private, or back to processing, behind the API's back)
    /// is not drawn: the menu falls back rather than drawing a broken image, and the editor stops calling it shown.
    /// </summary>
    [Theory]
    [InlineData("private")]
    [InlineData("processing")]
    public async Task A_card_whose_picture_cannot_be_shown_is_not_drawn(string flaw)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);
        using var visitor = host.CreateAnonymousClient();

        var image = await UploadImageAsync(client, "A dress");
        using var created = await SendAsync(client, "POST", CardsUrl, CardBody(await DepartmentIdAsync(host, "women"), image, title: "Picture " + Suffix()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var cardId = (await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("id").GetInt32();

        try
        {
            Assert.NotNull(await StorefrontCardAsync(visitor, "women"));

            await using (var scope = host.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var assets = db.MediaAssets.Where(asset => asset.PublicId == image);
                _ = flaw == "private"
                    ? await assets.ExecuteUpdateAsync(set => set.SetProperty(asset => asset.Visibility, MediaVisibility.Private), Token)
                    : await assets.ExecuteUpdateAsync(set => set.SetProperty(asset => asset.Status, MediaAssetStatus.Processing), Token);
            }

            Assert.Null(await StorefrontCardAsync(visitor, "women"));
            Assert.False((await EditorCardAsync(client, cardId)).GetProperty("isShownNow").GetBoolean());
        }
        finally
        {
            await RemoveCardAsync(host, cardId);
        }
    }

    [Fact]
    public async Task A_department_takes_up_to_the_limit_and_the_next_card_is_a_conflict()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);
        var kids = await DepartmentIdAsync(host, "kids");

        var existing = await CardCountAsync(host, kids);
        var ids = new List<int>();
        try
        {
            for (var index = existing; index < MegaMenuPromoCardLimits.MaxCardsPerDepartment; index++)
            {
                using var created = await SendAsync(client, "POST", CardsUrl, CardBody(kids, null, title: "Fill " + index, isActive: false));
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                ids.Add((await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("id").GetInt32());
            }

            using var full = await SendAsync(client, "POST", CardsUrl, CardBody(kids, null, title: "One too many", isActive: false));
            Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
            Assert.Contains("too_many_mega_menu_cards", await full.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        }
        finally
        {
            foreach (var id in ids)
            {
                await RemoveCardAsync(host, id);
            }
        }
    }

    // ================================================================== the cache

    /// <summary>
    /// Every write evicts the homepage cache (the ADM-17 rule), and the departments feed the menu reads has no
    /// cache to evict: a change shows on its next request, which the lifecycle test above proves.
    /// </summary>
    [Fact]
    public async Task Every_write_evicts_the_homepage_cache()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Everything);
        using var visitor = host.CreateAnonymousClient();
        var cache = host.Factory.Services.GetRequiredService<IHomePageCache>();
        var men = await DepartmentIdAsync(host, "men");

        await GetJsonAsync(visitor, "/api/content/home");
        Assert.NotNull(cache.GetPage());

        using var created = await SendAsync(client, "POST", CardsUrl, CardBody(men, null, title: "Evict " + Suffix(), isActive: false));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var saved = await created.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
        var cardId = saved.GetProperty("id").GetInt32();
        Assert.Null(cache.GetPage());

        try
        {
            await GetJsonAsync(visitor, "/api/content/home");
            Assert.NotNull(cache.GetPage());

            var edit = CardBody(men, null, title: "Evicted " + Suffix(), isActive: false);
            edit["rowVersion"] = saved.GetProperty("rowVersion").GetString();
            using var updated = await SendAsync(client, "PUT", $"{CardsUrl}/{cardId}", edit);
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            Assert.Null(cache.GetPage());

            await GetJsonAsync(visitor, "/api/content/home");
            Assert.NotNull(cache.GetPage());

            var version = (await updated.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("rowVersion").GetString();
            using var removed = await DeleteAsync(client, $"{CardsUrl}/{cardId}", version);
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
            Assert.Null(cache.GetPage());
            cardId = 0;
        }
        finally
        {
            await RemoveCardAsync(host, cardId);
        }
    }

    // ================================================================== helpers

    private async Task<MediaTestHost> StartAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        return MediaTestHost.Create(sqlServer.ConnectionString);
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static string WallClock(DateTimeOffset colombo) =>
        colombo.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    private static Dictionary<string, object?> CardBody(
        int departmentId,
        Guid? image,
        string? title = null,
        bool isActive = true,
        int sortOrder = 5) => new()
    {
        ["departmentId"] = departmentId,
        ["title"] = title ?? "New in " + Suffix(),
        ["subtitle"] = "Everyday essentials",
        ["buttonLabel"] = "Shop now",
        ["linkUrl"] = "/new-arrivals",
        ["imagePublicId"] = image,
        ["sortOrder"] = sortOrder,
        ["isActive"] = isActive,
        ["startsAt"] = null,
        ["endsAt"] = null,
    };

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

    private static async Task<JsonElement?> StorefrontCardAsync(HttpClient visitor, string slug)
    {
        var feed = await GetJsonAsync(visitor, DepartmentsUrl);
        var cards = feed.EnumerateArray().Single(department => department.GetProperty("slug").GetString() == slug)
            .GetProperty("promoCards");

        return cards.GetArrayLength() == 0 ? null : cards[0];
    }

    private static async Task<JsonElement> EditorCardAsync(HttpClient client, int cardId) =>
        (await GetJsonAsync(client, MenuUrl)).GetProperty("departments").EnumerateArray()
            .SelectMany(department => department.GetProperty("cards").EnumerateArray())
            .Single(card => card.GetProperty("id").GetInt32() == cardId);

    private static async Task<Guid> UploadImageAsync(HttpClient client, string altText)
    {
        using var form = MediaTestHost.FilePart("file", TestImages.UniqueJpeg(), "card.jpg", "image/jpeg");
        form.Add(new StringContent(altText), "altText");

        using var response = await client.PostAsync(new Uri("/api/admin/media/images", UriKind.Relative), form, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("publicId").GetGuid();
    }

    private static async Task<int> DepartmentIdAsync(MediaTestHost host, string slug)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Departments.Where(department => department.Slug == slug).Select(department => department.Id).SingleAsync(Token);
    }

    private static async Task<int> CardCountAsync(MediaTestHost host, int? departmentId = null)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.MegaMenuPromoCards.CountAsync(card => departmentId == null || card.DepartmentId == departmentId, Token);
    }

    private static async Task<List<(Guid PublicId, string Role)>> UsagesAsync(MediaTestHost host, int cardId)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = cardId.ToString(CultureInfo.InvariantCulture);

        var rows = await db.MediaUsages.AsNoTracking()
            .Where(usage => usage.OwnerType == MediaUsageOwnerTypes.MegaMenuPromoCard && usage.OwnerId == owner)
            .Select(usage => new { usage.MediaAsset!.PublicId, usage.UsageRole })
            .ToListAsync(Token);

        return rows.Select(row => (row.PublicId, row.UsageRole)).ToList();
    }

    /// <summary>
    /// The card's audit rows. A create is recorded before the row has an id (<c>"new"</c>, as every ADM-17 create
    /// is), so it is found through the card's unique title in its after-image.
    /// </summary>
    private static async Task<List<SpencerDenim.Domain.Auditing.AuditLog>> AuditAsync(MediaTestHost host, int cardId, string title)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = cardId.ToString(CultureInfo.InvariantCulture);

        return await db.AuditLogs.AsNoTracking()
            .Where(row => row.EntityType == "MegaMenuPromoCard"
                && (row.EntityId == id || (row.EntityId == "new" && row.AfterJson!.Contains(title))))
            .OrderBy(row => row.Id)
            .ToListAsync(Token);
    }

    /// <summary>Removes a card behind the API's back, with its usage rows, so the shared database is left as found.</summary>
    private static async Task RemoveCardAsync(MediaTestHost host, int cardId)
    {
        if (cardId == 0)
        {
            return;
        }

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = cardId.ToString(CultureInfo.InvariantCulture);

        await db.MediaUsages
            .Where(usage => usage.OwnerType == MediaUsageOwnerTypes.MegaMenuPromoCard && usage.OwnerId == owner)
            .ExecuteDeleteAsync(Token);
        await db.MegaMenuPromoCards.Where(card => card.Id == cardId).ExecuteDeleteAsync(Token);
    }
}
