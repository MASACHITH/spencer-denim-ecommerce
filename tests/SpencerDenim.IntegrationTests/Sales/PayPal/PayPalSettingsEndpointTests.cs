using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Payments.PayPal;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales.PayPal;

/// <summary>
/// PayPal's panel on the settings screen, <c>PUT /api/admin/settings/paypal</c> (PAY-02, ADM-22; doc 08 §3.8):
/// who may use it, that the client secret is write-only, that the rate's age cannot be faked, and that every
/// change is audited and tells the Super Admins.
/// <para>
/// <b>Every test puts the rows back.</b> The assembly shares one database; a PayPal credential or a rate left
/// behind would be read by the next class's checkout. The alerts a test raised are removed too, since banner
/// counts are other tests' assertions - the audit rows are not, because <c>ops.AuditLogs</c> has no delete path
/// (and these tests only ever look at rows they wrote themselves).
/// </para>
/// </summary>
public sealed class PayPalSettingsEndpointTests(SqlServerDatabaseFixture sqlServer) : BankTransferTestBase(sqlServer)
{
    private const string SettingsUrl = "/api/admin/settings";
    private const string PayPalUrl = "/api/admin/settings/paypal";

    private static readonly string[] AllKeys =
    [
        .. SettingCatalog.KeysIn(SettingKeys.PayPal.Group),
        SettingKeys.PayPal.ExpiryMinutes,
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly DateTime _startedAt = DateTime.UtcNow.AddSeconds(-1);
    private readonly Dictionary<string, string?> _original = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The newest audit row that existed when this test began. <see cref="_startedAt"/> alone cannot say which
    /// rows are this test's: it looks back a second, audit rows cannot be deleted, and the previous test in the
    /// class may have saved the same key well inside that second - so <c>Assert.Single</c> counted its row too
    /// (it failed in the class run and passed alone). An id is exact.
    /// </summary>
    private long? _auditBaseline;

    /// <summary>A Super Admin: both settings permissions, which is what the panel's fields need between them.</summary>
    private async Task<HttpClient> SuperAdminAsync()
    {
        await RememberAsync();
        await RememberAuditBaselineAsync();
        return await MediaTestHost.SignInAsync(Factory, Permissions.Settings.Payments, Permissions.Settings.Edit);
    }

    private async Task RememberAuditBaselineAsync()
    {
        if (_auditBaseline is not null)
        {
            return;
        }

        using var scope = CreateScope();

        _auditBaseline = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .AsNoTracking()
            .MaxAsync(log => (long?)log.Id, Token) ?? 0;
    }

    private async Task RememberAsync()
    {
        if (_original.Count > 0)
        {
            return;
        }

        using var scope = CreateScope();

        foreach (var row in await scope.ServiceProvider.GetRequiredService<AppDbContext>().Settings
            .AsNoTracking()
            .Where(setting => AllKeys.Contains(setting.Key))
            .Select(setting => new { setting.Key, setting.Value })
            .ToListAsync(Token))
        {
            _original[row.Key] = row.Value;
        }
    }

    private static async Task<string> VersionAsync(HttpClient client)
    {
        var body = await client.GetFromJsonAsync<JsonElement>(SettingsUrl, Token);

        return body.GetProperty("groups").EnumerateArray()
            .Single(group => group.GetProperty("groupKey").GetString() == SettingKeys.PayPal.Group)
            .GetProperty("rowVersion").GetString()!;
    }

    /// <summary>Sends the panel's body, with the group's current row version unless the test supplies one.</summary>
    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, Dictionary<string, object?> fields, string? rowVersion = null)
    {
        fields["rowVersion"] = rowVersion ?? await VersionAsync(client);

        return await client.PutAsJsonAsync(PayPalUrl, fields, Json, Token);
    }

    private async Task<string?> StoredAsync(string key)
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Settings
            .AsNoTracking()
            .Where(setting => setting.Key == key)
            .Select(setting => setting.Value)
            .SingleAsync(Token);
    }

    private async Task<PayPalSettings> ReadSettingsAsync()
    {
        using var scope = Factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IPayPalSettingsReader>().ReadAsync(Token);
    }

    private async Task<List<AuditLog>> AuditRowsAsync(string key)
    {
        using var scope = CreateScope();
        var baseline = _auditBaseline ?? 0;

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .AsNoTracking()
            .Where(log => log.EntityType == SettingsAuditTrail.EntityType
                && log.EntityId == key
                && log.Id > baseline
                && log.OccurredAtUtc >= _startedAt)
            .OrderBy(log => log.Id)
            .ToListAsync(Token);
    }

    private async Task<List<AdminAlert>> SensitiveAlertsAsync()
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminAlerts
            .AsNoTracking()
            .Where(alert => alert.AlertType == AdminAlertTypes.SensitiveSettingsChanged && alert.CreatedAtUtc >= _startedAt)
            .OrderBy(alert => alert.Id)
            .ToListAsync(Token);
    }

    public override async ValueTask DisposeAsync()
    {
        if (SqlServer.IsAvailable && _original.Count > 0)
        {
            using var scope = SqlServer.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            foreach (var (key, value) in _original)
            {
                await db.Settings
                    .Where(setting => setting.Key == key)
                    .ExecuteUpdateAsync(setting => setting.SetProperty(row => row.Value, value), CancellationToken.None);
            }

            await db.AdminAlerts
                .Where(alert => alert.AlertType == AdminAlertTypes.SensitiveSettingsChanged && alert.CreatedAtUtc >= _startedAt)
                .ExecuteDeleteAsync(CancellationToken.None);
        }

        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------------------ who may

    [Fact]
    public async Task The_panel_refuses_an_unauthenticated_caller()
    {
        using var client = Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = BaseAddress,
        });

        using var response = await client.PutAsJsonAsync(PayPalUrl, new { rowVersion = "AAAA" }, Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_panel_refuses_a_user_who_holds_only_Settings_Edit()
    {
        await RememberAsync();
        using var client = await MediaTestHost.SignInAsync(Factory, Permissions.Settings.Edit);

        using var response = await client.PutAsJsonAsync(PayPalUrl, new { clientId = "x", rowVersion = "AAAA" }, Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await StoredAsync(SettingKeys.PayPal.ClientId));
    }

    /// <summary>Doc 08 §3.8 DB2: a group the caller may not read is absent, so the PayPal group's existence is not confirmed to a Store Manager.</summary>
    [Fact]
    public async Task The_PayPal_group_is_absent_from_the_read_for_a_user_without_Settings_Payments()
    {
        await RememberAsync();
        using var editor = await MediaTestHost.SignInAsync(Factory, Permissions.Settings.Edit);

        var body = await editor.GetFromJsonAsync<JsonElement>(SettingsUrl, Token);
        var groups = body.GetProperty("groups").EnumerateArray().Select(group => group.GetProperty("groupKey").GetString()).ToList();

        Assert.DoesNotContain(SettingKeys.PayPal.Group, groups);

        // But the one PayPal key doc 08 files under Orders is Settings.Edit, and so is theirs to read.
        var keys = body.GetProperty("groups").EnumerateArray()
            .SelectMany(group => group.GetProperty("settings").EnumerateArray())
            .Select(setting => setting.GetProperty("key").GetString())
            .ToList();

        Assert.Contains(SettingKeys.PayPal.ExpiryMinutes, keys);
        Assert.DoesNotContain(SettingKeys.PayPal.ClientSecret, keys);
    }

    [Fact]
    public async Task A_user_with_Settings_Payments_alone_can_save_the_panel_but_not_the_one_Settings_Edit_field()
    {
        await RememberAsync();
        using var payments = await MediaTestHost.SignInAsync(Factory, Permissions.Settings.Payments);

        using (var ok = await PutAsync(payments, new() { ["clientId"] = "payments-only-client" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        }

        Assert.Equal("payments-only-client", await StoredAsync(SettingKeys.PayPal.ClientId));

        // expiryMinutes is doc 08's Orders.PayPalExpiryMinutes, Settings.Edit: refused by name, nothing written.
        using var refused = await PutAsync(payments, new() { ["expiryMinutes"] = "45" });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Contains(
            SettingKeys.PayPal.ExpiryMinutes,
            problem.GetProperty("keys").EnumerateArray().Select(key => key.GetString()));
        Assert.Equal("60", await StoredAsync(SettingKeys.PayPal.ExpiryMinutes));
    }

    // ------------------------------------------------------------------------------ the secret

    [Fact]
    public async Task The_client_secret_is_stored_encrypted_and_never_returned_by_any_read()
    {
        using var client = await SuperAdminAsync();
        const string Secret = "EBWKjlELKMYqRNQ6sYvFo64FtaRLRR5BdHEESmha49TM";

        using (var response = await PutAsync(client, new() { ["clientSecret"] = Secret }))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        // The column holds ciphertext - not the plaintext, and not anything containing it.
        var stored = await StoredAsync(SettingKeys.PayPal.ClientSecret);
        Assert.False(string.IsNullOrEmpty(stored));
        Assert.DoesNotContain(Secret, stored, StringComparison.Ordinal);

        using (var scope = Factory.Services.CreateScope())
        {
            Assert.True(scope.ServiceProvider.GetRequiredService<ISettingSecretProtector>().TryUnprotect(stored, out var roundTrip));
            Assert.Equal(Secret, roundTrip);
        }

        // The read model says only that one is set.
        var body = await client.GetFromJsonAsync<JsonElement>(SettingsUrl, Token);
        var raw = body.GetRawText();
        Assert.DoesNotContain(Secret, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(stored, raw, StringComparison.Ordinal);

        var setting = body.GetProperty("groups").EnumerateArray()
            .SelectMany(group => group.GetProperty("settings").EnumerateArray())
            .Single(candidate => candidate.GetProperty("key").GetString() == SettingKeys.PayPal.ClientSecret);

        Assert.Equal(JsonValueKind.Null, setting.GetProperty("value").ValueKind);
        Assert.True(setting.GetProperty("isSecret").GetBoolean());
        Assert.True(setting.GetProperty("hasValue").GetBoolean());
        Assert.Equal("Secret", setting.GetProperty("valueType").GetString());

        // The response to the save carries nothing either.
        using var again = await PutAsync(client, new() { ["clientSecret"] = Secret });
        Assert.DoesNotContain(Secret, await again.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blank_or_missing_secret_keeps_the_stored_one_and_a_new_one_replaces_it()
    {
        using var client = await SuperAdminAsync();

        using (await PutAsync(client, new() { ["clientSecret"] = "first-secret-value" }))
        {
        }

        var storedFirst = await StoredAsync(SettingKeys.PayPal.ClientSecret);

        // Absent: another field is saved, the secret is untouched.
        using (var other = await PutAsync(client, new() { ["clientId"] = "some-client" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, other.StatusCode);
        }

        // Blank, empty, and null: all mean "keep what is saved".
        foreach (var blank in new object?[] { string.Empty, "   ", null })
        {
            using var response = await PutAsync(client, new() { ["clientSecret"] = blank, ["clientId"] = "some-client" });
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        Assert.Equal(storedFirst, await StoredAsync(SettingKeys.PayPal.ClientSecret));
        Assert.Equal("first-secret-value", (await ReadSettingsAsync()).ClientSecret);

        // A new one replaces it.
        using (var replaced = await PutAsync(client, new() { ["clientSecret"] = "second-secret-value" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, replaced.StatusCode);
        }

        Assert.Equal("second-secret-value", (await ReadSettingsAsync()).ClientSecret);
    }

    /// <summary>Retyping the secret that is already saved is not a change: no audit row and no banner.</summary>
    [Fact]
    public async Task Sending_the_same_secret_again_is_not_a_change()
    {
        using var client = await SuperAdminAsync();

        using (await PutAsync(client, new() { ["clientSecret"] = "same-secret-value" }))
        {
        }

        var audits = (await AuditRowsAsync(SettingKeys.PayPal.ClientSecret)).Count;
        var alerts = (await SensitiveAlertsAsync()).Count;

        using var response = await PutAsync(client, new() { ["clientSecret"] = "same-secret-value" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(audits, (await AuditRowsAsync(SettingKeys.PayPal.ClientSecret)).Count);
        Assert.Equal(alerts, (await SensitiveAlertsAsync()).Count);
    }

    /// <summary>Doc 08 §3.8 redaction list: a secret is logged as "[secret changed]", never as a value.</summary>
    [Fact]
    public async Task The_audit_trail_records_one_row_per_changed_key_and_never_a_secret()
    {
        using var client = await SuperAdminAsync();
        const string Secret = "audit-me-not-this-value-7788";

        using (var response = await PutAsync(client, new()
        {
            ["clientId"] = "audited-client",
            ["clientSecret"] = Secret,
            ["webhookId"] = "AUDITED-WEBHOOK",
        }))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var secretRow = Assert.Single(await AuditRowsAsync(SettingKeys.PayPal.ClientSecret));
        Assert.Equal(AuditActions.SettingChange, secretRow.Action);
        Assert.Contains(SettingsAuditTrail.RedactedSecret, secretRow.AfterJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, secretRow.AfterJson ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, secretRow.BeforeJson ?? string.Empty, StringComparison.Ordinal);

        var clientRow = Assert.Single(await AuditRowsAsync(SettingKeys.PayPal.ClientId));
        Assert.Contains("audited-client", clientRow.AfterJson, StringComparison.Ordinal);
        Assert.NotNull(clientRow.UserId);

        Assert.Single(await AuditRowsAsync(SettingKeys.PayPal.WebhookId));

        // Nothing that did not change has a row.
        Assert.Empty(await AuditRowsAsync(SettingKeys.PayPal.Mode));
    }

    // ------------------------------------------------------------------------------ the alert

    [Fact]
    public async Task A_change_raises_a_SensitiveSettingsChanged_alert_naming_keys_and_never_values()
    {
        using var client = await SuperAdminAsync();
        const string Secret = "alert-must-not-hold-this-4455";

        using (var response = await PutAsync(client, new()
        {
            ["clientId"] = "alert-client-id",
            ["clientSecret"] = Secret,
            ["mode"] = "live",
        }))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var alert = Assert.Single(await SensitiveAlertsAsync());

        Assert.Equal(AdminAlertSeverity.Critical, alert.Severity);
        Assert.Null(alert.AcknowledgedAtUtc);

        using var details = JsonDocument.Parse(alert.DetailsJson!);
        var keys = details.RootElement.GetProperty("keys").EnumerateArray().Select(key => key.GetString()).ToList();

        Assert.Contains(SettingKeys.PayPal.ClientId, keys);
        Assert.Contains(SettingKeys.PayPal.ClientSecret, keys);
        Assert.Contains(SettingKeys.PayPal.Mode, keys);
        Assert.True(details.RootElement.GetProperty("changedByUserId").GetInt32() > 0);

        // Names, never values.
        Assert.DoesNotContain(Secret, alert.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("alert-client-id", alert.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("alert-client-id", alert.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("@", alert.DetailsJson, StringComparison.Ordinal);
    }

    /// <summary>"Every change alerts all Super Admins" - and a save that changed nothing is not a change.</summary>
    [Fact]
    public async Task A_save_that_changes_nothing_raises_no_alert_and_writes_no_audit_row()
    {
        using var client = await SuperAdminAsync();

        using (await PutAsync(client, new() { ["clientId"] = "steady-client" }))
        {
        }

        var alerts = (await SensitiveAlertsAsync()).Count;
        var audits = (await AuditRowsAsync(SettingKeys.PayPal.ClientId)).Count;

        using var response = await PutAsync(client, new() { ["clientId"] = "steady-client" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(alerts, (await SensitiveAlertsAsync()).Count);
        Assert.Equal(audits, (await AuditRowsAsync(SettingKeys.PayPal.ClientId)).Count);
    }

    /// <summary>Only Super Admins (GrantsAll) see it: an Orders.Approve holder gets nothing from the alerts endpoint.</summary>
    [Fact]
    public async Task The_alert_is_visible_to_a_Super_Admin_and_not_to_order_staff()
    {
        using var client = await SuperAdminAsync();

        using (await PutAsync(client, new() { ["clientId"] = "visibility-client" }))
        {
        }

        using var orderStaff = await MediaTestHost.SignInAsync(Factory, Permissions.Orders.Approve, Permissions.Orders.Refund);
        var seen = await orderStaff.GetFromJsonAsync<JsonElement>("/api/admin/alerts", Token);

        Assert.DoesNotContain(
            seen.GetProperty("alerts").EnumerateArray(),
            alert => alert.GetProperty("alertType").GetString() == AdminAlertTypes.SensitiveSettingsChanged);
    }

    // ------------------------------------------------------------------------------ the rate

    [Fact]
    public async Task Changing_the_rate_stamps_when_it_was_set_and_nothing_a_caller_sends_can_fake_that()
    {
        using var client = await SuperAdminAsync();
        var before = DateTime.UtcNow;

        using (var response = await PutAsync(client, new() { ["lkrPerUsd"] = "301.5" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        Assert.Equal("301.50", await StoredAsync(SettingKeys.PayPal.LkrPerUsd));

        var stamp = await StampAsync();
        Assert.InRange(stamp, before.AddSeconds(-2), DateTime.UtcNow.AddSeconds(2));

        // Saving something else does not freshen a rate nobody confirmed.
        await Task.Delay(50, Token);

        using (await PutAsync(client, new() { ["fxBufferPercent"] = "4" }))
        {
        }

        Assert.Equal(stamp, await StampAsync());

        // Neither does sending the same rate again.
        using (await PutAsync(client, new() { ["lkrPerUsd"] = "301.50" }))
        {
        }

        Assert.Equal(stamp, await StampAsync());

        // And a date typed into the body is not a field: it is ignored, so a stale rate cannot be made fresh.
        using (var forged = await PutAsync(client, new()
        {
            ["rateUpdatedAtUtc"] = "2099-01-01T00:00:00Z",
            ["fxBufferPercent"] = "4.5",
        }))
        {
            Assert.Equal(HttpStatusCode.NoContent, forged.StatusCode);
        }

        Assert.Equal(stamp, await StampAsync());
    }

    [Fact]
    public async Task Confirming_the_rate_restamps_it_without_changing_it()
    {
        using var client = await SuperAdminAsync();

        using (await PutAsync(client, new() { ["lkrPerUsd"] = "300" }))
        {
        }

        var stamp = await StampAsync();
        await Task.Delay(1100, Token);

        using (var response = await PutAsync(client, new() { ["confirmRate"] = true }))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        Assert.Equal("300.00", await StoredAsync(SettingKeys.PayPal.LkrPerUsd));
        Assert.True(await StampAsync() > stamp);
    }

    [Fact]
    public async Task There_is_no_rate_to_confirm_until_one_is_entered()
    {
        using var client = await SuperAdminAsync();
        await Clear(client, SettingKeys.PayPal.LkrPerUsd, "lkrPerUsd");

        using var response = await PutAsync(client, new() { ["confirmRate"] = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await StoredAsync(SettingKeys.PayPal.RateUpdatedAtUtc));
    }

    [Fact]
    public async Task Clearing_the_rate_leaves_PayPal_unavailable()
    {
        using var client = await SuperAdminAsync();

        using (await PutAsync(client, new() { ["clientId"] = "c", ["clientSecret"] = "s", ["webhookId"] = "w", ["lkrPerUsd"] = "300" }))
        {
        }

        Assert.Equal(PayPalAvailability.Available, (await ReadSettingsAsync()).AvailabilityAt(DateTime.UtcNow));

        using (await PutAsync(client, new() { ["lkrPerUsd"] = null }))
        {
        }

        Assert.Equal(PayPalAvailability.RateMissing, (await ReadSettingsAsync()).AvailabilityAt(DateTime.UtcNow));
    }

    private async Task Clear(HttpClient client, string key, string field)
    {
        if (await StoredAsync(key) is not null)
        {
            using var response = await PutAsync(client, new() { [field] = null });
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
    }

    private async Task<DateTime> StampAsync() =>
        DateTime.Parse(
            (await StoredAsync(SettingKeys.PayPal.RateUpdatedAtUtc))!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    // ------------------------------------------------------------------------------ the switch needs no restart

    [Fact]
    public async Task Switching_between_sandbox_and_live_takes_effect_on_the_next_read_with_no_restart()
    {
        using var client = await SuperAdminAsync();

        using (await PutAsync(client, new() { ["mode"] = "live" }))
        {
        }

        var live = await ReadSettingsAsync();
        Assert.Equal(PayPalMode.Live, live.Mode);
        Assert.Equal("https://api-m.paypal.com", live.ApiBaseUrl);

        using (await PutAsync(client, new() { ["mode"] = "sandbox" }))
        {
        }

        var sandbox = await ReadSettingsAsync();
        Assert.Equal(PayPalMode.Sandbox, sandbox.Mode);
        Assert.Equal("https://api-m.sandbox.paypal.com", sandbox.ApiBaseUrl);
    }

    // ------------------------------------------------------------------------------ validation and concurrency

    [Theory]
    [InlineData("mode", "production")]
    [InlineData("mode", "")]
    [InlineData("lkrPerUsd", "-3")]
    [InlineData("lkrPerUsd", "three hundred")]
    [InlineData("lkrPerUsd", "99999")]
    [InlineData("fxBufferPercent", "50")]
    [InlineData("rateMaxAgeDays", "0")]
    [InlineData("captureLockHours", "99")]
    [InlineData("captureRecoveryMaxHours", "1000")]
    [InlineData("pendingCaptureMaxDays", "30")]
    [InlineData("expiryMinutes", "5")]
    [InlineData("clientId", "has space in it")]
    [InlineData("webhookId", "no;semicolons")]
    public async Task A_value_that_cannot_be_right_is_a_400_naming_the_field_and_writes_nothing(string field, string value)
    {
        using var client = await SuperAdminAsync();
        var audits = (await AuditRowsAsync(SettingKeys.PayPal.Mode)).Count;

        using var response = await PutAsync(client, new() { [field] = value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Equal(audits, (await AuditRowsAsync(SettingKeys.PayPal.Mode)).Count);
        Assert.Empty(await SensitiveAlertsAsync());
    }

    [Fact]
    public async Task A_secret_with_whitespace_in_it_is_refused_without_repeating_it()
    {
        using var client = await SuperAdminAsync();

        using var response = await PutAsync(client, new() { ["clientSecret"] = "secret with spaces inside" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("secret with spaces", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_field_of_the_wrong_json_type_is_a_named_field_error_and_not_malformed_json()
    {
        using var client = await SuperAdminAsync();

        using var response = await PutAsync(client, new() { ["lkrPerUsd"] = 300 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.True(problem.GetProperty("errors").TryGetProperty("lkrPerUsd", out _));
    }

    [Fact]
    public async Task A_stale_or_missing_row_version_is_a_409_and_writes_nothing()
    {
        using var client = await SuperAdminAsync();
        var stale = await VersionAsync(client);

        using (await PutAsync(client, new() { ["clientId"] = "moves-the-version" }))
        {
        }

        using (var response = await PutAsync(client, new() { ["clientId"] = "from-a-stale-form" }, stale))
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        Assert.Equal("moves-the-version", await StoredAsync(SettingKeys.PayPal.ClientId));

        using var missing = await client.PutAsJsonAsync(PayPalUrl, new { clientId = "no-version" }, Json, Token);

        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
    }

    [Fact]
    public async Task The_panel_saves_the_Settings_Edit_field_for_a_user_who_holds_both_permissions()
    {
        using var client = await SuperAdminAsync();

        using var response = await PutAsync(client, new() { ["expiryMinutes"] = "45" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("45", await StoredAsync(SettingKeys.PayPal.ExpiryMinutes));
    }

    [Fact]
    public async Task The_seeded_PayPal_rows_carry_the_defaults_the_docs_state_and_nothing_a_client_knows()
    {
        using var scope = CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Settings
            .AsNoTracking()
            .Where(setting => AllKeys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, StringComparer.OrdinalIgnoreCase, Token);

        Assert.Equal(AllKeys.Length, rows.Count);

        // The seeded numbers are the docs' - read back through the catalogue the seeder wrote them from, but
        // asserted by value here so a changed default is a conscious edit of both.
        var seeded = SettingCatalog.All
            .Where(definition => AllKeys.Contains(definition.Key))
            .ToDictionary(definition => definition.Key, definition => definition.SeedValue, StringComparer.OrdinalIgnoreCase);

        Assert.Equal("sandbox", seeded[SettingKeys.PayPal.Mode]);
        Assert.Equal("3.00", seeded[SettingKeys.PayPal.FxBufferPercent]);
        Assert.Equal("7", seeded[SettingKeys.PayPal.RateMaxAgeDays]);
        Assert.Equal("3", seeded[SettingKeys.PayPal.CaptureLockHours]);
        Assert.Equal("24", seeded[SettingKeys.PayPal.CaptureRecoveryMaxHours]);
        Assert.Equal("5", seeded[SettingKeys.PayPal.PendingCaptureMaxDays]);
        Assert.Equal("60", seeded[SettingKeys.PayPal.ExpiryMinutes]);
        Assert.Null(seeded[SettingKeys.PayPal.ClientId]);
        Assert.Null(seeded[SettingKeys.PayPal.ClientSecret]);
        Assert.Null(seeded[SettingKeys.PayPal.WebhookId]);
        Assert.Null(seeded[SettingKeys.PayPal.LkrPerUsd]);

        Assert.True(rows[SettingKeys.PayPal.ClientSecret].IsSecret);
        Assert.Equal(SettingValueType.Secret, rows[SettingKeys.PayPal.ClientSecret].ValueType);
        Assert.Equal(1, rows.Values.Count(row => row.IsSecret));
    }
}
