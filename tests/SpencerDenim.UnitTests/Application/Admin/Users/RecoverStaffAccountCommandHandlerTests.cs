using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Admin.Users;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.UnitTests.Application.Admin.Users;

/// <summary>
/// #254 staff account recovery, at the handler: who may, on whom, in what order, and what the audit row holds. The
/// Identity work (the password, the lockout, the authenticator, the stamp) is proven against SQL Server in
/// <c>StaffAccountRecoveryEndpointTests</c>.
/// </summary>
public sealed class RecoverStaffAccountCommandHandlerTests
{
    private const int Actor = 1;
    private const int Member = 2;

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
    }

    private static readonly StaffUserSnapshot OrdinaryMember =
        new(Member, "member@example.com", "Member", null, true, "stamp-0", []);

    private static (RecoverStaffAccountCommandHandler Handler, FakeStaffUserRoleManagementStore Store, RecordingAuditLogWriter Audit) Create(
        bool actorGrantsAll = true,
        IProtectedStaffAccounts? accounts = null,
        string actorEmail = "owner@example.com",
        StaffUserSnapshot? target = null,
        bool actorActive = true)
    {
        var store = new FakeStaffUserRoleManagementStore();
        var audit = new RecordingAuditLogWriter();
        store.AuditWriter = audit;
        store.Users[Member] = target ?? OrdinaryMember;

        var handler = new RecoverStaffAccountCommandHandler(
            store,
            new StubEffectivePermissionsProvider(new EffectivePermissions(actorGrantsAll, actorGrantsAll ? [] : [Permissions.Users.Manage, Permissions.Users.View])),
            new StubAdminUserLookup(new AdminUserSummary(Actor, actorEmail, "Actor", null, actorActive, false, "actor-stamp")),
            accounts ?? new StubProtectedStaffAccounts(),
            new FixedClock());

        return (handler, store, audit);
    }

    private static RecoverStaffAccountCommand Command(StaffAccountRecovery operation, int target = Member, string? password = "Temporary-Pass-1") =>
        new(Actor, target, operation, operation == StaffAccountRecovery.ResetPassword ? password : null, "203.0.113.4", "Browser");

    [Theory]
    [InlineData(StaffAccountRecovery.ResetPassword)]
    [InlineData(StaffAccountRecovery.Unlock)]
    [InlineData(StaffAccountRecovery.ResetTwoFactor)]
    public async Task A_Super_Admin_recovers_a_member_and_the_audit_row_names_who_whom_and_what(StaffAccountRecovery operation)
    {
        var (handler, store, audit) = Create();

        var result = await handler.HandleAsync(Command(operation), TestContext.Current.CancellationToken);

        var success = Assert.IsType<RecoverStaffAccountResult.Success>(result);
        Assert.True(success.Changed);
        Assert.Equal(operation, store.RecoverCall!.Value.Operation);

        var row = Assert.Single(audit.Entries);
        Assert.Equal(RecoverStaffAccountCommandHandler.AuditActionFor(operation), row.Action);
        Assert.Equal((Actor, "owner@example.com", "User", "2"), (row.UserId, row.UserEmail, row.EntityType, row.EntityId));
        Assert.Equal("203.0.113.4", row.IpAddress);
    }

    [Fact]
    public void The_audit_actions_are_the_ones_doc_11_names_plus_StaffUnlocked()
    {
        Assert.Equal(AuditActions.AdminPasswordReset, RecoverStaffAccountCommandHandler.AuditActionFor(StaffAccountRecovery.ResetPassword));
        Assert.Equal(AuditActions.StaffUnlocked, RecoverStaffAccountCommandHandler.AuditActionFor(StaffAccountRecovery.Unlock));
        Assert.Equal(AuditActions.TwoFactorReset, RecoverStaffAccountCommandHandler.AuditActionFor(StaffAccountRecovery.ResetTwoFactor));
    }

    /// <summary>The temporary password never reaches the audit row, in any column (doc 08 DB24).</summary>
    [Fact]
    public async Task The_temporary_password_is_never_in_the_audit_row()
    {
        var (handler, _, audit) = Create();

        await handler.HandleAsync(Command(StaffAccountRecovery.ResetPassword, password: "Secret-Temp-99"), TestContext.Current.CancellationToken);

        var row = Assert.Single(audit.Entries);
        foreach (var column in typeof(AuditLog).GetProperties().Where(property => property.PropertyType == typeof(string)))
        {
            Assert.DoesNotContain("Secret-Temp-99", (string?)column.GetValue(row) ?? string.Empty, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Super Admin only, and decided <b>before the target is read</b>: the same answer for a real member and for an id
    /// that does not exist, so a Users.Manage holder learns nothing (#101's lesson).
    /// </summary>
    [Theory]
    [InlineData(Member)]
    [InlineData(404)]
    public async Task A_member_who_is_not_a_Super_Admin_is_refused_before_anything_is_read(int target)
    {
        var (handler, store, audit) = Create(actorGrantsAll: false);

        var result = await handler.HandleAsync(Command(StaffAccountRecovery.ResetPassword, target), TestContext.Current.CancellationToken);

        var forbidden = Assert.IsType<RecoverStaffAccountResult.Forbidden>(result);
        Assert.Equal(RecoverStaffAccountCommandHandler.SuperAdminOnly, forbidden.Message);
        Assert.Null(store.RecoverCall);
        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// #254 tester: a Super Admin deactivated in the last minute still has a valid cookie until the 60-second
    /// revalidation; these routes do not wait for it, and say the same as to any non-Super Admin.
    /// </summary>
    [Fact]
    public async Task A_deactivated_Super_Admin_is_refused_at_once()
    {
        var (handler, store, audit) = Create(actorActive: false);

        var forbidden = Assert.IsType<RecoverStaffAccountResult.Forbidden>(
            await handler.HandleAsync(Command(StaffAccountRecovery.Unlock), TestContext.Current.CancellationToken));

        Assert.Equal(RecoverStaffAccountCommandHandler.SuperAdminOnly, forbidden.Message);
        Assert.Null(store.RecoverCall);
        Assert.Empty(audit.Entries);
    }

    [Theory]
    [InlineData(StaffAccountRecovery.ResetPassword)]
    [InlineData(StaffAccountRecovery.Unlock)]
    [InlineData(StaffAccountRecovery.ResetTwoFactor)]
    public async Task Never_on_oneself(StaffAccountRecovery operation)
    {
        var (handler, store, _) = Create();

        var result = await handler.HandleAsync(Command(operation, target: Actor), TestContext.Current.CancellationToken);

        Assert.IsType<RecoverStaffAccountResult.SelfNotAllowed>(result);
        Assert.Null(store.RecoverCall);
    }

    [Fact]
    public async Task A_protected_account_is_never_recovered_from_the_admin()
    {
        var (handler, store, _) = Create(accounts: new StubProtectedStaffAccounts("member@example.com", "owner@example.com"));

        var result = await handler.HandleAsync(Command(StaffAccountRecovery.ResetTwoFactor), TestContext.Current.CancellationToken);

        Assert.IsType<RecoverStaffAccountResult.ProtectedAccount>(result);
        Assert.Null(store.RecoverCall);
    }

    [Fact]
    public async Task A_guarded_account_is_recovered_only_by_a_protected_account()
    {
        var guarded = new StubProtectedStaffAccounts("owner@example.com").Guarding("member@example.com");

        var (refusedHandler, refusedStore, _) = Create(accounts: guarded, actorEmail: "another-admin@example.com");
        Assert.IsType<RecoverStaffAccountResult.GuardedAccount>(
            await refusedHandler.HandleAsync(Command(StaffAccountRecovery.ResetPassword), TestContext.Current.CancellationToken));
        Assert.Null(refusedStore.RecoverCall);

        var (allowedHandler, allowedStore, _) = Create(accounts: guarded, actorEmail: "owner@example.com");
        Assert.IsType<RecoverStaffAccountResult.Success>(
            await allowedHandler.HandleAsync(Command(StaffAccountRecovery.ResetPassword), TestContext.Current.CancellationToken));
        Assert.NotNull(allowedStore.RecoverCall);
    }

    [Fact]
    public async Task An_unknown_member_is_not_found_for_a_Super_Admin()
    {
        var (handler, store, _) = Create();

        Assert.IsType<RecoverStaffAccountResult.TargetNotFound>(
            await handler.HandleAsync(Command(StaffAccountRecovery.Unlock, target: 99), TestContext.Current.CancellationToken));
        Assert.Null(store.RecoverCall);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_reset_needs_a_temporary_password(string? password)
    {
        var (handler, store, _) = Create();

        var rejected = Assert.IsType<RecoverStaffAccountResult.PasswordRejected>(
            await handler.HandleAsync(Command(StaffAccountRecovery.ResetPassword, password: password), TestContext.Current.CancellationToken));

        Assert.Contains("Type a temporary password.", rejected.Errors);
        Assert.Null(store.RecoverCall);
    }

    [Fact]
    public async Task Nothing_to_do_is_a_success_that_changed_nothing_and_writes_no_audit_row()
    {
        var (handler, store, audit) = Create();
        store.NextRecoverResult = new RecoverAccountStoreResult.NothingToDo();

        var success = Assert.IsType<RecoverStaffAccountResult.Success>(
            await handler.HandleAsync(Command(StaffAccountRecovery.Unlock), TestContext.Current.CancellationToken));

        Assert.False(success.Changed);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_password_the_policy_refuses_comes_back_as_its_reasons()
    {
        var (handler, store, audit) = Create();
        store.NextRecoverResult = new RecoverAccountStoreResult.PasswordRejected(["Passwords must be at least 10 characters."]);

        var rejected = Assert.IsType<RecoverStaffAccountResult.PasswordRejected>(
            await handler.HandleAsync(Command(StaffAccountRecovery.ResetPassword, password: "short"), TestContext.Current.CancellationToken));

        Assert.Equal(["Passwords must be at least 10 characters."], rejected.Errors);
        Assert.Empty(audit.Entries);
    }
}
