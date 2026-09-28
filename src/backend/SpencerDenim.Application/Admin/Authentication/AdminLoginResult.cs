namespace SpencerDenim.Application.Admin.Authentication;

/// <summary>Result of <see cref="AdminLoginCommand"/>, returned to the Api endpoint.</summary>
public abstract record AdminLoginResult
{
    private AdminLoginResult()
    {
    }

    /// <summary>
    /// Credentials are valid. The endpoint builds the <see cref="System.Security.Claims.ClaimsPrincipal"/> from
    /// these fields and signs it into the admin cookie scheme itself (doc 03 §3a) — the handler never touches
    /// <c>HttpContext</c>.
    /// </summary>
    public sealed record Success(
        int UserId,
        string Email,
        string FirstName,
        string? LastName,
        bool GrantsAllPermissions,
        IReadOnlyCollection<string> Permissions,
        string SecurityStamp) : AdminLoginResult;

    /// <summary>Unknown email, wrong password, a non-staff or inactive account. Deliberately generic.</summary>
    public sealed record InvalidCredentials : AdminLoginResult;

    /// <summary>Doc 08 §3.1 N14: automatic, time-boxed lockout after repeated failures.</summary>
    public sealed record LockedOut(DateTimeOffset LockoutEndUtc) : AdminLoginResult;
}
