using SpencerDenim.Application.Abstractions.Results;

namespace SpencerDenim.Application.Admin.Users;

public abstract record UpdateStaffUserProfileResult
{
    private UpdateStaffUserProfileResult()
    {
    }

    public sealed record Success(int UserId, string Email, string FirstName, string? LastName) : UpdateStaffUserProfileResult;

    public sealed record ValidationFailed(IReadOnlyList<ValidationError> Errors) : UpdateStaffUserProfileResult;

    public sealed record TargetNotFound : UpdateStaffUserProfileResult;

    public sealed record Forbidden(RbacDenialReason Reason, string Message) : UpdateStaffUserProfileResult;

    public sealed record EmailAlreadyInUse : UpdateStaffUserProfileResult;
}
