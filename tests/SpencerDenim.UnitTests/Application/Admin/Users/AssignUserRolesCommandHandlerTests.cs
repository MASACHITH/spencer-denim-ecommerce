using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Admin.Users;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.UnitTests.Application.Admin.Users;

/// <summary>
/// <see cref="AssignUserRolesCommandHandler"/> is the real caller <c>RoleAssignmentPolicy</c> was written for
/// (W1-08, issue #8). Covers doc 08 §3.1 DB4 rules 1 (GrantsAll gating), 2 (the subset rule), 3 (self-edit) and
/// 5 (SecurityStamp rotation) at the handler level; rule 4's transactional guard is proven for real against SQL
/// Server in <c>StaffUserRoleManagementStoreTests</c> — here the store double simply reports the outcome the
/// handler must translate correctly.
/// </summary>
public sealed class AssignUserRolesCommandHandlerTests
{
    private static readonly IClockStub Clock = new();

    private sealed class IClockStub : SpencerDenim.Application.Abstractions.Time.IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);
    }

    private static AssignUserRolesCommandHandler CreateHandler(
        FakeStaffUserRoleManagementStore store,
        EffectivePermissions actorPermissions,
        RecordingAuditLogWriter audit,
        string? actorEmail = "actor@example.com") =>
        new(store, new StubEffectivePermissionsProvider(actorPermissions), new StubAdminUserLookup(new AdminUserSummary(1, actorEmail!, "Actor", null, true, false, "actor-stamp")), audit, Clock);

    private static RoleGrantSnapshot Role(int id, string name, bool grantsAll, params string[] permissions) =>
        new(id, name, grantsAll, permissions.ToHashSet());

    private static StaffUserSnapshot Target(int id, params RoleGrantSnapshot[] roles) =>
        new(id, "target@example.com", "Target", "User", true, "target-stamp-0", roles);

    [Fact]
    public async Task Self_role_edit_is_blocked_before_any_other_check()
    {
        var store = new FakeStaffUserRoleManagementStore();
        var audit = new RecordingAuditLogWriter();
        // actorId == targetId == 1: even a GrantsAll actor cannot edit their own roles (rule 3 has no exception).
        var handler = CreateHandler(store, new EffectivePermissions(true, []), audit);

        var result = await handler.HandleAsync(new AssignUserRolesCommand(1, 1, [1], null, null), TestContext.Current.CancellationToken);

        Assert.IsType<AssignUserRolesResult.SelfEditNotAllowed>(result);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Non_grants_all_actor_can_never_assign_the_grants_all_role()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Users[2] = Target(2);
        store.Roles[10] = Role(10, "Super Admin-like", grantsAll: true);
        var audit = new RecordingAuditLogWriter();
        var actorPermissions = new EffectivePermissions(false, [Permissions.Users.Manage]);
        var handler = CreateHandler(store, actorPermissions, audit);

        var result = await handler.HandleAsync(new AssignUserRolesCommand(1, 2, [10], null, null), TestContext.Current.CancellationToken);

        // Tester-round-1 fix (defect 1): a non-GrantsAll actor must get the exact same reason as any other
        // over-permissioned target, not a distinct "this one is Super Admin" reason (identity oracle).
        var forbidden = Assert.IsType<AssignUserRolesResult.Forbidden>(result);
        Assert.Equal(RbacDenialReason.NotSubsetOfActorPermissions, forbidden.Reason);
        Assert.Null(store.ReplaceRolesCall);
    }

    [Fact]
    public async Task UsersManage_actor_cannot_assign_a_role_exceeding_their_own_permissions()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Users[2] = Target(2);
        store.Roles[20] = Role(20, "Content Editor-like", grantsAll: false, Permissions.Content.View, Permissions.Content.Edit);
        var audit = new RecordingAuditLogWriter();
        var actorPermissions = new EffectivePermissions(false, [Permissions.Users.Manage, Permissions.Orders.View]);
        var handler = CreateHandler(store, actorPermissions, audit);

        var result = await handler.HandleAsync(new AssignUserRolesCommand(1, 2, [20], null, null), TestContext.Current.CancellationToken);

        var forbidden = Assert.IsType<AssignUserRolesResult.Forbidden>(result);
        Assert.Equal(RbacDenialReason.NotSubsetOfActorPermissions, forbidden.Reason);
        Assert.Null(store.ReplaceRolesCall);
    }

    [Fact]
    public async Task UsersManage_actor_can_assign_a_role_that_is_a_subset_of_their_own_permissions()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Users[2] = Target(2);
        store.Roles[30] = Role(30, "Order Staff-like", grantsAll: false, Permissions.Orders.View);
        var audit = new RecordingAuditLogWriter();
        var actorPermissions = new EffectivePermissions(false, [Permissions.Users.Manage, Permissions.Orders.View, Permissions.Orders.Edit]);
        var handler = CreateHandler(store, actorPermissions, audit);

        var result = await handler.HandleAsync(new AssignUserRolesCommand(1, 2, [30], "203.0.113.4", "agent"), TestContext.Current.CancellationToken);

        var success = Assert.IsType<AssignUserRolesResult.Success>(result);
        Assert.Equal(2, success.UserId);
        Assert.Equal((2, (IReadOnlyList<int>)[30]), store.ReplaceRolesCall);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(SpencerDenim.Domain.Auditing.AuditActions.PermissionChange, entry.Action);
        Assert.Contains("Orders.View", entry.AfterJson);
        Assert.DoesNotContain("SecurityStamp", entry.BeforeJson + entry.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_grants_all_actor_can_never_manage_a_target_who_already_holds_GrantsAll()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Users[2] = Target(2, Role(99, "Super Admin", grantsAll: true));
        store.Roles[30] = Role(30, "Order Staff-like", grantsAll: false, Permissions.Orders.View);
        var audit = new RecordingAuditLogWriter();
        // Actor has every declared permission except Settings.Security and is not GrantsAll — still refused.
        var wideButNotGrantsAll = PermissionCatalog.All.Where(p => p != Permissions.Settings.Security).ToArray();
        var handler = CreateHandler(store, new EffectivePermissions(false, wideButNotGrantsAll), audit);

        var result = await handler.HandleAsync(new AssignUserRolesCommand(1, 2, [30], null, null), TestContext.Current.CancellationToken);

        var forbidden = Assert.IsType<AssignUserRolesResult.Forbidden>(result);
        Assert.Equal(RbacDenialReason.NotSubsetOfActorPermissions, forbidden.Reason);
    }

    /// <summary>Tester-round-1 fix (defect 1): the oracle the tester flagged specifically combined the two —
    /// a non-GrantsAll actor must not be able to tell "target doesn't exist" apart from "target is protected"
    /// by getting a 404-shaped result for one and a 403-shaped result for the other.</summary>
    [Fact]
    public async Task Non_grants_all_actor_gets_the_same_generic_denial_for_a_target_that_does_not_exist()
    {
        var store = new FakeStaffUserRoleManagementStore();
        var audit = new RecordingAuditLogWriter();
        var actorPermissions = new EffectivePermissions(false, [Permissions.Users.Manage, Permissions.Orders.View]);
        var handler = CreateHandler(store, actorPermissions, audit);

        var result = await handler.HandleAsync(new AssignUserRolesCommand(1, 404, [], null, null), TestContext.Current.CancellationToken);

        var forbidden = Assert.IsType<AssignUserRolesResult.Forbidden>(result);
        Assert.Equal(RbacDenialReason.NotSubsetOfActorPermissions, forbidden.Reason);
        Assert.Equal("That action grants or affects permissions you do not hold yourself.", forbidden.Message);
    }

    /// <summary>Tester-round-1 fix (defect 2): the store detecting a stale authorization at write time must
    /// surface through the handler as the same generic denial as any other subset-rule refusal, not a new,
    /// separately-observable reason.</summary>
    [Fact]
    public async Task Store_reported_authorization_stale_surfaces_as_the_generic_subset_denial()
    {
        var store = new FakeStaffUserRoleManagementStore { NextReplaceRolesTriggersAuthorizationStale = true };
        store.Users[2] = Target(2);
        store.Roles[30] = Role(30, "Order Staff-like", grantsAll: false, Permissions.Orders.View);
        var audit = new RecordingAuditLogWriter();
        var actorPermissions = new EffectivePermissions(false, [Permissions.Users.Manage, Permissions.Orders.View]);
        var handler = CreateHandler(store, actorPermissions, audit);

        var result = await handler.HandleAsync(new AssignUserRolesCommand(1, 2, [30], null, null), TestContext.Current.CancellationToken);

        var forbidden = Assert.IsType<AssignUserRolesResult.Forbidden>(result);
        Assert.Equal(RbacDenialReason.NotSubsetOfActorPermissions, forbidden.Reason);
        Assert.Equal("That action grants or affects permissions you do not hold yourself.", forbidden.Message);
        Assert.Empty(audit.Entries);
    }

    /// <summary>A GrantsAll actor is allowed to know a target genuinely doesn't exist (unaffected by defect 1).</summary>
    [Fact]
    public async Task GrantsAll_actor_still_gets_a_real_not_found_for_a_missing_target()
    {
        var store = new FakeStaffUserRoleManagementStore();
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(true, []), audit);

        var result = await handler.HandleAsync(new AssignUserRolesCommand(1, 404, [], null, null), TestContext.Current.CancellationToken);

        Assert.IsType<AssignUserRolesResult.TargetNotFound>(result);
    }

    [Fact]
    public async Task GrantsAll_actor_can_assign_the_grants_all_role()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Users[2] = Target(2);
        store.Roles[10] = Role(10, "Super Admin", grantsAll: true);
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(true, []), audit);

        var result = await handler.HandleAsync(new AssignUserRolesCommand(1, 2, [10], null, null), TestContext.Current.CancellationToken);

        Assert.IsType<AssignUserRolesResult.Success>(result);
    }

    [Fact]
    public async Task Unknown_role_ids_are_reported_before_any_write()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Users[2] = Target(2);
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(true, []), audit);

        var result = await handler.HandleAsync(new AssignUserRolesCommand(1, 2, [999], null, null), TestContext.Current.CancellationToken);

        var roleNotFound = Assert.IsType<AssignUserRolesResult.RoleNotFound>(result);
        Assert.Equal([999], roleNotFound.UnknownRoleIds);
        Assert.Null(store.ReplaceRolesCall);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Target_not_found_is_reported()
    {
        var store = new FakeStaffUserRoleManagementStore();
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(true, []), audit);

        var result = await handler.HandleAsync(new AssignUserRolesCommand(1, 404, [], null, null), TestContext.Current.CancellationToken);

        Assert.IsType<AssignUserRolesResult.TargetNotFound>(result);
    }

    /// <summary>
    /// Doc 08 §3.1 DB4 rule 4: the store is the authoritative source for this outcome (its own real-SQL tests
    /// prove the transaction); this test only proves the handler passes the outcome straight through instead of
    /// second-guessing it after the fact.
    /// </summary>
    [Fact]
    public async Task Last_super_admin_guard_from_the_store_is_surfaced_untouched()
    {
        var store = new FakeStaffUserRoleManagementStore { NextReplaceRolesTriggersGuard = true };
        store.Users[2] = Target(2, Role(99, "Super Admin", grantsAll: true));
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(true, []), audit);

        var result = await handler.HandleAsync(new AssignUserRolesCommand(1, 2, [], null, null), TestContext.Current.CancellationToken);

        Assert.IsType<AssignUserRolesResult.LastSuperAdminGuardTriggered>(result);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Successful_assignment_rotates_the_targets_security_stamp_via_the_store()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Users[2] = Target(2);
        store.Roles[30] = Role(30, "Order Staff-like", grantsAll: false, Permissions.Orders.View);
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(true, []), audit);

        await handler.HandleAsync(new AssignUserRolesCommand(1, 2, [30], null, null), TestContext.Current.CancellationToken);

        Assert.NotEqual("target-stamp-0", store.Users[2].SecurityStamp);
    }
}
