using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Payments.PayHere;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// The PayHere panel's endpoints (doc 11 P8b; <c>Settings.Payments</c>): who may use them, that the merchant
/// secret is write-only at every layer, that a save is audited and raises the banner, and that the new value
/// is what the very next checkout uses - against a real SQL Server.
/// </summary>
public sealed class PayHereSettingsEndpointTests(SqlServerDatabaseFixture sqlServer) : PayHereTestBase(sqlServer)
{
    private const string Url = "/api/admin/settings/payhere";
    private const string StatusUrl = "/api/admin/settings/payhere/status";
    private const string SettingsUrl = "/api/admin/settings";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ---- who may use it ----------------------------------------------------------------------------

    [Fact]
    public async Task Every_route_refuses_an_unauthenticated_caller()
    {
        using var client = NewNotifyClient();

        using var put = await client.PutAsJsonAsync(new Uri(Url, UriKind.Relative), new { mode = "live" }, JsonOptions, Token);
        using var status = await client.GetAsync(new Uri(StatusUrl, UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
    }

    [Fact]
    public async Task Settings_Edit_is_not_enough_to_read_or_change_the_PayHere_account()
    {
        using var editor = await MediaTestHost.SignInAsync(Factory, Permissions.Settings.Edit);

        var (refused, _) = await PutRawAsync(editor, """{"merchantSecret":"not-theirs-to-set"}""");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using var status = await editor.GetAsync(new Uri(StatusUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Forbidden, status.StatusCode);

        // And the group is absent from what they can read, not merely empty: an empty group would confirm
        // that a merchant account exists and name the keys to go looking for.
        Assert.DoesNotContain(SettingKeys.PayHere.Group, await GroupKeysAsync(editor));

        Assert.Null(await StoredValueAsync(SettingKeys.PayHere.MerchantSecret));
        Assert.Empty(await SettingsAlertsAsync());
    }

    [Fact]
    public async Task A_holder_of_Settings_Payments_sees_the_group_with_its_seeded_defaults_and_no_secret()
    {
        using var client = await MediaTestHost.SignInAsync(Factory, Permissions.Settings.Payments);

        var group = await GroupAsync(client);
        var settings = group.GetProperty("settings").EnumerateArray()
            .ToDictionary(setting => setting.GetProperty("key").GetString()!);

        Assert.Equal(
            [
                SettingKeys.PayHere.Mode,
                SettingKeys.PayHere.MerchantId,
                SettingKeys.PayHere.MerchantSecret,
                SettingKeys.PayHere.PendingMaxDays,
                SettingKeys.PayHere.ExpiryMinutes,
            ],
            settings.Keys);

        Assert.Equal("sandbox", settings[SettingKeys.PayHere.Mode].GetProperty("value").GetString());
        Assert.Equal("5", settings[SettingKeys.PayHere.PendingMaxDays].GetProperty("value").GetString());
        Assert.Equal("60", settings[SettingKeys.PayHere.ExpiryMinutes].GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, settings[SettingKeys.PayHere.MerchantId].GetProperty("value").ValueKind);

        var secret = settings[SettingKeys.PayHere.MerchantSecret];
        Assert.True(secret.GetProperty("isSecret").GetBoolean());
        Assert.False(secret.GetProperty("hasValue").GetBoolean());
        Assert.Equal(JsonValueKind.Null, secret.GetProperty("value").ValueKind);
    }

    // ---- the secret is write-only ------------------------------------------------------------------

    [Fact]
    public async Task A_save_stores_the_secret_encrypted_and_no_endpoint_ever_returns_it()
    {
        using var client = await MediaTestHost.SignInAsync(Factory, Permissions.Settings.Payments);

        var (saved, _) = await PutAsync(client, new
        {
            mode = "live",
            merchantId = MerchantId,
            merchantSecret = Secret,
            pendingMaxDays = "3",
            expiryMinutes = "90",
            rowVersion = await VersionAsync(client),
        });

        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        WillRestorePayHere();

        // Stored as ciphertext: not the secret, and readable only by the host's own protector.
        var stored = (await StoredValueAsync(SettingKeys.PayHere.MerchantSecret))!;
        Assert.DoesNotContain(Secret, stored, StringComparison.Ordinal);

        using (var scope = CreateApplicationScope())
        {
            Assert.True(scope.ServiceProvider.GetRequiredService<ISettingSecretProtector>().TryUnprotect(stored, out var plaintext));
            Assert.Equal(Secret, plaintext);
        }

        Assert.Equal("live", await StoredValueAsync(SettingKeys.PayHere.Mode));
        Assert.Equal(MerchantId, await StoredValueAsync(SettingKeys.PayHere.MerchantId));
        Assert.Equal("3", await StoredValueAsync(SettingKeys.PayHere.PendingMaxDays));
        Assert.Equal("90", await StoredValueAsync(SettingKeys.PayHere.ExpiryMinutes));

        // Every read that could carry it: the settings read, the status read, and the save's own answer.
        using var settingsResponse = await client.GetAsync(new Uri(SettingsUrl, UriKind.Relative), Token);
        var settingsText = await settingsResponse.Content.ReadAsStringAsync(Token);

        using var statusResponse = await client.GetAsync(new Uri(StatusUrl, UriKind.Relative), Token);
        var statusText = await statusResponse.Content.ReadAsStringAsync(Token);

        foreach (var text in new[] { settingsText, statusText, await saved.Content.ReadAsStringAsync(Token) })
        {
            Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain(stored, text, StringComparison.Ordinal);
            Assert.DoesNotContain(PayHereHash.SecretDigest(Secret), text, StringComparison.OrdinalIgnoreCase);
        }

        var secretRow = (await GroupAsync(client)).GetProperty("settings").EnumerateArray()
            .Single(setting => setting.GetProperty("key").GetString() == SettingKeys.PayHere.MerchantSecret);

        Assert.True(secretRow.GetProperty("hasValue").GetBoolean());
        Assert.Equal(JsonValueKind.Null, secretRow.GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task A_blank_secret_keeps_the_stored_one_and_checkout_goes_on_working()
    {
        using var client = await MediaTestHost.SignInAsync(Factory, Permissions.Settings.Payments);

        WillRestorePayHere();

        var (first, _) = await PutAsync(client, new
        {
            merchantId = MerchantId,
            merchantSecret = Secret,
            rowVersion = await VersionAsync(client),
        });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var before = await StoredValueAsync(SettingKeys.PayHere.MerchantSecret);

        // The screen cannot send back a value it never received: blank, then absent.
        foreach (var body in new object[]
        {
            new { merchantSecret = "", expiryMinutes = "50" },
            new { merchantSecret = (string?)null, expiryMinutes = "55" },
            new { expiryMinutes = "60" },
        })
        {
            var version = await VersionAsync(client);
            var (response, _) = await PutRawAsync(client, WithVersion(body, version));

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(before, await StoredValueAsync(SettingKeys.PayHere.MerchantSecret));
        }

        // Still usable: enable the provider row and place an order whose form is signed with the original.
        await EnableProviderRowAsync();

        var placed = await PlacePayHereOrderAsync();
        var fields = (await OrderJsonAsync(placed.PublicId)).GetProperty("payment").GetProperty("redirectForm")
            .GetProperty("fields").EnumerateArray()
            .ToDictionary(field => field.GetProperty("name").GetString()!, field => field.GetProperty("value").GetString()!);

        Assert.Equal(
            PayHereHash.CheckoutHash(MerchantId, placed.OrderReference, PayHereHash.FormatAmount(placed.GrandTotal), "LKR", Secret),
            fields["hash"]);
    }

    [Fact]
    public async Task A_new_secret_takes_effect_at_once_and_the_old_one_stops_working()
    {
        using var client = await MediaTestHost.SignInAsync(Factory, Permissions.Settings.Payments);
        WillRestorePayHere();

        var (first, _) = await PutAsync(client, new
        {
            merchantId = MerchantId,
            merchantSecret = "first-secret-value",
            rowVersion = await VersionAsync(client),
        });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        await EnableProviderRowAsync();
        var placed = await PlacePayHereOrderAsync();

        var (second, _) = await PutAsync(client, new
        {
            merchantSecret = "second-secret-value",
            rowVersion = await VersionAsync(client),
        });
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        using var old = await PostNotifyAsync(NotifyForm(placed.OrderReference, placed.GrandTotal, secret: "first-secret-value"));
        Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);

        using var current = await PostNotifyAsync(NotifyForm(placed.OrderReference, placed.GrandTotal, secret: "second-secret-value"));
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    // ---- audit and the banner ----------------------------------------------------------------------

    [Fact]
    public async Task A_save_writes_one_audit_row_per_changed_key_with_the_secret_redacted_and_raises_one_banner()
    {
        using var client = await MediaTestHost.SignInAsync(Factory, Permissions.Settings.Payments);
        WillRestorePayHere();

        var auditBefore = (await SettingAuditAsync()).Count;
        var alertsBefore = (await SettingsAlertsAsync()).Count;

        var (saved, _) = await PutAsync(client, new
        {
            mode = "live",
            merchantId = MerchantId,
            merchantSecret = "an-audited-secret",
            pendingMaxDays = "4",
            expiryMinutes = "75",
            rowVersion = await VersionAsync(client),
        });
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        var audit = (await SettingAuditAsync()).Skip(auditBefore).ToList();

        string[] changed =
        [
            SettingKeys.PayHere.Mode,
            SettingKeys.PayHere.MerchantId,
            SettingKeys.PayHere.MerchantSecret,
            SettingKeys.PayHere.PendingMaxDays,
            SettingKeys.PayHere.ExpiryMinutes,
        ];

        Assert.Equal(
            changed.Order(StringComparer.Ordinal),
            audit.Select(row => row.EntityId!).Order(StringComparer.Ordinal));

        Assert.All(audit, row => Assert.Equal(SpencerDenim.Domain.Auditing.AuditActions.SettingChange, row.Action));

        var secretRow = audit.Single(row => row.EntityId == SettingKeys.PayHere.MerchantSecret);
        Assert.Contains(SettingsAuditTrail.RedactedSecret, secretRow.AfterJson, StringComparison.Ordinal);

        var stored = (await StoredValueAsync(SettingKeys.PayHere.MerchantSecret))!;

        foreach (var row in audit)
        {
            Assert.DoesNotContain("an-audited-secret", row.BeforeJson + row.AfterJson, StringComparison.Ordinal);
            Assert.DoesNotContain(stored, row.BeforeJson + row.AfterJson, StringComparison.Ordinal);
        }

        // One banner for the save, naming the keys and never a value.
        var banners = (await SettingsAlertsAsync()).Skip(alertsBefore).ToList();
        var banner = Assert.Single(banners);

        Assert.Equal(AdminAlertSeverity.Critical, banner.Severity);
        Assert.Null(banner.AcknowledgedAtUtc);

        using var details = JsonDocument.Parse(banner.DetailsJson!);
        var keys = details.RootElement.GetProperty("keys").EnumerateArray().Select(key => key.GetString()).ToList();

        Assert.Equal(5, keys.Count);
        Assert.Contains(SettingKeys.PayHere.MerchantSecret, keys);

        foreach (var forbidden in new[] { "an-audited-secret", MerchantId, "live", stored })
        {
            Assert.DoesNotContain(forbidden, banner.DetailsJson + banner.Title, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_save_that_changes_nothing_writes_no_audit_row_and_raises_no_banner()
    {
        using var client = await MediaTestHost.SignInAsync(Factory, Permissions.Settings.Payments);
        WillRestorePayHere();

        var auditBefore = (await SettingAuditAsync()).Count;
        var alertsBefore = (await SettingsAlertsAsync()).Count;

        var (response, _) = await PutAsync(client, new
        {
            mode = "sandbox",
            pendingMaxDays = "5",
            expiryMinutes = "60",
            rowVersion = await VersionAsync(client),
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(auditBefore, (await SettingAuditAsync()).Count);
        Assert.Equal(alertsBefore, (await SettingsAlertsAsync()).Count);
    }

    // ---- validation and concurrency ----------------------------------------------------------------

    [Fact]
    public async Task An_invalid_save_is_a_400_naming_the_fields_and_writes_nothing()
    {
        using var client = await MediaTestHost.SignInAsync(Factory, Permissions.Settings.Payments);
        WillRestorePayHere();

        var alertsBefore = (await SettingsAlertsAsync()).Count;

        var (response, body) = await PutAsync(client, new
        {
            mode = "production",
            merchantId = "12 11149",
            pendingMaxDays = "99",
            expiryMinutes = "5",
            merchantSecret = "a-valid-secret",
            rowVersion = await VersionAsync(client),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var errors = body.GetProperty("errors");
        Assert.True(errors.TryGetProperty("mode", out _));
        Assert.True(errors.TryGetProperty("merchantId", out _));
        Assert.True(errors.TryGetProperty("pendingMaxDays", out _));
        Assert.True(errors.TryGetProperty("expiryMinutes", out _));
        Assert.False(errors.TryGetProperty("merchantSecret", out _));

        // Nothing was written, including the valid secret in the same request.
        Assert.Null(await StoredValueAsync(SettingKeys.PayHere.MerchantSecret));
        Assert.Equal("sandbox", await StoredValueAsync(SettingKeys.PayHere.Mode));
        Assert.Equal(alertsBefore, (await SettingsAlertsAsync()).Count);
    }

    [Fact]
    public async Task A_stale_or_missing_version_is_a_409_and_writes_nothing()
    {
        using var client = await MediaTestHost.SignInAsync(Factory, Permissions.Settings.Payments);
        WillRestorePayHere();

        var stale = await VersionAsync(client);

        var (fresh, _) = await PutAsync(client, new { mode = "live", rowVersion = stale });
        Assert.Equal(HttpStatusCode.NoContent, fresh.StatusCode);

        var (conflict, _) = await PutAsync(client, new { mode = "sandbox", rowVersion = stale });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var (missing, _) = await PutRawAsync(client, """{"mode":"sandbox"}""");
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);

        Assert.Equal("live", await StoredValueAsync(SettingKeys.PayHere.Mode));
    }

    [Fact]
    public async Task A_field_of_the_wrong_json_type_is_a_named_field_error()
    {
        using var client = await MediaTestHost.SignInAsync(Factory, Permissions.Settings.Payments);

        var (response, body) = await PutRawAsync(client, """{"expiryMinutes":90,"rowVersion":"x"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(body.GetProperty("errors").TryGetProperty("expiryMinutes", out _));
    }

    // ---- the status read ---------------------------------------------------------------------------

    [Fact]
    public async Task The_status_names_the_notify_address_and_what_is_missing()
    {
        using var client = await MediaTestHost.SignInAsync(Factory, Permissions.Settings.Payments);

        var missing = await StatusAsync(client);

        Assert.Equal($"{Origin}/api/payments/payhere/notify", missing.GetProperty("notifyUrl").GetString());
        Assert.False(missing.GetProperty("providerEnabled").GetBoolean());
        Assert.False(missing.GetProperty("offeredAtCheckout").GetBoolean());
        Assert.Equal("sandbox", missing.GetProperty("mode").GetString());

        var problems = missing.GetProperty("problems").EnumerateArray().Select(problem => problem.GetString()).ToList();
        Assert.Contains(problems, problem => problem!.Contains("merchant ID", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem!.Contains("merchant secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_status_says_offered_only_when_the_row_is_on_and_nothing_is_missing()
    {
        using var client = await MediaTestHost.SignInAsync(Factory, Permissions.Settings.Payments);

        await ConfigurePayHereAsync(enabled: false);
        var off = await StatusAsync(client);
        Assert.False(off.GetProperty("providerEnabled").GetBoolean());
        Assert.False(off.GetProperty("offeredAtCheckout").GetBoolean());
        Assert.Empty(off.GetProperty("problems").EnumerateArray());

        await ConfigurePayHereAsync(enabled: true);
        var on = await StatusAsync(client);
        Assert.True(on.GetProperty("providerEnabled").GetBoolean());
        Assert.True(on.GetProperty("offeredAtCheckout").GetBoolean());

        // The status and checkout agree.
        Assert.Contains(SpencerDenim.Domain.Sales.PaymentProviderCodes.PayHere, await OfferedMethodCodesAsync());
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private async Task EnableProviderRowAsync()
    {
        using var scope = CreateScope();

        var moved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().PaymentProviders
            .Where(provider => provider.Code == SpencerDenim.Domain.Sales.PaymentProviderCodes.PayHere)
            .ExecuteUpdateAsync(provider => provider.SetProperty(row => row.IsEnabled, true), Token);

        Assert.Equal(1, moved);
    }

    private async Task<string?> StoredValueAsync(string key)
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Settings
            .AsNoTracking()
            .Where(setting => setting.Key == key)
            .Select(setting => setting.Value)
            .SingleAsync(Token);
    }

    private static async Task<JsonElement> StatusAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri(StatusUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
    }

    private static async Task<JsonElement> GroupAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri(SettingsUrl, UriKind.Relative), Token);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        return body.GetProperty("groups").EnumerateArray()
            .Single(group => group.GetProperty("groupKey").GetString() == SettingKeys.PayHere.Group)
            .Clone();
    }

    private static async Task<string> VersionAsync(HttpClient client) =>
        (await GroupAsync(client)).GetProperty("rowVersion").GetString()!;

    private static async Task<List<string>> GroupKeysAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri(SettingsUrl, UriKind.Relative), Token);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        return body.GetProperty("groups").EnumerateArray()
            .Select(group => group.GetProperty("groupKey").GetString()!)
            .ToList();
    }

    private static string WithVersion(object body, string version)
    {
        var node = JsonSerializer.SerializeToNode(body, JsonOptions)!.AsObject();
        node["rowVersion"] = version;

        return node.ToJsonString();
    }

    private static Task<(HttpResponseMessage Response, JsonElement Body)> PutAsync(HttpClient client, object body) =>
        PutRawAsync(client, JsonSerializer.Serialize(body, JsonOptions));

    private static async Task<(HttpResponseMessage Response, JsonElement Body)> PutRawAsync(HttpClient client, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PutAsync(new Uri(Url, UriKind.Relative), content, Token);

        JsonElement body = default;
        var text = await response.Content.ReadAsStringAsync(Token);

        if (!string.IsNullOrWhiteSpace(text) && response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.Ordinal) == true)
        {
            body = JsonDocument.Parse(text).RootElement.Clone();
        }

        return (response, body);
    }
}
