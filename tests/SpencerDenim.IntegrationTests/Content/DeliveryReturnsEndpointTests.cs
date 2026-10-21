using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Settings;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Content;

/// <summary>
/// Issue #33 end to end against SQL Server: the product page's delivery and returns panel
/// (<c>GET /api/content/delivery-returns</c>) and the admin editor for its two texts
/// (<c>PUT /api/admin/settings/product-page-info</c>).
/// <para>
/// <b>The assembly's database is shared</b>, so every test that changes a rate, a pickup row or a text puts it back
/// in a <c>finally</c>. The pickup test uses a location named with its own marker and removes it.
/// </para>
/// </summary>
public sealed class DeliveryReturnsEndpointTests(SqlServerDatabaseFixture sqlServer)
{
    private const string PanelUrl = "/api/content/delivery-returns";
    private const string EditorUrl = "/api/admin/settings/product-page-info";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- the panel ------------------------------------------------------------------------------------

    [Fact]
    public async Task The_panel_is_public_and_carries_the_seeded_rates_zones_and_texts()
    {
        await using var factory = await StartAsync();
        await ResetTextsAsync(factory);
        using var client = factory.CreateClient();

        var panel = await GetPanelAsync(client);

        Assert.Equal(
            ["Colombo", "Western", "IslandWide"],
            panel.GetProperty("zones").EnumerateArray().Select(zone => zone.GetProperty("zone").GetString()));
        Assert.Equal("Gampaha and Kalutara", panel.GetProperty("zones")[1].GetProperty("label").GetString());
        Assert.Equal("Rest of Sri Lanka", panel.GetProperty("zones")[2].GetProperty("label").GetString());

        var methods = panel.GetProperty("methods").EnumerateArray().ToList();
        Assert.Equal(["STANDARD", "EXPRESS"], methods.Select(method => method.GetProperty("code").GetString()));

        var standard = RateFor(methods[0], "Colombo");
        Assert.Equal(400m, standard.GetProperty("fee").GetDecimal());
        Assert.Equal(10_000m, standard.GetProperty("freeAboveAmount").GetDecimal());

        // Express is seeded unavailable island-wide, so it is not quoted there.
        Assert.DoesNotContain(
            methods[1].GetProperty("rates").EnumerateArray(),
            rate => rate.GetProperty("zone").GetString() == "IslandWide");

        Assert.Equal(1, panel.GetProperty("estimate").GetProperty("daysMin").GetInt32());
        Assert.Equal(5, panel.GetProperty("estimate").GetProperty("daysMax").GetInt32());

        // PICKUP is seeded disabled and no pickup location is seeded (doc 08 §9).
        Assert.Empty(panel.GetProperty("pickupPoints").EnumerateArray());

        Assert.Equal(ContentSettingDefinitions.DeliveryInfoSeed, panel.GetProperty("deliveryInfoHtml").GetString());
        Assert.Equal(ContentSettingDefinitions.ReturnsInfoSeed, panel.GetProperty("returnsInfoHtml").GetString());
    }

    [Fact]
    public async Task A_fee_changed_in_the_rate_table_shows_on_the_next_read()
    {
        await using var factory = await StartAsync();
        using var client = factory.CreateClient();

        var (methodId, before) = await StandardColomboAsync(factory);

        try
        {
            await SetStandardColomboAsync(factory, methodId, 425m, 12_500m, 2, 4);

            var rate = RateFor(
                (await GetPanelAsync(client)).GetProperty("methods").EnumerateArray().First(),
                "Colombo");

            Assert.Equal(425m, rate.GetProperty("fee").GetDecimal());
            Assert.Equal(12_500m, rate.GetProperty("freeAboveAmount").GetDecimal());
            Assert.Equal((2, 4), (rate.GetProperty("daysMin").GetInt32(), rate.GetProperty("daysMax").GetInt32()));
        }
        finally
        {
            await SetStandardColomboAsync(factory, methodId, before.Fee, before.FreeAboveAmount, before.EstimatedDaysMin, before.EstimatedDaysMax);
        }
    }

    [Fact]
    public async Task Pickup_appears_only_when_the_method_is_enabled_and_an_active_location_exists()
    {
        await using var factory = await StartAsync();
        using var client = factory.CreateClient();
        var marker = $"xunit-pickup-{Guid.NewGuid():N}"[..30];

        try
        {
            // Enabled, but no location: still none.
            await SetPickupEnabledAsync(factory, true);
            Assert.Empty((await GetPanelAsync(client)).GetProperty("pickupPoints").EnumerateArray());

            // A location: offered, priced by the pickup rate for its district's zone.
            var locationId = await AddLocationAsync(factory, marker);
            var point = Assert.Single(
                (await GetPanelAsync(client)).GetProperty("pickupPoints").EnumerateArray(),
                candidate => candidate.GetProperty("name").GetString() == marker);
            Assert.Equal(0m, point.GetProperty("fee").GetDecimal());
            Assert.Equal("Colombo 3", point.GetProperty("city").GetString());

            // Location switched off: gone.
            await SetLocationActiveAsync(factory, locationId, false);
            Assert.DoesNotContain(
                (await GetPanelAsync(client)).GetProperty("pickupPoints").EnumerateArray(),
                candidate => candidate.GetProperty("name").GetString() == marker);

            // Location on, method off: gone.
            await SetLocationActiveAsync(factory, locationId, true);
            await SetPickupEnabledAsync(factory, false);
            Assert.Empty((await GetPanelAsync(client)).GetProperty("pickupPoints").EnumerateArray());
        }
        finally
        {
            await SetPickupEnabledAsync(factory, false);
            await RemoveLocationAsync(factory, marker);
        }
    }

    [Fact]
    public async Task A_text_written_round_the_save_handler_is_still_sanitised_on_the_way_out()
    {
        await using var factory = await StartAsync();
        using var client = factory.CreateClient();

        try
        {
            await WriteTextDirectlyAsync(
                factory,
                SettingKeys.Content.PdpDeliveryInfoHtml,
                "<p>Quick<script>window.x=1</script></p><img src=x onerror=\"window.y=1\">");

            var html = (await GetPanelAsync(client)).GetProperty("deliveryInfoHtml").GetString()!;

            Assert.Contains("Quick", html, StringComparison.Ordinal);
            Assert.DoesNotContain("script", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("onerror", html, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await ResetTextsAsync(factory);
        }
    }

    // ---- the editor -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_editor_refuses_an_anonymous_caller()
    {
        await using var factory = await StartAsync();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        using var response = await client.PutAsJsonAsync(new Uri(EditorUrl, UriKind.Relative), new { deliveryInfoHtml = "<p>x</p>" }, JsonOptions, Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(DefaultRoleDefinitions.StoreManagerName, HttpStatusCode.OK)]
    [InlineData(DefaultRoleDefinitions.ContentEditorName, HttpStatusCode.Forbidden)]
    [InlineData(DefaultRoleDefinitions.OrderStaffName, HttpStatusCode.Forbidden)]
    [InlineData("Settings.Payments only", HttpStatusCode.Forbidden)]
    public async Task Each_role_gets_the_answer_the_role_matrix_gives_it(string role, HttpStatusCode expected)
    {
        await using var factory = await StartAsync();

        var permissions = role switch
        {
            DefaultRoleDefinitions.StoreManagerName => DefaultRoleDefinitions.StoreManager.Permissions,
            DefaultRoleDefinitions.ContentEditorName => DefaultRoleDefinitions.ContentEditor.Permissions,
            DefaultRoleDefinitions.OrderStaffName => DefaultRoleDefinitions.OrderStaff.Permissions,
            _ => (IReadOnlySet<string>)new HashSet<string> { Permissions.Settings.Payments },
        };

        using var client = await MediaTestHost.SignInAsync(factory, [.. permissions]);

        try
        {
            var (status, _) = await PutAsync(client, new { returnsInfoHtml = "<p>Role test.</p>", rowVersion = await VersionAsync(factory) });

            Assert.Equal(expected, status);
            Assert.Equal(
                expected == HttpStatusCode.OK ? "<p>Role test.</p>" : ContentSettingDefinitions.ReturnsInfoSeed,
                await ValueAsync(factory, SettingKeys.Content.PdpReturnsInfoHtml));
        }
        finally
        {
            await ResetTextsAsync(factory);
        }
    }

    [Fact]
    public async Task A_save_sanitises_reports_what_it_removed_audits_each_change_and_shows_on_the_panel()
    {
        await using var factory = await StartAsync();
        using var editor = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);
        using var anonymous = factory.CreateClient();

        try
        {
            var start = await AuditMaxIdAsync(factory);

            var (status, body) = await PutAsync(editor, new
            {
                deliveryInfoHtml = "<p onclick=\"steal()\">Two days.</p><script>alert(1)</script>",
                returnsInfoHtml = "<ul><li>Thirty days.</li></ul>",
                rowVersion = await VersionAsync(factory),
            });

            Assert.Equal(HttpStatusCode.OK, status);
            var removed = body.GetProperty("removed").EnumerateArray().Select(item => item.GetString()).ToList();
            Assert.Contains("<script>", removed);
            Assert.Contains("onclick", removed);

            Assert.Equal("<p>Two days.</p>", await ValueAsync(factory, SettingKeys.Content.PdpDeliveryInfoHtml));

            var audits = await AuditRowsAsync(factory, start);
            Assert.Equal(
                [SettingKeys.Content.PdpDeliveryInfoHtml, SettingKeys.Content.PdpReturnsInfoHtml],
                audits.Select(row => row.EntityId));
            Assert.All(audits, row => Assert.NotNull(row.UserId));
            Assert.DoesNotContain("script", audits[0].AfterJson!, StringComparison.OrdinalIgnoreCase);

            var panel = await GetPanelAsync(anonymous);
            Assert.Equal("<p>Two days.</p>", panel.GetProperty("deliveryInfoHtml").GetString());
            Assert.Equal("<ul><li>Thirty days.</li></ul>", panel.GetProperty("returnsInfoHtml").GetString());

            // The same again: 200, nothing removed, no audit row.
            var afterFirst = await AuditMaxIdAsync(factory);
            var (again, againBody) = await PutAsync(editor, new
            {
                deliveryInfoHtml = "<p>Two days.</p>",
                rowVersion = await VersionAsync(factory),
            });
            Assert.Equal(HttpStatusCode.OK, again);
            Assert.Empty(againBody.GetProperty("removed").EnumerateArray());
            Assert.Equal(afterFirst, await AuditMaxIdAsync(factory));
        }
        finally
        {
            await ResetTextsAsync(factory);
        }
    }

    [Fact]
    public async Task A_stale_token_is_a_409_and_an_oversized_text_is_a_400_on_its_field()
    {
        await using var factory = await StartAsync();
        using var editor = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            var stale = await VersionAsync(factory);
            Assert.Equal(HttpStatusCode.OK, (await PutAsync(editor, new { deliveryInfoHtml = "<p>First.</p>", rowVersion = stale })).Status);
            Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(editor, new { deliveryInfoHtml = "<p>Second.</p>", rowVersion = stale })).Status);
            Assert.Equal("<p>First.</p>", await ValueAsync(factory, SettingKeys.Content.PdpDeliveryInfoHtml));

            var (tooLong, problem) = await PutAsync(editor, new
            {
                returnsInfoHtml = "<p>" + new string('a', SettingKeys.Content.PdpInfoMaxLength) + "</p>",
                rowVersion = await VersionAsync(factory),
            });
            Assert.Equal(HttpStatusCode.BadRequest, tooLong);
            Assert.True(problem.GetProperty("errors").TryGetProperty("returnsInfoHtml", out _));

            var (number, numberProblem) = await PutAsync(editor, new { returnsInfoHtml = 12, rowVersion = await VersionAsync(factory) });
            Assert.Equal(HttpStatusCode.BadRequest, number);
            Assert.True(numberProblem.GetProperty("errors").TryGetProperty("returnsInfoHtml", out _));
        }
        finally
        {
            await ResetTextsAsync(factory);
        }
    }

    [Fact]
    public async Task The_editor_refuses_a_request_without_the_antiforgery_header()
    {
        await using var factory = await StartAsync();
        using var editor = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);
        editor.DefaultRequestHeaders.Remove(MediaTestHost.AntiforgeryHeader);

        var (status, problem) = await PutAsync(editor, new { deliveryInfoHtml = "<p>x</p>", rowVersion = await VersionAsync(factory) });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_antiforgery_token", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_settings_editor_sees_the_content_group_with_both_texts()
    {
        await using var factory = await StartAsync();
        using var editor = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        using var response = await editor.GetAsync(new Uri("/api/admin/settings", UriKind.Relative), Token);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        var group = body.GetProperty("groups").EnumerateArray().Single(candidate => candidate.GetProperty("groupKey").GetString() == "Content");
        Assert.Equal(
            [SettingKeys.Content.PdpDeliveryInfoHtml, SettingKeys.Content.PdpReturnsInfoHtml],
            group.GetProperty("settings").EnumerateArray().Select(setting => setting.GetProperty("key").GetString()));
        Assert.All(group.GetProperty("settings").EnumerateArray(), setting => Assert.Equal("TextHtml", setting.GetProperty("valueType").GetString()));
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private async Task<ApiFactory> StartAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        return new ApiFactory(sqlServer.ConnectionString);
    }

    private static async Task<JsonElement> GetPanelAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri(PanelUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
    }

    private static JsonElement RateFor(JsonElement method, string zone) =>
        method.GetProperty("rates").EnumerateArray().Single(rate => rate.GetProperty("zone").GetString() == zone);

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

    private static async Task WriteTextDirectlyAsync(ApiFactory factory, string key, string? value)
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

    private static async Task<(int MethodId, DeliveryMethodZoneRate Rate)> StandardColomboAsync(ApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var methodId = await db.DeliveryMethods.Where(method => method.Code == DeliveryMethod.StandardCode).Select(method => method.Id).SingleAsync(Token);
        var rate = await db.DeliveryMethodZoneRates.AsNoTracking()
            .SingleAsync(row => row.DeliveryMethodId == methodId && row.DeliveryZone == DeliveryZone.Colombo, Token);

        return (methodId, rate);
    }

    private static async Task SetStandardColomboAsync(ApiFactory factory, int methodId, decimal fee, decimal? freeAbove, byte min, byte max)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.DeliveryMethodZoneRates
            .Where(row => row.DeliveryMethodId == methodId && row.DeliveryZone == DeliveryZone.Colombo)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(row => row.Fee, fee)
                    .SetProperty(row => row.FreeAboveAmount, freeAbove)
                    .SetProperty(row => row.EstimatedDaysMin, min)
                    .SetProperty(row => row.EstimatedDaysMax, max),
                Token);
    }

    private static async Task SetPickupEnabledAsync(ApiFactory factory, bool enabled)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.DeliveryMethods
            .Where(method => method.Code == DeliveryMethod.PickupCode)
            .ExecuteUpdateAsync(update => update.SetProperty(method => method.IsEnabled, enabled), Token);
    }

    private static async Task<int> AddLocationAsync(ApiFactory factory, string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var colombo = await db.Districts.Where(district => district.DeliveryZone == DeliveryZone.Colombo).Select(district => district.Id).FirstAsync(Token);
        var location = new PickupLocation
        {
            Name = name,
            AddressLine = "1 Galle Road",
            City = "Colombo 3",
            DistrictId = colombo,
            IsActive = true,
        };

        db.PickupLocations.Add(location);
        await db.SaveChangesAsync(Token);

        return location.Id;
    }

    private static async Task SetLocationActiveAsync(ApiFactory factory, int id, bool active)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.PickupLocations.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.IsActive, active), Token);
    }

    private static async Task RemoveLocationAsync(ApiFactory factory, string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.PickupLocations.Where(row => row.Name == name).ExecuteDeleteAsync(Token);
    }

    private static async Task<long> AuditMaxIdAsync(ApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.AuditLogs.AsNoTracking().Where(log => log.EntityType == "Setting").MaxAsync(log => (long?)log.Id, Token) ?? 0;
    }

    private static async Task<List<SpencerDenim.Domain.Auditing.AuditLog>> AuditRowsAsync(ApiFactory factory, long afterId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.AuditLogs.AsNoTracking()
            .Where(log => log.EntityType == "Setting" && log.Id > afterId)
            .OrderBy(log => log.Id)
            .ToListAsync(Token);
    }
}
