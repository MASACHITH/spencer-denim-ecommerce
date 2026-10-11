using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Identity.Seeding;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Seeding;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Identity;

/// <summary>
/// <b>Issue #122: a new permission reaches the default roles of a database that already exists.</b> Doc 08 §9 / N13
/// say the seeder never touches a role that exists, so before this step Store Manager and Content Editor on any
/// database created before W2-11 silently lacked <c>Customers.Edit</c>, <c>Content.Create</c> and
/// <c>Content.Delete</c> — and so would the client's, once it exists, for whatever is added after its creation.
/// <para>
/// Every test starts from an empty database of its own (<see cref="DefaultRoleSyncDatabase"/>) and builds the
/// state it is about: "an older build seeded these roles" is the roles with the version-2 permissions deleted,
/// which is exactly what W1-08's seeder wrote.
/// </para>
/// </summary>
public sealed class DefaultRolePermissionSyncTests(DefaultRoleSyncDatabase database) : IClassFixture<DefaultRoleSyncDatabase>
{
    private const string StoreManager = "Store Manager";
    private const string OrderStaff = "Order Staff";
    private const string ContentEditor = "Content Editor";

    private static readonly string SyncCategory = typeof(DefaultRolePermissionSync).FullName!;

    private static HashSet<string> Shipped(string roleName) =>
        DefaultRoleDefinitions.All.Single(role => role.Name == roleName).Permissions.ToHashSet(StringComparer.Ordinal);

    /// <summary>The role as W1-08's seeder wrote it: today's definition without anything a later release added.</summary>
    private static HashSet<string> AsSeededAtTheBaseline(string roleName)
    {
        var later = DefaultRoleSeedManifest.Introductions
            .Where(introduction => introduction.RoleName == roleName)
            .Select(introduction => introduction.Permission)
            .ToHashSet(StringComparer.Ordinal);

        return Shipped(roleName).Where(permission => !later.Contains(permission)).ToHashSet(StringComparer.Ordinal);
    }

    private async Task<(int StoreManagerId, int OrderStaffId, int ContentEditorId)> SeedTheRolesAsAnOlderBuildDidAsync()
    {
        var storeManager = await database.CreateRoleAsync(StoreManager, AsSeededAtTheBaseline(StoreManager));
        var orderStaff = await database.CreateRoleAsync(OrderStaff, AsSeededAtTheBaseline(OrderStaff));
        var contentEditor = await database.CreateRoleAsync(ContentEditor, AsSeededAtTheBaseline(ContentEditor));
        return (storeManager, orderStaff, contentEditor);
    }

    private DefaultRolePermissionSync Sync() => database.Services.GetRequiredService<DefaultRolePermissionSync>();

    private async Task<List<AuditLog>> PermissionChangeAuditAsync(params int[] roleIds)
    {
        await using var scope = database.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var ids = roleIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList();

        return await dbContext.AuditLogs.AsNoTracking()
            .Where(entry => entry.Action == AuditActions.PermissionChange && entry.EntityType == "Role" && ids.Contains(entry.EntityId!))
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private List<CapturedLogEntry> SyncLog() =>
        [.. database.Logs.Entries.Where(entry => entry.Category == SyncCategory)];

    // ------------------------------------------------------------------------------------------------------
    // The acceptance criteria.
    // ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>A fresh database</b>: the admin seed creates each default role complete, and the reference-data phase
    /// that follows records the current version without granting or auditing anything — nothing is re-applied.
    /// Through the real host, because that is the sequence a first deployment runs.
    /// </summary>
    [Fact]
    public async Task A_fresh_database_seeds_every_role_in_full_and_records_the_current_version()
    {
        database.SkipIfUnavailable();
        await database.ResetAsync();

        var email = $"sync-fresh-{Guid.NewGuid():N}@example.com";
        await using (var factory = new ApiFactory(
            database.ConnectionString,
            additionalConfiguration: new Dictionary<string, string?>
            {
                ["Seed:Admin:Enabled"] = "true",
                ["Seed:Admin:Email"] = email,
                ["Seed:Admin:Password"] = "Correct-Horse-Battery-Staple-1!",
            }))
        {
            _ = factory.Services; // Touching Services starts the host, which runs both seeders.
        }

        foreach (var definition in DefaultRoleDefinitions.All)
        {
            Assert.Equal(definition.Permissions.ToHashSet(StringComparer.Ordinal), await database.HeldAsync(definition.Name));
        }

        Assert.Equal(
            DefaultRoleSeedManifest.CurrentVersion.ToString(CultureInfo.InvariantCulture),
            await database.RecordedVersionAsync());

        // Nothing was "granted": the roles were complete when the version was recorded.
        await using var scope = database.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var roleIds = await dbContext.Roles.AsNoTracking().Select(role => role.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await PermissionChangeAuditAsync([.. roleIds]));
    }

    /// <summary>
    /// <b>An existing database with the old roles</b> gets exactly the permissions the roles are missing, the
    /// version is recorded, the grant is audited, and no other role or permission moves. The three W2-11
    /// permissions are the real case: Customers.Edit, Content.Create and Content.Delete.
    /// </summary>
    [Fact]
    public async Task An_existing_database_gets_exactly_the_missing_permissions_and_the_version_is_recorded()
    {
        database.SkipIfUnavailable();
        await database.ResetAsync();
        var (storeManagerId, orderStaffId, contentEditorId) = await SeedTheRolesAsAnOlderBuildDidAsync();

        // The premise: the roles really are missing the three permissions, and nothing is recorded.
        Assert.DoesNotContain(Permissions.Customers.Edit, await database.HeldAsync(StoreManager));
        Assert.DoesNotContain(Permissions.Content.Create, await database.HeldAsync(StoreManager));
        Assert.DoesNotContain(Permissions.Content.Delete, await database.HeldAsync(ContentEditor));
        Assert.Null(await database.RecordedVersionAsync());
        var orderStaffBefore = await database.ClaimRowsAsync(OrderStaff);

        var result = await Sync().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DefaultRoleSyncOutcome.Completed, result.Outcome);
        Assert.Null(result.PreviousVersion);

        // Exactly the manifest's version-2 grants, per role, and nothing for Order Staff.
        var granted = result.Grants.ToDictionary(
            grant => grant.RoleName,
            grant => grant.Permissions.Order(StringComparer.Ordinal).ToArray());

        Assert.Equal(2, granted.Count);
        Assert.Equal(
            [Permissions.Content.Create, Permissions.Content.Delete, Permissions.Customers.Edit],
            granted[StoreManager]);
        Assert.Equal([Permissions.Content.Create, Permissions.Content.Delete], granted[ContentEditor]);

        Assert.Equal(Shipped(StoreManager), await database.HeldAsync(StoreManager));
        Assert.Equal(Shipped(ContentEditor), await database.HeldAsync(ContentEditor));
        Assert.Equal(Shipped(OrderStaff), await database.HeldAsync(OrderStaff));
        Assert.Equal(orderStaffBefore, await database.ClaimRowsAsync(OrderStaff));

        Assert.Equal("2", await database.RecordedVersionAsync());
        Assert.Empty(result.Differences);

        // One PermissionChange per role that gained something, with no acting user, and the lists to prove it.
        var audit = await PermissionChangeAuditAsync(storeManagerId, orderStaffId, contentEditorId);
        Assert.Equal(2, audit.Count);
        Assert.All(audit, entry =>
        {
            Assert.Null(entry.UserId);
            Assert.NotNull(entry.BeforeJson);
            Assert.NotNull(entry.AfterJson);
        });

        var storeManagerAudit = Assert.Single(audit, entry => entry.EntityId == storeManagerId.ToString(CultureInfo.InvariantCulture));
        Assert.DoesNotContain(Permissions.Customers.Edit, storeManagerAudit.BeforeJson, StringComparison.Ordinal);
        Assert.Contains(Permissions.Customers.Edit, storeManagerAudit.AfterJson, StringComparison.Ordinal);
    }

    /// <summary>A second start finds the version current and changes nothing — not a row, not a stamp, not a log of work.</summary>
    [Fact]
    public async Task A_second_start_changes_nothing()
    {
        database.SkipIfUnavailable();
        await database.ResetAsync();
        var (storeManagerId, orderStaffId, contentEditorId) = await SeedTheRolesAsAnOlderBuildDidAsync();
        var userId = await database.CreateUserInRoleAsync($"sync-second-{Guid.NewGuid():N}@example.com", StoreManager);

        await Sync().RunAsync(TestContext.Current.CancellationToken);

        var storeManagerRows = await database.ClaimRowsAsync(StoreManager);
        var contentEditorRows = await database.ClaimRowsAsync(ContentEditor);
        var orderStaffRows = await database.ClaimRowsAsync(OrderStaff);
        var stampAfterFirst = await StampAsync(userId);
        var auditAfterFirst = (await PermissionChangeAuditAsync(storeManagerId, orderStaffId, contentEditorId)).Count;
        var recordedAfterFirst = await RecordedRowVersionAsync();

        database.Logs.Clear();
        var second = await Sync().RunAsync(TestContext.Current.CancellationToken);

        Assert.Empty(second.Grants);
        Assert.Empty(second.Differences);

        // The same rows with the same ids: nothing was deleted and re-added, nothing was added.
        Assert.Equal(storeManagerRows, await database.ClaimRowsAsync(StoreManager));
        Assert.Equal(contentEditorRows, await database.ClaimRowsAsync(ContentEditor));
        Assert.Equal(orderStaffRows, await database.ClaimRowsAsync(OrderStaff));
        Assert.Equal(stampAfterFirst, await StampAsync(userId));
        Assert.Equal(auditAfterFirst, (await PermissionChangeAuditAsync(storeManagerId, orderStaffId, contentEditorId)).Count);
        Assert.Equal(recordedAfterFirst, await RecordedRowVersionAsync());

        // And it says so in one line, at Information.
        var line = Assert.Single(SyncLog());
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("in step", line.Message, StringComparison.Ordinal);
        Assert.Contains("0 permission(s) granted", line.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A permission a Super Admin removed stays removed.</b> The version gate is what guarantees it: once a
    /// version is recorded, nothing introduced at or before it is ever granted again.
    /// </summary>
    [Fact]
    public async Task A_permission_removed_after_its_version_was_applied_stays_removed()
    {
        database.SkipIfUnavailable();
        await database.ResetAsync();

        // The roles are complete and the version is current — a database this step has already been through.
        await database.CreateRoleAsync(StoreManager, Shipped(StoreManager).Where(p => p != Permissions.Content.Delete && p != Permissions.Orders.Refund));
        await database.CreateRoleAsync(OrderStaff, Shipped(OrderStaff));
        await database.CreateRoleAsync(ContentEditor, Shipped(ContentEditor));
        await database.RecordVersionAsync("2");

        var result = await Sync().RunAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Grants);
        var held = await database.HeldAsync(StoreManager);
        Assert.DoesNotContain(Permissions.Content.Delete, held); // introduced in 2, removed after 2 was applied
        Assert.DoesNotContain(Permissions.Orders.Refund, held); // a baseline permission, removed

        // Reported by name, at Warning, as missing.
        var difference = Assert.Single(result.Differences);
        Assert.Equal(StoreManager, difference.RoleName);
        Assert.Equal(DefaultRoleDifferenceKind.Missing, difference.Kind);
        Assert.Equal([Permissions.Content.Delete, Permissions.Orders.Refund], difference.Permissions.Order(StringComparer.Ordinal));

        var warning = Assert.Single(SyncLog(), entry => entry.Level == LogLevel.Warning);
        Assert.Contains(StoreManager, warning.Message, StringComparison.Ordinal);
        Assert.Contains(Permissions.Content.Delete, warning.Message, StringComparison.Ordinal);
        Assert.Contains(Permissions.Orders.Refund, warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A baseline permission that a role deliberately lacks on a database with <b>no recorded version</b> is not
    /// granted — the first run assumes the baseline, so only what came after it is owed — while what came after
    /// it is.
    /// </summary>
    [Fact]
    public async Task A_baseline_permission_a_role_lacks_is_not_granted_when_no_version_is_recorded()
    {
        database.SkipIfUnavailable();
        await database.ResetAsync();

        await database.CreateRoleAsync(StoreManager, AsSeededAtTheBaseline(StoreManager).Where(p => p != Permissions.Orders.Refund));
        await database.CreateRoleAsync(OrderStaff, AsSeededAtTheBaseline(OrderStaff));
        await database.CreateRoleAsync(ContentEditor, AsSeededAtTheBaseline(ContentEditor));

        await Sync().RunAsync(TestContext.Current.CancellationToken);

        var held = await database.HeldAsync(StoreManager);
        Assert.DoesNotContain(Permissions.Orders.Refund, held);
        Assert.Contains(Permissions.Customers.Edit, held);
        Assert.Contains(Permissions.Content.Create, held);
        Assert.Contains(Permissions.Content.Delete, held);
    }

    /// <summary>A permission a Super Admin <i>added</i> is kept, and reported by name at Information.</summary>
    [Fact]
    public async Task A_permission_a_super_admin_added_is_kept_and_reported()
    {
        database.SkipIfUnavailable();
        await database.ResetAsync();

        await database.CreateRoleAsync(StoreManager, Shipped(StoreManager));
        await database.CreateRoleAsync(OrderStaff, Shipped(OrderStaff).Append(Permissions.Reports.View));
        await database.CreateRoleAsync(ContentEditor, Shipped(ContentEditor));
        await database.RecordVersionAsync("2");

        var result = await Sync().RunAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Grants);
        Assert.Contains(Permissions.Reports.View, await database.HeldAsync(OrderStaff));

        var difference = Assert.Single(result.Differences);
        Assert.Equal(OrderStaff, difference.RoleName);
        Assert.Equal(DefaultRoleDifferenceKind.Extra, difference.Kind);
        Assert.Equal([Permissions.Reports.View], difference.Permissions);

        var line = Assert.Single(SyncLog(), entry => entry.Message.Contains(Permissions.Reports.View, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains(OrderStaff, line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SyncLog(), entry => entry.Level >= LogLevel.Warning);
    }

    /// <summary>
    /// <b>Custom roles and Super Admin are untouched</b> — and so is a role that merely shares a default role's
    /// name without being the seeded system role.
    /// </summary>
    [Fact]
    public async Task A_custom_role_a_look_alike_and_super_admin_are_not_touched()
    {
        database.SkipIfUnavailable();
        await database.ResetAsync();

        var superAdminId = await database.CreateRoleAsync("Super Admin", [], isSystem: true, grantsAll: true);
        var customId = await database.CreateRoleAsync("Regional Manager", [Permissions.Orders.View, Permissions.Customers.View]);

        // A role the client made (IsSystem false) that happens to carry a default role's name: not the seeded one.
        var lookAlikeId = await database.CreateRoleAsync(ContentEditor, [Permissions.Media.View], isSystem: false);

        await database.CreateRoleAsync(StoreManager, AsSeededAtTheBaseline(StoreManager));

        var superAdminBefore = await database.ClaimRowsAsync("Super Admin");
        var customBefore = await database.ClaimRowsAsync("Regional Manager");
        var lookAlikeBefore = await database.ClaimRowsAsync(ContentEditor);

        var result = await Sync().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(superAdminBefore, await database.ClaimRowsAsync("Super Admin"));
        Assert.Empty(superAdminBefore);
        Assert.Equal(customBefore, await database.ClaimRowsAsync("Regional Manager"));
        Assert.Equal(lookAlikeBefore, await database.ClaimRowsAsync(ContentEditor));

        // The managed role in the same database still got its grants.
        Assert.Equal(Shipped(StoreManager), await database.HeldAsync(StoreManager));

        Assert.Equal([ContentEditor], result.UnmanagedRoles);
        Assert.Contains(SyncLog(), entry => entry.Level == LogLevel.Warning && entry.Message.Contains(ContentEditor, StringComparison.Ordinal));

        // No audit row for any of the three roles that were left alone.
        Assert.Empty(await PermissionChangeAuditAsync(superAdminId, customId, lookAlikeId));
    }

    /// <summary>A default role that does not exist is not created — that is the admin seed's job, behind its flag.</summary>
    [Fact]
    public async Task A_missing_default_role_is_not_created()
    {
        database.SkipIfUnavailable();
        await database.ResetAsync();

        await database.CreateRoleAsync(OrderStaff, Shipped(OrderStaff));

        var result = await Sync().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { ContentEditor, StoreManager },
            result.AbsentRoles.Order(StringComparer.Ordinal).ToArray());
        await using var scope = database.Services.CreateAsyncScope();
        var roleNames = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Roles.AsNoTracking()
            .Select(role => role.Name)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([OrderStaff], roleNames);
        Assert.Equal("2", await database.RecordedVersionAsync());
    }

    /// <summary>
    /// With no default roles at all, the current version is still recorded: a role created afterwards is created
    /// complete, so there is nothing for a later start to owe it.
    /// </summary>
    [Fact]
    public async Task A_database_with_no_default_roles_records_the_version_and_creates_nothing()
    {
        database.SkipIfUnavailable();
        await database.ResetAsync();

        var result = await Sync().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, result.AbsentRoles.Count);
        Assert.Equal("2", await database.RecordedVersionAsync());

        await using var scope = database.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Roles.AnyAsync(TestContext.Current.CancellationToken));
    }

    // ------------------------------------------------------------------------------------------------------
    // Concurrency.
    // ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Overlapping starts end consistent.</b> Eight runs released together against the old roles: none throws,
    /// the grants land exactly once, the unique index never has to refuse anything, and the version and the audit
    /// trail are written once.
    /// </summary>
    [Fact]
    public async Task Overlapping_runs_end_consistent_with_no_duplicate_and_no_crash()
    {
        database.SkipIfUnavailable();
        await database.ResetAsync();
        var (storeManagerId, orderStaffId, contentEditorId) = await SeedTheRolesAsAnOlderBuildDidAsync();
        var userId = await database.CreateUserInRoleAsync($"sync-race-{Guid.NewGuid():N}@example.com", StoreManager);
        var stampBefore = await StampAsync(userId);

        using var gate = new ManualResetEventSlim(initialState: false);

        var runs = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(
                async () =>
                {
                    gate.Wait(TestContext.Current.CancellationToken);
                    return await Sync().RunAsync(TestContext.Current.CancellationToken);
                },
                TestContext.Current.CancellationToken))
            .ToList();

        gate.Set();
        var results = await Task.WhenAll(runs);

        Assert.All(results, result => Assert.Equal(DefaultRoleSyncOutcome.Completed, result.Outcome));

        // One run did the work; the rest waited for its lock, re-read the version it committed and did none.
        Assert.Equal(1, results.Count(result => result.Grants.Count > 0));

        Assert.Equal(Shipped(StoreManager), await database.HeldAsync(StoreManager));
        Assert.Equal(Shipped(ContentEditor), await database.HeldAsync(ContentEditor));
        Assert.Equal(Shipped(OrderStaff), await database.HeldAsync(OrderStaff));
        Assert.Equal("2", await database.RecordedVersionAsync());

        Assert.Equal(2, (await PermissionChangeAuditAsync(storeManagerId, orderStaffId, contentEditorId)).Count);

        // The holder of the role that gained permissions was asked to revalidate.
        Assert.NotEqual(stampBefore, await StampAsync(userId));
    }

    // ------------------------------------------------------------------------------------------------------
    // Signed-in users.
    // ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>A user who already holds the role sees the new permission on their next request.</b> The permission
    /// cache is keyed by <c>(user, SecurityStamp)</c> and lives five minutes, so a grant that did not move the stamp
    /// would be invisible to a signed-in user — and to an older instance in a rolling deployment — until it
    /// expired. The user in a role that gained nothing keeps their stamp.
    /// </summary>
    [Fact]
    public async Task A_user_holding_the_role_sees_the_new_permission_on_the_next_request()
    {
        database.SkipIfUnavailable();
        await database.ResetAsync();
        await SeedTheRolesAsAnOlderBuildDidAsync();

        var storeManagerUser = await database.CreateUserInRoleAsync($"sync-sm-{Guid.NewGuid():N}@example.com", StoreManager);
        var orderStaffUser = await database.CreateUserInRoleAsync($"sync-os-{Guid.NewGuid():N}@example.com", OrderStaff);

        var stampBefore = await StampAsync(storeManagerUser);
        var orderStaffStampBefore = await StampAsync(orderStaffUser);

        // The request before the grant: the answer is computed and cached under the user's current stamp.
        var before = await EffectivePermissionsAsync(storeManagerUser);
        Assert.False(before.Has(Permissions.Content.Create));
        Assert.False(before.Has(Permissions.Customers.Edit));

        // A second look is served from the cache, which is the thing a missing stamp refresh would leave stale.
        Assert.False((await EffectivePermissionsAsync(storeManagerUser)).Has(Permissions.Content.Create));

        await Sync().RunAsync(TestContext.Current.CancellationToken);

        var after = await EffectivePermissionsAsync(storeManagerUser);
        Assert.True(after.Has(Permissions.Content.Create));
        Assert.True(after.Has(Permissions.Content.Delete));
        Assert.True(after.Has(Permissions.Customers.Edit));

        Assert.NotEqual(stampBefore, await StampAsync(storeManagerUser));
        Assert.Equal(orderStaffStampBefore, await StampAsync(orderStaffUser));

        // Order Staff gained nothing, so the user in it is not asked to revalidate for no reason.
        var orderStaffPermissions = await EffectivePermissionsAsync(orderStaffUser);
        Assert.False(orderStaffPermissions.Has(Permissions.Content.Create));
    }

    // ------------------------------------------------------------------------------------------------------
    // The recorded version.
    // ------------------------------------------------------------------------------------------------------

    /// <summary>A database newer than the build is left as found, and the build says so rather than granting.</summary>
    [Fact]
    public async Task A_recorded_version_newer_than_the_build_is_left_alone()
    {
        database.SkipIfUnavailable();
        await database.ResetAsync();
        await SeedTheRolesAsAnOlderBuildDidAsync();
        await database.RecordVersionAsync("99");

        var result = await Sync().RunAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Grants);
        Assert.Equal("99", await database.RecordedVersionAsync());
        Assert.DoesNotContain(Permissions.Customers.Edit, await database.HeldAsync(StoreManager));
        Assert.Contains(SyncLog(), entry => entry.Level == LogLevel.Warning && entry.Message.Contains("99", StringComparison.Ordinal));
    }

    /// <summary>A recorded value that is not a version is read as "none", reported, and replaced.</summary>
    [Fact]
    public async Task An_unreadable_recorded_version_is_treated_as_the_baseline()
    {
        database.SkipIfUnavailable();
        await database.ResetAsync();
        await SeedTheRolesAsAnOlderBuildDidAsync();
        await database.RecordVersionAsync("two");

        var result = await Sync().RunAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Grants);
        Assert.Equal("2", await database.RecordedVersionAsync());
        Assert.Contains(SyncLog(), entry => entry.Level == LogLevel.Warning && entry.Message.Contains("two", StringComparison.Ordinal));
    }

    /// <summary>
    /// A version earlier than current is brought up, and only what came after it is granted — the shape of the
    /// next release, exercised against the real manifest by recording version 1 explicitly.
    /// </summary>
    [Fact]
    public async Task A_database_recorded_at_the_baseline_is_owed_the_same_as_one_with_nothing_recorded()
    {
        database.SkipIfUnavailable();
        await database.ResetAsync();
        await SeedTheRolesAsAnOlderBuildDidAsync();
        await database.RecordVersionAsync("1");

        await Sync().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Shipped(StoreManager), await database.HeldAsync(StoreManager));
        Assert.Equal(Shipped(ContentEditor), await database.HeldAsync(ContentEditor));
        Assert.Equal("2", await database.RecordedVersionAsync());
    }

    // ------------------------------------------------------------------------------------------------------
    // Wiring.
    // ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The reference-data phase runs it, every start, whatever <c>Seed:Admin:Enabled</c> says — which is what
    /// makes it reach a live database that has the admin seed turned off. This host never sets that flag.
    /// </summary>
    [Fact]
    public async Task The_reference_data_phase_brings_the_roles_up_without_the_admin_seed_flag()
    {
        database.SkipIfUnavailable();
        await database.ResetAsync();
        await SeedTheRolesAsAnOlderBuildDidAsync();

        await using var scope = database.Services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<CatalogSeedRunner>();

        await runner.RunReferenceDataAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Shipped(StoreManager), await database.HeldAsync(StoreManager));
        Assert.Equal(Shipped(ContentEditor), await database.HeldAsync(ContentEditor));
        Assert.Equal("2", await database.RecordedVersionAsync());
    }

    /// <summary>
    /// The version row is invisible to the settings screen and its writers: they work from
    /// <c>SettingCatalog</c>, which does not list it. Asserted rather than assumed, because the row is in the
    /// same table as the client's shop details.
    /// </summary>
    [Fact]
    public async Task The_version_row_is_not_a_catalogue_setting_and_needs_super_admin_permission()
    {
        database.SkipIfUnavailable();
        await database.ResetAsync();
        await Sync().RunAsync(TestContext.Current.CancellationToken);

        Assert.Null(SpencerDenim.Domain.Settings.SettingCatalog.Find(DefaultRolePermissionSync.VersionKey));

        await using var scope = database.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Settings.AsNoTracking()
            .SingleAsync(setting => setting.Key == DefaultRolePermissionSync.VersionKey, TestContext.Current.CancellationToken);

        Assert.Equal(Permissions.Settings.Security, row.RequiredPermission);
        Assert.False(row.IsSecret);
        Assert.Equal(SpencerDenim.Domain.Settings.SettingValueType.Int, row.ValueType);
    }

    // ------------------------------------------------------------------------------------------------------
    // Helpers that read what the tests are about.
    // ------------------------------------------------------------------------------------------------------

    private async Task<string?> StampAsync(int userId)
    {
        await using var scope = database.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.SecurityStamp)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> RecordedRowVersionAsync()
    {
        await using var scope = database.Services.CreateAsyncScope();
        var rowVersion = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Settings.AsNoTracking()
            .Where(setting => setting.Key == DefaultRolePermissionSync.VersionKey)
            .Select(setting => setting.RowVersion)
            .SingleAsync(TestContext.Current.CancellationToken);

        return Convert.ToHexString(rowVersion!);
    }

    private async Task<EffectivePermissions> EffectivePermissionsAsync(int userId)
    {
        // A scope per request, as the API has; the cache underneath is the singleton IMemoryCache.
        await using var scope = database.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IEffectivePermissionsProvider>()
            .GetEffectivePermissionsAsync(userId, TestContext.Current.CancellationToken);
    }
}
