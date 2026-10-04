using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Admin.Users;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.UnitTests.Application.Admin.Users;

/// <summary>Hand-written test doubles for the Users &amp; Roles command handler tests (W1-08, issue #8) — this
/// codebase writes its own fakes rather than take a mocking library dependency, the same convention
/// <c>ChangeAdminPasswordCommandHandlerTests</c> uses.</summary>
internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

internal sealed class RecordingAuditLogWriter : IAuditLogWriter
{
    public List<AuditLog> Entries { get; } = [];

    public Task WriteAsync(AuditLog entry, CancellationToken cancellationToken)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public void Enlist(AuditLog entry) => Entries.Add(entry);
}

internal sealed class StubEffectivePermissionsProvider(EffectivePermissions permissions) : IEffectivePermissionsProvider
{
    public Task<EffectivePermissions> GetEffectivePermissionsAsync(int userId, CancellationToken cancellationToken) =>
        Task.FromResult(permissions);
}

internal sealed class StubAdminUserLookup(AdminUserSummary? summary) : IAdminUserLookup
{
    public Task<AdminUserSummary?> FindByIdAsync(int userId, CancellationToken cancellationToken) => Task.FromResult(summary);
}

/// <summary>
/// An in-memory stand-in for <see cref="IStaffUserRoleManagementStore"/>. Snapshots and roles are seeded by the
/// test; the last-Super-Admin guard is modelled with an explicit switch rather than the real
/// <c>sp_getapplock</c> transaction (that mechanism is proven for real against SQL Server in
/// <c>StaffUserRoleManagementStoreTests</c> and <c>UserEndpointTests</c>) — this double exists to test the
/// *handler's* orchestration and its calls into <see cref="RoleAssignmentPolicy"/>, not the transaction itself.
/// </summary>
internal sealed class FakeStaffUserRoleManagementStore : IStaffUserRoleManagementStore
{
    public Dictionary<int, StaffUserSnapshot> Users { get; } = [];

    public Dictionary<int, RoleGrantSnapshot> Roles { get; } = [];

    public HashSet<string> EmailsInUse { get; } = [];

    /// <summary>
    /// Where this fake enlists the audit row the handler hands it, exactly as the real store does inside its
    /// transaction (issue #59): on the success path only. A refused write (stale authorization, last-Super-Admin
    /// guard, target not found) enlists nothing, so <c>Assert.Empty(audit.Entries)</c> keeps meaning "a refused
    /// write leaves no trail".
    /// </summary>
    public IAuditLogWriter? AuditWriter { get; set; }

    /// <summary>The audit row most recently handed to a write method, whether or not the write succeeded.</summary>
    public AuditLog? LastAuditRowOffered { get; private set; }

    public bool NextReplaceRolesTriggersGuard { get; set; }

    public bool NextSetActiveTriggersGuard { get; set; }

    /// <summary>
    /// Tester-round-1 fix (defect 2): lets a handler test simulate the store detecting, at write time, that a
    /// concurrent change made the handler's earlier authorization decision stale — without needing the real
    /// <c>sp_getapplock</c> transaction (that mechanism is proven for real against SQL Server in
    /// <c>StaffUserRoleManagementStoreTests</c>).
    /// </summary>
    public bool NextReplaceRolesTriggersAuthorizationStale { get; set; }

    public bool NextSetActiveTriggersAuthorizationStale { get; set; }

    public bool NextUpdateProfileTriggersAuthorizationStale { get; set; }

    public (int TargetUserId, IReadOnlyList<int> RoleIds)? ReplaceRolesCall { get; private set; }

    public (int TargetUserId, bool Active)? SetActiveCall { get; private set; }

    public RbacActorContext? LastActorContext { get; private set; }

    public Task<StaffUserSnapshot?> GetStaffUserSnapshotAsync(int userId, CancellationToken cancellationToken) =>
        Task.FromResult(Users.TryGetValue(userId, out var user) ? user : null);

    public Task<IReadOnlyDictionary<int, RoleGrantSnapshot>> GetRolesAsync(IReadOnlyCollection<int> roleIds, CancellationToken cancellationToken)
    {
        var result = roleIds
            .Where(Roles.ContainsKey)
            .ToDictionary(id => id, id => Roles[id]);
        return Task.FromResult<IReadOnlyDictionary<int, RoleGrantSnapshot>>(result);
    }

    public Task<bool> IsEmailInUseAsync(string email, int? excludingUserId, CancellationToken cancellationToken) =>
        Task.FromResult(EmailsInUse.Contains(email));

    public Task<CreateStaffUserStoreResult> CreateStaffUserAsync(
        NewStaffUserData data,
        Func<int, AuditLog> auditForNewUser,
        CancellationToken cancellationToken)
    {
        var newId = Users.Count == 0 ? 1000 : Users.Keys.Max() + 1;
        var roles = data.RoleIds.Where(Roles.ContainsKey).Select(id => Roles[id]).ToList();
        Users[newId] = new StaffUserSnapshot(newId, data.Email, data.FirstName, data.LastName, true, "stamp-0", roles);

        var audit = auditForNewUser(newId);
        LastAuditRowOffered = audit;
        AuditWriter?.Enlist(LastAuditRowOffered!);
        return Task.FromResult<CreateStaffUserStoreResult>(new CreateStaffUserStoreResult.Success(newId, "stamp-0"));
    }

    public Task<ReplaceRolesStoreResult> ReplaceRolesAsync(
        int targetUserId,
        IReadOnlyList<int> roleIds,
        RbacActorContext actor,
        Func<AuditLog> audit,
        CancellationToken cancellationToken)
    {
        ReplaceRolesCall = (targetUserId, roleIds);
        LastActorContext = actor;
        LastAuditRowOffered = audit();

        if (!Users.TryGetValue(targetUserId, out var existing))
        {
            return Task.FromResult<ReplaceRolesStoreResult>(new ReplaceRolesStoreResult.TargetNotFound());
        }

        if (NextReplaceRolesTriggersAuthorizationStale)
        {
            return Task.FromResult<ReplaceRolesStoreResult>(new ReplaceRolesStoreResult.AuthorizationStale());
        }

        if (NextReplaceRolesTriggersGuard)
        {
            return Task.FromResult<ReplaceRolesStoreResult>(new ReplaceRolesStoreResult.LastSuperAdminGuardTriggered());
        }

        var newRoles = roleIds.Where(Roles.ContainsKey).Select(id => Roles[id]).ToList();
        var newStamp = $"stamp-{Guid.NewGuid():N}";
        Users[targetUserId] = existing with { Roles = newRoles, SecurityStamp = newStamp };
        AuditWriter?.Enlist(LastAuditRowOffered!);
        return Task.FromResult<ReplaceRolesStoreResult>(new ReplaceRolesStoreResult.Success(newStamp));
    }

    public Task<SetActiveStoreResult> SetActiveAsync(
        int targetUserId,
        bool active,
        RbacActorContext actor,
        Func<AuditLog> audit,
        CancellationToken cancellationToken)
    {
        SetActiveCall = (targetUserId, active);
        LastActorContext = actor;
        LastAuditRowOffered = audit();

        if (!Users.TryGetValue(targetUserId, out var existing))
        {
            return Task.FromResult<SetActiveStoreResult>(new SetActiveStoreResult.TargetNotFound());
        }

        if (NextSetActiveTriggersAuthorizationStale)
        {
            return Task.FromResult<SetActiveStoreResult>(new SetActiveStoreResult.AuthorizationStale());
        }

        if (NextSetActiveTriggersGuard)
        {
            return Task.FromResult<SetActiveStoreResult>(new SetActiveStoreResult.LastSuperAdminGuardTriggered());
        }

        Users[targetUserId] = existing with { IsActive = active };
        AuditWriter?.Enlist(LastAuditRowOffered!);
        return Task.FromResult<SetActiveStoreResult>(new SetActiveStoreResult.Success());
    }

    public Task<UpdateProfileStoreResult> UpdateProfileAsync(
        int targetUserId,
        string firstName,
        string? lastName,
        string? newEmail,
        RbacActorContext actor,
        Func<AuditLog> audit,
        CancellationToken cancellationToken)
    {
        LastActorContext = actor;
        LastAuditRowOffered = audit();

        if (!Users.TryGetValue(targetUserId, out var existing))
        {
            return Task.FromResult<UpdateProfileStoreResult>(new UpdateProfileStoreResult.TargetNotFound());
        }

        if (NextUpdateProfileTriggersAuthorizationStale)
        {
            return Task.FromResult<UpdateProfileStoreResult>(new UpdateProfileStoreResult.AuthorizationStale());
        }

        Users[targetUserId] = existing with { FirstName = firstName, LastName = lastName, Email = newEmail ?? existing.Email };
        AuditWriter?.Enlist(LastAuditRowOffered!);
        return Task.FromResult<UpdateProfileStoreResult>(new UpdateProfileStoreResult.Success());
    }
}
