using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Admin.Users;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.UnitTests.Application.Admin.Users;

/// <summary>Deactivate/reactivate (ADM-02, W1-08, issue #8): doc 08 §3.1 DB4 rules 2 and 4, plus the deliberate self-lockout extension (see <see cref="SetStaffUserActiveResult.SelfEditNotAllowed"/>).</summary>
public sealed class SetStaffUserActiveCommandHandlerTests
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);
    }

    private static SetStaffUserActiveCommandHandler CreateHandler(FakeStaffUserRoleManagementStore store, EffectivePermissions actorPermissions, RecordingAuditLogWriter audit) =>
        new(store, new StubEffectivePermissionsProvider(actorPermissions), new StubAdminUserLookup(new AdminUserSummary(1, "actor@example.com", "Actor", null, true, false, "actor-stamp")), audit, new FixedClock());

    private static RoleGrantSnapshot Role(int id, bool grantsAll, params string[] permissions) => new(id, $"role-{id}", grantsAll, permissions.ToHashSet());

    private static StaffUserSnapshot Target(int id, bool isActive, params RoleGrantSnapshot[] roles) => new(id, "target@example.com", "Target", "User", isActive, "stamp-0", roles);

    [Fact]
    public async Task Cannot_deactivate_own_account()
    {
        var store = new FakeStaffUserRoleManagementStore();
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(true, []), audit);

        var result = await handler.HandleAsync(new SetStaffUserActiveCommand(1, 1, false, null, null), TestContext.Current.CancellationToken);

        Assert.IsType<SetStaffUserActiveResult.SelfEditNotAllowed>(result);
        Assert.Null(store.SetActiveCall);
    }

    [Fact]
    public async Task Non_grants_all_actor_cannot_deactivate_a_GrantsAll_holder()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Users[2] = Target(2, true, Role(99, grantsAll: true));
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(false, [Permissions.Users.Manage]), audit);

        var result = await handler.HandleAsync(new SetStaffUserActiveCommand(1, 2, false, null, null), TestContext.Current.CancellationToken);

        var forbidden = Assert.IsType<SetStaffUserActiveResult.Forbidden>(result);
        Assert.Equal(RbacDenialReason.TargetIsOrWouldBecomeGrantsAll, forbidden.Reason);
        Assert.Null(store.SetActiveCall);
    }

    [Fact]
    public async Task Non_grants_all_actor_cannot_deactivate_a_user_whose_permissions_exceed_their_own()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Users[2] = Target(2, true, Role(30, grantsAll: false, Permissions.Content.View, Permissions.Content.Edit));
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(false, [Permissions.Users.Manage, Permissions.Orders.View]), audit);

        var result = await handler.HandleAsync(new SetStaffUserActiveCommand(1, 2, false, null, null), TestContext.Current.CancellationToken);

        var forbidden = Assert.IsType<SetStaffUserActiveResult.Forbidden>(result);
        Assert.Equal(RbacDenialReason.NotSubsetOfActorPermissions, forbidden.Reason);
    }

    [Fact]
    public async Task UsersManage_actor_can_deactivate_a_user_whose_permissions_are_a_subset_of_their_own()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Users[2] = Target(2, true, Role(30, grantsAll: false, Permissions.Orders.View));
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(false, [Permissions.Users.Manage, Permissions.Orders.View]), audit);

        var result = await handler.HandleAsync(new SetStaffUserActiveCommand(1, 2, false, "203.0.113.5", "agent"), TestContext.Current.CancellationToken);

        var success = Assert.IsType<SetStaffUserActiveResult.Success>(result);
        Assert.False(success.IsActive);
        Assert.Equal((2, false), store.SetActiveCall);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(SpencerDenim.Domain.Auditing.AuditActions.StaffDeactivated, entry.Action);
    }

    [Fact]
    public async Task Reactivating_uses_the_StaffReactivated_audit_action()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Users[2] = Target(2, false, Role(30, grantsAll: false, Permissions.Orders.View));
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(true, []), audit);

        var result = await handler.HandleAsync(new SetStaffUserActiveCommand(1, 2, true, null, null), TestContext.Current.CancellationToken);

        Assert.IsType<SetStaffUserActiveResult.Success>(result);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(SpencerDenim.Domain.Auditing.AuditActions.StaffReactivated, entry.Action);
    }

    [Fact]
    public async Task Already_in_the_requested_state_is_a_no_op_and_writes_no_audit_row()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Users[2] = Target(2, false, Role(30, grantsAll: false, Permissions.Orders.View));
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(true, []), audit);

        var result = await handler.HandleAsync(new SetStaffUserActiveCommand(1, 2, false, null, null), TestContext.Current.CancellationToken);

        Assert.IsType<SetStaffUserActiveResult.Success>(result);
        Assert.Null(store.SetActiveCall);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Last_super_admin_guard_from_the_store_is_surfaced_untouched()
    {
        var store = new FakeStaffUserRoleManagementStore { NextSetActiveTriggersGuard = true };
        store.Users[2] = Target(2, true, Role(99, grantsAll: true));
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(true, []), audit);

        var result = await handler.HandleAsync(new SetStaffUserActiveCommand(1, 2, false, null, null), TestContext.Current.CancellationToken);

        Assert.IsType<SetStaffUserActiveResult.LastSuperAdminGuardTriggered>(result);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Target_not_found_is_reported()
    {
        var store = new FakeStaffUserRoleManagementStore();
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(true, []), audit);

        var result = await handler.HandleAsync(new SetStaffUserActiveCommand(1, 404, false, null, null), TestContext.Current.CancellationToken);

        Assert.IsType<SetStaffUserActiveResult.TargetNotFound>(result);
    }
}
