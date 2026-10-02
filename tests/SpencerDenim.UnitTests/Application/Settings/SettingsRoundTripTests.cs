using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Application.Settings.Admin;
using SpencerDenim.Application.Settings.Contracts;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.UnitTests.Application.Settings;

/// <summary>
/// <b>The read model and the write path, against each other.</b>
/// <para>
/// This is the test that catches the defect the first version of ADM-22 shipped with: the read model
/// published a concurrency token <i>per group</i>, and the contact-and-social save compared it against the
/// rows of <i>both</i> groups, because one form saves both. Every valid save answered <b>409</b> — a screen
/// that could not save anything at all. The two sides of the comparison lived in two files and nothing made
/// them agree.
/// </para>
/// <para>
/// The integration suite found it (seven failures, "Expected: NoContent / Actual: Conflict"), and this is the
/// cheap version of the same assertion: <b>take the token the read model published, send it straight back,
/// and require the save to accept it.</b> It is phrased over every group rather than over the one that broke,
/// so a third group added to a form is caught here too.
/// </para>
/// </summary>
public sealed class SettingsRoundTripTests
{
    private static readonly AdminActor Actor = new(7, "staff@example.com", null, null, null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The round trip, for the contact and social form. Red before <see cref="SettingCatalog.SaveUnits"/>
    /// existed; green after.
    /// </summary>
    [Fact]
    public async Task A_token_the_read_model_published_is_accepted_by_the_contact_and_social_save()
    {
        var store = new FakeSettingsStore();
        var token = await PublishedTokenAsync(store, SettingKeys.Contact.Group);

        var result = await new UpdateContactAndSocialCommandHandler(
            store, new RecordingAuditLog(), new NoCache(), new FixedClock())
            .HandleAsync(
                new UpdateContactAndSocialCommand(
                    "94771234567",
                    "https://www.facebook.com/shop",
                    "https://www.instagram.com/shop",
                    "https://www.tiktok.com/@shop",
                    token,
                    Actor),
                Token);

        Assert.IsType<SettingsWriteResult.Succeeded>(result);
    }

    /// <summary>
    /// The same token read from the <i>other</i> group of the same form. A screen may have taken it from
    /// either entry, so both must work — which is only true if the token covers the whole save unit.
    /// </summary>
    [Fact]
    public async Task The_social_groups_token_is_the_same_token_and_is_also_accepted()
    {
        var store = new FakeSettingsStore();

        Assert.Equal(
            await PublishedTokenAsync(store, SettingKeys.Contact.Group),
            await PublishedTokenAsync(store, SettingKeys.Social.Group));
    }

    /// <summary>And for the logo form, which is one group on its own.</summary>
    [Fact]
    public async Task A_token_the_read_model_published_is_accepted_by_the_logo_save()
    {
        var store = new FakeSettingsStore();
        store.Images[Guid.Parse("11111111-1111-1111-1111-111111111111")] = 41;

        var token = await PublishedTokenAsync(store, SettingKeys.Store.Group);

        var result = await new SetStoreLogoCommandHandler(
            store, new RecordingAuditLog(), new NoCache(), new FixedClock())
            .HandleAsync(
                new SetStoreLogoCommand(Guid.Parse("11111111-1111-1111-1111-111111111111"), token, Actor),
                Token);

        Assert.IsType<SettingsWriteResult.Succeeded>(result);
    }

    /// <summary>
    /// The other half of the invariant, so the fix cannot have been "stop checking": a change to a row in the
    /// <i>other</i> group of the same form still invalidates the token.
    /// </summary>
    [Fact]
    public async Task A_change_in_the_other_group_of_the_same_form_is_still_a_conflict()
    {
        var store = new FakeSettingsStore();
        var token = await PublishedTokenAsync(store, SettingKeys.Contact.Group);

        // Somebody else saves the Instagram address, which moves that row's version.
        store.Rows[SettingKeys.Social.InstagramUrl].RowVersion = [9, 9, 9, 9, 9, 9, 9, 9];

        var result = await new UpdateContactAndSocialCommandHandler(
            store, new RecordingAuditLog(), new NoCache(), new FixedClock())
            .HandleAsync(
                new UpdateContactAndSocialCommand("94771234567", null, null, null, token, Actor),
                Token);

        var conflict = Assert.IsType<SettingsWriteResult.Conflict>(result);
        Assert.Equal(SettingsConflictCodes.StaleRowVersion, conflict.Code);
    }

    /// <summary>A save with no token at all is refused, rather than taken as "no opinion".</summary>
    [Fact]
    public async Task A_save_with_no_token_is_refused()
    {
        var store = new FakeSettingsStore();

        var result = await new UpdateContactAndSocialCommandHandler(
            store, new RecordingAuditLog(), new NoCache(), new FixedClock())
            .HandleAsync(new UpdateContactAndSocialCommand("94771234567", null, null, null, null, Actor), Token);

        Assert.IsType<SettingsWriteResult.Conflict>(result);
        Assert.Equal(0, store.Saves);
    }

    /// <summary>
    /// A missing seeded row is a server-side fault, not a bad field: the endpoint answers 500 naming the
    /// seeder rather than telling a staff user to fix a box they cannot see.
    /// </summary>
    [Fact]
    public async Task A_missing_seeded_row_is_reported_as_not_seeded()
    {
        var store = new FakeSettingsStore();
        store.Rows.Remove(SettingKeys.Social.TikTokUrl);

        var result = await new UpdateContactAndSocialCommandHandler(
            store, new RecordingAuditLog(), new NoCache(), new FixedClock())
            .HandleAsync(new UpdateContactAndSocialCommand(null, null, null, null, "AAAA", Actor), Token);

        var notSeeded = Assert.IsType<SettingsWriteResult.NotSeeded>(result);
        Assert.Contains(SettingKeys.Social.TikTokUrl, notSeeded.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A save that changes nothing writes nothing: no audit row, no statement, no cache eviction. Otherwise an
    /// admin opening the screen and pressing Save would move every row version and invalidate their colleague's
    /// open form.
    /// </summary>
    [Fact]
    public async Task A_save_that_changes_nothing_writes_nothing()
    {
        var store = new FakeSettingsStore();
        store.Rows[SettingKeys.Contact.WhatsApp].Value = "94771234567";

        var audit = new RecordingAuditLog();
        var cache = new NoCache();

        var result = await new UpdateContactAndSocialCommandHandler(store, audit, cache, new FixedClock())
            .HandleAsync(
                new UpdateContactAndSocialCommand(
                    "94771234567", null, null, null, await PublishedTokenAsync(store, SettingKeys.Contact.Group), Actor),
                Token);

        Assert.IsType<SettingsWriteResult.Unchanged>(result);
        Assert.Empty(audit.Rows);
        Assert.Equal(0, store.Saves);
        Assert.Equal(0, cache.Evictions);
    }

    /// <summary>
    /// The logo's usage row is rewritten in the same unit of work as the value, and the previous asset is
    /// released — the half that is easy to forget, and the half the owner actually asked for.
    /// </summary>
    [Fact]
    public async Task Replacing_the_logo_rewrites_the_usage_row()
    {
        var store = new FakeSettingsStore();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        store.Images[first] = 41;
        store.Images[second] = 57;

        var handler = new SetStoreLogoCommandHandler(store, new RecordingAuditLog(), new NoCache(), new FixedClock());

        await handler.HandleAsync(
            new SetStoreLogoCommand(first, await PublishedTokenAsync(store, SettingKeys.Store.Group), Actor), Token);
        Assert.Equal(41, store.Usage);

        await handler.HandleAsync(
            new SetStoreLogoCommand(second, await PublishedTokenAsync(store, SettingKeys.Store.Group), Actor), Token);
        Assert.Equal(57, store.Usage);

        await handler.HandleAsync(
            new SetStoreLogoCommand(null, await PublishedTokenAsync(store, SettingKeys.Store.Group), Actor), Token);
        Assert.Null(store.Usage);
    }

    /// <summary>A successful save evicts the storefront cache, so a logo change is visible at once.</summary>
    [Fact]
    public async Task A_successful_save_evicts_the_storefront_cache()
    {
        var store = new FakeSettingsStore();
        var cache = new NoCache();

        await new UpdateContactAndSocialCommandHandler(store, new RecordingAuditLog(), cache, new FixedClock())
            .HandleAsync(
                new UpdateContactAndSocialCommand(
                    "94771234567", null, null, null, await PublishedTokenAsync(store, SettingKeys.Contact.Group), Actor),
                Token);

        Assert.Equal(1, cache.Evictions);
    }

    /// <summary>
    /// A save the database refuses leaves the cache alone: evicting before the commit would publish a change
    /// that then failed, and the storefront would serve it for up to a minute with no row behind it.
    /// </summary>
    [Fact]
    public async Task A_refused_save_does_not_evict_the_cache()
    {
        var store = new FakeSettingsStore { NextSaveOutcome = SettingsSaveOutcome.ConcurrencyConflict };
        var cache = new NoCache();

        var result = await new UpdateContactAndSocialCommandHandler(store, new RecordingAuditLog(), cache, new FixedClock())
            .HandleAsync(
                new UpdateContactAndSocialCommand(
                    "94771234567", null, null, null, await PublishedTokenAsync(store, SettingKeys.Contact.Group), Actor),
                Token);

        Assert.IsType<SettingsWriteResult.Conflict>(result);
        Assert.Equal(0, cache.Evictions);
    }

    /// <summary>
    /// The read model's own permission filter: a group whose <c>RequiredPermission</c> the caller lacks is
    /// absent rather than empty, so the API does not confirm the group exists.
    /// </summary>
    [Fact]
    public async Task A_group_the_caller_cannot_read_is_absent_from_the_read_model()
    {
        var store = new FakeSettingsStore();
        store.Rows[SettingKeys.Store.LogoAssetId].RequiredPermission = Permissions.Settings.Payments;

        var settings = await ReadAsync(store, Permissions.Settings.Edit);

        Assert.DoesNotContain(settings.Groups, group => group.GroupKey == SettingKeys.Store.Group);
        Assert.Contains(settings.Groups, group => group.GroupKey == SettingKeys.Social.Group);
    }

    [Fact]
    public async Task A_caller_with_no_session_reads_nothing()
    {
        var settings = await new GetAdminSettingsQueryHandler(new FakeSettingsStore(), new FakePermissions(true, []))
            .HandleAsync(new GetAdminSettingsQuery(null), Token);

        Assert.Empty(settings.Groups);
        Assert.Null(settings.Logo);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static async Task<string> PublishedTokenAsync(FakeSettingsStore store, string groupKey)
    {
        var settings = await ReadAsync(store, Permissions.Settings.Edit);

        return settings.Groups.Single(group => group.GroupKey == groupKey).RowVersion;
    }

    private static Task<AdminSettingsDto> ReadAsync(FakeSettingsStore store, params string[] permissions) =>
        new GetAdminSettingsQueryHandler(store, new FakePermissions(false, permissions))
            .HandleAsync(new GetAdminSettingsQuery(7), TestContext.Current.CancellationToken);

    /// <summary>
    /// An in-memory <see cref="ISettingsStore"/> seeded exactly as <c>SettingsSeeder</c> seeds the real one:
    /// every catalogued key, every value null.
    /// <para>
    /// Deliberately not more forgiving than the real store (MEMORY's lesson about politely shaped stubs): it
    /// hands back the same instances the handler mutates, as EF's tracked entities do; it moves a row version
    /// on save, as <c>rowversion</c> does; and it refuses an asset it has not been told about, as the real
    /// "image, public, ready, not deleted" query does.
    /// </para>
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
                    RowVersion = [0, 0, 0, 0, 0, 0, 0, version++],
                };
            }
        }

        public Dictionary<string, Setting> Rows { get; } = new(StringComparer.Ordinal);

        /// <summary>Public id to internal id, for the assets this store will admit exist.</summary>
        public Dictionary<Guid, int> Images { get; } = [];

        /// <summary>The asset the logo's usage row names, or null when there is none.</summary>
        public int? Usage { get; private set; }

        public int Saves { get; private set; }

        public SettingsSaveOutcome? NextSaveOutcome { get; set; }

        public Task<IReadOnlyList<Setting>> ListAsync(
            IReadOnlyCollection<string> keys,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Setting>>(
                keys.Where(Rows.ContainsKey).Select(key => Rows[key]).OrderBy(row => row.Key, StringComparer.Ordinal).ToList());

        public Task<SettingImageAsset?> FindUsableImageAsync(Guid publicId, CancellationToken cancellationToken) =>
            Task.FromResult(Images.TryGetValue(publicId, out var id)
                ? new SettingImageAsset(id, publicId, "/media/logo.webp", 600, 150, "Logo", "logo.png")
                : null);

        public Task<SettingImageAsset?> FindUsableImageByIdAsync(int assetId, CancellationToken cancellationToken)
        {
            var publicId = Images.FirstOrDefault(pair => pair.Value == assetId).Key;

            return Task.FromResult(publicId == Guid.Empty
                ? null
                : new SettingImageAsset(assetId, publicId, "/media/logo.webp", 600, 150, "Logo", "logo.png"));
        }

        public Task ReplaceMediaUsageAsync(
            string settingKey,
            string usageRole,
            int? newAssetId,
            CancellationToken cancellationToken)
        {
            Usage = newAssetId;
            return Task.CompletedTask;
        }

        public Task<SettingsSaveOutcome> SaveAsync(int? actingUserId, CancellationToken cancellationToken)
        {
            if (NextSaveOutcome is { } forced)
            {
                return Task.FromResult(forced);
            }

            Saves++;

            // A rowversion moves itself on every update; a fake that did not would let a handler re-use a
            // token it had just invalidated.
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

    private sealed class NoCache : ISiteSettingsCache
    {
        public int Evictions { get; private set; }

        public SiteSettingsDto? GetSettings() => null;

        public void SetSettings(SiteSettingsDto settings)
        {
        }

        public void Evict() => Evictions++;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 2, 11, 30, 0, TimeSpan.Zero);
    }
}
