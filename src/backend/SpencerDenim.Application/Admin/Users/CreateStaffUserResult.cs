using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Admin.Users.Contracts;

namespace SpencerDenim.Application.Admin.Users;

public abstract record CreateStaffUserResult
{
    private CreateStaffUserResult()
    {
    }

    public sealed record Success(
        int UserId,
        string Email,
        string FirstName,
        string? LastName,
        bool GrantsAllPermissions,
        IReadOnlyList<RoleRefDto> Roles) : CreateStaffUserResult;

    public sealed record ValidationFailed(IReadOnlyList<ValidationError> Errors) : CreateStaffUserResult;

    /// <summary>Doc 08 §3.1 DB4: the actor's own permissions do not authorize the requested role set.</summary>
    public sealed record Forbidden(RbacDenialReason Reason, string Message) : CreateStaffUserResult;

    public sealed record RoleNotFound(IReadOnlyList<int> UnknownRoleIds) : CreateStaffUserResult;

    public sealed record EmailAlreadyInUse : CreateStaffUserResult;

    public sealed record PasswordRejected(IReadOnlyList<string> Errors) : CreateStaffUserResult;
}
