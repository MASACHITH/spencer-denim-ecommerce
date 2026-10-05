namespace SpencerDenim.Application.Admin.Users.Abstractions;

/// <summary>
/// Result of <see cref="IStaffUserRoleManagementStore.CreateStaffUserAsync"/>. RBAC authorization (the subset
/// rule, GrantsAll gating) has already happened in the command handler by the time this runs — this only
/// reports the Identity-level outcome, split by which input it is about, because the caller has to key a 400 on
/// a field and only Identity's error codes know which field that is (review round 4, item 4).
/// </summary>
public abstract record CreateStaffUserStoreResult
{
    private CreateStaffUserStoreResult()
    {
    }

    public sealed record Success(int UserId, string SecurityStamp) : CreateStaffUserStoreResult;

    /// <summary>
    /// Identity refused the address as already taken. The handler checks for this before writing, so reaching
    /// here means two requests raced between that check and the insert — the same case, and the same answer,
    /// as <see cref="UpdateProfileStoreResult.EmailAlreadyInUse"/>: a 409, not a 400 about the password.
    /// </summary>
    public sealed record EmailAlreadyInUse : CreateStaffUserStoreResult;

    /// <summary>Identity's password policy refused <c>InitialPassword</c>, and nothing else was wrong.</summary>
    public sealed record PasswordRejected(IReadOnlyList<string> Errors) : CreateStaffUserStoreResult;

    /// <summary>
    /// Identity refused the account itself rather than the password — in every failure reachable here, the
    /// address: <c>UserName</c> mirrors <c>Email</c>, so an <c>InvalidUserName</c> on this path is a complaint
    /// about the address, which is why the caller keys this on the email field exactly as it does
    /// <see cref="UpdateProfileStoreResult.IdentityErrors"/>.
    /// </summary>
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

    /// <summary>
    /// Tester-round-1 fix (defect 2): the handler's pre-write authorization decision no longer holds against
    /// the target's fresh, in-transaction state (re-checked under the per-target <c>sp_getapplock</c>) — a
    /// concurrent change landed between the handler's read and this write. The caller must map this to the
    /// exact same generic denial shape as an ordinary subset-rule refusal (see <c>RbacDenialClassifier</c>);
    /// it is not a new, separately-observable reason.
    /// </summary>
    public sealed record AuthorizationStale : ReplaceRolesStoreResult;
}

/// <summary>Result of <see cref="IStaffUserRoleManagementStore.SetActiveAsync"/>. See <see cref="ReplaceRolesStoreResult"/> for the guard and <see cref="ReplaceRolesStoreResult.AuthorizationStale"/> notes.</summary>
public abstract record SetActiveStoreResult
{
    private SetActiveStoreResult()
    {
    }

    public sealed record Success : SetActiveStoreResult;

    public sealed record TargetNotFound : SetActiveStoreResult;

    public sealed record LastSuperAdminGuardTriggered : SetActiveStoreResult;

    /// <summary>See <see cref="ReplaceRolesStoreResult.AuthorizationStale"/>.</summary>
    public sealed record AuthorizationStale : SetActiveStoreResult;
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

    /// <summary>See <see cref="ReplaceRolesStoreResult.AuthorizationStale"/>.</summary>
    public sealed record AuthorizationStale : UpdateProfileStoreResult;
}
