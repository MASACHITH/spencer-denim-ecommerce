using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Application.Settings.Admin;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Content;

namespace SpencerDenim.UnitTests.Application.Settings;

/// <summary>
/// The product page texts' write path (issue #33): sanitising on save, the removed-markup report, the length cap,
/// the order of the checks, one audit row per changed key, and absent-is-not-written. Uses the real
/// <see cref="HtmlSanitiser"/>, because what is stored is what it produces.
/// </summary>
public sealed class UpdateProductPageInfoCommandHandlerTests
{
    private const string Delivery = UpdateProductPageInfoCommandHandler.DeliveryField;
    private const string Returns = UpdateProductPageInfoCommandHandler.ReturnsField;

    private static readonly AdminActor Actor = new(7, "staff@example.com", null, null, null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Saving_both_texts_stores_them_and_writes_one_audit_row_per_changed_key()
    {
        var store = new FakeSettingsStore();
        var audit = new RecordingAuditLog();

        var result = await Handler(store, audit).HandleAsync(
            Command(store, (Delivery, "<p>Two days in Colombo.</p>"), (Returns, "<p>Thirty days.</p>")),
            Token);

        var succeeded = Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Equal([SettingKeys.Content.PdpDeliveryInfoHtml, SettingKeys.Content.PdpReturnsInfoHtml], succeeded.ChangedKeys);
        Assert.Empty(succeeded.Removed!);
        Assert.Equal("<p>Two days in Colombo.</p>", store.Rows[SettingKeys.Content.PdpDeliveryInfoHtml].Value);
        Assert.Equal("<p>Thirty days.</p>", store.Rows[SettingKeys.Content.PdpReturnsInfoHtml].Value);
        Assert.Equal(7, store.Rows[SettingKeys.Content.PdpReturnsInfoHtml].UpdatedByUserId);
        Assert.NotNull(store.Rows[SettingKeys.Content.PdpReturnsInfoHtml].UpdatedAtUtc);
        Assert.Equal(1, store.Saves);

        Assert.Equal(
            [SettingKeys.Content.PdpDeliveryInfoHtml, SettingKeys.Content.PdpReturnsInfoHtml],
            audit.Rows.Select(row => row.EntityId));
        Assert.All(audit.Rows, row => Assert.Equal("Setting", row.EntityType));
        Assert.Contains("14 days", audit.Rows[1].BeforeJson, StringComparison.Ordinal);
        Assert.Contains("Thirty days", audit.Rows[1].AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Script_and_handlers_are_removed_before_saving_and_the_removal_is_reported()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(
            Command(store, (Delivery, "<p onclick=\"steal()\">Fast</p><script>alert(1)</script><a href=\"javascript:x()\">x</a>")),
            Token);

        var succeeded = Assert.IsType<SettingsWriteResult.Succeeded>(result);
        var stored = store.Rows[SettingKeys.Content.PdpDeliveryInfoHtml].Value!;
        Assert.DoesNotContain("script", stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript", stored, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Fast", stored, StringComparison.Ordinal);
        Assert.Contains("<script>", succeeded.Removed!);
        Assert.Contains("onclick", succeeded.Removed!);
    }

    [Fact]
    public async Task A_paste_whose_clean_form_is_already_stored_is_unchanged_but_still_reports_the_removal()
    {
        var store = new FakeSettingsStore();
        var audit = new RecordingAuditLog();

        var result = await Handler(store, audit).HandleAsync(
            Command(store, (Returns, ContentSettingDefinitions.ReturnsInfoSeed + "<script>x()</script>")),
            Token);

        var unchanged = Assert.IsType<SettingsWriteResult.Unchanged>(result);
        Assert.Equal(["<script>"], unchanged.Removed);
        Assert.Empty(audit.Rows);
        Assert.Equal(0, store.Saves);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<script>only()</script>")]
    public async Task An_empty_text_is_stored_as_null(string? value)
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(Command(store, (Delivery, value)), Token);

        Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Null(store.Rows[SettingKeys.Content.PdpDeliveryInfoHtml].Value);
        Assert.Equal(ContentSettingDefinitions.ReturnsInfoSeed, store.Rows[SettingKeys.Content.PdpReturnsInfoHtml].Value);
    }

    [Fact]
    public async Task An_absent_field_is_not_written()
    {
        var store = new FakeSettingsStore();
        var audit = new RecordingAuditLog();

        var result = await Handler(store, audit).HandleAsync(Command(store, (Returns, "<p>New.</p>")), Token);

        var succeeded = Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Equal([SettingKeys.Content.PdpReturnsInfoHtml], succeeded.ChangedKeys);
        Assert.Equal(ContentSettingDefinitions.DeliveryInfoSeed, store.Rows[SettingKeys.Content.PdpDeliveryInfoHtml].Value);
        Assert.Null(store.Rows[SettingKeys.Content.PdpDeliveryInfoHtml].UpdatedAtUtc);
        Assert.Single(audit.Rows);
    }

    [Fact]
    public async Task No_fields_at_all_is_unchanged_with_nothing_saved()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(Command(store), Token);

        var unchanged = Assert.IsType<SettingsWriteResult.Unchanged>(result);
        Assert.Empty(unchanged.Removed!);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task Text_at_the_cap_is_accepted_and_one_character_more_is_a_400_on_that_field()
    {
        var store = new FakeSettingsStore();
        var max = SettingKeys.Content.PdpInfoMaxLength;
        var atCap = "<p>" + new string('a', max - 7) + "</p>";
        Assert.Equal(max, atCap.Length);

        Assert.IsType<SettingsWriteResult.Succeeded>(await Handler(store).HandleAsync(Command(store, (Delivery, atCap)), Token));

        var over = "<p>" + new string('b', max - 6) + "</p>";
        var result = await Handler(store).HandleAsync(Command(store, (Returns, over)), Token);

        AssertRefused(result, Returns);
        Assert.Equal(ContentSettingDefinitions.ReturnsInfoSeed, store.Rows[SettingKeys.Content.PdpReturnsInfoHtml].Value);
    }

    [Fact]
    public async Task Markup_the_sanitiser_refuses_to_parse_is_a_400_and_nothing_is_written()
    {
        var store = new FakeSettingsStore();
        var nested = string.Concat(Enumerable.Repeat("<b>", HtmlSanitiser.MaxNestingDepth + 5));

        var result = await Handler(store).HandleAsync(Command(store, (Delivery, nested), (Returns, "<p>Fine.</p>")), Token);

        AssertRefused(result, Delivery);
        Assert.Equal(0, store.Saves);
        Assert.Equal(ContentSettingDefinitions.ReturnsInfoSeed, store.Rows[SettingKeys.Content.PdpReturnsInfoHtml].Value);
    }

    [Fact]
    public async Task A_stale_token_is_a_conflict_and_nothing_is_written()
    {
        var store = new FakeSettingsStore();
        var stale = TokenOf(store);
        store.Rows[SettingKeys.Content.PdpReturnsInfoHtml].RowVersion = [9, 9, 9, 9, 9, 9, 9, 9];

        var result = await Handler(store).HandleAsync(
            new UpdateProductPageInfoCommand(Fields((Delivery, "<p>x</p>")), stale, Actor),
            Token);

        var conflict = Assert.IsType<SettingsWriteResult.Conflict>(result);
        Assert.Equal(SettingsConflictCodes.StaleRowVersion, conflict.Code);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task A_missing_token_is_a_conflict()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(
            new UpdateProductPageInfoCommand(Fields((Delivery, "<p>x</p>")), null, Actor),
            Token);

        Assert.IsType<SettingsWriteResult.Conflict>(result);
    }

    [Fact]
    public async Task A_caller_without_settings_edit_is_forbidden_and_nothing_is_written()
    {
        var store = new FakeSettingsStore();
        var audit = new RecordingAuditLog();

        var result = await Handler(store, audit, [Permissions.Settings.Payments]).HandleAsync(
            Command(store, (Delivery, "<p>x</p>")),
            Token);

        var forbidden = Assert.IsType<SettingsWriteResult.Forbidden>(result);
        Assert.Equal([SettingKeys.Content.PdpDeliveryInfoHtml], forbidden.Keys);
        Assert.Empty(audit.Rows);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task A_holder_of_settings_edit_alone_may_save()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store, null, [Permissions.Settings.Edit]).HandleAsync(
            Command(store, (Delivery, "<p>x</p>")),
            Token);

        Assert.IsType<SettingsWriteResult.Succeeded>(result);
    }

    [Fact]
    public async Task An_actor_with_no_user_id_is_forbidden()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(
            new UpdateProductPageInfoCommand(
                Fields((Delivery, "<p>x</p>")),
                TokenOf(store),
                new AdminActor(null, null, null, null, null)),
            Token);

        Assert.IsType<SettingsWriteResult.Forbidden>(result);
    }

    [Fact]
    public async Task A_database_without_the_rows_says_so()
    {
        var store = new FakeSettingsStore();
        var command = Command(store, (Delivery, "<p>x</p>"));
        store.Rows.Remove(SettingKeys.Content.PdpReturnsInfoHtml);

        var result = await Handler(store).HandleAsync(command, Token);

        var notSeeded = Assert.IsType<SettingsWriteResult.NotSeeded>(result);
        Assert.Contains(SettingKeys.Content.PdpReturnsInfoHtml, notSeeded.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_concurrent_save_that_loses_is_a_conflict()
    {
        var store = new FakeSettingsStore { NextSaveOutcome = SettingsSaveOutcome.ConcurrencyConflict };

        var result = await Handler(store).HandleAsync(Command(store, (Delivery, "<p>x</p>")), Token);

        Assert.IsType<SettingsWriteResult.Conflict>(result);
    }

    /// <summary>
    /// Tester (#33): doc 08 §3.8 DB2 — the permission is read <b>per row</b>, so a row whose permission was raised
    /// refuses the save even when it is the second of the two keys. Kills the mutant that checks the first only.
    /// </summary>
    [Fact]
    public async Task Every_requested_row_is_permission_checked_not_only_the_first()
    {
        var store = new FakeSettingsStore();
        store.Rows[SettingKeys.Content.PdpReturnsInfoHtml].RequiredPermission = Permissions.Settings.Security;
        var audit = new RecordingAuditLog();

        var result = await Handler(store, audit, [Permissions.Settings.Edit]).HandleAsync(
            Command(store, (Delivery, "<p>d</p>"), (Returns, "<p>r</p>")),
            Token);

        var forbidden = Assert.IsType<SettingsWriteResult.Forbidden>(result);
        Assert.Equal([SettingKeys.Content.PdpReturnsInfoHtml], forbidden.Keys);
        Assert.Equal(0, store.Saves);
        Assert.Empty(audit.Rows);
    }

    /// <summary>
    /// Tester (#33): the screen shows the removed list as one sentence, so the same removal in both texts is named
    /// once. Kills the mutant that drops the <c>Distinct</c>.
    /// </summary>
    [Fact]
    public async Task The_same_removal_in_both_texts_is_reported_once()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(
            Command(store, (Delivery, "<p>d</p><script>a()</script>"), (Returns, "<p>r</p><script>b()</script>")),
            Token);

        var succeeded = Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Equal(1, succeeded.Removed!.Count(item => item == "<script>"));
    }

    [Fact]
    public void The_fields_the_endpoint_reads_are_the_two_the_form_sends()
    {
        Assert.Equal(["deliveryInfoHtml", "returnsInfoHtml"], UpdateProductPageInfoCommandHandler.Fields);
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private static UpdateProductPageInfoCommandHandler Handler(
        FakeSettingsStore store,
        RecordingAuditLog? audit = null,
        string[]? permissions = null) =>
        new(
            store,
            new HtmlSanitiser(),
            audit ?? new RecordingAuditLog(),
            new FakePermissions(permissions is null, permissions ?? []),
            new FixedClock());

    private static UpdateProductPageInfoCommand Command(FakeSettingsStore store, params (string Field, string? Value)[] fields) =>
        new(Fields(fields), TokenOf(store), Actor);

    private static Dictionary<string, string?> Fields(params (string Field, string? Value)[] fields) =>
        fields.ToDictionary(field => field.Field, field => field.Value, StringComparer.Ordinal);

    private static string TokenOf(FakeSettingsStore store) =>
        SettingsGroupVersion.For(
            SettingCatalog.KeysInSaveUnitFor(SettingKeys.Content.Group).Select(key => store.Rows[key]));

    private static void AssertRefused(SettingsWriteResult result, string field)
    {
        var invalid = Assert.IsType<SettingsWriteResult.Invalid>(result);
        var error = Assert.Single(invalid.Errors);
        Assert.Equal(field, error.Field);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

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
        public DateTimeOffset UtcNow => new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    }
}
