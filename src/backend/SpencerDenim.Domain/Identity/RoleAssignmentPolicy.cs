namespace SpencerDenim.Domain.Identity;

/// <summary>
/// RBAC safety rules (doc 08 §3.1 "RBAC safety rules (DB4)"). Pure domain rules: no persistence access, so the
/// caller (an Application command handler) supplies effective permission sets it already loaded.
/// <para>
/// The assign/unassign endpoints W1-08 built are now the real callers — this stopped being "ready for that
/// endpoint to call" when they landed. <b>Role</b> management (creating and editing roles themselves) is still
/// to come and is tracked as issue #105, which carries two blockers that only become reachable once a role's
/// permissions can change: #101's timing oracle, and a stale permission snapshot in the store that calls this
/// policy. Whoever builds that must take a <i>role</i>-scoped lock on both sides, because re-reading inside the
/// transaction is not enough when the lock is per user.
/// </para>
/// </summary>
public static class RoleAssignmentPolicy
{
    /// <summary>
    /// DB4 rule 2: a <see cref="Permissions.Users.Manage"/> holder without GrantsAll may modify, reset the
    /// password or 2FA of, deactivate, reactivate or change the roles of a staff user only if the target's
    /// effective permissions, both before and after the change, are a subset of the actor's own. GrantsAll
    /// holders are never a subset of anything smaller, so they are protected from being managed this way.
    /// </summary>
    public static bool CanManageStaffUser(
        bool actorHasGrantsAll,
        bool actorHasUsersManage,
        IReadOnlySet<string> actorEffectivePermissions,
        bool targetHasGrantsAllBefore,
        IReadOnlySet<string> targetPermissionsBefore,
        bool targetHasGrantsAllAfter,
        IReadOnlySet<string> targetPermissionsAfter)
    {
        ArgumentNullException.ThrowIfNull(actorEffectivePermissions);
        ArgumentNullException.ThrowIfNull(targetPermissionsBefore);
        ArgumentNullException.ThrowIfNull(targetPermissionsAfter);

        if (actorHasGrantsAll)
        {
            return true;
        }

        if (!actorHasUsersManage)
        {
            return false;
        }

        if (targetHasGrantsAllBefore || targetHasGrantsAllAfter)
        {
            return false;
        }

        return targetPermissionsBefore.IsSubsetOf(actorEffectivePermissions)
            && targetPermissionsAfter.IsSubsetOf(actorEffectivePermissions);
    }

    /// <summary>DB4 rule 2: changing a staff user's email needs GrantsAll (it is the password-reset channel).</summary>
    public static bool CanChangeStaffEmail(bool actorHasGrantsAll) => actorHasGrantsAll;

    /// <summary>DB4 rule 3: nobody can edit their own role assignments — true when this would be a self-edit.</summary>
    public static bool IsSelfRoleEditBlocked(int actorUserId, int targetUserId) => actorUserId == targetUserId;

    /// <summary>
    /// DB4 rule 4 (last Super Admin guard): the caller counts active GrantsAll holders excluding the target
    /// user inside a serialised transaction (<c>sp_getapplock 'rbac:grants-all'</c>, doc 08 §3.1) and passes
    /// the count here. An operation that could reduce active GrantsAll holders is allowed only if at least one
    /// other active holder remains. The automatic time-boxed lockout is exempt (N14) — it is not a call site
    /// for this method.
    /// </summary>
    public static bool CanReduceActiveGrantsAllHolders(int activeGrantsAllHoldersExcludingTarget) =>
        activeGrantsAllHoldersExcludingTarget >= 1;
}
