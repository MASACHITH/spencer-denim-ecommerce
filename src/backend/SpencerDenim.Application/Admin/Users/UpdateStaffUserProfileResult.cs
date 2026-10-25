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

    /// <summary>
    /// The target is a protected account and the request would change its email address. The protection is keyed
    /// on the address, so renaming the account would be a way round it (<see cref="Abstractions.IProtectedStaffAccounts"/>).
    /// </summary>
    public sealed record ProtectedEmail : UpdateStaffUserProfileResult;

    /// <summary>
    /// The target is a guarded account, the request would change its email address, and the actor is not a protected
    /// account (<see cref="Abstractions.IProtectedStaffAccounts.IsGuarded"/>).
    /// </summary>
    public sealed record GuardedEmail : UpdateStaffUserProfileResult;
}
