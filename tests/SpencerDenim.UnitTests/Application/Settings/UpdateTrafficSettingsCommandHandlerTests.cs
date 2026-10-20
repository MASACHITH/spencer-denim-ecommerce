using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Application.Settings.Admin;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.UnitTests.Application.Settings;

/// <summary>
/// The Traffic settings form's write path (issue #193): the bounds, the cross-field rule, the order of the checks,
/// one audit row per changed key, and absent-is-not-written. The real store is covered by
/// <c>AdminTrafficSettingsTests</c>; this pins the rules in milliseconds and is what the mutation runs exercise.
/// </summary>
public sealed class UpdateTrafficSettingsCommandHandlerTests
{
    private const string Raw = UpdateTrafficSettingsCommandHandler.RawField;
    private const string Bot = UpdateTrafficSettingsCommandHandler.BotField;

    private static readonly AdminActor Actor = new(7, "staff@example.com", null, null, null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- the happy path and the audit trail -----------------------------------------------------------

    [Fact]
    public async Task Saving_both_periods_stores_invariant_digits_and_writes_one_audit_row_per_changed_key()
    {
        var store = new FakeSettingsStore();
        var audit = new RecordingAuditLog();

        var result = await Handler(store, audit).HandleAsync(Command(store, (Raw, "120"), (Bot, "14")), Token);

        var succeeded = Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Equal([SettingKeys.Traffic.RawRetentionDays, SettingKeys.Traffic.BotRetentionDays], succeeded.ChangedKeys);
        Assert.Equal("120", store.Rows[SettingKeys.Traffic.RawRetentionDays].Value);
        Assert.Equal("14", store.Rows[SettingKeys.Traffic.BotRetentionDays].Value);
        Assert.Equal(7, store.Rows[SettingKeys.Traffic.RawRetentionDays].UpdatedByUserId);
        Assert.NotNull(store.Rows[SettingKeys.Traffic.RawRetentionDays].UpdatedAtUtc);
        Assert.Equal(1, store.Saves);

        Assert.Equal(2, audit.Rows.Count);
        Assert.All(audit.Rows, row => Assert.Equal("Setting", row.EntityType));
        Assert.Equal(
            [SettingKeys.Traffic.RawRetentionDays, SettingKeys.Traffic.BotRetentionDays],
            audit.Rows.Select(row => row.EntityId));
        Assert.Contains("\"90\"", audit.Rows[0].BeforeJson, StringComparison.Ordinal);
        Assert.Contains("\"120\"", audit.Rows[0].AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leading_and_trailing_spaces_are_trimmed_before_the_number_is_read()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(Command(store, (Raw, " 60 ")), Token);

        Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Equal("60", store.Rows[SettingKeys.Traffic.RawRetentionDays].Value);
    }

    [Fact]
    public async Task A_save_that_changes_one_key_writes_one_audit_row_and_leaves_the_other_untouched()
    {
        var store = new FakeSettingsStore();
        var audit = new RecordingAuditLog();

        var result = await Handler(store, audit).HandleAsync(Command(store, (Raw, "90"), (Bot, "10")), Token);

        var succeeded = Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Equal([SettingKeys.Traffic.BotRetentionDays], succeeded.ChangedKeys);
        Assert.Single(audit.Rows);
        Assert.Null(store.Rows[SettingKeys.Traffic.RawRetentionDays].UpdatedAtUtc);
    }

    [Fact]
    public async Task Saving_the_values_already_stored_is_unchanged_with_no_audit_row_and_no_save()
    {
        var store = new FakeSettingsStore();
        var audit = new RecordingAuditLog();

        var result = await Handler(store, audit).HandleAsync(Command(store, (Raw, "90"), (Bot, "7")), Token);

        Assert.IsType<SettingsWriteResult.Unchanged>(result);
        Assert.Empty(audit.Rows);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task A_field_the_request_leaves_out_is_not_written()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(Command(store, (Bot, "30")), Token);

        Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Equal("90", store.Rows[SettingKeys.Traffic.RawRetentionDays].Value);
        Assert.Equal("30", store.Rows[SettingKeys.Traffic.BotRetentionDays].Value);
    }

    // ---- the bounds -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Raw, "30")]
    [InlineData(Raw, "365")]
    [InlineData(Bot, "7")]
    [InlineData(Bot, "90")]
    public async Task The_bounds_themselves_are_accepted(string field, string value)
    {
        var store = new FakeSettingsStore();

        // Bots may not exceed people, so a 90-day bot period needs the raw period at least that long; 90 is the
        // seeded raw value, which is exactly that.
        var result = await Handler(store).HandleAsync(Command(store, (field, value)), Token);

        Assert.True(
            result is SettingsWriteResult.Succeeded or SettingsWriteResult.Unchanged,
            $"{field}={value} was refused: {result}");
        Assert.Equal(value, store.Rows[field == Raw ? SettingKeys.Traffic.RawRetentionDays : SettingKeys.Traffic.BotRetentionDays].Value);
    }

    [Theory]
    [InlineData(Raw, "29")]
    [InlineData(Raw, "366")]
    [InlineData(Raw, "0")]
    [InlineData(Raw, "4")]
    [InlineData(Raw, "3650")]
    [InlineData(Bot, "6")]
    [InlineData(Bot, "91")]
    [InlineData(Bot, "0")]
    [InlineData(Bot, "4")]
    public async Task A_period_outside_its_bounds_is_refused_on_that_field(string field, string value)
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(Command(store, (field, value)), Token);

        AssertRefused(result, field);
        AssertUntouched(store);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task An_empty_period_is_refused_rather_than_cleared(string? value)
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(Command(store, (Raw, value)), Token);

        AssertRefused(result, Raw);
        Assert.Equal("90", store.Rows[SettingKeys.Traffic.RawRetentionDays].Value);
    }

    [Theory]
    [InlineData("ninety")]
    [InlineData("90.0")]
    [InlineData("+90")]
    [InlineData("-90")]
    [InlineData("9 0")]
    [InlineData("1e2")]
    [InlineData("٩٠")]
    [InlineData("99999999999999999999")]
    public async Task A_period_that_is_not_whole_invariant_digits_is_refused(string value)
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(Command(store, (Bot, value)), Token);

        AssertRefused(result, Bot);
        AssertUntouched(store);
    }

    [Fact]
    public async Task Both_bad_fields_are_reported_in_one_answer()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(Command(store, (Raw, "1"), (Bot, "x")), Token);

        var invalid = Assert.IsType<SettingsWriteResult.Invalid>(result);
        Assert.Equal([Raw, Bot], invalid.Errors.Select(error => error.Field));
    }

    // ---- bots never longer than people ----------------------------------------------------------------

    [Fact]
    public async Task Bots_longer_than_people_in_one_request_is_refused_on_the_bot_field()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(Command(store, (Raw, "60"), (Bot, "61")), Token);

        AssertRefused(result, Bot);
        AssertUntouched(store);
    }

    [Fact]
    public async Task Bots_equal_to_people_is_accepted()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(Command(store, (Raw, "60"), (Bot, "60")), Token);

        Assert.IsType<SettingsWriteResult.Succeeded>(result);
    }

    [Fact]
    public async Task Raising_bots_past_the_stored_raw_period_is_refused()
    {
        var store = new FakeSettingsStore();
        store.Rows[SettingKeys.Traffic.RawRetentionDays].Value = "40";

        var result = await Handler(store).HandleAsync(Command(store, (Bot, "41")), Token);

        AssertRefused(result, Bot);
    }

    [Fact]
    public async Task Lowering_raw_below_the_stored_bot_period_is_refused_on_the_raw_field()
    {
        var store = new FakeSettingsStore();
        store.Rows[SettingKeys.Traffic.BotRetentionDays].Value = "60";

        var result = await Handler(store).HandleAsync(Command(store, (Raw, "59")), Token);

        AssertRefused(result, Raw);
        Assert.Equal("90", store.Rows[SettingKeys.Traffic.RawRetentionDays].Value);
    }

    /// <summary>
    /// A stored row the purge would not use (scripted in by hand) is not compared against: the purge falls back to
    /// configuration for it, and refusing the save would leave the staff user no way to fix the other one.
    /// </summary>
    [Fact]
    public async Task A_stored_value_out_of_bounds_does_not_block_saving_the_other_field()
    {
        var store = new FakeSettingsStore();
        store.Rows[SettingKeys.Traffic.RawRetentionDays].Value = "5";

        var result = await Handler(store).HandleAsync(Command(store, (Bot, "30")), Token);

        Assert.IsType<SettingsWriteResult.Succeeded>(result);
    }

    // ---- the order of the checks ----------------------------------------------------------------------

    [Fact]
    public async Task A_stale_token_is_a_conflict_and_nothing_is_written()
    {
        var store = new FakeSettingsStore();
        var token = TokenOf(store);
        store.Rows[SettingKeys.Traffic.BotRetentionDays].RowVersion = [0, 0, 0, 0, 0, 0, 9, 9];

        var result = await Handler(store).HandleAsync(
            new UpdateTrafficSettingsCommand(Fields((Raw, "100")), token, Actor), Token);

        var conflict = Assert.IsType<SettingsWriteResult.Conflict>(result);
        Assert.Equal(SettingsConflictCodes.StaleRowVersion, conflict.Code);
        AssertUntouched(store);
    }

    [Fact]
    public async Task A_missing_token_is_a_conflict()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(
            new UpdateTrafficSettingsCommand(Fields((Raw, "100")), null, Actor), Token);

        Assert.IsType<SettingsWriteResult.Conflict>(result);
    }

    [Fact]
    public async Task A_caller_without_settings_edit_is_forbidden_by_the_rows_own_permission()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store, permissions: [Permissions.Settings.Payments, Permissions.Traffic.View])
            .HandleAsync(Command(store, (Raw, "100")), Token);

        var forbidden = Assert.IsType<SettingsWriteResult.Forbidden>(result);
        Assert.Equal([SettingKeys.Traffic.RawRetentionDays], forbidden.Keys);
        AssertUntouched(store);
    }

    [Fact]
    public async Task A_caller_with_settings_edit_alone_may_save()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store, permissions: [Permissions.Settings.Edit])
            .HandleAsync(Command(store, (Raw, "100")), Token);

        Assert.IsType<SettingsWriteResult.Succeeded>(result);
    }

    [Fact]
    public async Task A_request_with_no_acting_user_is_forbidden()
    {
        var store = new FakeSettingsStore();
        var anonymous = new AdminActor(null, null, null, null, null);

        var result = await Handler(store).HandleAsync(
            new UpdateTrafficSettingsCommand(Fields((Raw, "100")), TokenOf(store), anonymous), Token);

        Assert.IsType<SettingsWriteResult.Forbidden>(result);
    }

    /// <summary>
    /// The cross-field rule runs after the permission check, so a caller who may not write these keys learns
    /// nothing about their stored values from it — a 403, never a 400 that names the stored figure.
    /// </summary>
    [Fact]
    public async Task A_forbidden_caller_gets_forbidden_rather_than_the_cross_field_error()
    {
        var store = new FakeSettingsStore();
        store.Rows[SettingKeys.Traffic.RawRetentionDays].Value = "30";

        var result = await Handler(store, permissions: [Permissions.Settings.Payments])
            .HandleAsync(Command(store, (Bot, "60")), Token);

        Assert.IsType<SettingsWriteResult.Forbidden>(result);
    }

    [Fact]
    public async Task A_field_error_is_reported_before_the_token_is_looked_at()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(
            new UpdateTrafficSettingsCommand(Fields((Raw, "1")), null, Actor), Token);

        AssertRefused(result, Raw);
    }

    [Fact]
    public async Task A_database_without_the_rows_says_so_rather_than_failing_later()
    {
        var store = new FakeSettingsStore();
        var command = Command(store, (Raw, "100"));
        store.Rows.Remove(SettingKeys.Traffic.BotRetentionDays);

        var result = await Handler(store).HandleAsync(command, Token);

        var notSeeded = Assert.IsType<SettingsWriteResult.NotSeeded>(result);
        Assert.Contains(SettingKeys.Traffic.BotRetentionDays, notSeeded.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_concurrent_save_that_loses_is_a_conflict()
    {
        var store = new FakeSettingsStore { NextSaveOutcome = SettingsSaveOutcome.ConcurrencyConflict };

        var result = await Handler(store).HandleAsync(Command(store, (Raw, "100")), Token);

        Assert.IsType<SettingsWriteResult.Conflict>(result);
    }

    [Fact]
    public void The_fields_the_endpoint_reads_are_the_two_the_form_sends()
    {
        Assert.Equal(["rawRetentionDays", "botRetentionDays"], UpdateTrafficSettingsCommandHandler.Fields);
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private static UpdateTrafficSettingsCommandHandler Handler(
        FakeSettingsStore store,
        RecordingAuditLog? audit = null,
        string[]? permissions = null) =>
        new(
            store,
            audit ?? new RecordingAuditLog(),
            new FakePermissions(permissions is null, permissions ?? []),
            new FixedClock());

    private static UpdateTrafficSettingsCommand Command(FakeSettingsStore store, params (string Field, string? Value)[] fields) =>
        new(Fields(fields), TokenOf(store), Actor);

    private static Dictionary<string, string?> Fields(params (string Field, string? Value)[] fields) =>
        fields.ToDictionary(field => field.Field, field => field.Value, StringComparer.Ordinal);

    private static string TokenOf(FakeSettingsStore store) =>
        SettingsGroupVersion.For(
            SettingCatalog.KeysInSaveUnitFor(SettingKeys.Traffic.Group).Select(key => store.Rows[key]));

    private static void AssertRefused(SettingsWriteResult result, string field)
    {
        var invalid = Assert.IsType<SettingsWriteResult.Invalid>(result);
        var error = Assert.Single(invalid.Errors);
        Assert.Equal(field, error.Field);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    private static void AssertUntouched(FakeSettingsStore store)
    {
        Assert.Equal("90", store.Rows[SettingKeys.Traffic.RawRetentionDays].Value);
        Assert.Equal("7", store.Rows[SettingKeys.Traffic.BotRetentionDays].Value);
        Assert.Equal(0, store.Saves);
    }

    /// <summary>
    /// Seeded as <c>SettingsSeeder</c> seeds the real table — every catalogued key, with its seed value — and no
    /// more forgiving than the real store: it hands back the instances the handler mutates and moves a row
    /// version on save.
    /// </summary>
    private sealed class FakeSettingsStore : ISettingsStore
    {
        public FakeSettingsStore()
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
                    RequiredPermission = definition.RequiredPermission,
                    Description = definition.Description,
                    Value = definition.SeedValue,
                    RowVersion = [0, 0, 0, 0, 0, 0, 0, version++],
                };
            }
        }

        public Dictionary<string, Setting> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Saves { get; private set; }

        public SettingsSaveOutcome? NextSaveOutcome { get; set; }

        public Task<IReadOnlyList<Setting>> ListAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Setting>>(keys.Where(Rows.ContainsKey).Select(key => Rows[key]).ToList());

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

            foreach (var row in Rows.Values)
            {
                row.RowVersion = [0, 0, 0, 0, 0, 0, (byte)Saves, row.RowVersion![7]];
            }

            return Task.FromResult(SettingsSaveOutcome.Saved);
        }
    }

    private sealed class FakePermissions(bool grantsAll, IReadOnlyCollection<string> permissions)
        : IEffectivePermissionsProvider
    {
        public Task<EffectivePermissions> GetEffectivePermissionsAsync(int userId, CancellationToken cancellationToken) =>
            Task.FromResult(new EffectivePermissions(grantsAll, permissions));
    }

    private sealed class RecordingAuditLog : IAuditLogWriter
    {
        public List<AuditLog> Rows { get; } = [];

        public Task WriteAsync(AuditLog entry, CancellationToken cancellationToken)
        {
            Rows.Add(entry);
            return Task.CompletedTask;
        }

        public void Enlist(AuditLog entry) => Rows.Add(entry);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
    }
}
