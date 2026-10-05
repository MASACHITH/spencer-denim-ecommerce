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
/// This file catches the defect ADM-22 shipped with in its first draft: the read model published a
/// concurrency token <i>per group</i>, and the contact-and-social save compared it against the rows of
/// <i>both</i> groups, because one form saves both. Every valid save answered <b>409</b> — a screen that could
/// not save anything at all. The two sides of the comparison lived in two files and nothing made them agree.
/// </para>
/// <para>
/// The assertion is: <b>take the token the read model published, send it straight back, and require the save
/// to accept it.</b> It is phrased over every group rather than over the one that broke.
/// </para>
/// <para>
/// Round 1 added the second half of the same idea — the write path now reads each row's
/// <c>RequiredPermission</c>, which its own comment used to claim and the code did not do — and the
/// absent-versus-null distinction that stops a caller clearing a group their screen never received.
/// </para>
/// </summary>
public sealed class SettingsRoundTripTests
{
    private static readonly AdminActor Actor = new(7, "staff@example.com", null, null, null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Every field the contact-and-social form sends, which is what an ordinary save carries.</summary>
    private static Dictionary<string, string?> AllFields(
        string? whatsApp = null,
        string? facebook = null,
        string? instagram = null,
        string? tiktok = null) =>
        new(StringComparer.Ordinal)
        {
            ["whatsApp"] = whatsApp,
            ["facebookUrl"] = facebook,
            ["instagramUrl"] = instagram,
            ["tikTokUrl"] = tiktok,
        };

    // ---- the token round trip ---------------------------------------------------------------------

    [Fact]
    public async Task A_token_the_read_model_published_is_accepted_by_the_contact_and_social_save()
    {
        var store = new FakeSettingsStore();
        var token = await PublishedTokenAsync(store, SettingKeys.Contact.Group);

        var result = await Contact(store).HandleAsync(
            new UpdateContactAndSocialCommand(
                AllFields(
                    "94771234567",
                    "https://www.facebook.com/shop",
                    "https://www.instagram.com/shop",
                    "https://www.tiktok.com/@shop"),
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
    public async Task The_social_groups_token_is_the_same_token_as_the_contact_groups()
    {
        var store = new FakeSettingsStore();

        Assert.Equal(
            await PublishedTokenAsync(store, SettingKeys.Contact.Group),
            await PublishedTokenAsync(store, SettingKeys.Social.Group));
    }

    [Fact]
    public async Task A_token_the_read_model_published_is_accepted_by_the_logo_save()
    {
        var store = new FakeSettingsStore();
        var asset = Guid.NewGuid();
        store.Images[asset] = 41;

        var result = await Logo(store).HandleAsync(
            new SetStoreLogoCommand(
                true, asset, await PublishedTokenAsync(store, SettingKeys.Store.Group), Actor),
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

        store.Rows[SettingKeys.Social.InstagramUrl].RowVersion = [9, 9, 9, 9, 9, 9, 9, 9];

        var result = await Contact(store).HandleAsync(
            new UpdateContactAndSocialCommand(AllFields("94771234567"), token, Actor), Token);

        var conflict = Assert.IsType<SettingsWriteResult.Conflict>(result);
        Assert.Equal(SettingsConflictCodes.StaleRowVersion, conflict.Code);
    }

    [Fact]
    public async Task A_save_with_no_token_is_refused()
    {
        var store = new FakeSettingsStore();

        var result = await Contact(store).HandleAsync(
            new UpdateContactAndSocialCommand(AllFields("94771234567"), null, Actor), Token);

        Assert.IsType<SettingsWriteResult.Conflict>(result);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task A_missing_seeded_row_is_reported_as_not_seeded()
    {
        var store = new FakeSettingsStore();
        store.Rows.Remove(SettingKeys.Social.TikTokUrl);

        var result = await Contact(store).HandleAsync(
            new UpdateContactAndSocialCommand(AllFields(), "AAAA", Actor), Token);

        var notSeeded = Assert.IsType<SettingsWriteResult.NotSeeded>(result);
        Assert.Contains(SettingKeys.Social.TikTokUrl, notSeeded.Message, StringComparison.Ordinal);
    }

    // ---- absent is not "clear" (round 1, Major 2's second half) -----------------------------------

    /// <summary>
    /// <b>A field the request did not carry is not written.</b>
    /// <para>
    /// The first version read absent as <see langword="null"/> and cleared the key — W1-12's Blocker shape. It
    /// could not bite while every caller could read every group, and it bites the moment #53 adds a role that
    /// cannot: that caller's screen omits the social addresses it never received, and their first WhatsApp save
    /// would wipe the client's social links.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_field_the_request_did_not_carry_is_left_alone()
    {
        var store = new FakeSettingsStore();
        store.Rows[SettingKeys.Social.FacebookUrl].Value = "https://www.facebook.com/the-clients-page";
        store.Rows[SettingKeys.Social.InstagramUrl].Value = "https://www.instagram.com/the-clients-page";

        // Only the WhatsApp number is sent — exactly what a screen that could not read the Social group does.
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal) { ["whatsApp"] = "94771234567" };

        var result = await Contact(store).HandleAsync(
            new UpdateContactAndSocialCommand(
                fields, await PublishedTokenAsync(store, SettingKeys.Contact.Group), Actor),
            Token);

        var succeeded = Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Equal([SettingKeys.Contact.WhatsApp], succeeded.ChangedKeys);

        Assert.Equal("https://www.facebook.com/the-clients-page", store.Rows[SettingKeys.Social.FacebookUrl].Value);
        Assert.Equal("https://www.instagram.com/the-clients-page", store.Rows[SettingKeys.Social.InstagramUrl].Value);
    }

    /// <summary>
    /// And an <i>explicit</i> null still clears, because that is how a client says "we have no TikTok". Absent
    /// and null have to be different, not both ignored.
    /// </summary>
    [Fact]
    public async Task An_explicit_null_still_clears_the_key()
    {
        var store = new FakeSettingsStore();
        store.Rows[SettingKeys.Social.TikTokUrl].Value = "https://www.tiktok.com/@old";

        var fields = new Dictionary<string, string?>(StringComparer.Ordinal) { ["tikTokUrl"] = null };

        var result = await Contact(store).HandleAsync(
            new UpdateContactAndSocialCommand(
                fields, await PublishedTokenAsync(store, SettingKeys.Contact.Group), Actor),
            Token);

        Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Null(store.Rows[SettingKeys.Social.TikTokUrl].Value);
    }

    /// <summary>
    /// The logo route insists on an explicit decision: an absent field is refused rather than read as
    /// "remove", so a screen that stopped sending it cannot silently delete the shop's logo.
    /// </summary>
    [Fact]
    public async Task A_logo_request_that_omits_the_asset_field_is_refused_rather_than_removing_it()
    {
        var store = new FakeSettingsStore();
        store.Rows[SettingKeys.Store.LogoAssetId].Value = "41";
        store.Images[Guid.NewGuid()] = 41;

        var result = await Logo(store).HandleAsync(
            new SetStoreLogoCommand(
                false, null, await PublishedTokenAsync(store, SettingKeys.Store.Group), Actor),
            Token);

        var invalid = Assert.IsType<SettingsWriteResult.Invalid>(result);
        Assert.Equal("logoAssetPublicId", Assert.Single(invalid.Errors).Field);

        Assert.Equal("41", store.Rows[SettingKeys.Store.LogoAssetId].Value);
    }

    [Fact]
    public async Task An_explicit_null_asset_removes_the_logo()
    {
        var store = new FakeSettingsStore();
        store.Rows[SettingKeys.Store.LogoAssetId].Value = "41";

        var result = await Logo(store).HandleAsync(
            new SetStoreLogoCommand(
                true, null, await PublishedTokenAsync(store, SettingKeys.Store.Group), Actor),
            Token);

        Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Null(store.Rows[SettingKeys.Store.LogoAssetId].Value);
    }

    // ---- the per-row permission check (round 1, Major 2's first half) -----------------------------

    /// <summary>
    /// <b>Doc 08 §3.8 DB2 on the write path.</b> The first version's own comment said "the permission is still
    /// per row" and the handler never read the column: <c>grep RequiredPermission</c> found it only in the read
    /// model. Nothing could exploit it while every settings key required <c>Settings.Edit</c> — and #53 adds
    /// the keys that do not.
    /// </summary>
    [Fact]
    public async Task A_key_whose_permission_the_caller_lacks_cannot_be_changed()
    {
        var store = new FakeSettingsStore();
        var token = await PublishedTokenAsync(store, SettingKeys.Contact.Group);
        store.Rows[SettingKeys.Social.FacebookUrl].RequiredPermission = Permissions.Settings.Payments;

        var result = await Contact(store, Permissions.Settings.Edit).HandleAsync(
            new UpdateContactAndSocialCommand(
                AllFields("94771234567", "https://www.facebook.com/shop"), token, Actor),
            Token);

        var forbidden = Assert.IsType<SettingsWriteResult.Forbidden>(result);
        Assert.Equal([SettingKeys.Social.FacebookUrl], forbidden.Keys);

        // Nothing at all was written — not even the key they *could* have changed. A partly applied save is
        // worse than a refused one, because nobody can tell which half landed.
        Assert.Equal(0, store.Saves);
        Assert.Null(store.Rows[SettingKeys.Contact.WhatsApp].Value);
        Assert.Null(store.Rows[SettingKeys.Social.FacebookUrl].Value);
    }

    /// <summary>
    /// **Reversed on purpose, and this test is the record of why.**
    ///
    /// It used to assert the opposite: that a key the caller may not write is <i>not</i> a refusal when the
    /// submitted value happens to equal the stored one, so that a screen re-submitting a field it cannot
    /// change would still save the fields it can. That was a deliberate choice and it was wrong — it made the
    /// endpoint a **one-bit oracle** on a value the caller is not allowed to read. Submit a guess that matches
    /// and the answer is success; submit one that differs and it is a refusal. One guess, one bit, repeatable.
    ///
    /// The usability reason for it went away with the absent-is-not-null fix: a screen that cannot read a group
    /// no longer <i>sends</i> that group, so it is never refused for it. The permission is therefore checked
    /// over the keys the request asked to write, before the diff — and a request that names a key it may not
    /// write is refused whether or not it guessed the value correctly.
    ///
    /// See <c>A_refusal_does_not_reveal_whether_the_submitted_value_matched</c>, which asserts the two answers
    /// are the same rather than asserting either one.
    /// </summary>
    [Fact]
    public async Task A_key_the_caller_cannot_write_is_refused_even_when_the_value_would_not_change()
    {
        var store = new FakeSettingsStore();
        store.Rows[SettingKeys.Social.FacebookUrl].Value = "https://www.facebook.com/unchanged";
        var token = await PublishedTokenAsync(store, SettingKeys.Contact.Group);
        store.Rows[SettingKeys.Social.FacebookUrl].RequiredPermission = Permissions.Settings.Payments;

        var result = await Contact(store, Permissions.Settings.Edit).HandleAsync(
            new UpdateContactAndSocialCommand(
                AllFields("94771234567", "https://www.facebook.com/unchanged"), token, Actor),
            Token);

        var forbidden = Assert.IsType<SettingsWriteResult.Forbidden>(result);
        Assert.Equal([SettingKeys.Social.FacebookUrl], forbidden.Keys);

        // And nothing was written, including the key they could have changed.
        Assert.Equal(0, store.Saves);
        Assert.Null(store.Rows[SettingKeys.Contact.WhatsApp].Value);
    }

    /// <summary>
    /// The half that still has to work: a screen that <b>omits</b> the group it cannot read saves the group it
    /// can. This is what the reversed test above was reaching for, and it is the correct way to get it.
    /// </summary>
    [Fact]
    public async Task A_request_that_omits_the_group_it_cannot_write_still_saves_the_one_it_can()
    {
        var store = new FakeSettingsStore();
        var token = await PublishedTokenAsync(store, SettingKeys.Contact.Group);
        store.Rows[SettingKeys.Social.FacebookUrl].RequiredPermission = Permissions.Settings.Payments;

        var result = await Contact(store, Permissions.Settings.Edit).HandleAsync(
            new UpdateContactAndSocialCommand(
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["whatsApp"] = "94771234567" },
                token,
                Actor),
            Token);

        var succeeded = Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Equal([SettingKeys.Contact.WhatsApp], succeeded.ChangedKeys);
    }

    [Fact]
    public async Task The_logo_cannot_be_changed_without_its_rows_permission()
    {
        var store = new FakeSettingsStore();
        var asset = Guid.NewGuid();
        store.Images[asset] = 41;

        // The token is read *before* the permission changes, because afterwards the Store group is filtered out
        // of the read model and there is no token to read. (The integration test does the opposite, because
        // there the UPDATE moves the real rowversion — see its own comment. Two stores, two orderings, and
        // both are about the same ordering rule: concurrency is checked before permission.)
        var token = await PublishedTokenAsync(store, SettingKeys.Store.Group);
        store.Rows[SettingKeys.Store.LogoAssetId].RequiredPermission = Permissions.Settings.Security;

        var result = await Logo(store, Permissions.Settings.Edit).HandleAsync(
            new SetStoreLogoCommand(true, asset, token, Actor),
            Token);

        var forbidden = Assert.IsType<SettingsWriteResult.Forbidden>(result);
        Assert.Equal([SettingKeys.Store.LogoAssetId], forbidden.Keys);

        Assert.Null(store.Rows[SettingKeys.Store.LogoAssetId].Value);
        Assert.Null(store.Usage);
    }

    /// <summary>
    /// A caller nothing is known about writes nothing. Unreachable from the endpoint, which requires a
    /// session; refusing rather than allowing is the only safe answer.
    /// </summary>
    [Fact]
    public async Task A_command_with_no_user_is_refused()
    {
        var store = new FakeSettingsStore();

        var result = await Contact(store).HandleAsync(
            new UpdateContactAndSocialCommand(
                AllFields("94771234567"),
                await PublishedTokenAsync(store, SettingKeys.Contact.Group),
                AdminActor.Unknown),
            Token);

        Assert.IsType<SettingsWriteResult.Forbidden>(result);
        Assert.Equal(0, store.Saves);
    }

    // ---- normalisation on the write path ---------------------------------------------------------

    /// <summary>
    /// The stored value is the <b>normalised</b> address, so the text a client reads back tells them where
    /// the link goes. U+3002 is the case that made this necessary: stored raw, it read <c>instagram.com</c>
    /// and went to <c>evil.example</c>.
    /// </summary>
    [Fact]
    public async Task The_stored_social_address_is_the_normalised_one()
    {
        var store = new FakeSettingsStore();

        var result = await Contact(store).HandleAsync(
            new UpdateContactAndSocialCommand(
                AllFields(instagram: "https://INSTAGRAM.com。evil.example:443/Shop"),
                await PublishedTokenAsync(store, SettingKeys.Contact.Group),
                Actor),
            Token);

        Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Equal(
            "https://instagram.com.evil.example/Shop",
            store.Rows[SettingKeys.Social.InstagramUrl].Value);
    }

    // ---- the rest ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_save_that_changes_nothing_writes_nothing()
    {
        var store = new FakeSettingsStore();
        store.Rows[SettingKeys.Contact.WhatsApp].Value = "94771234567";

        var audit = new RecordingAuditLog();
        var cache = new NoCache();

        var result = await Contact(store, audit: audit, cache: cache).HandleAsync(
            new UpdateContactAndSocialCommand(
                AllFields("94771234567"),
                await PublishedTokenAsync(store, SettingKeys.Contact.Group),
                Actor),
            Token);

        Assert.IsType<SettingsWriteResult.Unchanged>(result);
        Assert.Empty(audit.Rows);
        Assert.Equal(0, store.Saves);
        Assert.Equal(0, cache.Evictions);
    }

    [Fact]
    public async Task Replacing_the_logo_rewrites_the_usage_row()
    {
        var store = new FakeSettingsStore();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        store.Images[first] = 41;
        store.Images[second] = 57;

        var handler = Logo(store);

        await handler.HandleAsync(
            new SetStoreLogoCommand(true, first, await PublishedTokenAsync(store, SettingKeys.Store.Group), Actor),
            Token);
        Assert.Equal(41, store.Usage);

        await handler.HandleAsync(
            new SetStoreLogoCommand(true, second, await PublishedTokenAsync(store, SettingKeys.Store.Group), Actor),
            Token);
        Assert.Equal(57, store.Usage);

        await handler.HandleAsync(
            new SetStoreLogoCommand(true, null, await PublishedTokenAsync(store, SettingKeys.Store.Group), Actor),
            Token);
        Assert.Null(store.Usage);
    }

    [Fact]
    public async Task A_successful_save_evicts_the_storefront_cache()
    {
        var store = new FakeSettingsStore();
        var cache = new NoCache();

        await Contact(store, cache: cache).HandleAsync(
            new UpdateContactAndSocialCommand(
                AllFields("94771234567"),
                await PublishedTokenAsync(store, SettingKeys.Contact.Group),
                Actor),
            Token);

        Assert.Equal(1, cache.Evictions);
    }

    [Fact]
    public async Task A_refused_save_does_not_evict_the_cache()
    {
        var store = new FakeSettingsStore { NextSaveOutcome = SettingsSaveOutcome.ConcurrencyConflict };
        var cache = new NoCache();

        var result = await Contact(store, cache: cache).HandleAsync(
            new UpdateContactAndSocialCommand(
                AllFields("94771234567"),
                await PublishedTokenAsync(store, SettingKeys.Contact.Group),
                Actor),
            Token);

        Assert.IsType<SettingsWriteResult.Conflict>(result);
        Assert.Equal(0, cache.Evictions);
    }

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

    /// <summary>
    /// The database collation is case-insensitive, so a row scripted in with different casing is one row to
    /// SQL Server — and the first version's <c>Ordinal</c> dictionaries missed it, which made the box vanish
    /// from the screen and every save answer <c>NotSeeded</c> (a 500) for ever.
    /// </summary>
    [Fact]
    public async Task A_row_whose_key_is_cased_differently_is_still_found()
    {
        var store = new FakeSettingsStore();
        store.Recase(SettingKeys.Social.FacebookUrl, "social.facebookurl");

        var settings = await ReadAsync(store, Permissions.Settings.Edit);

        Assert.Contains(
            settings.Groups.SelectMany(group => group.Settings),
            setting => string.Equals(setting.Key, "social.facebookurl", StringComparison.Ordinal));

        var result = await Contact(store).HandleAsync(
            new UpdateContactAndSocialCommand(
                AllFields(facebook: "https://www.facebook.com/shop"),
                await PublishedTokenAsync(store, SettingKeys.Contact.Group),
                Actor),
            Token);

        Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Equal("https://www.facebook.com/shop", store.Rows["social.facebookurl"].Value);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static UpdateContactAndSocialCommandHandler Contact(
        FakeSettingsStore store,
        params string[] permissions) =>
        Contact(store, new RecordingAuditLog(), new NoCache(), permissions);

    private static UpdateContactAndSocialCommandHandler Contact(
        FakeSettingsStore store,
        RecordingAuditLog? audit = null,
        NoCache? cache = null,
        params string[] permissions) =>
        new(
            store,
            audit ?? new RecordingAuditLog(),
            cache ?? new NoCache(),
            PermissionsFor(permissions),
            new FixedClock());

    private static SetStoreLogoCommandHandler Logo(FakeSettingsStore store, params string[] permissions) =>
        new(store, new RecordingAuditLog(), new NoCache(), PermissionsFor(permissions), new FixedClock());

    /// <summary>
    /// A permissions provider. With no permissions named the caller is a GrantsAll holder, so permission is
    /// never the thing a test fails on by accident; a test that is <i>about</i> permissions names them.
    /// </summary>
    private static FakePermissions PermissionsFor(string[] permissions) =>
        permissions.Length == 0 ? new FakePermissions(true, []) : new FakePermissions(false, permissions);

    private static async Task<string> PublishedTokenAsync(FakeSettingsStore store, string groupKey)
    {
        var settings = await ReadAsync(store, SpencerDenim.Domain.Identity.Permissions.Settings.Edit);

        return settings.Groups.Single(group => group.GroupKey == groupKey).RowVersion;
    }

    private static Task<AdminSettingsDto> ReadAsync(FakeSettingsStore store, params string[] permissions) =>
        new GetAdminSettingsQueryHandler(store, PermissionsFor(permissions))
            .HandleAsync(new GetAdminSettingsQuery(7), TestContext.Current.CancellationToken);

    /// <summary>
    /// An in-memory <see cref="ISettingsStore"/> seeded exactly as <c>SettingsSeeder</c> seeds the real one:
    /// every catalogued key, every value null.
    /// <para>
    /// Deliberately not more forgiving than the real store (MEMORY's lesson about politely shaped stubs): it
    /// hands back the same instances the handler mutates, as EF's tracked entities do; it moves a row version
    /// on save, as <c>rowversion</c> does; it matches keys case-insensitively, as the collation does; and it
    /// refuses an asset it has not been told about, as the real "image, public, ready, not deleted" query does.
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

        /// <summary>Keyed case-insensitively, as <c>Latin1_General_100_CI_AS_SC</c> is.</summary>
        public Dictionary<string, Setting> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<Guid, int> Images { get; } = [];

        public int? Usage { get; private set; }

        public int Saves { get; private set; }

        public SettingsSaveOutcome? NextSaveOutcome { get; set; }

        /// <summary>Re-cases a row's key, standing in for a row scripted into the database by hand.</summary>
        public void Recase(string key, string newCasing)
        {
            var row = Rows[key];
            Rows.Remove(key);
            row.Key = newCasing;
            Rows[newCasing] = row;
        }

        public Task<IReadOnlyList<Setting>> ListAsync(
            IReadOnlyCollection<string> keys,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Setting>>(
                keys.Where(Rows.ContainsKey)
                    .Select(key => Rows[key])
                    .OrderBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList());

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
