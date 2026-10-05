using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Admin.Users;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.UnitTests.Application.Admin.Users;

/// <summary>
/// Creating a staff user (ADM-02, W1-08, issue #8): doc 08 §3.1 DB4 "may create staff users only with roles
/// that pass the same subset check", modelled as <c>CanManageStaffUser</c> against an empty "before" state.
/// </summary>
public sealed class CreateStaffUserCommandHandlerTests
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);
    }

    private static CreateStaffUserCommandHandler CreateHandler(FakeStaffUserRoleManagementStore store, EffectivePermissions actorPermissions, RecordingAuditLogWriter audit)
    {
        store.AuditWriter = audit;
        return new(store, new StubEffectivePermissionsProvider(actorPermissions), new StubAdminUserLookup(new AdminUserSummary(1, "actor@example.com", "Actor", null, true, false, "actor-stamp")), new FixedClock());
    }

    private static RoleGrantSnapshot Role(int id, bool grantsAll, params string[] permissions) => new(id, $"role-{id}", grantsAll, permissions.ToHashSet());

    private static CreateStaffUserCommand Command(IReadOnlyList<int> roleIds) =>
        new(1, "new-staff@example.com", "New", "Staff", "Temp-Password-1!", roleIds, "203.0.113.1", "agent");

    [Fact]
    public async Task Missing_required_fields_are_all_reported_together()
    {
        var store = new FakeStaffUserRoleManagementStore();
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(true, []), audit);

        var result = await handler.HandleAsync(new CreateStaffUserCommand(1, "", "", null, "", [], null, null), TestContext.Current.CancellationToken);

        var validationFailed = Assert.IsType<CreateStaffUserResult.ValidationFailed>(result);
        Assert.Equal(4, validationFailed.Errors.Count); // email, firstName, initialPassword, roleIds
    }

    [Fact]
    public async Task UsersManage_actor_cannot_create_a_user_with_a_role_exceeding_their_own_permissions()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Roles[30] = Role(30, grantsAll: false, Permissions.Content.View, Permissions.Content.Edit);
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(false, [Permissions.Users.Manage, Permissions.Orders.View]), audit);

        var result = await handler.HandleAsync(Command([30]), TestContext.Current.CancellationToken);

        var forbidden = Assert.IsType<CreateStaffUserResult.Forbidden>(result);
        Assert.Equal(RbacDenialReason.NotSubsetOfActorPermissions, forbidden.Reason);
        Assert.Empty(store.Users);
    }

    [Fact]
    public async Task Non_grants_all_actor_cannot_create_a_user_with_the_GrantsAll_role()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Roles[10] = Role(10, grantsAll: true);
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(false, [Permissions.Users.Manage]), audit);

        var result = await handler.HandleAsync(Command([10]), TestContext.Current.CancellationToken);

        // Tester-round-1 fix (defect 1): same generic reason as any other over-permissioned request.
        var forbidden = Assert.IsType<CreateStaffUserResult.Forbidden>(result);
        Assert.Equal(RbacDenialReason.NotSubsetOfActorPermissions, forbidden.Reason);
    }

    [Fact]
    public async Task UsersManage_actor_can_create_a_user_with_a_role_that_is_a_subset_of_their_own_permissions()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Roles[30] = Role(30, grantsAll: false, Permissions.Orders.View);
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(false, [Permissions.Users.Manage, Permissions.Orders.View, Permissions.Orders.Edit]), audit);

        var result = await handler.HandleAsync(Command([30]), TestContext.Current.CancellationToken);

        var success = Assert.IsType<CreateStaffUserResult.Success>(result);
        Assert.Equal("new-staff@example.com", success.Email);
        Assert.Single(store.Users);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(SpencerDenim.Domain.Auditing.AuditActions.Create, entry.Action);
        Assert.DoesNotContain("Temp-Password-1!", entry.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Email_already_in_use_is_reported_after_the_permission_check()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Roles[30] = Role(30, grantsAll: false, Permissions.Orders.View);
        store.EmailsInUse.Add("new-staff@example.com");
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(true, []), audit);

        var result = await handler.HandleAsync(Command([30]), TestContext.Current.CancellationToken);

        Assert.IsType<CreateStaffUserResult.EmailAlreadyInUse>(result);
    }

    /// <summary>
    /// Review round 4, item 4. Every Identity failure used to become <c>PasswordRejected</c>, which the endpoint
    /// turns into a 400 keyed on <c>initialPassword</c> titled "The initial password does not meet the password
    /// rules." and the form renders under the password box — so a refused <i>address</i> told the admin to change
    /// the <i>password</i>, quoting the address back at them in the message. Each failure now goes to the input it
    /// is about.
    /// </summary>
    [Fact]
    public async Task A_password_only_Identity_failure_is_still_reported_against_the_password()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Roles[30] = Role(30, grantsAll: false, Permissions.Orders.View);
        store.NextCreateResult = new CreateStaffUserStoreResult.PasswordRejected(["Passwords must have at least one digit ('0'-'9')."]);
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(true, []), audit);

        var result = await handler.HandleAsync(Command([30]), TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<CreateStaffUserResult.PasswordRejected>(result);
        Assert.Equal(["Passwords must have at least one digit ('0'-'9')."], rejected.Errors);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task An_account_level_Identity_failure_is_reported_against_the_email_not_the_password()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Roles[30] = Role(30, grantsAll: false, Permissions.Orders.View);
        store.NextCreateResult = new CreateStaffUserStoreResult.IdentityErrors(
            ["User name 'josé perera@example.com' is invalid, can only contain letters or digits."]);
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(true, []), audit);

        var result = await handler.HandleAsync(Command([30]), TestContext.Current.CancellationToken);

        var validationFailed = Assert.IsType<CreateStaffUserResult.ValidationFailed>(result);
        var error = Assert.Single(validationFailed.Errors);
        Assert.Equal("email", error.Field);
        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// The duplicate-email race: <c>IsEmailInUseAsync</c> is not transactional, so two creates for one address
    /// can both pass it and the loser is refused by Identity's unique index instead. It gets the same 409 as the
    /// one that lost the earlier check — not a 400 about the password, which is where it used to land.
    /// </summary>
    [Fact]
    public async Task A_duplicate_email_detected_only_at_write_time_is_reported_as_a_conflict()
    {
        var store = new FakeStaffUserRoleManagementStore();
        store.Roles[30] = Role(30, grantsAll: false, Permissions.Orders.View);
        store.NextCreateResult = new CreateStaffUserStoreResult.EmailAlreadyInUse();
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(true, []), audit);

        var result = await handler.HandleAsync(Command([30]), TestContext.Current.CancellationToken);

        Assert.IsType<CreateStaffUserResult.EmailAlreadyInUse>(result);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Unknown_role_ids_are_reported()
    {
        var store = new FakeStaffUserRoleManagementStore();
        var audit = new RecordingAuditLogWriter();
        var handler = CreateHandler(store, new EffectivePermissions(true, []), audit);

        var result = await handler.HandleAsync(Command([999]), TestContext.Current.CancellationToken);

        var roleNotFound = Assert.IsType<CreateStaffUserResult.RoleNotFound>(result);
        Assert.Equal([999], roleNotFound.UnknownRoleIds);
    }
}
