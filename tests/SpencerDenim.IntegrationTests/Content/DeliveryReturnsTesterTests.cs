using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Application.Settings;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Seeding;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Content;

/// <summary>
/// Issue #33, the independent tester's additions: the panel against the rate checkout places an order with, for
/// every zone and method; a disabled method and an unavailable rate; a battery of hostile markup through the editor
/// and written straight into the row; two editors saving on one token at once; couriers an admin renamed or
/// edited; and a policy page holding only whitespace.
/// <para>
/// Shared database for the endpoint tests, so every change is put back in a <c>finally</c>; a throwaway database
/// for the seeding tests.
/// </para>
/// </summary>
public sealed partial class DeliveryReturnsTesterTests(SqlServerDatabaseFixture sqlServer)
{
    private const string PanelUrl = "/api/content/delivery-returns";
    private const string EditorUrl = "/api/admin/settings/product-page-info";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Anything that can run script, load a frame or carry a script URL, anywhere in the markup.</summary>
    [GeneratedRegex(
        @"<\s*(script|svg|math|iframe|frame|object|embed|style|link|meta|base|form|input|button|textarea|select|template|noscript|xmp|noembed|video|audio|source)\b"
        + @"|\son[a-z]+\s*=|javascript\s*:|vbscript\s*:|data\s*:|srcdoc|formaction|xlink:href|style\s*=",
        RegexOptions.IgnoreCase)]
    private static partial Regex Dangerous();

    public static TheoryData<string> HostileMarkup => new()
    {
        "<p>a</p><script>alert(1)</script>",
        "<img src=x onerror=alert(1)>",
        "<a href=\"javascript:alert(1)\">x</a>",
        "<a href=\"JaVaScRiPt:alert(1)\">x</a>",
        "<a href=\" &#106;avascript:alert(1)\">x</a>",
        "<a href=\"java&#x09;script:alert(1)\">x</a>",
        "<a href=\"data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==\">x</a>",
        "<svg><script>alert(1)</script></svg>",
        "<svg onload=alert(1)>",
        "<math><mtext><table><mglyph><style><img src=x onerror=alert(1)>",
        "<noscript><p title=\"</noscript><img src=x onerror=alert(1)>\">",
        "<form><math><mtext></form><form><mglyph><style></math><img src onerror=alert(1)>",
        "<iframe srcdoc=\"<script>alert(1)</script>\"></iframe>",
        "<p style=\"background:url(javascript:alert(1))\">x</p>",
        "<details open ontoggle=alert(1)>",
        "<a href=\"https://ok.example\" onmouseover=\"alert(1)\">ok</a>",
        "<img src=\"https://x.example/a.png\" srcset=\"javascript:alert(1)\">",
        "<table background=\"javascript:alert(1)\"><tr><td>x</td></tr></table>",
        "<!--<img src=x onerror=alert(1)>-->",
        "<<script>script>alert(1)<</script>/script>",
    };

    // ---- fees: the panel against what checkout charges --------------------------------------------------

    [Fact]
    public async Task Every_panel_rate_equals_the_rate_order_placement_reads_for_every_zone_and_method()
    {
        await using var factory = await StartAsync();
        using var client = factory.CreateClient();

        var panel = await GetPanelAsync(client);
        await AssertParityAsync(factory, panel);
    }

    [Fact]
    public async Task A_disabled_method_and_an_unavailable_rate_leave_the_panel_as_checkout_leaves_its_options()
    {
        await using var factory = await StartAsync();
        using var client = factory.CreateClient();

        var (standardId, colombo) = await RateAsync(factory, DeliveryMethod.StandardCode, DeliveryZone.Colombo);

        try
        {
            // Express off: gone from the panel, as from checkout.
            await SetEnabledAsync(factory, "EXPRESS", false);
            var panel = await GetPanelAsync(client);
            Assert.DoesNotContain(panel.GetProperty("methods").EnumerateArray(), method => method.GetProperty("code").GetString() == "EXPRESS");
            await AssertParityAsync(factory, panel);

            // Standard unavailable in Colombo: Colombo is not quoted for Standard; the other zones still are.
            await SetAvailableAsync(factory, standardId, DeliveryZone.Colombo, false);
            panel = await GetPanelAsync(client);
            var standard = panel.GetProperty("methods").EnumerateArray().Single(method => method.GetProperty("code").GetString() == "STANDARD");
            Assert.DoesNotContain(standard.GetProperty("rates").EnumerateArray(), rate => rate.GetProperty("zone").GetString() == "Colombo");
            Assert.Contains(standard.GetProperty("rates").EnumerateArray(), rate => rate.GetProperty("zone").GetString() == "Western");
            await AssertParityAsync(factory, panel);

            // Nothing deliverable at all: no method, no estimate, and the endpoint still answers.
            await SetEnabledAsync(factory, "STANDARD", false);
            panel = await GetPanelAsync(client);
            Assert.Empty(panel.GetProperty("methods").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, panel.GetProperty("estimate").ValueKind);
        }
        finally
        {
            await SetEnabledAsync(factory, "EXPRESS", true);
            await SetEnabledAsync(factory, "STANDARD", true);
            await SetAvailableAsync(factory, standardId, DeliveryZone.Colombo, colombo.IsAvailable);
        }
    }

    // ---- hostile markup --------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(HostileMarkup))]
    public async Task Hostile_markup_saved_through_the_editor_is_stored_and_served_inert_and_reported(string hostile)
    {
        await using var factory = await StartAsync();
        using var editor = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);
        using var anonymous = factory.CreateClient();

        try
        {
            using var response = await editor.PutAsJsonAsync(
                new Uri(EditorUrl, UriKind.Relative),
                new { deliveryInfoHtml = hostile, rowVersion = await VersionAsync(factory) },
                JsonOptions,
                Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

            var stored = await ValueAsync(factory, SettingKeys.Content.PdpDeliveryInfoHtml) ?? string.Empty;
            Assert.DoesNotMatch(Dangerous(), stored);

            var served = (await GetPanelAsync(anonymous)).GetProperty("deliveryInfoHtml").GetString()!;
            Assert.DoesNotMatch(Dangerous(), served);

            // Something was taken out of every one of these, so the screen must be told.
            Assert.NotEmpty(body.GetProperty("removed").EnumerateArray());
        }
        finally
        {
            await ResetTextsAsync(factory);
        }
    }

    [Theory]
    [MemberData(nameof(HostileMarkup))]
    public async Task Hostile_markup_written_straight_into_the_row_is_served_inert(string hostile)
    {
        await using var factory = await StartAsync();
        using var anonymous = factory.CreateClient();

        try
        {
            await WriteDirectlyAsync(factory, SettingKeys.Content.PdpReturnsInfoHtml, hostile);

            var served = (await GetPanelAsync(anonymous)).GetProperty("returnsInfoHtml").GetString()!;
            Assert.DoesNotMatch(Dangerous(), served);
        }
        finally
        {
            await ResetTextsAsync(factory);
        }
    }

    [Fact]
    public async Task The_cap_is_on_the_cleaned_markup_so_entity_growth_is_refused_and_stripped_bulk_is_accepted()
    {
        await using var factory = await StartAsync();
        using var editor = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            // 1,000 ampersands become 5,000 characters once encoded: over the cap after cleaning.
            var (grown, _) = await PutAsync(editor, new { deliveryInfoHtml = new string('&', 1_000), rowVersion = await VersionAsync(factory) });
            Assert.Equal(HttpStatusCode.BadRequest, grown);

            // 50,000 characters of script around a short sentence clean down to the sentence: accepted.
            var bulky = "<p>Short.</p><script>" + new string('x', 50_000) + "</script>";
            var (accepted, body) = await PutAsync(editor, new { deliveryInfoHtml = bulky, rowVersion = await VersionAsync(factory) });
            Assert.Equal(HttpStatusCode.OK, accepted);
            Assert.Equal("<p>Short.</p>", await ValueAsync(factory, SettingKeys.Content.PdpDeliveryInfoHtml));
            Assert.Contains("<script>", body.GetProperty("removed").EnumerateArray().Select(item => item.GetString()));

            // Exactly at the cap after cleaning: accepted.
            var exact = "<p>" + new string('a', SettingKeys.Content.PdpInfoMaxLength - 7) + "</p>";
            Assert.Equal(HttpStatusCode.OK, (await PutAsync(editor, new { deliveryInfoHtml = exact, rowVersion = await VersionAsync(factory) })).Status);
        }
        finally
        {
            await ResetTextsAsync(factory);
        }
    }

    /// <summary>
    /// A paste whose clean form is what is already stored changes nothing, but the endpoint must still say what it
    /// took out — otherwise the admin believes the script they pasted is live. Kills the mutant that answers the
    /// unchanged case with an empty list.
    /// </summary>
    [Fact]
    public async Task An_unchanged_save_still_reports_what_was_removed_over_http()
    {
        await using var factory = await StartAsync();
        using var editor = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            Assert.Equal(HttpStatusCode.OK, (await PutAsync(editor, new { deliveryInfoHtml = "<p>Same.</p>", rowVersion = await VersionAsync(factory) })).Status);

            var (status, body) = await PutAsync(editor, new
            {
                deliveryInfoHtml = "<p>Same.</p><script>steal()</script>",
                rowVersion = await VersionAsync(factory),
            });

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("<script>", body.GetProperty("removed").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal("<p>Same.</p>", await ValueAsync(factory, SettingKeys.Content.PdpDeliveryInfoHtml));
        }
        finally
        {
            await ResetTextsAsync(factory);
        }
    }

    // ---- two editors at once -----------------------------------------------------------------------------

    [Fact]
    public async Task Two_editors_saving_on_one_token_at_once_get_one_success_and_one_conflict()
    {
        await using var factory = await StartAsync();
        using var first = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);
        using var second = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            for (var round = 0; round < 5; round++)
            {
                var token = await VersionAsync(factory);
                var a = PutAsync(first, new { deliveryInfoHtml = $"<p>First {round}.</p>", rowVersion = token });
                var b = PutAsync(second, new { deliveryInfoHtml = $"<p>Second {round}.</p>", rowVersion = token });
                var results = await Task.WhenAll(a, b);

                var statuses = results.Select(result => result.Status).OrderBy(status => (int)status).ToList();
                Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], statuses);

                var winner = results[0].Status == HttpStatusCode.OK ? $"<p>First {round}.</p>" : $"<p>Second {round}.</p>";
                Assert.Equal(winner, await ValueAsync(factory, SettingKeys.Content.PdpDeliveryInfoHtml));
            }
        }
        finally
        {
            await ResetTextsAsync(factory);
        }
    }

    // ---- seeding -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_courier_an_admin_renamed_edited_or_switched_off_is_never_re_created_or_overwritten()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await new SalesReferenceDataSeeder(db).SeedAsync(Token);

            await db.Couriers.Where(courier => courier.Name == "Domex")
                .ExecuteUpdateAsync(update => update.SetProperty(courier => courier.Name, "Domex Lanka"), Token);
            await db.Couriers.Where(courier => courier.Name == "Citypak")
                .ExecuteUpdateAsync(update => update
                    .SetProperty(courier => courier.TrackingUrlTemplate, "https://own.example/{trackingNumber}")
                    .SetProperty(courier => courier.Phone, "0110000000")
                    .SetProperty(courier => courier.IsActive, false), Token);
            await db.Couriers.Where(courier => courier.Name == "Pronto Lanka")
                .ExecuteUpdateAsync(update => update.SetProperty(courier => courier.IsActive, true), Token);
        }

        for (var run = 0; run < 3; run++)
        {
            await using var scope = deployment.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, (await new SalesReferenceDataSeeder(db).SeedAsync(Token)).Couriers);
        }

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var couriers = await db.Couriers.AsNoTracking().ToListAsync(Token);

            Assert.Equal(SalesReferenceData.Couriers.Count, couriers.Count);
            Assert.DoesNotContain(couriers, courier => courier.Name == "Domex");
            Assert.Contains(couriers, courier => courier.Name == "Domex Lanka");

            var citypak = couriers.Single(courier => courier.Name == "Citypak");
            Assert.Equal(("https://own.example/{trackingNumber}", "0110000000", false), (citypak.TrackingUrlTemplate, citypak.Phone, citypak.IsActive));
            Assert.True(couriers.Single(courier => courier.Name == "Pronto Lanka").IsActive);
        }
    }

    [Fact]
    public async Task Seeded_tracking_templates_are_exactly_the_research_documents_verified_patterns()
    {
        // docs/research/2026-10-21-sri-lanka-delivery-returns-couriers.md §3, re-checked live by the tester on
        // 2026-10-21 with the dummy number 12345678 (each page read the number back from the address).
        var expected = new Dictionary<string, string?>
        {
            ["Domex"] = "https://www.domex.lk/Order-Details.php?wbno={trackingNumber}",
            ["Koombiyo Delivery"] = "https://koombiyodelivery.lk/Track/track_id?id={trackingNumber}&phone=",
            ["Citypak"] = "https://track.citypak.lk/track?tracking_number={trackingNumber}",
            ["Pronto Lanka"] = null,
            ["Trans Express"] = null,
            ["Sri Lanka Post (Speed Post)"] = null,
            ["Sri Lanka Post EMS"] = null,
            ["DHL Express"] = null,
        };

        Assert.Equal(expected.Keys, SalesReferenceData.Couriers.Select(seed => seed.Name));
        foreach (var seed in SalesReferenceData.Couriers)
        {
            Assert.Equal(expected[seed.Name], seed.TrackingUrlTemplate);
            Assert.Equal(seed.TrackingUrlTemplate is not null, seed.IsActive);
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_policy_page_holding_only_whitespace_counts_as_empty_and_a_page_holding_one_empty_paragraph_does_not()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);

        // The shells exist, as on a pre-#33 database, with no policy marker.
        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await new HomeContentSeeder(db, new TestClock()).SeedAsync(Token);
            await db.Settings.Where(row => row.Key == SettingKeys.Seed.PolicyPagesVersion).ExecuteDeleteAsync(Token);
            await db.ContentPages.Where(page => page.SystemKey == ContentPageKeys.Shipping)
                .ExecuteUpdateAsync(update => update.SetProperty(page => page.BodyHtml, "  \n ").SetProperty(page => page.IsPublished, false), Token);
            await db.ContentPages.Where(page => page.SystemKey == ContentPageKeys.Returns)
                .ExecuteUpdateAsync(update => update.SetProperty(page => page.BodyHtml, "<p></p>").SetProperty(page => page.IsPublished, false), Token);
        }

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var counts = await new HomeContentSeeder(db, new TestClock()).SeedAsync(Token);
            Assert.Equal(1, counts.PolicyPages);
        }

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pages = await db.ContentPages.AsNoTracking()
                .Where(page => page.SystemKey == ContentPageKeys.Shipping || page.SystemKey == ContentPageKeys.Returns)
                .ToDictionaryAsync(page => page.SystemKey!, Token);

            Assert.Equal(PolicyPageContent.ShippingBody, pages[ContentPageKeys.Shipping].BodyHtml);
            Assert.True(pages[ContentPageKeys.Shipping].IsPublished);

            // An admin's own (empty-looking) markup is still theirs.
            Assert.Equal("<p></p>", pages[ContentPageKeys.Returns].BodyHtml);
            Assert.False(pages[ContentPageKeys.Returns].IsPublished);
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private async Task<ApiFactory> StartAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        return new ApiFactory(sqlServer.ConnectionString);
    }

    private static async Task AssertParityAsync(ApiFactory factory, JsonElement panel)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<ICheckoutReader>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var zones = (await reader.ActiveDistrictsAsync(Token)).Select(district => district.DeliveryZone).Distinct().ToList();
        Assert.Equal(
            zones.Order().Select(zone => zone.ToString()),
            panel.GetProperty("zones").EnumerateArray().Select(zone => zone.GetProperty("zone").GetString()));

        var methods = await db.DeliveryMethods.AsNoTracking().Where(method => method.RequiresAddress).ToListAsync(Token);
        var panelMethods = panel.GetProperty("methods").EnumerateArray().ToDictionary(method => method.GetProperty("code").GetString()!);

        foreach (var method in methods)
        {
            foreach (var zone in zones)
            {
                // What order placement reads (OrderPlacement → FindZoneRateAsync) and what checkout prices with.
                var rate = method.IsEnabled ? await reader.FindZoneRateAsync(method.Id, zone, Token) : null;
                var quoted = panelMethods.TryGetValue(method.Code, out var panelMethod)
                    ? panelMethod.GetProperty("rates").EnumerateArray().Where(candidate => candidate.GetProperty("zone").GetString() == zone.ToString()).ToList()
                    : [];

                if (rate is not { IsAvailable: true } available)
                {
                    Assert.Empty(quoted);
                    continue;
                }

                var only = Assert.Single(quoted);
                Assert.Equal(available.Fee, only.GetProperty("fee").GetDecimal());
                Assert.Equal(
                    available.FreeAboveAmount,
                    only.GetProperty("freeAboveAmount").ValueKind == JsonValueKind.Null ? null : only.GetProperty("freeAboveAmount").GetDecimal());
                Assert.Equal(available.EstimatedDaysMin, only.GetProperty("daysMin").GetInt32());
                Assert.Equal(available.EstimatedDaysMax, only.GetProperty("daysMax").GetInt32());
            }
        }
    }

    private static async Task<JsonElement> GetPanelAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri(PanelUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PutAsync(HttpClient client, object body)
    {
        using var response = await client.PutAsJsonAsync(new Uri(EditorUrl, UriKind.Relative), body, JsonOptions, Token);
        var payload = await response.Content.ReadAsStringAsync(Token);

        return (response.StatusCode, string.IsNullOrWhiteSpace(payload) ? default : JsonSerializer.Deserialize<JsonElement>(payload, JsonOptions));
    }

    private static async Task<string> VersionAsync(ApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var keys = SettingCatalog.KeysInSaveUnitFor(SettingKeys.Content.Group);

        return SettingsGroupVersion.For(await db.Settings.AsNoTracking().Where(row => keys.Contains(row.Key)).ToListAsync(Token));
    }

    private static async Task<string?> ValueAsync(ApiFactory factory, string key)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Settings.AsNoTracking().Where(row => row.Key == key).Select(row => row.Value).SingleAsync(Token);
    }

    private static async Task WriteDirectlyAsync(ApiFactory factory, string key, string? value)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Settings.Where(row => row.Key == key).ExecuteUpdateAsync(update => update.SetProperty(row => row.Value, value), Token);
    }

    private static async Task ResetTextsAsync(ApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var definition in ContentSettingDefinitions.All)
        {
            await db.Settings
                .Where(row => row.Key == definition.Key)
                .ExecuteUpdateAsync(
                    update => update
                        .SetProperty(row => row.Value, definition.SeedValue)
                        .SetProperty(row => row.UpdatedAtUtc, (DateTime?)null)
                        .SetProperty(row => row.UpdatedByUserId, (int?)null),
                    Token);
        }
    }

    private static async Task<(int MethodId, DeliveryMethodZoneRate Rate)> RateAsync(ApiFactory factory, string code, DeliveryZone zone)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var methodId = await db.DeliveryMethods.Where(method => method.Code == code).Select(method => method.Id).SingleAsync(Token);
        var rate = await db.DeliveryMethodZoneRates.AsNoTracking()
            .SingleAsync(row => row.DeliveryMethodId == methodId && row.DeliveryZone == zone, Token);

        return (methodId, rate);
    }

    private static async Task SetEnabledAsync(ApiFactory factory, string code, bool enabled)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.DeliveryMethods.Where(method => method.Code == code)
            .ExecuteUpdateAsync(update => update.SetProperty(method => method.IsEnabled, enabled), Token);
    }

    private static async Task SetAvailableAsync(ApiFactory factory, int methodId, DeliveryZone zone, bool available)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.DeliveryMethodZoneRates
            .Where(row => row.DeliveryMethodId == methodId && row.DeliveryZone == zone)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.IsAvailable, available), Token);
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
