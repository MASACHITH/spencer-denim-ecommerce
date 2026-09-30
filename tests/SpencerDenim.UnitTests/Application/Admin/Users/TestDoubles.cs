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

    public bool NextReplaceRolesTriggersGuard { get; set; }

    public bool NextSetActiveTriggersGuard { get; set; }

    public (int TargetUserId, IReadOnlyList<int> RoleIds)? ReplaceRolesCall { get; private set; }

    public (int TargetUserId, bool Active)? SetActiveCall { get; private set; }

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

    public Task<CreateStaffUserStoreResult> CreateStaffUserAsync(NewStaffUserData data, CancellationToken cancellationToken)
    {
        var newId = Users.Count == 0 ? 1000 : Users.Keys.Max() + 1;
        var roles = data.RoleIds.Where(Roles.ContainsKey).Select(id => Roles[id]).ToList();
        Users[newId] = new StaffUserSnapshot(newId, data.Email, data.FirstName, data.LastName, true, "stamp-0", roles);
        return Task.FromResult<CreateStaffUserStoreResult>(new CreateStaffUserStoreResult.Success(newId, "stamp-0"));
    }

    public Task<ReplaceRolesStoreResult> ReplaceRolesAsync(int targetUserId, IReadOnlyList<int> roleIds, CancellationToken cancellationToken)
    {
        ReplaceRolesCall = (targetUserId, roleIds);

        if (!Users.TryGetValue(targetUserId, out var existing))
        {
            return Task.FromResult<ReplaceRolesStoreResult>(new ReplaceRolesStoreResult.TargetNotFound());
        }

        if (NextReplaceRolesTriggersGuard)
        {
            return Task.FromResult<ReplaceRolesStoreResult>(new ReplaceRolesStoreResult.LastSuperAdminGuardTriggered());
        }

        var newRoles = roleIds.Where(Roles.ContainsKey).Select(id => Roles[id]).ToList();
        var newStamp = $"stamp-{Guid.NewGuid():N}";
        Users[targetUserId] = existing with { Roles = newRoles, SecurityStamp = newStamp };
        return Task.FromResult<ReplaceRolesStoreResult>(new ReplaceRolesStoreResult.Success(newStamp));
    }

    public Task<SetActiveStoreResult> SetActiveAsync(int targetUserId, bool active, CancellationToken cancellationToken)
    {
        SetActiveCall = (targetUserId, active);

        if (!Users.TryGetValue(targetUserId, out var existing))
        {
            return Task.FromResult<SetActiveStoreResult>(new SetActiveStoreResult.TargetNotFound());
        }

        if (NextSetActiveTriggersGuard)
        {
            return Task.FromResult<SetActiveStoreResult>(new SetActiveStoreResult.LastSuperAdminGuardTriggered());
        }

        Users[targetUserId] = existing with { IsActive = active };
        return Task.FromResult<SetActiveStoreResult>(new SetActiveStoreResult.Success());
    }

    public Task<UpdateProfileStoreResult> UpdateProfileAsync(int targetUserId, string firstName, string? lastName, string? newEmail, CancellationToken cancellationToken)
    {
        if (!Users.TryGetValue(targetUserId, out var existing))
        {
            return Task.FromResult<UpdateProfileStoreResult>(new UpdateProfileStoreResult.TargetNotFound());
        }

        Users[targetUserId] = existing with { FirstName = firstName, LastName = lastName, Email = newEmail ?? existing.Email };
        return Task.FromResult<UpdateProfileStoreResult>(new UpdateProfileStoreResult.Success());
    }
}
