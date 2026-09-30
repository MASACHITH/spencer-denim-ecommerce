namespace SpencerDenim.Application.Admin.Account;

/// <summary>Result of <see cref="ChangeAdminPasswordCommand"/>, returned to the Api endpoint.</summary>
public abstract record ChangeAdminPasswordResult
{
    private ChangeAdminPasswordResult()
    {
    }

    /// <summary>
    /// The password was changed and the stamp rotated. The endpoint signs the caller back in with these fields
    /// (same claims as sign-in, new <see cref="SecurityStamp"/>); every other session for the account ends at its
    /// next revalidation (doc 08 §3.1 DB10).
    /// </summary>
    public sealed record Success(
        int UserId,
        string Email,
        string FirstName,
        string? LastName,
        string SecurityStamp) : ChangeAdminPasswordResult;

    /// <summary>The account behind the session is gone or deactivated: the endpoint ends the session.</summary>
    public sealed record AccountUnavailable : ChangeAdminPasswordResult;

    public sealed record InvalidCurrentPassword : ChangeAdminPasswordResult;

    /// <summary>Doc 08 §3.1 N14: the same time-boxed lockout as sign-in.</summary>
    public sealed record LockedOut(DateTimeOffset LockoutEndUtc) : ChangeAdminPasswordResult;

    public sealed record NewPasswordRejected(IReadOnlyList<string> Errors) : ChangeAdminPasswordResult;
}
