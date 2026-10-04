using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Identity;

/// <summary>
/// Doc 08 §3.1 DB4 rule 4, the last-Super-Admin guard, exercised directly against
/// <see cref="IStaffUserRoleManagementStore"/> against a real SQL Server database (so the real
/// <c>sp_getapplock</c> transaction runs, not a mock). This deliberately bypasses the command handler's
/// actor/self-edit authorization layer — see the remarks on <c>UserEndpointTests
/// .A_lone_active_super_admin_cannot_be_deactivated_by_anyone_reachable_through_this_api</c> for why that
/// layer makes "the literal sole remaining Super Admin" scenario unreachable through the HTTP surface, even
/// though the store must still refuse it on its own merits (defence in depth, and because nothing but this
/// class actually enforces the guard).
/// <para>
/// <c>UX_Roles_GrantsAll</c> (doc 08 §3.1) allows at most one <c>GrantsAllPermissions = 1</c> role in the whole
/// database, and this suite shares one database for the whole test assembly (doc 06 §4, <c>AssemblyFixture</c>),
/// so every test here reuses the single existing GrantsAll role (by flag, creating it only if truly none
/// exists yet) instead of creating its own, and explicitly deactivates any other active holder left behind by
/// an earlier test before asserting a guard outcome that depends on an exact headcount.
/// </para>
/// </summary>
public sealed class StaffUserRoleManagementStoreTests(SqlServerDatabaseFixture sqlServer)
{
    /// <summary>
    /// A GrantsAll actor's context never fails the store's tester-round-1 defect-2 re-validation (see
    /// <see cref="RbacActorContext"/>), so tests that only exercise rule 4 (the last-Super-Admin guard) pass
    /// this — exactly mirroring how a real GrantsAll actor's authorization decision is never actually stale.
    /// </summary>
    private static readonly RbacActorContext GrantsAllActor = new(HasGrantsAll: true, HasUsersManage: true, EffectivePermissions: new HashSet<string>());

    [Fact]
    public async Task Deactivating_the_sole_active_GrantsAll_holder_is_rejected()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();

        var roleName = await GetOrCreateGrantsAllRoleNameAsync(scope.ServiceProvider);
        var soleAdminId = await CreateActiveGrantsAllUserAsync(scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(), roleName, "sole");
        await DeactivateAllOtherActiveGrantsAllHoldersAsync(scope.ServiceProvider, soleAdminId);

        var store = scope.ServiceProvider.GetRequiredService<IStaffUserRoleManagementStore>();
        var result = await store.SetActiveAsync(soleAdminId, active: false, GrantsAllActor, () => Audit(), TestContext.Current.CancellationToken);

        Assert.IsType<SetActiveStoreResult.LastSuperAdminGuardTriggered>(result);

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stillActive = await dbContext.Users.AsNoTracking().Where(u => u.Id == soleAdminId).Select(u => u.IsActive).SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(stillActive);

        await RemoveFromGrantsAllRoleAsync(scope.ServiceProvider, soleAdminId);
    }

    [Fact]
    public async Task Unassigning_the_GrantsAll_role_from_the_sole_holder_is_rejected()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();

        var roleName = await GetOrCreateGrantsAllRoleNameAsync(scope.ServiceProvider);
        var soleAdminId = await CreateActiveGrantsAllUserAsync(scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(), roleName, "sole");
        await DeactivateAllOtherActiveGrantsAllHoldersAsync(scope.ServiceProvider, soleAdminId);

        var store = scope.ServiceProvider.GetRequiredService<IStaffUserRoleManagementStore>();
        var result = await store.ReplaceRolesAsync(soleAdminId, [], GrantsAllActor, () => Audit(), TestContext.Current.CancellationToken);

        Assert.IsType<ReplaceRolesStoreResult.LastSuperAdminGuardTriggered>(result);

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await userManager.FindByIdAsync(soleAdminId.ToString(CultureInfo.InvariantCulture));
        Assert.NotNull(user);
        var roles = await userManager.GetRolesAsync(user);
        Assert.NotEmpty(roles);

        await RemoveFromGrantsAllRoleAsync(scope.ServiceProvider, soleAdminId);
    }

    [Fact]
    public async Task Deactivating_one_of_two_active_GrantsAll_holders_is_allowed()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roleName = await GetOrCreateGrantsAllRoleNameAsync(scope.ServiceProvider);

        var firstId = await CreateActiveGrantsAllUserAsync(userManager, roleName, "first");
        var secondId = await CreateActiveGrantsAllUserAsync(userManager, roleName, "second");
        await DeactivateAllOtherActiveGrantsAllHoldersAsync(scope.ServiceProvider, firstId, secondId);

        var store = scope.ServiceProvider.GetRequiredService<IStaffUserRoleManagementStore>();
        var result = await store.SetActiveAsync(firstId, active: false, GrantsAllActor, () => Audit(), TestContext.Current.CancellationToken);

        Assert.IsType<SetActiveStoreResult.Success>(result);

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var secondStillActive = await dbContext.Users.AsNoTracking().Where(u => u.Id == secondId).Select(u => u.IsActive).SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(secondStillActive);

        await RemoveFromGrantsAllRoleAsync(scope.ServiceProvider, firstId, secondId);
    }

    /// <summary>
    /// Tester-round-1 fix (W1-08, #8, defect 2): reproduces exactly the scenario the tester described —
    /// a non-GrantsAll actor's authorization decision is made against the target's state at read time, but
    /// before that actor's write reaches the store, a DIFFERENT (GrantsAll) admin fully commits a role change
    /// to the SAME target that pushes it above the first actor's own permissions. The first actor's write must
    /// now fail cleanly (<see cref="SetActiveStoreResult.AuthorizationStale"/>) instead of silently deactivating
    /// a user who, at the true moment of the write, the actor was no longer authorized to manage — proving the
    /// lost-update / stale-authorization gap is closed, not just "the last-Super-Admin guard still works".
    /// </summary>
    [Fact]
    public async Task Deactivation_authorized_against_a_stale_snapshot_is_rejected_after_a_concurrent_role_change_widens_the_target()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();

        var narrowRoleId = await CreateRoleAsync(roleManager, Permissions.Orders.View);
        var wideRoleId = await CreateRoleAsync(roleManager, Permissions.Content.Edit);
        var targetId = await CreateStaffUserWithRoleAsync(userManager, roleManager, "stale-target", narrowRoleId);

        // The low-privilege actor's authorization decision, made when the target only held the narrow role —
        // this is deliberately never refreshed, standing in for the handler's now-stale, non-transactional read.
        var lowPrivilegeActor = new RbacActorContext(HasGrantsAll: false, HasUsersManage: true, EffectivePermissions: new HashSet<string> { Permissions.Orders.View });

        // A different (GrantsAll) admin fully commits a role change to the SAME target in between — before the
        // low-privilege actor's write below ever reaches the store.
        var grantsAllStore = scope.ServiceProvider.GetRequiredService<IStaffUserRoleManagementStore>();
        var concurrentChange = await grantsAllStore.ReplaceRolesAsync(targetId, [wideRoleId], GrantsAllActor, () => Audit(), TestContext.Current.CancellationToken);
        Assert.IsType<ReplaceRolesStoreResult.Success>(concurrentChange);

        var store = scope.ServiceProvider.GetRequiredService<IStaffUserRoleManagementStore>();
        var result = await store.SetActiveAsync(targetId, active: false, lowPrivilegeActor, () => Audit(), TestContext.Current.CancellationToken);

        Assert.IsType<SetActiveStoreResult.AuthorizationStale>(result);

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stillActive = await dbContext.Users.AsNoTracking().Where(u => u.Id == targetId).Select(u => u.IsActive).SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(stillActive, "The stale write must not have silently deactivated the target.");
    }

    /// <summary>
    /// Tester-round-1 fix (defect 2), true concurrency: two writers race for the SAME target — a GrantsAll
    /// admin widening the target's roles, and a non-GrantsAll actor whose authorization was only ever valid
    /// against the target's original (narrow) roles. Whichever of the two actually acquires the per-target
    /// <c>sp_getapplock</c> first, the outcome must never violate the subset invariant: either the low-privilege
    /// write commits first (while the target still legitimately qualifies) and the GrantsAll write then
    /// (unconditionally) supersedes it, or the GrantsAll write commits first and the low-privilege write is
    /// then correctly refused against the now-fresh, wider state. Either way the target's FINAL role must be the
    /// GrantsAll admin's, never silently reverted by a stale low-privilege write racing in afterwards — that
    /// reversion is exactly the lost update this fix closes.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_writers_to_the_same_target_never_leave_a_lost_update_from_the_stale_actor()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await using var setupScope = factory.Services.CreateAsyncScope();
        var userManager = setupScope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roleManager = setupScope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();

        var narrowRoleId = await CreateRoleAsync(roleManager, Permissions.Orders.View);
        var wideRoleId = await CreateRoleAsync(roleManager, Permissions.Content.Edit);
        var targetId = await CreateStaffUserWithRoleAsync(userManager, roleManager, "race-target", narrowRoleId);

        var lowPrivilegeActor = new RbacActorContext(HasGrantsAll: false, HasUsersManage: true, EffectivePermissions: new HashSet<string> { Permissions.Orders.View });

        // Two independent scopes, exactly like two concurrent HTTP requests each getting their own DbContext.
        await using var scopeA = factory.Services.CreateAsyncScope();
        await using var scopeB = factory.Services.CreateAsyncScope();
        var storeA = scopeA.ServiceProvider.GetRequiredService<IStaffUserRoleManagementStore>();
        var storeB = scopeB.ServiceProvider.GetRequiredService<IStaffUserRoleManagementStore>();

        var lowPrivilegeWriteTask = storeA.ReplaceRolesAsync(targetId, [narrowRoleId], lowPrivilegeActor, () => Audit(), TestContext.Current.CancellationToken);
        var grantsAllWriteTask = storeB.ReplaceRolesAsync(targetId, [wideRoleId], GrantsAllActor, () => Audit(), TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(lowPrivilegeWriteTask, grantsAllWriteTask);
        var lowPrivilegeResult = results[0];
        var grantsAllResult = results[1];

        // The GrantsAll write is never subject to the subset rule, so it must always succeed regardless of
        // which writer's per-target applock acquisition won the race.
        Assert.IsType<ReplaceRolesStoreResult.Success>(grantsAllResult);

        // The low-privilege write must either have committed legitimately (it ran first, while the target was
        // still narrow) or been cleanly refused as stale (the GrantsAll write ran first) — never anything else.
        Assert.True(
            lowPrivilegeResult is ReplaceRolesStoreResult.Success or ReplaceRolesStoreResult.AuthorizationStale,
            $"Unexpected low-privilege result: {lowPrivilegeResult.GetType().Name}");

        // Whichever order the two writers actually ran in, the target's FINAL role must be the GrantsAll
        // admin's — proving the low-privilege actor never got to (re-)apply its stale narrow role on top of the
        // GrantsAll admin's change, which is exactly the lost update this fix closes.
        await using var assertionScope = factory.Services.CreateAsyncScope();
        var dbContext = assertionScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var finalUserManager = assertionScope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var targetUser = await finalUserManager.FindByIdAsync(targetId.ToString(CultureInfo.InvariantCulture));
        Assert.NotNull(targetUser);
        var finalRoles = await finalUserManager.GetRolesAsync(targetUser);
        var wideRoleName = await dbContext.Roles.AsNoTracking().Where(r => r.Id == wideRoleId).Select(r => r.Name).SingleAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(wideRoleName);
        Assert.Equal([wideRoleName], finalRoles);
    }

    /// <summary>
    /// Issue #59 for the staff-user writes: the audit row and the change it describes are one transaction. A write
    /// the store refuses (here, a stale authorization) must leave <b>no</b> row, and one it accepts must leave
    /// exactly one. Before the conversion to <c>Enlist</c> the handler wrote the row after the store had already
    /// committed, so the first half held only because the handler returned early, not because anything made it so.
    /// </summary>
    [Fact]
    public async Task A_refused_write_leaves_no_audit_row_and_an_accepted_one_leaves_exactly_one()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();
        var store = scope.ServiceProvider.GetRequiredService<IStaffUserRoleManagementStore>();

        var narrowRoleId = await CreateRoleAsync(roleManager, Permissions.Orders.View);
        var wideRoleId = await CreateRoleAsync(roleManager, Permissions.Content.Edit);
        var targetId = await CreateStaffUserWithRoleAsync(userManager, roleManager, "audit-pair", narrowRoleId);

        var acceptedMarker = $"accepted-{Guid.NewGuid():N}";
        var accepted = await store.ReplaceRolesAsync(targetId, [wideRoleId], GrantsAllActor, () => Audit(acceptedMarker), TestContext.Current.CancellationToken);
        Assert.IsType<ReplaceRolesStoreResult.Success>(accepted);

        // The target now holds the wide role, so this low-privilege actor's earlier decision is stale.
        var lowPrivilegeActor = new RbacActorContext(HasGrantsAll: false, HasUsersManage: true, EffectivePermissions: new HashSet<string> { Permissions.Orders.View });
        var refusedMarker = $"refused-{Guid.NewGuid():N}";
        var refused = await store.SetActiveAsync(targetId, active: false, lowPrivilegeActor, () => Audit(refusedMarker), TestContext.Current.CancellationToken);
        Assert.IsType<SetActiveStoreResult.AuthorizationStale>(refused);

        await using var readScope = factory.Services.CreateAsyncScope();
        var dbContext = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await CountAuditRowsAsync(dbContext, acceptedMarker));
        Assert.Equal(0, await CountAuditRowsAsync(dbContext, refusedMarker));
    }

    /// <summary>
    /// The other direction of issue #59: if the audit row itself cannot be written, the change must not survive
    /// without it. An over-long <c>UserAgent</c> is rejected by SQL Server only at the final save, after the role
    /// change and the stamp rotation have already been saved inside the transaction, so a pass here proves those
    /// earlier saves are rolled back with it rather than left behind as a change with no trail.
    /// </summary>
    [Fact]
    public async Task A_failure_writing_the_audit_row_rolls_back_the_role_change_with_it()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        int narrowRoleId;
        int wideRoleId;
        int targetId;
        string stampBefore;
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var userManager = setup.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var roleManager = setup.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();
            narrowRoleId = await CreateRoleAsync(roleManager, Permissions.Orders.View);
            wideRoleId = await CreateRoleAsync(roleManager, Permissions.Content.Edit);
            targetId = await CreateStaffUserWithRoleAsync(userManager, roleManager, "audit-fails", narrowRoleId);
            stampBefore = (await userManager.FindByIdAsync(targetId.ToString(CultureInfo.InvariantCulture)))!.SecurityStamp!;
        }

        await using (var writeScope = factory.Services.CreateAsyncScope())
        {
            var store = writeScope.ServiceProvider.GetRequiredService<IStaffUserRoleManagementStore>();
            await Assert.ThrowsAnyAsync<Exception>(() =>
                store.ReplaceRolesAsync(targetId, [wideRoleId], GrantsAllActor, UnwritableAudit, TestContext.Current.CancellationToken));
        }

        await using var readScope = factory.Services.CreateAsyncScope();
        var readUserManager = readScope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var readRoleManager = readScope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();
        var user = await readUserManager.FindByIdAsync(targetId.ToString(CultureInfo.InvariantCulture));
        Assert.NotNull(user);

        var narrowName = await readRoleManager.Roles.Where(r => r.Id == narrowRoleId).Select(r => r.Name!).SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal([narrowName], await readUserManager.GetRolesAsync(user));
        Assert.Equal(stampBefore, user.SecurityStamp);
    }

    [Fact]
    public async Task A_failure_writing_the_audit_row_rolls_back_a_deactivation_and_a_profile_edit_with_it()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        int targetId;
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var roleId = await CreateRoleAsync(setup.ServiceProvider.GetRequiredService<RoleManager<AppRole>>(), Permissions.Orders.View);
            targetId = await CreateStaffUserWithRoleAsync(
                setup.ServiceProvider.GetRequiredService<UserManager<AppUser>>(),
                setup.ServiceProvider.GetRequiredService<RoleManager<AppRole>>(),
                "audit-fails-2",
                roleId);
        }

        await using (var deactivateScope = factory.Services.CreateAsyncScope())
        {
            var store = deactivateScope.ServiceProvider.GetRequiredService<IStaffUserRoleManagementStore>();
            await Assert.ThrowsAnyAsync<Exception>(() =>
                store.SetActiveAsync(targetId, active: false, GrantsAllActor, UnwritableAudit, TestContext.Current.CancellationToken));
        }

        await using (var editScope = factory.Services.CreateAsyncScope())
        {
            var store = editScope.ServiceProvider.GetRequiredService<IStaffUserRoleManagementStore>();
            await Assert.ThrowsAnyAsync<Exception>(() =>
                store.UpdateProfileAsync(targetId, "Changed", "Name", newEmail: null, GrantsAllActor, UnwritableAudit, TestContext.Current.CancellationToken));
        }

        await using var readScope = factory.Services.CreateAsyncScope();
        var user = await readScope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking()
            .SingleAsync(u => u.Id == targetId, TestContext.Current.CancellationToken);
        Assert.True(user.IsActive, "The deactivation must not survive a failed audit write.");
        Assert.Equal("Test", user.FirstName);
        Assert.Equal("Staff", user.LastName);
    }

    /// <summary>
    /// Creating a user was the one write here with no transaction at all: a failure after the user row was
    /// inserted left a staff user who existed but held no roles. It is one transaction now, so a failed audit
    /// write leaves no user, and a successful one leaves a row naming the id that was really inserted, which
    /// the handler cannot know until the store has inserted them.
    /// </summary>
    [Fact]
    public async Task Creating_a_user_is_one_transaction_with_its_audit_row()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        int roleId;
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            roleId = await CreateRoleAsync(setup.ServiceProvider.GetRequiredService<RoleManager<AppRole>>(), Permissions.Orders.View);
        }

        var failedEmail = $"never-created-{Guid.NewGuid():N}@example.com";
        await using (var failingScope = factory.Services.CreateAsyncScope())
        {
            var store = failingScope.ServiceProvider.GetRequiredService<IStaffUserRoleManagementStore>();
            await Assert.ThrowsAnyAsync<Exception>(() =>
                store.CreateStaffUserAsync(
                    new NewStaffUserData(failedEmail, "Never", "Created", "Correct-Password-1!", [roleId]),
                    _ => UnwritableAudit(),
                    TestContext.Current.CancellationToken));
        }

        var okEmail = $"created-{Guid.NewGuid():N}@example.com";
        var marker = $"create-{Guid.NewGuid():N}";
        CreateStaffUserStoreResult created;
        await using (var okScope = factory.Services.CreateAsyncScope())
        {
            var store = okScope.ServiceProvider.GetRequiredService<IStaffUserRoleManagementStore>();
            created = await store.CreateStaffUserAsync(
                new NewStaffUserData(okEmail, "Really", "Created", "Correct-Password-1!", [roleId]),
                newUserId => Audit(marker, entityId: newUserId.ToString(CultureInfo.InvariantCulture)),
                TestContext.Current.CancellationToken);
        }

        var success = Assert.IsType<CreateStaffUserStoreResult.Success>(created);

        await using var readScope = factory.Services.CreateAsyncScope();
        var dbContext = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = readScope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        Assert.Null(await userManager.FindByEmailAsync(failedEmail));
        Assert.NotNull(await userManager.FindByEmailAsync(okEmail));

        var row = await dbContext.AuditLogs.AsNoTracking().SingleAsync(a => a.UserAgent == marker, TestContext.Current.CancellationToken);
        Assert.Equal(success.UserId.ToString(CultureInfo.InvariantCulture), row.EntityId);
    }

    /// <summary>A valid audit row. <paramref name="marker"/> goes in <c>UserAgent</c> so a test can find exactly its own row in a shared database.</summary>
    private static AuditLog Audit(string? marker = null, string? entityId = null) => new()
    {
        OccurredAtUtc = DateTime.UtcNow,
        Action = AuditActions.PermissionChange,
        EntityType = "User",
        EntityId = entityId ?? "0",
        UserAgent = marker ?? "store-test",
    };

    /// <summary>A row SQL Server rejects at the last save: <c>UserAgent</c> is capped at 400 characters.</summary>
    private static AuditLog UnwritableAudit() => Audit(new string('x', 401));

    private static Task<int> CountAuditRowsAsync(AppDbContext dbContext, string marker) =>
        dbContext.AuditLogs.AsNoTracking().CountAsync(a => a.UserAgent == marker, TestContext.Current.CancellationToken);

    /// <summary>Creates a fresh, uniquely-named, non-GrantsAll role granting exactly one permission.</summary>
    private static async Task<int> CreateRoleAsync(RoleManager<AppRole> roleManager, string permission)
    {
        var role = new AppRole { Name = $"{permission}-{Guid.NewGuid():N}", GrantsAllPermissions = false, IsSystem = false };
        var createResult = await roleManager.CreateAsync(role);
        Assert.True(createResult.Succeeded, string.Join("; ", createResult.Errors.Select(e => e.Description)));

        var claimResult = await roleManager.AddClaimAsync(role, new System.Security.Claims.Claim("perm", permission));
        Assert.True(claimResult.Succeeded, string.Join("; ", claimResult.Errors.Select(e => e.Description)));

        return role.Id;
    }

    private static async Task<int> CreateStaffUserWithRoleAsync(UserManager<AppUser> userManager, RoleManager<AppRole> roleManager, string label, int roleId)
    {
        var email = $"{label}-{Guid.NewGuid():N}@example.com";
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            UserType = UserType.Staff,
            FirstName = "Test",
            LastName = "Staff",
            IsActive = true,
        };

        var createResult = await userManager.CreateAsync(user, "Correct-Password-1!");
        Assert.True(createResult.Succeeded, string.Join("; ", createResult.Errors.Select(e => e.Description)));

        var roleName = await roleManager.Roles.Where(r => r.Id == roleId).Select(r => r.Name!).SingleAsync(TestContext.Current.CancellationToken);
        var addResult = await userManager.AddToRoleAsync(user, roleName);
        Assert.True(addResult.Succeeded, string.Join("; ", addResult.Errors.Select(e => e.Description)));

        return user.Id;
    }

    /// <summary>Finds the one existing GrantsAll role by its flag, or creates it (named "Super Admin", matching the real seeder) only if truly none exists yet.</summary>
    private static async Task<string> GetOrCreateGrantsAllRoleNameAsync(IServiceProvider services)
    {
        var dbContext = services.GetRequiredService<AppDbContext>();
        var existingName = await dbContext.Roles.AsNoTracking()
            .Where(r => r.GrantsAllPermissions)
            .Select(r => r.Name)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        if (existingName is not null)
        {
            return existingName;
        }

        var roleManager = services.GetRequiredService<RoleManager<AppRole>>();
        var role = new AppRole { Name = "Super Admin", GrantsAllPermissions = true, IsSystem = true };
        var roleResult = await roleManager.CreateAsync(role);
        Assert.True(roleResult.Succeeded, string.Join("; ", roleResult.Errors.Select(e => e.Description)));
        return role.Name!;
    }

    /// <summary>See the class remarks: keeps a guard test's headcount precondition exact regardless of what earlier tests in this shared database left active.</summary>
    private static async Task DeactivateAllOtherActiveGrantsAllHoldersAsync(IServiceProvider services, params int[] exceptUserIds)
    {
        var dbContext = services.GetRequiredService<AppDbContext>();
        var keep = exceptUserIds.ToHashSet();

        var others = await dbContext.Users
            .Where(u => u.IsActive
                && !keep.Contains(u.Id)
                && dbContext.UserRoles.Any(ur => ur.UserId == u.Id && dbContext.Roles.Any(r => r.Id == ur.RoleId && r.GrantsAllPermissions)))
            .ToListAsync(TestContext.Current.CancellationToken);

        foreach (var user in others)
        {
            user.IsActive = false;
        }

        if (others.Count > 0)
        {
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// See the class remarks: restores the shared "Super Admin" role's membership to what it was before this
    /// test, so <c>AdminAuthEndpointTests.Admin_seeding_creates_exactly_one_super_admin_who_can_then_sign_in</c>
    /// still finds it empty whenever it happens to run.
    /// </summary>
    private static async Task RemoveFromGrantsAllRoleAsync(IServiceProvider services, params int[] userIds)
    {
        var userManager = services.GetRequiredService<UserManager<AppUser>>();

        foreach (var userId in userIds)
        {
            var user = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));
            if (user is null)
            {
                continue;
            }

            var roles = await userManager.GetRolesAsync(user);
            if (roles.Count > 0)
            {
                await userManager.RemoveFromRolesAsync(user, roles);
            }
        }
    }

    private static async Task<int> CreateActiveGrantsAllUserAsync(UserManager<AppUser> userManager, string roleName, string label)
    {
        var email = $"{label}-{Guid.NewGuid():N}@example.com";
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            UserType = UserType.Staff,
            FirstName = "Test",
            LastName = "Admin",
            IsActive = true,
        };

        var createResult = await userManager.CreateAsync(user, "Correct-Password-1!");
        Assert.True(createResult.Succeeded, string.Join("; ", createResult.Errors.Select(e => e.Description)));

        var addResult = await userManager.AddToRoleAsync(user, roleName);
        Assert.True(addResult.Succeeded, string.Join("; ", addResult.Errors.Select(e => e.Description)));

        return user.Id;
    }
}
