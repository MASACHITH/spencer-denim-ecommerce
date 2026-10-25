using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Admin.Users;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Application.Admin.Users.Contracts;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.UnitTests.Application.Admin.Users;

/// <summary>
/// A <i>guarded</i> account is one handed to someone else (the client's own Super Admin): only a <i>protected</i>
/// account, the technical owner's, may deactivate it, reactivate it or change its email address. The people who
/// use it, other Super Admins included, cannot lock it out or hand it over. Addresses here are made up; the real
/// ones are server configuration.
/// </summary>
public sealed class GuardedStaffAccountTests
{
    private const string OwnerEmail = "owner@example.test";
    private const string ClientEmail = "Client@Example.test";
    private const int ActorId = 1;
    private const int ClientId = 2;

    private static readonly EffectivePermissions SuperAdmin = new(true, []);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    }

    private static StubProtectedStaffAccounts Accounts() => new StubProtectedStaffAccounts(OwnerEmail).Guarding(ClientEmail);

    private static StubAdminUserLookup Actor(string? email) =>
        new(email is null ? null : new AdminUserSummary(ActorId, email, "Actor", null, true, false, "actor-stamp"));

    private static StaffUserSnapshot ClientAccount(bool isActive, string email = "client@example.test") =>
        new(ClientId, email, "Client", "Owner", isActive, "stamp-0", [new RoleGrantSnapshot(99, "Super Admin", true, new HashSet<string>())]);

    private static (SetStaffUserActiveCommandHandler Handler, FakeStaffUserRoleManagementStore Store, RecordingAuditLogWriter Audit) SetActiveHandler(
        string? actorEmail, StaffUserSnapshot target, EffectivePermissions? actorPermissions = null)
    {
        var store = new FakeStaffUserRoleManagementStore();
        var audit = new RecordingAuditLogWriter { };
        store.AuditWriter = audit;
        store.Users[target.Id] = target;

        return (new SetStaffUserActiveCommandHandler(store, new StubEffectivePermissionsProvider(actorPermissions ?? SuperAdmin), Actor(actorEmail), Accounts(), new FixedClock()), store, audit);
    }

    // ---- activating and deactivating ------------------------------------------------------------

    [Fact]
    public async Task Another_Super_Admin_cannot_deactivate_a_guarded_account()
    {
        var (handler, store, audit) = SetActiveHandler("someone-else@example.test", ClientAccount(isActive: true));

        var result = await handler.HandleAsync(new SetStaffUserActiveCommand(ActorId, ClientId, false, null, null), TestContext.Current.CancellationToken);

        Assert.IsType<SetStaffUserActiveResult.GuardedAccount>(result);
        Assert.Null(store.SetActiveCall);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Another_Super_Admin_cannot_reactivate_a_guarded_account_either()
    {
        var (handler, store, _) = SetActiveHandler("someone-else@example.test", ClientAccount(isActive: false));

        var result = await handler.HandleAsync(new SetStaffUserActiveCommand(ActorId, ClientId, true, null, null), TestContext.Current.CancellationToken);

        Assert.IsType<SetStaffUserActiveResult.GuardedAccount>(result);
        Assert.Null(store.SetActiveCall);
    }

    [Fact]
    public async Task A_guarded_account_is_refused_whatever_state_it_is_already_in()
    {
        var (handler, store, _) = SetActiveHandler("someone-else@example.test", ClientAccount(isActive: false));

        var result = await handler.HandleAsync(new SetStaffUserActiveCommand(ActorId, ClientId, false, null, null), TestContext.Current.CancellationToken);

        Assert.IsType<SetStaffUserActiveResult.GuardedAccount>(result);
        Assert.Null(store.SetActiveCall);
    }

    [Fact]
    public async Task A_protected_account_can_deactivate_a_guarded_account()
    {
        var (handler, store, audit) = SetActiveHandler(OwnerEmail, ClientAccount(isActive: true));

        var result = await handler.HandleAsync(new SetStaffUserActiveCommand(ActorId, ClientId, false, null, null), TestContext.Current.CancellationToken);

        var success = Assert.IsType<SetStaffUserActiveResult.Success>(result);
        Assert.False(success.IsActive);
        Assert.Equal((ClientId, false), store.SetActiveCall);
        Assert.Single(audit.Entries);
    }

    [Fact]
    public async Task A_protected_account_can_reactivate_a_guarded_account()
    {
        var (handler, store, _) = SetActiveHandler(OwnerEmail, ClientAccount(isActive: false));

        var result = await handler.HandleAsync(new SetStaffUserActiveCommand(ActorId, ClientId, true, null, null), TestContext.Current.CancellationToken);

        Assert.True(Assert.IsType<SetStaffUserActiveResult.Success>(result).IsActive);
        Assert.Equal((ClientId, true), store.SetActiveCall);
    }

    [Fact]
    public async Task The_match_ignores_case_in_the_actors_and_the_targets_address()
    {
        var (handler, store, _) = SetActiveHandler("OWNER@EXAMPLE.TEST", ClientAccount(isActive: true, email: "CLIENT@example.test"));

        var result = await handler.HandleAsync(new SetStaffUserActiveCommand(ActorId, ClientId, false, null, null), TestContext.Current.CancellationToken);

        Assert.IsType<SetStaffUserActiveResult.Success>(result);
        Assert.Equal((ClientId, false), store.SetActiveCall);
    }

    [Fact]
    public async Task An_actor_who_cannot_be_found_is_not_a_protected_account()
    {
        var (handler, store, _) = SetActiveHandler(actorEmail: null, ClientAccount(isActive: true));

        var result = await handler.HandleAsync(new SetStaffUserActiveCommand(ActorId, ClientId, false, null, null), TestContext.Current.CancellationToken);

        Assert.IsType<SetStaffUserActiveResult.GuardedAccount>(result);
        Assert.Null(store.SetActiveCall);
    }

    [Fact]
    public async Task An_actor_who_may_not_manage_the_guarded_account_gets_the_ordinary_refusal_and_learns_nothing_about_it()
    {
        var (handler, _, _) = SetActiveHandler(
            "someone-else@example.test",
            ClientAccount(isActive: true),
            new EffectivePermissions(false, [Permissions.Users.Manage]));

        var result = await handler.HandleAsync(new SetStaffUserActiveCommand(ActorId, ClientId, false, null, null), TestContext.Current.CancellationToken);

        var forbidden = Assert.IsType<SetStaffUserActiveResult.Forbidden>(result);
        Assert.Equal(RbacDenialReason.NotSubsetOfActorPermissions, forbidden.Reason);
    }

    [Fact]
    public async Task Guarding_one_account_changes_nothing_for_the_others()
    {
        var other = new StaffUserSnapshot(3, "other@example.test", "Other", "Person", true, "stamp-0", [new RoleGrantSnapshot(30, "Role", false, new HashSet<string> { Permissions.Orders.View })]);
        var (handler, store, _) = SetActiveHandler("someone-else@example.test", other);

        var result = await handler.HandleAsync(new SetStaffUserActiveCommand(ActorId, 3, false, null, null), TestContext.Current.CancellationToken);

        Assert.IsType<SetStaffUserActiveResult.Success>(result);
        Assert.Equal((3, false), store.SetActiveCall);
    }

    // ---- the email address -----------------------------------------------------------------------

    private static (UpdateStaffUserProfileCommandHandler Handler, FakeStaffUserRoleManagementStore Store, RecordingAuditLogWriter Audit) ProfileHandler(string? actorEmail)
    {
        var store = new FakeStaffUserRoleManagementStore();
        var audit = new RecordingAuditLogWriter();
        store.AuditWriter = audit;
        store.Users[ClientId] = ClientAccount(isActive: true);

        return (new UpdateStaffUserProfileCommandHandler(store, new StubEffectivePermissionsProvider(SuperAdmin), Actor(actorEmail), Accounts(), new FixedClock()), store, audit);
    }

    [Fact]
    public async Task Another_Super_Admin_cannot_change_a_guarded_accounts_email()
    {
        var (handler, _, audit) = ProfileHandler("someone-else@example.test");

        var result = await handler.HandleAsync(new UpdateStaffUserProfileCommand(ActorId, ClientId, "Client", "Owner", "renamed@example.test", null, null), TestContext.Current.CancellationToken);

        Assert.IsType<UpdateStaffUserProfileResult.GuardedEmail>(result);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_protected_account_can_change_a_guarded_accounts_email()
    {
        var (handler, _, _) = ProfileHandler(OwnerEmail);

        var result = await handler.HandleAsync(new UpdateStaffUserProfileCommand(ActorId, ClientId, "Client", "Owner", "renamed@example.test", null, null), TestContext.Current.CancellationToken);

        Assert.Equal("renamed@example.test", Assert.IsType<UpdateStaffUserProfileResult.Success>(result).Email);
    }

    [Fact]
    public async Task Another_Super_Admin_can_still_change_a_guarded_accounts_name()
    {
        var (handler, _, _) = ProfileHandler("someone-else@example.test");

        var result = await handler.HandleAsync(new UpdateStaffUserProfileCommand(ActorId, ClientId, "Client", "Director", null, null, null), TestContext.Current.CancellationToken);

        Assert.IsType<UpdateStaffUserProfileResult.Success>(result);
    }

    // ---- what the screens are told ---------------------------------------------------------------

    private static StaffUserSummaryDto Row(int id, string email) =>
        new(id, email, "First", null, true, false, null, [], IsProtected: false);

    private sealed class Reader(IReadOnlyList<StaffUserSummaryDto> rows) : IStaffUserDirectoryReader
    {
        public Task<PagedResult<StaffUserSummaryDto>> ListAsync(GetStaffUsersQuery query, bool includeGrantsAllHolders, CancellationToken cancellationToken) =>
            Task.FromResult(new PagedResult<StaffUserSummaryDto>(rows, 1, 25, rows.Count));

        public Task<StaffUserDetailDto?> GetByIdAsync(int userId, bool includeGrantsAllHolders, CancellationToken cancellationToken)
        {
            var row = rows.FirstOrDefault(candidate => candidate.Id == userId);
            return Task.FromResult(row is null
                ? null
                : new StaffUserDetailDto(row.Id, row.Email, row.FirstName, row.LastName, row.IsActive, row.MustChangePassword, null, DateTime.UnixEpoch, true, [], [], false));
        }
    }

    private static readonly IReadOnlyList<StaffUserSummaryDto> Rows = [Row(2, "client@example.test"), Row(3, "other@example.test")];

    [Fact]
    public async Task The_list_locks_a_guarded_row_for_everybody_but_a_protected_account()
    {
        var handler = new GetStaffUsersQueryHandler(new Reader(Rows), new StubEffectivePermissionsProvider(SuperAdmin), Actor("someone-else@example.test"), Accounts());

        var page = await handler.HandleAsync(GetStaffUsersQuery.Create(new StaffUserListCriteria(), ActorId).Value, TestContext.Current.CancellationToken);

        Assert.True(page.Items.Single(row => row.Id == 2).LockedForViewer);
        Assert.False(page.Items.Single(row => row.Id == 3).LockedForViewer);
        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task The_list_locks_nothing_for_a_protected_account()
    {
        var handler = new GetStaffUsersQueryHandler(new Reader(Rows), new StubEffectivePermissionsProvider(SuperAdmin), Actor(OwnerEmail), Accounts());

        var page = await handler.HandleAsync(GetStaffUsersQuery.Create(new StaffUserListCriteria(), ActorId).Value, TestContext.Current.CancellationToken);

        Assert.All(page.Items, row => Assert.False(row.LockedForViewer));
    }

    [Fact]
    public async Task The_detail_is_locked_for_everybody_but_a_protected_account()
    {
        var ordinary = new GetStaffUserByIdQueryHandler(new Reader(Rows), new StubEffectivePermissionsProvider(SuperAdmin), Actor("someone-else@example.test"), Accounts());
        var owner = new GetStaffUserByIdQueryHandler(new Reader(Rows), new StubEffectivePermissionsProvider(SuperAdmin), Actor(OwnerEmail), Accounts());

        Assert.True((await ordinary.HandleAsync(new GetStaffUserByIdQuery(2, ActorId), TestContext.Current.CancellationToken))!.LockedForViewer);
        Assert.False((await ordinary.HandleAsync(new GetStaffUserByIdQuery(3, ActorId), TestContext.Current.CancellationToken))!.LockedForViewer);
        Assert.False((await owner.HandleAsync(new GetStaffUserByIdQuery(2, ActorId), TestContext.Current.CancellationToken))!.LockedForViewer);
    }

    [Fact]
    public async Task A_missing_user_is_still_null_not_a_locked_one()
    {
        var handler = new GetStaffUserByIdQueryHandler(new Reader(Rows), new StubEffectivePermissionsProvider(SuperAdmin), Actor("someone-else@example.test"), Accounts());

        Assert.Null(await handler.HandleAsync(new GetStaffUserByIdQuery(404, ActorId), TestContext.Current.CancellationToken));
    }
}
