using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Admin.Users;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.UnitTests.Application.Admin.Users;

/// <summary>Editing a staff user's profile (ADM-02, W1-08, issue #8): doc 08 §3.1 DB4's email-specific GrantsAll gate.</summary>
public sealed class UpdateStaffUserProfileCommandHandlerTests
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);
    }

    private static UpdateStaffUserProfileCommandHandler CreateHandler(FakeStaffUserRoleManagementStore store, EffectivePermissions actorPermissions, RecordingAuditLogWriter audit)
    {
        store.AuditWriter = audit;
        return new(store, new StubEffectivePermissionsProvider(actorPermissions), new StubAdminUserLookup(new AdminUserSummary(1, "actor@example.com", "Actor", null, true, false, "actor-stamp")), new FixedClock());
    }

    private static RoleGrantSnapshot Role(int id, bool grantsAll, params string[] permissions) => new(id, $"role-{id}", grantsAll, permissions.ToHashSet());

    private static StaffUserSnapshot Target(int id, params RoleGrantSnapshot[] roles) => new(id, "old@example.com", "Old", "Name", true, "stamp-0", roles);

    [Fact]
    public async Task UsersManage_actor_without_GrantsAll_cannot_change_a_staff_users_email()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Users[2] = Target(2, Role(30, grantsAll: false, Permissions.Orders.View));
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(false, [Permissions.Users.Manage, Permissions.Orders.View]), audit);

        var result = await handler.HandleAsync(new UpdateStaffUserProfileCommand(1, 2, "New", "Name", "new@example.com", null, null), TestContext.Current.CancellationToken);

        var forbidden = Assert.IsType<UpdateStaffUserProfileResult.Forbidden>(result);
        Assert.Equal(RbacDenialReason.EmailChangeRequiresGrantsAll, forbidden.Reason);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task GrantsAll_actor_can_change_a_staff_users_email()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Users[2] = Target(2);
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(true, []), audit);

        var result = await handler.HandleAsync(new UpdateStaffUserProfileCommand(1, 2, "New", "Name", "new@example.com", null, null), TestContext.Current.CancellationToken);

        var success = Assert.IsType<UpdateStaffUserProfileResult.Success>(result);
        Assert.Equal("new@example.com", success.Email);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(SpencerDenim.Domain.Auditing.AuditActions.EmailChanged, entry.Action);
    }

    [Fact]
    public async Task Non_email_edits_by_a_UsersManage_actor_without_GrantsAll_are_allowed_within_the_subset_rule()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Users[2] = Target(2, Role(30, grantsAll: false, Permissions.Orders.View));
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(false, [Permissions.Users.Manage, Permissions.Orders.View]), audit);

        var result = await handler.HandleAsync(new UpdateStaffUserProfileCommand(1, 2, "New", "Name", null, null, null), TestContext.Current.CancellationToken);

        var success = Assert.IsType<UpdateStaffUserProfileResult.Success>(result);
        Assert.Equal("old@example.com", success.Email);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(SpencerDenim.Domain.Auditing.AuditActions.Update, entry.Action);
    }

    [Fact]
    public async Task Non_grants_all_actor_cannot_edit_a_GrantsAll_targets_profile_even_without_touching_email()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Users[2] = Target(2, Role(99, grantsAll: true));
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(false, [Permissions.Users.Manage]), audit);

        var result = await handler.HandleAsync(new UpdateStaffUserProfileCommand(1, 2, "New", "Name", null, null, null), TestContext.Current.CancellationToken);

        // Tester-round-1 fix (defect 1): same generic reason as any other over-permissioned target.
        var forbidden = Assert.IsType<UpdateStaffUserProfileResult.Forbidden>(result);
        Assert.Equal(RbacDenialReason.NotSubsetOfActorPermissions, forbidden.Reason);
    }

    /// <summary>Tester-round-1 fix (defect 1): a non-GrantsAll actor must not be able to tell "doesn't exist"
    /// apart from "exists but is protected" by the response shape.</summary>
    [Fact]
    public async Task Non_grants_all_actor_gets_the_same_generic_denial_for_a_target_that_does_not_exist()
    {
        var store = new FakeStaffUserRoleManagementStore();
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(false, [Permissions.Users.Manage]), audit);

        var result = await handler.HandleAsync(new UpdateStaffUserProfileCommand(1, 404, "New", "Name", null, null, null), TestContext.Current.CancellationToken);

        var forbidden = Assert.IsType<UpdateStaffUserProfileResult.Forbidden>(result);
        Assert.Equal(RbacDenialReason.NotSubsetOfActorPermissions, forbidden.Reason);
    }

    /// <summary>Tester-round-1 fix (defect 2): a stale-authorization outcome from the store surfaces as the
    /// same generic denial as any other subset-rule refusal.</summary>
    [Fact]
    public async Task Store_reported_authorization_stale_surfaces_as_the_generic_subset_denial()
    {
        var store = new FakeStaffUserRoleManagementStore { NextUpdateProfileTriggersAuthorizationStale = true };
        store.Users[2] = Target(2, Role(30, grantsAll: false, Permissions.Orders.View));
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(false, [Permissions.Users.Manage, Permissions.Orders.View]), audit);

        var result = await handler.HandleAsync(new UpdateStaffUserProfileCommand(1, 2, "New", "Name", null, null, null), TestContext.Current.CancellationToken);

        var forbidden = Assert.IsType<UpdateStaffUserProfileResult.Forbidden>(result);
        Assert.Equal(RbacDenialReason.NotSubsetOfActorPermissions, forbidden.Reason);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Blank_first_name_is_rejected()
    {
        var store = new FakeStaffUserRoleManagementStore();
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(true, []), audit);

        var result = await handler.HandleAsync(new UpdateStaffUserProfileCommand(1, 2, "  ", null, null, null, null), TestContext.Current.CancellationToken);

        Assert.IsType<UpdateStaffUserProfileResult.ValidationFailed>(result);
    }
}
