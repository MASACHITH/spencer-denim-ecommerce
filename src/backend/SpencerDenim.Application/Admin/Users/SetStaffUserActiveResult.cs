namespace SpencerDenim.Application.Admin.Users;

public abstract record SetStaffUserActiveResult
{
    private SetStaffUserActiveResult()
    {
    }

    public sealed record Success(int UserId, bool IsActive) : SetStaffUserActiveResult;

    public sealed record TargetNotFound : SetStaffUserActiveResult;

    /// <summary>
    /// Not one of doc 08 §3.1 DB4's four numbered rules by name, but the same "self-lockout" rationale rule 3
    /// gives for blocking self role-edits: a non-GrantsAll actor could otherwise deactivate their own account
    /// (their own permissions are trivially a subset of themselves, so <c>CanManageStaffUser</c> alone would
    /// not catch it). Flagged in the final report as a deliberate extension beyond the rule's literal wording,
    /// for the architect to confirm.
    /// </summary>
    public sealed record SelfEditNotAllowed : SetStaffUserActiveResult;

    public sealed record Forbidden(RbacDenialReason Reason, string Message) : SetStaffUserActiveResult;

    /// <summary>Doc 08 §3.1 DB4 rule 4: deactivating this user would leave zero active Super Admins.</summary>
    public sealed record LastSuperAdminGuardTriggered : SetStaffUserActiveResult;
}
