using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Settings;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Identity.Seeding;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Seeding;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The display-currency rates' save (issue #248), end to end against a real SQL Server: who may save, the number
/// rules over the wire, clearing, the audit trail, no sensitive-change alert, the concurrency token the read model
/// publishes, and seeding on a database that already exists (UAT's).
/// <para>
/// <b>Every test puts the five rows back to empty</b> in a <c>finally</c>: the assembly's database is shared and
/// its classes run one after another, so a rate left behind would show up in another class's public-feed test.
/// </para>
/// </summary>
public sealed class AdminCurrencyRatesTests(SqlServerDatabaseFixture sqlServer)
{
    private const string SettingsUrl = "/api/admin/settings";
    private const string RatesUrl = "/api/admin/settings/currency-rates";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- who may save it ------------------------------------------------------------------------------

    [Fact]
    public async Task The_route_refuses_an_anonymous_caller()
    {
        await using var factory = await StartAsync();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        using var response = await client.PutAsJsonAsync(new Uri(RatesUrl, UriKind.Relative), new { usd = "301.5" }, JsonOptions, Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The default roles by their own permission sets (doc 08 §9). Store Manager holds <c>Settings.Edit</c> and
    /// saves, which is the point of not putting the rates under <c>Payments.*</c>; Order Staff and Content Editor do
    /// not; and a caller holding only <c>Settings.Payments</c> is refused too, because the group is
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
            var (status, _) = await PutAsync(client, new { usd = "301.5", rowVersion = await VersionAsync(factory) });

            Assert.Equal(expected, status);
            Assert.Equal(expected == HttpStatusCode.NoContent ? "301.5" : null, await ValueAsync(factory, SettingKeys.CurrencyDisplay.UsdLkrPerUnit));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    // ---- the read model ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_settings_edit_holder_reads_the_group_with_its_five_rows_and_a_payments_only_holder_does_not()
    {
        await using var factory = await StartAsync();

        using var editor = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);
        using var payments = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Payments);

        var group = await GroupAsync(editor);
        var rows = group.GetProperty("settings").EnumerateArray().ToList();

        Assert.Equal(
            DisplayCurrencies.All.Select(currency => currency.SettingKey),
            rows.Select(row => row.GetProperty("key").GetString()));
        Assert.All(rows, row =>
        {
            Assert.Equal("Decimal", row.GetProperty("valueType").GetString());
            Assert.Equal(Permissions.Settings.Edit, row.GetProperty("requiredPermission").GetString());
            Assert.False(row.GetProperty("isSecret").GetBoolean());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("value").ValueKind);
            Assert.Equal(JsonValueKind.Null, row.GetProperty("updatedAtUtc").ValueKind);
        });

        Assert.DoesNotContain(
            await GroupKeysAsync(payments),
            groupKey => groupKey == SettingKeys.CurrencyDisplay.Group);
    }

    /// <summary>
    /// The token the screen reads is the token the save compares, with no hand-computed copy in between: read it
    /// from the read model, save with it, and the same token is stale afterwards.
    /// </summary>
    [Fact]
    public async Task The_token_the_read_model_publishes_is_the_one_the_save_accepts_and_it_goes_stale_after_one_save()
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            var token = (await GroupAsync(client)).GetProperty("rowVersion").GetString();

            Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(client, new { gbp = "399.5", rowVersion = token })).Status);
            Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(client, new { eur = "345", rowVersion = token })).Status);
            Assert.Null(await ValueAsync(factory, SettingKeys.CurrencyDisplay.EurLkrPerUnit));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    // ---- the save ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_save_stores_the_canonical_text_stamps_the_row_and_the_read_model_shows_the_age()
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            var before = DateTime.UtcNow.AddSeconds(-5);

            var (status, _) = await PutAsync(client, new { usd = "321.50", gbp = "399.5", rowVersion = await VersionAsync(factory) });

            Assert.Equal(HttpStatusCode.NoContent, status);
            Assert.Equal("321.5", await ValueAsync(factory, SettingKeys.CurrencyDisplay.UsdLkrPerUnit));

            var row = await RowAsync(factory, SettingKeys.CurrencyDisplay.UsdLkrPerUnit);
            Assert.NotNull(row.UpdatedByUserId);
            Assert.InRange(row.UpdatedAtUtc!.Value, before, DateTime.UtcNow.AddSeconds(5));

            var untouched = await RowAsync(factory, SettingKeys.CurrencyDisplay.EurLkrPerUnit);
            Assert.Null(untouched.Value);
            Assert.Null(untouched.UpdatedAtUtc);

            var shown = (await GroupAsync(client)).GetProperty("settings").EnumerateArray()
                .Single(setting => setting.GetProperty("key").GetString() == SettingKeys.CurrencyDisplay.UsdLkrPerUnit);
            Assert.Equal("321.5", shown.GetProperty("value").GetString());
            Assert.NotEqual(JsonValueKind.Null, shown.GetProperty("updatedAtUtc").ValueKind);
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_field_clears_the_rate(string empty)
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(client, new { aud = "205", rowVersion = await VersionAsync(factory) })).Status);

            var (status, _) = await PutAsync(client, new { aud = empty, rowVersion = await VersionAsync(factory) });

            Assert.Equal(HttpStatusCode.NoContent, status);
            Assert.Null(await ValueAsync(factory, SettingKeys.CurrencyDisplay.AudLkrPerUnit));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    [Fact]
    public async Task A_json_null_clears_the_rate_and_an_absent_field_leaves_it_alone()
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await PutAsync(client, new { aed = "82.275", eur = "345", rowVersion = await VersionAsync(factory) })).Status);

            var (status, _) = await PutAsync(client, new { aed = (string?)null, rowVersion = await VersionAsync(factory) });

            Assert.Equal(HttpStatusCode.NoContent, status);
            Assert.Null(await ValueAsync(factory, SettingKeys.CurrencyDisplay.AedLkrPerUnit));
            Assert.Equal("345", await ValueAsync(factory, SettingKeys.CurrencyDisplay.EurLkrPerUnit));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    [Theory]
    [InlineData("0.0001")]
    [InlineData("1000000")]
    public async Task The_edges_of_the_range_are_accepted(string value)
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            var (status, _) = await PutAsync(client, new { usd = value, rowVersion = await VersionAsync(factory) });

            Assert.Equal(HttpStatusCode.NoContent, status);
            Assert.Equal(value, await ValueAsync(factory, SettingKeys.CurrencyDisplay.UsdLkrPerUnit));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("0")]
    [InlineData("0.0000")]
    [InlineData("1e3")]
    [InlineData("1,5")]
    [InlineData("1 5")]
    [InlineData(".5")]
    [InlineData("399.12345")]
    [InlineData("0.00010")]
    [InlineData("1000000.01")]
    [InlineData("99999999999999999999999999999999")]
    public async Task A_rate_that_breaks_the_rules_is_a_400_on_that_field_and_nothing_is_written(string value)
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            var auditBefore = await AuditMaxIdAsync(factory);

            var (status, problem) = await PutAsync(
                client, new { usd = "301.5", gbp = value, rowVersion = await VersionAsync(factory) });

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.True(problem.GetProperty("errors").TryGetProperty("gbp", out _));
            Assert.False(problem.GetProperty("errors").TryGetProperty("usd", out _));

            // All or nothing: the good field in the same request was not written either.
            Assert.Null(await ValueAsync(factory, SettingKeys.CurrencyDisplay.UsdLkrPerUnit));
            Assert.Null(await ValueAsync(factory, SettingKeys.CurrencyDisplay.GbpLkrPerUnit));
            Assert.Equal(auditBefore, await AuditMaxIdAsync(factory));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    /// <summary>A JSON number, boolean, array or object is a named field error and not a silent coercion.</summary>
    [Theory]
    [InlineData("399.5")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    public async Task A_field_that_is_not_text_is_a_400_naming_it(string rawJson)
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        using var content = new StringContent(
            $"{{\"eur\":{rawJson},\"rowVersion\":\"{await VersionAsync(factory)}\"}}",
            System.Text.Encoding.UTF8,
            "application/json");
        using var response = await client.PutAsync(new Uri(RatesUrl, UriKind.Relative), content, Token);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty("eur", out _));
        Assert.Null(await ValueAsync(factory, SettingKeys.CurrencyDisplay.EurLkrPerUnit));
    }

    [Fact]
    public async Task A_missing_token_is_a_409_and_nothing_is_written()
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        var (status, _) = await PutAsync(client, new { usd = "301.5" });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Null(await ValueAsync(factory, SettingKeys.CurrencyDisplay.UsdLkrPerUnit));
    }

    [Fact]
    public async Task The_route_refuses_a_request_without_the_antiforgery_header()
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            var version = await VersionAsync(factory);
            client.DefaultRequestHeaders.Remove(MediaTestHost.AntiforgeryHeader);

            var (status, problem) = await PutAsync(client, new { usd = "301.5", rowVersion = version });

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("invalid_antiforgery_token", problem.GetProperty("code").GetString());
            Assert.Null(await ValueAsync(factory, SettingKeys.CurrencyDisplay.UsdLkrPerUnit));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    // ---- the two bodies that used to be a 500 (#147, on main since a9b7798) ------------------------------

    /// <summary>
    /// The independent tester saw two 500s on this route before the merge with <c>main</c>: a property named twice, and
    /// an escape that spells half a surrogate pair. #247 fixed them in the shared body reader, and this proves it
    /// through <b>this</b> route, with the real pipeline, as a 400 that names the problem and writes nothing.
    /// <para>
    /// The escape is built from a backslash and the letters, never typed as one sequence, so the test file cannot
    /// have it decoded into a plain letter on the way in.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("a property named twice", "repeats a property name")]
    [InlineData("a property named twice in another letter case", "repeats a property name")]
    [InlineData("half a surrogate pair in a value", "not valid Unicode")]
    [InlineData("half a surrogate pair in a name", "not valid Unicode")]
    public async Task A_body_that_repeats_a_property_or_holds_half_a_surrogate_pair_is_a_400_and_not_a_500(string shape, string expectedDetail)
    {
        const char Backslash = '\\';

        var raw = shape switch
        {
            "a property named twice" => "{\"usd\":\"1\",\"usd\":\"2\"}",
            "a property named twice in another letter case" => "{\"usd\":\"1\",\"USD\":\"2\"}",
            "half a surrogate pair in a value" => "{\"usd\":\"" + Backslash + "ud800\"}",
            "half a surrogate pair in a name" => "{\"" + Backslash + "ud800\":\"1\"}",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            using var content = new StringContent(raw, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PutAsync(new Uri(RatesUrl, UriKind.Relative), content, Token);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{shape}: answered {(int)response.StatusCode}, expected 400.");
            Assert.Equal("malformed_json", problem.GetProperty("code").GetString());
            Assert.Contains(expectedDetail, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
            Assert.Null(await ValueAsync(factory, SettingKeys.CurrencyDisplay.UsdLkrPerUnit));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    // ---- what the settings routes do with a field they do not know (m8) ---------------------------------

    /// <summary>
    /// A field the route does not know, and a body with nothing but the token, answer 204 and write nothing. That is
    /// not special to this route: the Traffic and the contact-and-social routes do exactly the same, and this puts
    /// the three side by side so a change to one of them is a change to all of them on purpose. (Each reads only the
    /// fields it owns, so a name such as <c>jpy</c> is never looked at, and a save with nothing in it is "unchanged".)
    /// </summary>
    [Fact]
    public async Task An_unknown_field_and_a_body_with_only_the_token_are_204_and_write_nothing_on_this_route_and_its_siblings()
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            var auditBefore = await AuditMaxIdAsync(factory);
            var trafficBefore = await ValueAsync(factory, SettingKeys.Traffic.RawRetentionDays);

            var (currencyUnknown, _) = await PutToAsync(
                client, RatesUrl, new { jpy = "5", rowVersion = await VersionAsync(factory) });
            var (currencyTokenOnly, _) = await PutToAsync(
                client, RatesUrl, new { rowVersion = await VersionAsync(factory) });
            var (trafficUnknown, _) = await PutToAsync(
                client, "/api/admin/settings/traffic", new { jpy = "5", rowVersion = await VersionAsync(factory, SettingKeys.Traffic.Group) });
            var (contactUnknown, _) = await PutToAsync(
                client, "/api/admin/settings/contact-social", new { jpy = "5", rowVersion = await VersionAsync(factory, SettingKeys.Contact.Group) });

            Assert.Equal(HttpStatusCode.NoContent, currencyUnknown);
            Assert.Equal(HttpStatusCode.NoContent, currencyTokenOnly);
            Assert.Equal(HttpStatusCode.NoContent, trafficUnknown);
            Assert.Equal(HttpStatusCode.NoContent, contactUnknown);

            foreach (var currency in DisplayCurrencies.All)
            {
                Assert.Null(await ValueAsync(factory, currency.SettingKey));
            }

            Assert.Equal(trafficBefore, await ValueAsync(factory, SettingKeys.Traffic.RawRetentionDays));
            Assert.Equal(auditBefore, await AuditMaxIdAsync(factory));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    // ---- the audit trail, the alert, PayPal -------------------------------------------------------------

    [Fact]
    public async Task Every_change_writes_one_audit_row_per_changed_key_and_an_unchanged_save_writes_none_and_keeps_the_age()
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            var start = await AuditMaxIdAsync(factory);

            Assert.Equal(
                HttpStatusCode.NoContent,
                (await PutAsync(client, new { usd = "301.5", gbp = "399.5", rowVersion = await VersionAsync(factory) })).Status);

            var first = await AuditRowsAsync(factory, start);
            Assert.Equal([SettingKeys.CurrencyDisplay.UsdLkrPerUnit, SettingKeys.CurrencyDisplay.GbpLkrPerUnit], first.Select(row => row.EntityId));
            Assert.All(first, row => Assert.Equal("Setting", row.EntityType));
            Assert.All(first, row => Assert.NotNull(row.UserId));
            Assert.Contains("301.5", first[0].AfterJson, StringComparison.Ordinal);
            Assert.DoesNotContain("301.5", first[0].BeforeJson ?? string.Empty, StringComparison.Ordinal);

            var stamped = (await RowAsync(factory, SettingKeys.CurrencyDisplay.UsdLkrPerUnit)).UpdatedAtUtc;
            var afterFirst = await AuditMaxIdAsync(factory);

            // The same rates again, one of them in another spelling: nothing is written and the age does not move.
            var (status, _) = await PutAsync(client, new { usd = "301.50", gbp = "399.5", rowVersion = await VersionAsync(factory) });

            Assert.Equal(HttpStatusCode.NoContent, status);
            Assert.Equal(afterFirst, await AuditMaxIdAsync(factory));
            Assert.Equal(stamped, (await RowAsync(factory, SettingKeys.CurrencyDisplay.UsdLkrPerUnit)).UpdatedAtUtc);

            // Clearing is a change, and is audited with the old value in the before side.
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await PutAsync(client, new { gbp = string.Empty, rowVersion = await VersionAsync(factory) })).Status);

            var cleared = Assert.Single(await AuditRowsAsync(factory, afterFirst));
            Assert.Equal(SettingKeys.CurrencyDisplay.GbpLkrPerUnit, cleared.EntityId);
            Assert.Contains("399.5", cleared.BeforeJson, StringComparison.Ordinal);
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    /// <summary>
    /// Doc 08 §3.8 N2 raises a <c>SensitiveSettingsChanged</c> alert for payments, SMTP and security keys. The rates
    /// are none of those and a Store Manager updates them weekly, so a save must not fill the Super Admins' banner.
    /// </summary>
    [Fact]
    public async Task A_save_raises_no_sensitive_settings_alert()
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            var before = await AlertCountAsync(factory);

            Assert.Equal(
                HttpStatusCode.NoContent,
                (await PutAsync(client, new { usd = "301.5", rowVersion = await VersionAsync(factory) })).Status);

            Assert.Equal(before, await AlertCountAsync(factory));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    /// <summary>PayPal's rate has a different job and is not touched by a display-rate save.</summary>
    [Fact]
    public async Task A_save_leaves_paypals_own_rate_untouched()
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            var paypalBefore = await RowAsync(factory, SettingKeys.PayPal.LkrPerUsd);

            Assert.Equal(
                HttpStatusCode.NoContent,
                (await PutAsync(client, new { usd = "999.5", rowVersion = await VersionAsync(factory) })).Status);

            var paypalAfter = await RowAsync(factory, SettingKeys.PayPal.LkrPerUsd);
            Assert.Equal(paypalBefore.Value, paypalAfter.Value);
            Assert.Equal(paypalBefore.UpdatedAtUtc, paypalAfter.UpdatedAtUtc);
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    // ---- seeding on a database that already exists ------------------------------------------------------

    /// <summary>
    /// <b>Seeding reaches a database that already exists, with no migration</b> (#152's mechanism), which is how UAT
    /// gets these rows. A database seeded before this feature lacks the five; the next start-up's seeder inserts
    /// exactly those five, empty, and touches no value a client has set. A second run inserts nothing.
    /// </summary>
    [Fact]
    public async Task The_seeder_adds_the_five_rows_to_an_existing_database_once_empty_and_leaves_client_values_alone()
    {
        sqlServer.SkipIfUnavailable();

        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);
        var added = DisplayCurrencies.All.Select(currency => currency.SettingKey).ToArray();

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(new SettingsSeedCounts(SettingCatalog.All.Count, 0), await new SettingsSeeder(db).SeedAsync(Token));

            // The state of a database seeded before this feature, with a client's own edits on it.
            await db.Settings.Where(setting => added.Contains(setting.Key)).ExecuteDeleteAsync(Token);
            await db.Settings
                .Where(setting => setting.Key == SettingKeys.Orders.AutoDeliverAfterDays)
                .ExecuteUpdateAsync(update => update.SetProperty(setting => setting.Value, "12"), Token);
            await db.Settings
                .Where(setting => setting.Key == SettingKeys.PayPal.LkrPerUsd)
                .ExecuteUpdateAsync(update => update.SetProperty(setting => setting.Value, "315.25"), Token);
        }

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(new SettingsSeedCounts(5, 0), await new SettingsSeeder(db).SeedAsync(Token));
        }

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(new SettingsSeedCounts(0, 0), await new SettingsSeeder(db).SeedAsync(Token));

            var values = await db.Settings.AsNoTracking().ToDictionaryAsync(row => row.Key, row => row.Value, Token);

            Assert.All(added, key => Assert.Null(values[key]));
            Assert.Equal("12", values[SettingKeys.Orders.AutoDeliverAfterDays]);
            Assert.Equal("315.25", values[SettingKeys.PayPal.LkrPerUsd]);

            foreach (var currency in DisplayCurrencies.All)
            {
                var row = await db.Settings.AsNoTracking().SingleAsync(setting => setting.Key == currency.SettingKey, Token);
                Assert.Equal(SettingKeys.CurrencyDisplay.Group, row.GroupKey);
                Assert.Equal(SettingValueType.Decimal, row.ValueType);
                Assert.Equal(Permissions.Settings.Edit, row.RequiredPermission);
                Assert.False(row.IsSecret);
            }
        }
    }

    /// <summary>A rate a staff user saved survives the next deployment's seeder: seed values apply on insert only.</summary>
    [Fact]
    public async Task A_saved_rate_survives_the_next_seeder_run()
    {
        await using var factory = await StartAsync();
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await PutAsync(client, new { gbp = "399.5", rowVersion = await VersionAsync(factory) })).Status);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<SettingsSeeder>().SeedAsync(Token);
            }

            Assert.Equal("399.5", await ValueAsync(factory, SettingKeys.CurrencyDisplay.GbpLkrPerUnit));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private async Task<ApiFactory> StartAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        return new ApiFactory(sqlServer.ConnectionString);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PutAsync(HttpClient client, object body)
    {
        using var response = await client.PutAsJsonAsync(new Uri(RatesUrl, UriKind.Relative), body, JsonOptions, Token);
        var payload = await response.Content.ReadAsStringAsync(Token);

        return (response.StatusCode, string.IsNullOrWhiteSpace(payload) ? default : JsonSerializer.Deserialize<JsonElement>(payload, JsonOptions));
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PutToAsync(HttpClient client, string url, object body)
    {
        using var response = await client.PutAsJsonAsync(new Uri(url, UriKind.Relative), body, JsonOptions, Token);
        var payload = await response.Content.ReadAsStringAsync(Token);

        return (response.StatusCode, string.IsNullOrWhiteSpace(payload) ? default : JsonSerializer.Deserialize<JsonElement>(payload, JsonOptions));
    }

    /// <summary>A save unit's token (the currency rates' by default), computed from the rows exactly as the read model publishes it.</summary>
    private static async Task<string> VersionAsync(ApiFactory factory, string group = SettingKeys.CurrencyDisplay.Group)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var keys = SettingCatalog.KeysInSaveUnitFor(group);

        return SettingsGroupVersion.For(await db.Settings.AsNoTracking().Where(row => keys.Contains(row.Key)).ToListAsync(Token));
    }

    private static async Task<JsonElement> GroupAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri(SettingsUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        return body.GetProperty("groups").EnumerateArray()
            .Single(group => group.GetProperty("groupKey").GetString() == SettingKeys.CurrencyDisplay.Group);
    }

    private static async Task<List<string>> GroupKeysAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri(SettingsUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        return body.GetProperty("groups").EnumerateArray().Select(group => group.GetProperty("groupKey").GetString()!).ToList();
    }

    private static async Task<string?> ValueAsync(ApiFactory factory, string key) =>
        (await RowAsync(factory, key)).Value;

    private static async Task<Setting> RowAsync(ApiFactory factory, string key)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Settings.AsNoTracking().SingleAsync(row => row.Key == key, Token);
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

    private static async Task<int> AlertCountAsync(ApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.AdminAlerts.AsNoTracking().CountAsync(alert => alert.AlertType == AdminAlertTypes.SensitiveSettingsChanged, Token);
    }

    private static async Task RestoreAsync(ApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var keys = DisplayCurrencies.All.Select(currency => currency.SettingKey).ToArray();

        await db.Settings
            .Where(row => keys.Contains(row.Key))
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(row => row.Value, (string?)null)
                    .SetProperty(row => row.UpdatedAtUtc, (DateTime?)null)
                    .SetProperty(row => row.UpdatedByUserId, (int?)null),
                Token);
    }
}
