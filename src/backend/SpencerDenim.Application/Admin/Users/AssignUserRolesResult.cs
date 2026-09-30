using SpencerDenim.Application.Admin.Users.Contracts;

namespace SpencerDenim.Application.Admin.Users;

public abstract record AssignUserRolesResult
{
    private AssignUserRolesResult()
    {
    }

    public sealed record Success(int UserId, bool GrantsAllPermissions, IReadOnlyList<RoleRefDto> Roles) : AssignUserRolesResult;

    public sealed record TargetNotFound : AssignUserRolesResult;

    public sealed record RoleNotFound(IReadOnlyList<int> UnknownRoleIds) : AssignUserRolesResult;

    /// <summary>Doc 08 §3.1 DB4 rule 3: nobody may edit their own role assignments.</summary>
    public sealed record SelfEditNotAllowed : AssignUserRolesResult;

    public sealed record Forbidden(RbacDenialReason Reason, string Message) : AssignUserRolesResult;

    /// <summary>
    /// Doc 08 §3.1 DB4 rule 4: this would have left zero active Super Admins. Caught inside the locked
    /// transaction, not by the handler's earlier (necessarily non-atomic) snapshot read.
    /// </summary>
    public sealed record LastSuperAdminGuardTriggered : AssignUserRolesResult;
}
