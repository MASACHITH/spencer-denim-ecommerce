using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Ops.Alerts.Abstractions;
using SpencerDenim.Application.Sales.Payments.PayHere;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Application.Settings.Admin;
using SpencerDenim.Application.Settings.Contracts;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.UnitTests.Application.Sales.PayHere;

/// <summary>
/// The PayHere panel's save (doc 11 P8b; doc 08 §3.8 DB2): validation, the write-only secret, permission,
/// concurrency, one audit row per changed key and one <c>SensitiveSettingsChanged</c> alert per save.
/// </summary>
public sealed class UpdatePayHereSettingsCommandHandlerTests
{
    private static readonly AdminActor Actor = new(7, "owner@example.test", "203.0.113.5", "xunit", "corr-1");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Arrangement
    {
        public Arrangement(bool grantsAll = true, params string[] permissions)
        {
            Store = new MemoryStore();
            Audit = new RecordingAudit();
            Alerts = new FakePayHereAlerts();
            Protector = new ReversibleProtector();
            Permissions = new FakePermissions(grantsAll, permissions);
        }

        public MemoryStore Store { get; }

        public RecordingAudit Audit { get; }

        public FakePayHereAlerts Alerts { get; }

        public ReversibleProtector Protector { get; }

        public FakePermissions Permissions { get; }

        public UpdatePayHereSettingsCommandHandler Handler =>
            new(Store, Audit, Alerts, Protector, Permissions, new PayHereTestClock(PayHereSample.Now));

        public string CurrentToken => SettingsGroupVersion.For(Store.Rows.Values.Where(
            row => row.GroupKey == SettingKeys.PayHere.Group));

        public Task<SettingsWriteResult> SaveAsync(Dictionary<string, string?> fields, string? token = null) =>
            Handler.HandleAsync(
                new UpdatePayHereSettingsCommand(fields, token ?? CurrentToken, Actor),
                UpdatePayHereSettingsCommandHandlerTests.Token);

        public string? Value(string key) => Store.Rows[key].Value;
    }

    private static Dictionary<string, string?> Everything(string secret = "MyMerchantSecret123") =>
        new(StringComparer.Ordinal)
        {
            ["mode"] = "live",
            ["merchantId"] = "1211149",
            ["merchantSecret"] = secret,
            ["pendingMaxDays"] = "3",
            ["expiryMinutes"] = "90",
        };

    // ---- a full save --------------------------------------------------------------------------------

    [Fact]
    public async Task A_full_save_writes_every_key_and_encrypts_the_secret()
    {
        var arranged = new Arrangement();

        var result = await arranged.SaveAsync(Everything());

        var saved = Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Equal(
            [
                SettingKeys.PayHere.Mode,
                SettingKeys.PayHere.MerchantId,
                SettingKeys.PayHere.MerchantSecret,
                SettingKeys.PayHere.PendingMaxDays,
                SettingKeys.PayHere.ExpiryMinutes,
            ],
            saved.ChangedKeys);

        Assert.Equal("live", arranged.Value(SettingKeys.PayHere.Mode));
        Assert.Equal("1211149", arranged.Value(SettingKeys.PayHere.MerchantId));
        Assert.Equal("3", arranged.Value(SettingKeys.PayHere.PendingMaxDays));
        Assert.Equal("90", arranged.Value(SettingKeys.PayHere.ExpiryMinutes));

        // Encrypted at rest: the stored text is the protector's output, not the secret.
        var stored = arranged.Value(SettingKeys.PayHere.MerchantSecret)!;
        Assert.DoesNotContain("MyMerchantSecret123", stored, StringComparison.Ordinal);
        Assert.True(arranged.Protector.TryUnprotect(stored, out var plaintext));
        Assert.Equal("MyMerchantSecret123", plaintext);
    }

    [Fact]
    public async Task Each_change_is_audited_per_key_and_the_secret_is_redacted()
    {
        var arranged = new Arrangement();

        await arranged.SaveAsync(Everything("TopSecretValue"));

        Assert.Equal(5, arranged.Audit.Rows.Count);

        var secretRow = Assert.Single(arranged.Audit.Rows, row => row.EntityId == SettingKeys.PayHere.MerchantSecret);
        Assert.Contains(SettingsAuditTrail.RedactedSecret, secretRow.AfterJson, StringComparison.Ordinal);

        foreach (var row in arranged.Audit.Rows)
        {
            Assert.Equal(AuditActions.SettingChange, row.Action);
            Assert.Equal(Actor.UserId, row.UserId);
            Assert.DoesNotContain("TopSecretValue", row.BeforeJson + row.AfterJson, StringComparison.Ordinal);
            Assert.DoesNotContain("TopSecretValue", row.BeforeJson + row.AfterJson, StringComparison.Ordinal);
        }

        // The ciphertext is not in the audit trail either.
        Assert.DoesNotContain(
            arranged.Value(SettingKeys.PayHere.MerchantSecret)!,
            string.Concat(arranged.Audit.Rows.Select(row => row.BeforeJson + row.AfterJson)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_save_raises_one_sensitive_settings_alert_naming_the_keys_and_never_a_value()
    {
        var arranged = new Arrangement();

        await arranged.SaveAsync(Everything("TopSecretValue"));

        var alert = Assert.Single(arranged.Alerts.Raised);

        Assert.Equal(AdminAlertTypes.SensitiveSettingsChanged, alert.AlertType);
        Assert.Equal(AdminAlertSeverity.Critical, alert.Severity);
        Assert.Equal(PayHereSample.Now, alert.CreatedAtUtc);

        using var details = JsonDocument.Parse(alert.DetailsJson!);
        var keys = details.RootElement.GetProperty("keys").EnumerateArray().Select(key => key.GetString()).ToList();

        Assert.Contains(SettingKeys.PayHere.MerchantSecret, keys);
        Assert.Equal(7, details.RootElement.GetProperty("actorUserId").GetInt32());

        foreach (var forbidden in new[] { "TopSecretValue", "1211149", "live", "owner@example.test", "203.0.113.5" })
        {
            Assert.DoesNotContain(forbidden, alert.DetailsJson + alert.Title, StringComparison.Ordinal);
        }
    }

    // ---- the secret is write-only -------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_secret_keeps_the_stored_one(string? blank)
    {
        var arranged = new Arrangement();
        await arranged.SaveAsync(Everything("OriginalSecret"));
        var before = arranged.Value(SettingKeys.PayHere.MerchantSecret);
        arranged.Audit.Rows.Clear();
        arranged.Alerts.Raised.Clear();

        var result = await arranged.SaveAsync(new Dictionary<string, string?>
        {
            ["merchantSecret"] = blank,
            ["expiryMinutes"] = "120",
        });

        var saved = Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Equal([SettingKeys.PayHere.ExpiryMinutes], saved.ChangedKeys);
        Assert.Equal(before, arranged.Value(SettingKeys.PayHere.MerchantSecret));
        Assert.Equal("120", arranged.Value(SettingKeys.PayHere.ExpiryMinutes));
        Assert.DoesNotContain(arranged.Audit.Rows, row => row.EntityId == SettingKeys.PayHere.MerchantSecret);
    }

    [Fact]
    public async Task A_save_that_does_not_mention_the_secret_leaves_it_alone()
    {
        var arranged = new Arrangement();
        await arranged.SaveAsync(Everything("OriginalSecret"));
        var before = arranged.Value(SettingKeys.PayHere.MerchantSecret);

        await arranged.SaveAsync(new Dictionary<string, string?> { ["mode"] = "sandbox" });

        Assert.Equal(before, arranged.Value(SettingKeys.PayHere.MerchantSecret));
        Assert.Equal("sandbox", arranged.Value(SettingKeys.PayHere.Mode));
    }

    [Fact]
    public async Task A_new_secret_replaces_the_old_one()
    {
        var arranged = new Arrangement();
        await arranged.SaveAsync(Everything("OriginalSecret"));

        await arranged.SaveAsync(new Dictionary<string, string?> { ["merchantSecret"] = "RotatedSecret" });

        Assert.True(arranged.Protector.TryUnprotect(arranged.Value(SettingKeys.PayHere.MerchantSecret), out var plaintext));
        Assert.Equal("RotatedSecret", plaintext);
    }

    [Fact]
    public async Task The_admin_read_model_shows_that_a_secret_is_set_and_never_what_it_is()
    {
        var arranged = new Arrangement();
        await arranged.SaveAsync(Everything("OriginalSecret"));

        var read = await new GetAdminSettingsQueryHandler(arranged.Store, arranged.Permissions)
            .HandleAsync(new GetAdminSettingsQuery(7), Token);

        var group = Assert.Single(read.Groups, candidate => candidate.GroupKey == SettingKeys.PayHere.Group);
        var secret = Assert.Single(group.Settings, setting => setting.Key == SettingKeys.PayHere.MerchantSecret);

        Assert.True(secret.IsSecret);
        Assert.True(secret.HasValue);
        Assert.Null(secret.Value);

        Assert.DoesNotContain("OriginalSecret", JsonSerializer.Serialize(read), StringComparison.Ordinal);
        Assert.DoesNotContain(
            arranged.Value(SettingKeys.PayHere.MerchantSecret)!,
            JsonSerializer.Serialize(read),
            StringComparison.Ordinal);
    }

    // ---- validation ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("mode", "production")]
    [InlineData("mode", "")]
    [InlineData("mode", null)]
    [InlineData("merchantId", "12 11149")]
    [InlineData("merchantId", "1211149;")]
    [InlineData("merchantId", "123456789012345678901234567890123")]
    [InlineData("pendingMaxDays", "0")]
    [InlineData("pendingMaxDays", "15")]
    [InlineData("pendingMaxDays", "-1")]
    [InlineData("pendingMaxDays", "five")]
    [InlineData("pendingMaxDays", "")]
    [InlineData("expiryMinutes", "14")]
    [InlineData("expiryMinutes", "1441")]
    [InlineData("expiryMinutes", "1.5")]
    [InlineData("expiryMinutes", null)]
    public async Task An_invalid_field_is_a_named_validation_error_and_nothing_is_written(string field, string? value)
    {
        var arranged = new Arrangement();

        var result = await arranged.SaveAsync(new Dictionary<string, string?> { [field] = value });

        var invalid = Assert.IsType<SettingsWriteResult.Invalid>(result);
        Assert.Equal(field, Assert.Single(invalid.Errors).Field);
        Assert.Equal(0, arranged.Store.Saves);
        Assert.Empty(arranged.Audit.Rows);
        Assert.Empty(arranged.Alerts.Raised);
    }

    [Fact]
    public async Task A_secret_with_a_control_character_or_over_the_length_is_refused()
    {
        var arranged = new Arrangement();

        foreach (var bad in new[] { "abc\u0007def", new string('x', UpdatePayHereSettingsCommandHandler.MerchantSecretMaxLength + 1) })
        {
            var result = await arranged.SaveAsync(new Dictionary<string, string?> { ["merchantSecret"] = bad });

            Assert.Equal("merchantSecret", Assert.Single(Assert.IsType<SettingsWriteResult.Invalid>(result).Errors).Field);
        }

        Assert.Null(arranged.Value(SettingKeys.PayHere.MerchantSecret));
    }

    [Theory]
    [InlineData("SANDBOX", "sandbox")]
    [InlineData(" Live ", "live")]
    public async Task The_mode_is_stored_in_lower_case(string typed, string stored)
    {
        var arranged = new Arrangement();

        await arranged.SaveAsync(new Dictionary<string, string?> { ["mode"] = typed });

        Assert.Equal(stored, arranged.Value(SettingKeys.PayHere.Mode));
    }

    [Fact]
    public async Task A_blank_merchant_id_clears_it()
    {
        var arranged = new Arrangement();
        await arranged.SaveAsync(Everything());

        await arranged.SaveAsync(new Dictionary<string, string?> { ["merchantId"] = "  " });

        Assert.Null(arranged.Value(SettingKeys.PayHere.MerchantId));
    }

    // ---- permission, concurrency, unchanged --------------------------------------------------------

    [Fact]
    public async Task Without_settings_payments_the_save_is_forbidden_and_names_the_keys()
    {
        var arranged = new Arrangement(grantsAll: false, Permissions.Settings.Edit);

        var result = await arranged.SaveAsync(Everything());

        var forbidden = Assert.IsType<SettingsWriteResult.Forbidden>(result);
        Assert.Contains(SettingKeys.PayHere.MerchantSecret, forbidden.Keys);
        Assert.Equal(0, arranged.Store.Saves);
        Assert.Empty(arranged.Alerts.Raised);
        Assert.Null(arranged.Value(SettingKeys.PayHere.MerchantSecret));
    }

    [Fact]
    public async Task A_holder_of_settings_payments_who_is_not_a_super_admin_may_save()
    {
        var arranged = new Arrangement(grantsAll: false, Permissions.Settings.Payments);

        Assert.IsType<SettingsWriteResult.Succeeded>(await arranged.SaveAsync(Everything()));
    }

    [Fact]
    public async Task A_caller_with_no_user_is_forbidden()
    {
        var arranged = new Arrangement();

        var result = await arranged.Handler.HandleAsync(
            new UpdatePayHereSettingsCommand(Everything(), arranged.CurrentToken, AdminActor.Unknown),
            Token);

        Assert.IsType<SettingsWriteResult.Forbidden>(result);
    }

    [Fact]
    public async Task A_stale_token_is_a_conflict_and_a_missing_one_is_refused()
    {
        var arranged = new Arrangement();
        var token = arranged.CurrentToken;

        await arranged.SaveAsync(new Dictionary<string, string?> { ["mode"] = "live" }, token);

        var stale = await arranged.Handler.HandleAsync(
            new UpdatePayHereSettingsCommand(Everything(), token, Actor), Token);
        Assert.IsType<SettingsWriteResult.Conflict>(stale);

        var missing = await arranged.Handler.HandleAsync(
            new UpdatePayHereSettingsCommand(Everything(), null, Actor), Token);
        Assert.IsType<SettingsWriteResult.Conflict>(missing);
    }

    [Fact]
    public async Task Saving_what_is_already_stored_changes_nothing_and_raises_nothing()
    {
        var arranged = new Arrangement();
        await arranged.SaveAsync(new Dictionary<string, string?> { ["mode"] = "live", ["expiryMinutes"] = "90" });
        arranged.Audit.Rows.Clear();
        arranged.Alerts.Raised.Clear();
        var saves = arranged.Store.Saves;

        var result = await arranged.SaveAsync(new Dictionary<string, string?> { ["mode"] = "live", ["expiryMinutes"] = "90" });

        Assert.IsType<SettingsWriteResult.Unchanged>(result);
        Assert.Equal(saves, arranged.Store.Saves);
        Assert.Empty(arranged.Audit.Rows);
        Assert.Empty(arranged.Alerts.Raised);
    }

    [Fact]
    public async Task A_missing_seeded_row_is_reported_as_not_seeded()
    {
        var arranged = new Arrangement();
        arranged.Store.Rows.Remove(SettingKeys.PayHere.ExpiryMinutes);

        Assert.IsType<SettingsWriteResult.NotSeeded>(
            await arranged.SaveAsync(new Dictionary<string, string?> { ["mode"] = "live" }));
    }

    [Fact]
    public async Task A_lost_race_at_save_time_is_a_conflict_with_nothing_alerted()
    {
        var arranged = new Arrangement();
        arranged.Store.NextSaveOutcome = SettingsSaveOutcome.ConcurrencyConflict;

        var result = await arranged.SaveAsync(new Dictionary<string, string?> { ["mode"] = "live" });

        Assert.IsType<SettingsWriteResult.Conflict>(result);
    }

    // ---- the catalogue ------------------------------------------------------------------------------

    [Fact]
    public void Every_PayHere_key_is_guarded_by_settings_payments_and_belongs_to_one_save_unit()
    {
        var keys = SettingCatalog.KeysIn(SettingKeys.PayHere.Group);

        Assert.Equal(5, keys.Count);
        Assert.All(keys, key => Assert.Equal(Permissions.Settings.Payments, SettingCatalog.Find(key)!.RequiredPermission));
        Assert.Equal(keys, SettingCatalog.KeysInSaveUnitFor(SettingKeys.PayHere.Group));
    }

    [Fact]
    public void The_seeded_defaults_are_the_figures_P8b_states_and_the_account_is_empty()
    {
        Assert.Equal("sandbox", SettingCatalog.Find(SettingKeys.PayHere.Mode)!.SeedValue);
        Assert.Equal("5", SettingCatalog.Find(SettingKeys.PayHere.PendingMaxDays)!.SeedValue);
        Assert.Equal("60", SettingCatalog.Find(SettingKeys.PayHere.ExpiryMinutes)!.SeedValue);
        Assert.Null(SettingCatalog.Find(SettingKeys.PayHere.MerchantId)!.SeedValue);
        Assert.Null(SettingCatalog.Find(SettingKeys.PayHere.MerchantSecret)!.SeedValue);
    }

    // ---- fakes --------------------------------------------------------------------------------------

    /// <summary>Seeded like the real store: every catalogued key, with a SeedValue where the catalogue has one.</summary>
    internal sealed class MemoryStore : ISettingsStore
    {
        public MemoryStore()
        {
            var version = (byte)1;

            foreach (var definition in SettingCatalog.All)
            {
                Rows[definition.Key] = new Setting
                {
                    Key = definition.Key,
                    GroupKey = definition.GroupKey,
                    ValueType = definition.ValueType,
                    IsSecret = definition.IsSecret,
                    Value = definition.SeedValue,
                    RequiredPermission = definition.RequiredPermission,
                    Description = definition.Description,
                    RowVersion = [0, 0, 0, 0, 0, 0, 0, version++],
                };
            }
        }

        public Dictionary<string, Setting> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Saves { get; private set; }

        public SettingsSaveOutcome? NextSaveOutcome { get; set; }

        public Task<IReadOnlyList<Setting>> ListAsync(
            IReadOnlyCollection<string> keys,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Setting>>(
                keys.Where(Rows.ContainsKey)
                    .Select(key => Rows[key])
                    .OrderBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList());

        public Task<SettingImageAsset?> FindUsableImageAsync(Guid publicId, CancellationToken cancellationToken) =>
            Task.FromResult<SettingImageAsset?>(null);

        public Task<SettingImageAsset?> FindUsableImageByIdAsync(int assetId, CancellationToken cancellationToken) =>
            Task.FromResult<SettingImageAsset?>(null);

        public Task ReplaceMediaUsageAsync(string settingKey, string usageRole, int? newAssetId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<SettingsSaveOutcome> SaveAsync(int? actingUserId, CancellationToken cancellationToken)
        {
            if (NextSaveOutcome is { } forced)
            {
                return Task.FromResult(forced);
            }

            Saves++;

            // A rowversion moves itself on every update.
            foreach (var row in Rows.Values)
            {
                row.RowVersion = [0, 0, 0, 0, 0, 0, (byte)Saves, row.RowVersion![7]];
            }

            return Task.FromResult(SettingsSaveOutcome.Saved);
        }
    }

    internal sealed class FakePermissions(bool grantsAll, IReadOnlyCollection<string> permissions)
        : IEffectivePermissionsProvider
    {
        public Task<EffectivePermissions> GetEffectivePermissionsAsync(int userId, CancellationToken cancellationToken) =>
            Task.FromResult(new EffectivePermissions(grantsAll, permissions));
    }

    internal sealed class RecordingAudit : IAuditLogWriter
    {
        public List<AuditLog> Rows { get; } = [];

        public Task WriteAsync(AuditLog entry, CancellationToken cancellationToken)
        {
            Rows.Add(entry);
            return Task.CompletedTask;
        }

        public void Enlist(AuditLog entry) => Rows.Add(entry);
    }

    /// <summary>
    /// A protector that is recognisably not the identity: the output is base64 of the reversed text, so a
    /// test that finds the plaintext in stored text has found a real leak.
    /// </summary>
    internal sealed class ReversibleProtector : ISettingSecretProtector
    {
        public string Protect(string plaintext) =>
            "CIPHER:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(new string(plaintext.Reverse().ToArray())));

        public bool TryUnprotect(string? protectedValue, out string plaintext)
        {
            plaintext = string.Empty;

            if (protectedValue is null || !protectedValue.StartsWith("CIPHER:", StringComparison.Ordinal))
            {
                return false;
            }

            plaintext = new string(System.Text.Encoding.UTF8
                .GetString(Convert.FromBase64String(protectedValue["CIPHER:".Length..]))
                .Reverse()
                .ToArray());

            return true;
        }
    }
}
