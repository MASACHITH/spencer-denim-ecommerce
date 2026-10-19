using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Admin;
using SpencerDenim.Domain.Analytics;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Analytics;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Identity.Seeding;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Seeding;
using SpencerDenim.IntegrationTests.Infrastructure;
using static SpencerDenim.IntegrationTests.Analytics.TrafficRows;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// Issue #193's Traffic settings form, end to end against a real SQL Server: who may save it, the bounds, an
/// audit row per change, the seeding on a database that already exists, and — the point of the whole thing —
/// that the nightly purge and the rollup's catch-up actually use the saved value, with configuration only as the
/// fallback.
/// <para>
/// <b>Every test puts the two rows back to their seed values</b> in a <c>finally</c>: the assembly's database is
/// shared and its classes run one after another, so a period left at 30 would quietly change what a later
/// traffic test purges. The purge and rollup tests use 1990 and 1991, years no other test writes
/// (<see cref="SpencerDenim.IntegrationTests.Analytics.TrafficRows"/>' list), and purge "as of" those years so
/// the delete can only reach rows older than anything another test inserts.
/// </para>
/// </summary>
public sealed class AdminTrafficSettingsTests(SqlServerDatabaseFixture sqlServer)
{
    private const string SettingsUrl = "/api/admin/settings";
    private const string TrafficUrl = "/api/admin/settings/traffic";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- who may save it ------------------------------------------------------------------------------

    [Fact]
    public async Task The_route_refuses_an_anonymous_caller()
    {
        await using var factory = await StartAsync();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        using var response = await client.PutAsJsonAsync(
            new Uri(TrafficUrl, UriKind.Relative), new { rawRetentionDays = "60" }, JsonOptions, Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The default roles, by their own permission sets (doc 08 §9). Store Manager holds <c>Settings.Edit</c> and
    /// saves; Order Staff and Content Editor do not and are refused at the route; a caller holding only
    /// <c>Settings.Payments</c> — a payments clerk, not a shop manager — is refused too, because the group is
    /// <c>Settings.Edit</c> and not "any settings permission".
    /// </summary>
    [Theory]
    [InlineData(DefaultRoleDefinitions.StoreManagerName, HttpStatusCode.NoContent)]
    [InlineData(DefaultRoleDefinitions.OrderStaffName, HttpStatusCode.Forbidden)]
    [InlineData(DefaultRoleDefinitions.ContentEditorName, HttpStatusCode.Forbidden)]
    [InlineData("Settings.Payments only", HttpStatusCode.Forbidden)]
    public async Task Each_role_gets_the_answer_the_role_matrix_gives_it(string role, HttpStatusCode expected)
    {
        await using var factory = await StartAsync();

        var permissions = role switch
        {
            DefaultRoleDefinitions.StoreManagerName => DefaultRoleDefinitions.StoreManager.Permissions,
            DefaultRoleDefinitions.OrderStaffName => DefaultRoleDefinitions.OrderStaff.Permissions,
            DefaultRoleDefinitions.ContentEditorName => DefaultRoleDefinitions.ContentEditor.Permissions,
            _ => (IReadOnlySet<string>)new HashSet<string> { Permissions.Settings.Payments },
        };

        using var client = await MediaTestHost.SignInAsync(factory, [.. permissions]);

        try
        {
            // The token is read with a Store Manager's view when the caller may not read the group, so a 403 is
            // the route's answer and not a missing-token conflict.
            var version = await TrafficVersionAsync(factory);

            var (status, _) = await PutAsync(client, new { rawRetentionDays = "120", rowVersion = version });

            Assert.Equal(expected, status);
            Assert.Equal(expected == HttpStatusCode.NoContent ? "120" : "90", await ValueAsync(factory, SettingKeys.Traffic.RawRetentionDays));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    /// <summary>
    /// A Super Admin (the GrantsAll role) may save too. Driven through the handler rather than HTTP, because a
    /// GrantsAll sign-in needs a second factor; everything below the route — the real permissions provider, the
    /// real store, the real audit writer — is the production path.
    /// </summary>
    [Fact]
    public async Task A_super_admin_may_save()
    {
        await using var factory = await StartAsync();
        var userId = await CreateUserAsync(factory);

        try
        {
            await AddToSuperAdminAsync(factory, userId);

            await using var scope = factory.Services.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<UpdateTrafficSettingsCommand, SettingsWriteResult>>();

            var result = await handler.HandleAsync(
                new UpdateTrafficSettingsCommand(
                    new Dictionary<string, string?> { ["botRetentionDays"] = "21" },
                    await TrafficVersionAsync(factory),
                    new AdminActor(userId, "super@example.com", null, null, null)),
                Token);

            Assert.IsType<SettingsWriteResult.Succeeded>(result);
            Assert.Equal("21", await ValueAsync(factory, SettingKeys.Traffic.BotRetentionDays));
        }
        finally
        {
            await RemoveFromSuperAdminAsync(factory, userId);
            await RestoreAsync(factory);
        }
    }

    [Fact]
    public async Task A_settings_edit_holder_reads_the_traffic_group_and_a_payments_only_holder_does_not()
    {
        await using var factory = await StartAsync();

        using var editor = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);
        using var payments = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Payments);

        var editorGroups = await GroupKeysAsync(editor);
        var paymentsGroups = await GroupKeysAsync(payments);

        Assert.Contains(SettingKeys.Traffic.Group, editorGroups);
        Assert.DoesNotContain(SettingKeys.Traffic.Group, paymentsGroups);

        var rows = await SettingsAsync(editor);
        Assert.Equal("90", rows[SettingKeys.Traffic.RawRetentionDays].GetProperty("value").GetString());
        Assert.Equal("7", rows[SettingKeys.Traffic.BotRetentionDays].GetProperty("value").GetString());
        Assert.Equal("Int", rows[SettingKeys.Traffic.RawRetentionDays].GetProperty("valueType").GetString());
        Assert.Equal(Permissions.Settings.Edit, rows[SettingKeys.Traffic.RawRetentionDays].GetProperty("requiredPermission").GetString());
    }

    // ---- the bounds -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("rawRetentionDays", "29")]
    [InlineData("rawRetentionDays", "366")]
    [InlineData("rawRetentionDays", "")]
    [InlineData("rawRetentionDays", "ninety")]
    [InlineData("botRetentionDays", "6")]
    [InlineData("botRetentionDays", "91")]
    [InlineData("botRetentionDays", "7.5")]
    public async Task A_period_outside_its_bounds_is_a_400_on_that_field_and_nothing_is_written(string field, string value)
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            var auditBefore = await AuditMaxIdAsync(factory);
            var body = new Dictionary<string, string?>
            {
                [field] = value,
                ["rowVersion"] = await TrafficVersionAsync(factory),
            };

            var (status, problem) = await PutAsync(client, body);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
            Assert.Equal("90", await ValueAsync(factory, SettingKeys.Traffic.RawRetentionDays));
            Assert.Equal("7", await ValueAsync(factory, SettingKeys.Traffic.BotRetentionDays));
            Assert.Equal(auditBefore, await AuditMaxIdAsync(factory));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    [Theory]
    [InlineData("30", "7")]
    [InlineData("365", "90")]
    public async Task The_bounds_themselves_are_accepted(string raw, string bot)
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            var (status, _) = await PutAsync(client, new { rawRetentionDays = raw, botRetentionDays = bot, rowVersion = await TrafficVersionAsync(factory) });

            Assert.Equal(HttpStatusCode.NoContent, status);
            Assert.Equal(raw, await ValueAsync(factory, SettingKeys.Traffic.RawRetentionDays));
            Assert.Equal(bot, await ValueAsync(factory, SettingKeys.Traffic.BotRetentionDays));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    [Fact]
    public async Task Bots_kept_longer_than_people_is_a_400()
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            var (status, problem) = await PutAsync(
                client,
                new { rawRetentionDays = "40", botRetentionDays = "41", rowVersion = await TrafficVersionAsync(factory) });

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.True(problem.GetProperty("errors").TryGetProperty("botRetentionDays", out _));
            Assert.Equal("90", await ValueAsync(factory, SettingKeys.Traffic.RawRetentionDays));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    [Fact]
    public async Task A_json_number_is_a_named_field_error_rather_than_a_coercion()
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        var (status, problem) = await PutAsync(client, new { rawRetentionDays = 60, rowVersion = await TrafficVersionAsync(factory) });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(problem.GetProperty("errors").TryGetProperty("rawRetentionDays", out _));
    }

    [Fact]
    public async Task A_stale_token_is_a_409()
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            var stale = await TrafficVersionAsync(factory);
            Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(client, new { rawRetentionDays = "100", rowVersion = stale })).Status);

            var (status, _) = await PutAsync(client, new { rawRetentionDays = "110", rowVersion = stale });

            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal("100", await ValueAsync(factory, SettingKeys.Traffic.RawRetentionDays));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    // ---- the audit trail ------------------------------------------------------------------------------

    [Fact]
    public async Task Every_change_writes_one_audit_row_per_changed_key_and_an_unchanged_save_writes_none()
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            var start = await AuditMaxIdAsync(factory);

            Assert.Equal(
                HttpStatusCode.NoContent,
                (await PutAsync(client, new { rawRetentionDays = "200", botRetentionDays = "14", rowVersion = await TrafficVersionAsync(factory) })).Status);

            var first = await AuditRowsAsync(factory, start);
            Assert.Equal([SettingKeys.Traffic.RawRetentionDays, SettingKeys.Traffic.BotRetentionDays], first.Select(row => row.EntityId));
            Assert.All(first, row => Assert.Equal("Setting", row.EntityType));
            Assert.All(first, row => Assert.NotNull(row.UserId));
            Assert.Contains("90", first[0].BeforeJson, StringComparison.Ordinal);
            Assert.Contains("200", first[0].AfterJson, StringComparison.Ordinal);

            var afterFirst = await AuditMaxIdAsync(factory);

            // Only the bot period moves: one row.
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await PutAsync(client, new { rawRetentionDays = "200", botRetentionDays = "15", rowVersion = await TrafficVersionAsync(factory) })).Status);

            var second = await AuditRowsAsync(factory, afterFirst);
            Assert.Equal([SettingKeys.Traffic.BotRetentionDays], second.Select(row => row.EntityId));

            var afterSecond = await AuditMaxIdAsync(factory);

            // The same values again: success, and no row.
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await PutAsync(client, new { rawRetentionDays = "200", botRetentionDays = "15", rowVersion = await TrafficVersionAsync(factory) })).Status);

            Assert.Equal(afterSecond, await AuditMaxIdAsync(factory));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    // ---- the purge and the rollup use the saved value -------------------------------------------------

    /// <summary>
    /// <b>The test the form exists for.</b> Thirty days saved on the screen, and the next purge deletes the
    /// thirty-first day and keeps the thirtieth — not doc 08's ninety, which is what configuration says.
    /// </summary>
    [Fact]
    public async Task The_purge_uses_the_periods_saved_on_the_form()
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);
        await ClearYearAsync(sqlServer, 1990, Token);

        try
        {
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await PutAsync(client, new { rawRetentionDays = "30", botRetentionDays = "10", rowVersion = await TrafficVersionAsync(factory) })).Status);

            await InsertAsync(
                sqlServer,
                [
                    View(Colombo(1990, 12, 1, 23, 59, 59), Guid.NewGuid(), "/day-31-gone"),
                    View(Colombo(1990, 12, 2, 0, 0, 0), Guid.NewGuid(), "/day-30-kept"),
                    View(Colombo(1990, 12, 21, 23, 59, 59), Guid.NewGuid(), "/bot-day-11-gone", bot: true),
                    View(Colombo(1990, 12, 22, 0, 0, 0), Guid.NewGuid(), "/bot-day-10-kept", bot: true),
                ],
                Token);

            // Rolled up first: the purge never deletes a day the rollup has not built (#193).
            using (var seed = sqlServer.CreateScope())
            {
                await seed.ServiceProvider.GetRequiredService<TrafficRollup>()
                    .RebuildAsync(new DateOnly(1990, 12, 1), new DateOnly(1990, 12, 31), Token);
            }

            var service = factory.Services.GetRequiredService<TrafficRetentionService>();
            var (human, bot, periods) = await service.RunOnceAsync(ColomboNoon(1990, 12, 31), Token);

            Assert.Equal(
                new TrafficRetentionPeriods(30, 10, TrafficRetentionSource.Settings, TrafficRetentionSource.Settings),
                periods);
            Assert.Equal((1, 1), (human, bot));
            Assert.Equal(["/bot-day-10-kept", "/day-30-kept"], await PathsAsync(1990));
        }
        finally
        {
            await ClearYearAsync(sqlServer, 1990, Token);
            await RestoreAsync(factory);
        }
    }

    /// <summary>
    /// The rollup's catch-up builds a never-built day only while it is within raw retention — the same period the
    /// purge applies, read from the same row, so it never rebuilds a day the purge has emptied (which would store
    /// zero) nor skips one it has kept.
    /// </summary>
    [Fact]
    public async Task The_rollups_catch_up_uses_the_saved_raw_period()
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);
        await ClearYearAsync(sqlServer, 1991, Token);

        try
        {
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await PutAsync(client, new { rawRetentionDays = "30", rowVersion = await TrafficVersionAsync(factory) })).Status);

            await InsertAsync(
                sqlServer,
                [
                    View(Colombo(1991, 5, 1), Guid.NewGuid(), "/fifty-days-ago"),   // inside configuration's 90, outside the saved 30
                    View(Colombo(1991, 6, 1), Guid.NewGuid(), "/nineteen-days-ago"), // inside both
                ],
                Token);

            var rebuilt = await factory.Services.GetRequiredService<TrafficRollupService>()
                .RunOnceAsync(new DateTimeOffset(1991, 6, 20, 1, 0, 0, TimeSpan.FromMinutes(330)), Token);

            Assert.Contains(new DateOnly(1991, 6, 1), rebuilt);
            Assert.DoesNotContain(new DateOnly(1991, 5, 1), rebuilt);
        }
        finally
        {
            await ClearYearAsync(sqlServer, 1991, Token);
            await RestoreAsync(factory);
        }
    }

    // ---- precedence -----------------------------------------------------------------------------------

    /// <summary>
    /// <b>The precedence rule against a host whose configuration disagrees with the rows</b>
    /// (<c>Traffic__RawRetentionDays=60</c>, <c>Traffic__BotRetentionDays=5</c>): a valid row wins; an empty row or
    /// one outside the form's bounds falls back to configuration, never to the code default or to a clamp.
    /// </summary>
    [Fact]
    public async Task A_valid_row_wins_and_configuration_is_the_fallback()
    {
        await using var factory = await StartAsync(new Dictionary<string, string?>
        {
            ["Traffic:RawRetentionDays"] = "60",
            ["Traffic:BotRetentionDays"] = "5",
        });

        try
        {
            // Seeded rows (90, 7) beat the configured 60 and 5.
            Assert.Equal(
                new TrafficRetentionPeriods(90, 7, TrafficRetentionSource.Settings, TrafficRetentionSource.Settings),
                await ResolveAsync(factory));

            await WriteDirectlyAsync(factory, SettingKeys.Traffic.RawRetentionDays, null);
            await WriteDirectlyAsync(factory, SettingKeys.Traffic.BotRetentionDays, "");

            Assert.Equal(
                new TrafficRetentionPeriods(60, 5, TrafficRetentionSource.Configuration, TrafficRetentionSource.Configuration),
                await ResolveAsync(factory));

            // Out of the form's bounds although inside configuration's 4–3,650: still configuration.
            await WriteDirectlyAsync(factory, SettingKeys.Traffic.RawRetentionDays, "1000");
            await WriteDirectlyAsync(factory, SettingKeys.Traffic.BotRetentionDays, "4");

            Assert.Equal(
                new TrafficRetentionPeriods(60, 5, TrafficRetentionSource.Configuration, TrafficRetentionSource.Configuration),
                await ResolveAsync(factory));

            await WriteDirectlyAsync(factory, SettingKeys.Traffic.RawRetentionDays, "45");

            Assert.Equal(
                new TrafficRetentionPeriods(45, 5, TrafficRetentionSource.Settings, TrafficRetentionSource.Configuration),
                await ResolveAsync(factory));

            // A row flipped to Secret by hand holds ciphertext, not a number: it is never read, even when its
            // stored text would parse. (CK_Settings_IsSecret moves both columns together.)
            await SetSecretAsync(factory, SettingKeys.Traffic.RawRetentionDays, secret: true);

            Assert.Equal(60, (await ResolveAsync(factory)).RawDays);
        }
        finally
        {
            await SetSecretAsync(factory, SettingKeys.Traffic.RawRetentionDays, secret: false);
            await RestoreAsync(factory);
        }
    }

    /// <summary>
    /// <b>The deployed default is unchanged.</b> A host with no <c>Traffic__*</c> configuration (every deployment
    /// today: compose did not pass any) and a database the seeder has just filled purges at doc 08's 90 and 7 —
    /// the figures it purged at before the form existed.
    /// </summary>
    [Fact]
    public async Task With_no_configuration_and_seeded_rows_the_purge_keeps_ninety_and_seven_days()
    {
        await using var factory = await StartAsync();

        Assert.Equal(
            new TrafficRetentionPeriods(90, 7, TrafficRetentionSource.Settings, TrafficRetentionSource.Settings),
            await ResolveAsync(factory));

        var options = factory.Services.GetRequiredService<TrafficOptions>();
        Assert.Equal((90, 7), (options.RawRetentionDays, options.BotRetentionDays));
    }

    // ---- seeding --------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Seeding reaches a database that already exists, and is idempotent</b> (#152's mechanism). A database
    /// seeded before #193 lacks the three new rows; the next start-up's seeder inserts exactly those three with
    /// their catalogue values — 90, 7 and empty — and touches no value the client has already set. A second run
    /// inserts nothing.
    /// </summary>
    [Fact]
    public async Task The_seeder_adds_the_new_keys_to_an_existing_database_once_and_leaves_client_values_alone()
    {
        sqlServer.SkipIfUnavailable();

        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);

        string[] added =
        [
            SettingKeys.Social.YouTubeUrl,
            SettingKeys.Traffic.RawRetentionDays,
            SettingKeys.Traffic.BotRetentionDays,
        ];

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var first = await new SettingsSeeder(db).SeedAsync(Token);
            Assert.Equal(new SettingsSeedCounts(SettingCatalog.All.Count, 0), first);

            // The state of a database seeded before #193, with a client's own edits on it.
            await db.Settings.Where(setting => added.Contains(setting.Key)).ExecuteDeleteAsync(Token);
            await db.Settings
                .Where(setting => setting.Key == SettingKeys.Orders.AutoDeliverAfterDays)
                .ExecuteUpdateAsync(update => update.SetProperty(setting => setting.Value, "12"), Token);
            await db.Settings
                .Where(setting => setting.Key == SettingKeys.Social.FacebookUrl)
                .ExecuteUpdateAsync(update => update.SetProperty(setting => setting.Value, "https://www.facebook.com/shop"), Token);
        }

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(new SettingsSeedCounts(3, 0), await new SettingsSeeder(db).SeedAsync(Token));
        }

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(new SettingsSeedCounts(0, 0), await new SettingsSeeder(db).SeedAsync(Token));

            var values = await db.Settings.AsNoTracking().ToDictionaryAsync(row => row.Key, row => row.Value, Token);

            Assert.Equal("90", values[SettingKeys.Traffic.RawRetentionDays]);
            Assert.Equal("7", values[SettingKeys.Traffic.BotRetentionDays]);
            Assert.Null(values[SettingKeys.Social.YouTubeUrl]);
            Assert.Equal("12", values[SettingKeys.Orders.AutoDeliverAfterDays]);
            Assert.Equal("https://www.facebook.com/shop", values[SettingKeys.Social.FacebookUrl]);

            var traffic = await db.Settings.AsNoTracking().SingleAsync(row => row.Key == SettingKeys.Traffic.RawRetentionDays, Token);
            Assert.Equal(SettingKeys.Traffic.Group, traffic.GroupKey);
            Assert.Equal(SettingValueType.Int, traffic.ValueType);
            Assert.Equal(Permissions.Settings.Edit, traffic.RequiredPermission);
            Assert.False(traffic.IsSecret);
        }
    }

    /// <summary>A value a staff user saved survives the next deployment's seeder: seed values apply on insert only.</summary>
    [Fact]
    public async Task A_saved_period_survives_the_next_seeder_run()
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await PutAsync(client, new { rawRetentionDays = "180", rowVersion = await TrafficVersionAsync(factory) })).Status);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<SettingsSeeder>().SeedAsync(Token);
            }

            Assert.Equal("180", await ValueAsync(factory, SettingKeys.Traffic.RawRetentionDays));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    // ---- independent tester (issue #193) ---------------------------------------------------------------

    /// <summary>
    /// The Traffic route is a state-changing admin write, so it must refuse a request without the anti-forgery
    /// header exactly as the other settings routes do (<c>A_mutating_request_without_the_antiforgery_header_is_refused</c>),
    /// and write nothing. Without this, removing the check from <c>UpdateTrafficAsync</c> left every test green.
    /// </summary>
    [Fact]
    public async Task The_traffic_route_refuses_a_request_without_the_antiforgery_header()
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            var version = await TrafficVersionAsync(factory);
            client.DefaultRequestHeaders.Remove(MediaTestHost.AntiforgeryHeader);

            var (status, problem) = await PutAsync(client, new { rawRetentionDays = "45", rowVersion = version });

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("invalid_antiforgery_token", problem.GetProperty("code").GetString());
            Assert.Equal("90", await ValueAsync(factory, SettingKeys.Traffic.RawRetentionDays));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    /// <summary>
    /// JSON type confusion and number spellings over the wire: each is a 400 naming the field, nothing is written
    /// and no audit row appears. A purge period is irreversible, so nothing but plain digits in bounds may land.
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("60.0")]
    [InlineData("\"60.0\"")]
    [InlineData("\"+60\"")]
    [InlineData("\"-30\"")]
    [InlineData("\"0x3C\"")]
    [InlineData("\"6e1\"")]
    [InlineData("\"\\uFF16\\uFF10\"")]
    [InlineData("\"99999999999\"")]
    [InlineData("\"0\"")]
    public async Task A_period_that_is_not_plain_digits_is_a_400_and_nothing_is_written(string rawJson)
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            var start = await AuditMaxIdAsync(factory);
            var version = await TrafficVersionAsync(factory);
            var body = "{\"rawRetentionDays\":" + rawJson + ",\"rowVersion\":\"" + version + "\"}";

            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PutAsync(new Uri(TrafficUrl, UriKind.Relative), content, Token);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty("rawRetentionDays", out _), body);
            Assert.Equal("90", await ValueAsync(factory, SettingKeys.Traffic.RawRetentionDays));
            Assert.Equal(start, await AuditMaxIdAsync(factory));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    /// <summary>
    /// Spaces and leading zeros are tolerated on the way in but never stored: the row holds canonical digits, so
    /// the purge's stricter reader (no whitespace) uses it rather than silently falling back to configuration.
    /// </summary>
    [Fact]
    public async Task A_padded_period_is_stored_as_canonical_digits_that_the_purge_then_uses()
    {
        await using var factory = await StartAsync(new Dictionary<string, string?>
        {
            ["Traffic:RawRetentionDays"] = "75",
        });
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            var (status, _) = await PutAsync(client, new { rawRetentionDays = " 060 ", rowVersion = await TrafficVersionAsync(factory) });

            Assert.Equal(HttpStatusCode.NoContent, status);
            Assert.Equal("60", await ValueAsync(factory, SettingKeys.Traffic.RawRetentionDays));

            var periods = await ResolveAsync(factory);
            Assert.Equal((60, TrafficRetentionSource.Settings), (periods.RawDays, periods.RawSource));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    /// <summary>
    /// Doc 08 §3.8 DB2: one audit row per changed key with <b>its own</b> before and after values. The developer's
    /// test checks the raw row by substring; this pins both rows exactly, so an audit row that carried the other
    /// key's figures, or the new value as "before", fails.
    /// </summary>
    [Fact]
    public async Task Each_audit_row_carries_its_own_keys_exact_before_and_after_values()
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            var start = await AuditMaxIdAsync(factory);

            Assert.Equal(
                HttpStatusCode.NoContent,
                (await PutAsync(client, new { rawRetentionDays = "120", botRetentionDays = "21", rowVersion = await TrafficVersionAsync(factory) })).Status);

            var rows = (await AuditRowsAsync(factory, start)).ToDictionary(row => row.EntityId!, StringComparer.Ordinal);

            Assert.Equal(2, rows.Count);
            Assert.Equal(("90", "120"), (ValueOf(rows[SettingKeys.Traffic.RawRetentionDays].BeforeJson), ValueOf(rows[SettingKeys.Traffic.RawRetentionDays].AfterJson)));
            Assert.Equal(("7", "21"), (ValueOf(rows[SettingKeys.Traffic.BotRetentionDays].BeforeJson), ValueOf(rows[SettingKeys.Traffic.BotRetentionDays].AfterJson)));
            Assert.All(rows.Values, row => Assert.Equal("SettingChange", row.Action));
        }
        finally
        {
            await RestoreAsync(factory);
        }

        static string? ValueOf(string? json) =>
            JsonSerializer.Deserialize<JsonElement>(json!).GetProperty("value").GetString();
    }

    /// <summary>
    /// <b>The catch-up boundary, to the day.</b> With 30 saved, last night's 01:30 purge kept the thirty Colombo days
    /// ending yesterday, so at 00:30 the oldest day still holding raw rows is <c>today - 30</c>. The catch-up must
    /// reach exactly that day (one day shorter and an unbuilt day is purged at 01:30 before anything counted it, a
    /// permanent gap in the history the daily tables keep for ever) and must not reach <c>today - 31</c>.
    /// </summary>
    [Fact]
    public async Task The_rollups_catch_up_reaches_the_oldest_day_the_purge_has_kept_and_no_further()
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);
        await ClearYearAsync(sqlServer, 1992, Token);

        try
        {
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await PutAsync(client, new { rawRetentionDays = "30", rowVersion = await TrafficVersionAsync(factory) })).Status);

            await InsertAsync(
                sqlServer,
                [
                    View(Colombo(1992, 5, 21, 0, 0, 0), Guid.NewGuid(), "/today-minus-30"),
                    View(Colombo(1992, 5, 20, 23, 59, 59), Guid.NewGuid(), "/today-minus-31"),
                ],
                Token);

            var rebuilt = await factory.Services.GetRequiredService<TrafficRollupService>()
                .RunOnceAsync(new DateTimeOffset(1992, 6, 20, 0, 30, 0, TimeSpan.FromMinutes(330)), Token);

            Assert.Contains(new DateOnly(1992, 5, 21), rebuilt);
            Assert.DoesNotContain(new DateOnly(1992, 5, 20), rebuilt);
        }
        finally
        {
            await ClearYearAsync(sqlServer, 1992, Token);
            await RestoreAsync(factory);
        }
    }

    /// <summary>
    /// <b>Tester, fix round: the kept day must be recoverable by the jobs as they actually run.</b> The guard keeps
    /// a never-built day past retention "until the rollup has counted it", but the rollup's catch-up only looks back
    /// as far as the raw period, which is exactly the window the kept day has fallen out of. Saved 30, a day 50 days
    /// back that the rollup never built: after a night of the real rollup service and the real purge service, the
    /// day must have its totals (and then its raw rows may go). Driven through the two hosted services' own
    /// <c>RunOnceAsync</c>, not <c>TrafficRollup.RebuildAsync</c> called by hand, because nothing in production
    /// calls a rebuild for an arbitrary old day.
    /// </summary>
    [Fact]
    public async Task A_day_the_purge_keeps_as_unbuilt_is_built_by_the_nightly_jobs_and_then_purged()
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);
        await ClearYearAsync(sqlServer, 1993, Token);

        try
        {
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await PutAsync(client, new { rawRetentionDays = "30", rowVersion = await TrafficVersionAsync(factory) })).Status);

            await InsertAsync(sqlServer, [View(Colombo(1993, 5, 1, 10), Guid.NewGuid(), "/never-built")], Token);

            var rollup = factory.Services.GetRequiredService<TrafficRollupService>();
            var purge = factory.Services.GetRequiredService<TrafficRetentionService>();

            // Three nights: 00:30 rollup, 01:30 purge.
            for (var night = 20; night <= 22; night++)
            {
                await rollup.RunOnceAsync(new DateTimeOffset(1993, 6, night, 0, 30, 0, TimeSpan.FromMinutes(330)), Token);
                await purge.RunOnceAsync(new DateTimeOffset(1993, 6, night, 1, 30, 0, TimeSpan.FromMinutes(330)), Token);
            }

            var stored = await TrafficDailyAsync(sqlServer, new DateOnly(1993, 5, 1), new DateOnly(1993, 5, 1), Token);
            Assert.True(
                stored.Sum(row => row.PageViews) == 1,
                "1 May 1993 was kept by the purge guard but never built by the nightly rollup: its raw rows will be "
                + "kept and warned about every night for ever, and its totals never reach the Traffic page.");
            Assert.Empty(await PathsAsync(1993));
        }
        finally
        {
            await ClearYearAsync(sqlServer, 1993, Token);
            await RestoreAsync(factory);
        }
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private async Task<ApiFactory> StartAsync(IReadOnlyDictionary<string, string?>? configuration = null)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        return new ApiFactory(sqlServer.ConnectionString, additionalConfiguration: configuration);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PutAsync(HttpClient client, object body)
    {
        using var response = await client.PutAsJsonAsync(new Uri(TrafficUrl, UriKind.Relative), body, JsonOptions, Token);
        var payload = await response.Content.ReadAsStringAsync(Token);

        return (response.StatusCode, string.IsNullOrWhiteSpace(payload) ? default : JsonSerializer.Deserialize<JsonElement>(payload, JsonOptions));
    }

    /// <summary>The Traffic save unit's token, computed from the rows exactly as the read model publishes it.</summary>
    private static async Task<string> TrafficVersionAsync(ApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var keys = SettingCatalog.KeysInSaveUnitFor(SettingKeys.Traffic.Group);

        return SettingsGroupVersion.For(await db.Settings.AsNoTracking().Where(row => keys.Contains(row.Key)).ToListAsync(Token));
    }

    private static async Task<List<string>> GroupKeysAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri(SettingsUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        return body.GetProperty("groups").EnumerateArray().Select(group => group.GetProperty("groupKey").GetString()!).ToList();
    }

    private static async Task<Dictionary<string, JsonElement>> SettingsAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri(SettingsUrl, UriKind.Relative), Token);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        return body.GetProperty("groups").EnumerateArray()
            .SelectMany(group => group.GetProperty("settings").EnumerateArray())
            .ToDictionary(setting => setting.GetProperty("key").GetString()!, setting => setting, StringComparer.Ordinal);
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

    private static async Task SetSecretAsync(ApiFactory factory, string key, bool secret)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Settings
            .Where(row => row.Key == key)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(row => row.IsSecret, secret)
                    .SetProperty(row => row.ValueType, secret ? SettingValueType.Secret : SettingValueType.Int),
                Token);
    }

    private static async Task<TrafficRetentionPeriods> ResolveAsync(ApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<TrafficRetentionSettings>().ReadAsync(Token);
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

    private async Task<List<string>> PathsAsync(int year)
    {
        using var scope = sqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var from = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(-6);
        var to = new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(6);

        return await db.PageViews.AsNoTracking()
            .Where(view => view.OccurredAtUtc >= from && view.OccurredAtUtc < to)
            .Select(view => view.Path)
            .OrderBy(path => path)
            .ToListAsync(Token);
    }

    private static DateTimeOffset ColomboNoon(int year, int month, int day) =>
        new(year, month, day, 12, 0, 0, TimeSpan.FromMinutes(330));

    private static async Task<int> CreateUserAsync(ApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var user = new AppUser
        {
            UserName = $"traffic-{Guid.NewGuid():N}@example.com",
            Email = $"traffic-{Guid.NewGuid():N}@example.com",
            EmailConfirmed = true,
            UserType = UserType.Staff,
            FirstName = "Traffic",
            LastName = "Tester",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        };

        var created = await userManager.CreateAsync(user, "Correct-Horse-Battery-Staple-1!");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));

        return user.Id;
    }

    private static async Task AddToSuperAdminAsync(ApiFactory factory, int userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        if (await roleManager.FindByNameAsync(AdminSeedHostedService.SuperAdminRoleName) is null)
        {
            var created = await roleManager.CreateAsync(new AppRole
            {
                Name = AdminSeedHostedService.SuperAdminRoleName,
                IsSystem = true,
                GrantsAllPermissions = true,
                CreatedAtUtc = DateTime.UtcNow,
            });
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));
        }

        var user = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));
        var added = await userManager.AddToRoleAsync(user!, AdminSeedHostedService.SuperAdminRoleName);
        Assert.True(added.Succeeded, string.Join("; ", added.Errors.Select(error => error.Description)));
    }

    /// <summary>
    /// Takes the test's user out of the GrantsAll role again: other classes count its holders (the first-boot
    /// seeding test), and a stray Super Admin would be a fact about this class leaking into theirs.
    /// </summary>
    private static async Task RemoveFromSuperAdminAsync(ApiFactory factory, int userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));

        if (user is not null && await userManager.IsInRoleAsync(user, AdminSeedHostedService.SuperAdminRoleName))
        {
            await userManager.RemoveFromRoleAsync(user, AdminSeedHostedService.SuperAdminRoleName);
        }
    }

    /// <summary>Both rows back to their seed values (90, 7), with no last-changed stamp, as the seeder leaves them.</summary>
    private static async Task RestoreAsync(ApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var key in new[] { SettingKeys.Traffic.RawRetentionDays, SettingKeys.Traffic.BotRetentionDays })
        {
            var seed = SettingCatalog.Find(key)!.SeedValue;

            await db.Settings
                .Where(row => row.Key == key)
                .ExecuteUpdateAsync(
                    update => update
                        .SetProperty(row => row.Value, seed)
                        .SetProperty(row => row.UpdatedAtUtc, (DateTime?)null)
                        .SetProperty(row => row.UpdatedByUserId, (int?)null),
                    Token);
        }
    }
}
