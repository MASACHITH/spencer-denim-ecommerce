namespace SpencerDenim.Application.Admin.Users.Abstractions;

/// <summary>
/// Result of <see cref="IStaffUserRoleManagementStore.CreateStaffUserAsync"/>. RBAC authorization (the subset
/// rule, GrantsAll gating) has already happened in the command handler by the time this runs — this only
/// reports the Identity-level outcome (duplicate email, password policy, etc.).
/// </summary>
public abstract record CreateStaffUserStoreResult
{
    private CreateStaffUserStoreResult()
    {
    }

    public sealed record Success(int UserId, string SecurityStamp) : CreateStaffUserStoreResult;

    public sealed record IdentityErrors(IReadOnlyList<string> Errors) : CreateStaffUserStoreResult;
}

/// <summary>
/// Result of <see cref="IStaffUserRoleManagementStore.ReplaceRolesAsync"/>. <see cref="LastSuperAdminGuardTriggered"/>
/// is the doc 08 §3.1 DB4 rule 4 outcome: the transactional recheck (inside the <c>sp_getapplock</c>'d
/// transaction) found this would leave zero active GrantsAll holders, even though an earlier, non-locked read in
/// the command handler may have looked safe — this is the authoritative answer, not that pre-check.
/// </summary>
public abstract record ReplaceRolesStoreResult
{
    private ReplaceRolesStoreResult()
    {
    }

    public sealed record Success(string NewSecurityStamp) : ReplaceRolesStoreResult;

    public sealed record TargetNotFound : ReplaceRolesStoreResult;

    public sealed record LastSuperAdminGuardTriggered : ReplaceRolesStoreResult;
}

/// <summary>Result of <see cref="IStaffUserRoleManagementStore.SetActiveAsync"/>. See <see cref="ReplaceRolesStoreResult"/> for the guard note.</summary>
public abstract record SetActiveStoreResult
{
    private SetActiveStoreResult()
    {
    }

    public sealed record Success : SetActiveStoreResult;

    public sealed record TargetNotFound : SetActiveStoreResult;

    public sealed record LastSuperAdminGuardTriggered : SetActiveStoreResult;
}

/// <summary>Result of <see cref="IStaffUserRoleManagementStore.UpdateProfileAsync"/>.</summary>
public abstract record UpdateProfileStoreResult
{
    private UpdateProfileStoreResult()
    {
    }

    public sealed record Success : UpdateProfileStoreResult;

    public sealed record TargetNotFound : UpdateProfileStoreResult;

    public sealed record EmailAlreadyInUse : UpdateProfileStoreResult;

    public sealed record IdentityErrors(IReadOnlyList<string> Errors) : UpdateProfileStoreResult;
}
