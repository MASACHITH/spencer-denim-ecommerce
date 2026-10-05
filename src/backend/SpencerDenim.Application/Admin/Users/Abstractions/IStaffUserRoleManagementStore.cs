using SpencerDenim.Application.Admin.Users.Contracts;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Application.Admin.Users.Abstractions;

/// <summary>
/// Write port for staff user and role-assignment mutations (ADM-02, doc 08 §3.1 DB4). Every RBAC
/// authorization decision (the subset rule, self-edit block, GrantsAll gating) is made by the command
/// handler calling this port, using <see cref="RoleAssignmentPolicy"/> against snapshots this port also
/// supplies — this port itself only ever enforces the one rule that needs real transactional machinery to be
/// correct under concurrency: DB4 rule 4, the last-Super-Admin guard (see the remarks on
/// <see cref="ReplaceRolesStoreResult.LastSuperAdminGuardTriggered"/>).
/// <para>
/// <b>Every write method takes a factory for the audit row it must leave behind</b> (issue #59) and enlists a
/// fresh row inside its own transaction, on the success path only. The change and its trail therefore commit
/// together or not at all: a refused write (a stale authorization, the last-Super-Admin guard) rolls back and
/// leaves no row, and a failure to write the row rolls the change back with it. The handler supplies the factory
/// because it holds the before and after state; the store owns the transaction, so only the store can enlist at
/// the right moment.
/// </para>
/// <para>
/// <b>Why a factory and not the row itself</b> (tester round 3, Major 1). <c>EnableRetryOnFailure</c> re-runs the
/// whole transaction on a transient failure, and an <see cref="AuditLog"/> that has already been through a failed
/// <c>SaveChanges</c> can carry the identity value EF assigned it, so re-enlisting that same instance makes the
/// retry fail with "Cannot insert explicit value for identity column". Each attempt therefore builds its own row.
/// </para>
/// </summary>
public interface IStaffUserRoleManagementStore
{
    Task<StaffUserSnapshot?> GetStaffUserSnapshotAsync(int userId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<int, RoleGrantSnapshot>> GetRolesAsync(IReadOnlyCollection<int> roleIds, CancellationToken cancellationToken);

    Task<bool> IsEmailInUseAsync(string email, int? excludingUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the user and assigns their roles in one transaction together with the audit row.
    /// <paramref name="auditForNewUser"/> is called inside that transaction, once the identity value exists (and
    /// again on a transient-failure retry), so the row always names the id that was really inserted.
    /// </summary>
    Task<CreateStaffUserStoreResult> CreateStaffUserAsync(
        NewStaffUserData data,
        Func<int, AuditLog> auditForNewUser,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the target's entire role assignment with <paramref name="roleIds"/> and, on success, rotates
    /// their <c>SecurityStamp</c> (doc 08 §3.1 DB4 rule 5) so the 1-minute revalidation loop (issue #16) picks
    /// up the change. The caller must have already authorized this exact before/after transition — but
    /// <paramref name="actor"/> is that same authorization re-validated by this store against the target's
    /// fresh in-transaction state immediately before writing (tester-round-1 fix, defect 2), so a concurrent
    /// change to the same target between the caller's read and this call fails this call cleanly
    /// (<see cref="ReplaceRolesStoreResult.AuthorizationStale"/>) instead of being silently overwritten.
    /// </summary>
    Task<ReplaceRolesStoreResult> ReplaceRolesAsync(
        int targetUserId,
        IReadOnlyList<int> roleIds,
        RbacActorContext actor,
        Func<AuditLog> audit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Flips <c>IsActive</c>. Deactivating rejects under the same last-Super-Admin guard as unassigning the
    /// GrantsAll role; reactivating never reduces the active GrantsAll count, so it never runs the guard.
    /// See <see cref="ReplaceRolesAsync"/>'s remarks on <paramref name="actor"/> — the same re-validation
    /// applies here against the target's current (roles unchanged by this operation) permission set.
    /// </summary>
    Task<SetActiveStoreResult> SetActiveAsync(
        int targetUserId,
        bool active,
        RbacActorContext actor,
        Func<AuditLog> audit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Updates profile fields. <paramref name="newEmail"/> is null when the email is not changing; the caller
    /// must have already checked <see cref="RoleAssignmentPolicy.CanChangeStaffEmail"/> before passing a value.
    /// See <see cref="ReplaceRolesAsync"/>'s remarks on <paramref name="actor"/>.
    /// </summary>
    Task<UpdateProfileStoreResult> UpdateProfileAsync(
        int targetUserId,
        string firstName,
        string? lastName,
        string? newEmail,
        RbacActorContext actor,
        Func<AuditLog> audit,
        CancellationToken cancellationToken);
}
