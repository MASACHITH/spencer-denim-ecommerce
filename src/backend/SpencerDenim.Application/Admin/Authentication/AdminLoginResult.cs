using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Application.Admin.Authentication;

/// <summary>
/// Result of <see cref="AdminLoginCommand"/> and of its second step, <see cref="CompleteAdminTwoFactorSignInCommand"/>
/// (W1-10), returned to the Api endpoints.
/// </summary>
public abstract record AdminLoginResult
{
    private AdminLoginResult()
    {
    }

    /// <summary>
    /// Credentials are valid. The endpoint builds the <see cref="System.Security.Claims.ClaimsPrincipal"/> from
    /// these fields and signs it into the admin cookie scheme itself (doc 03 §3a) — the handler never touches
    /// <c>HttpContext</c>. <see cref="MustChangePassword"/> tells the client to send the user to the change-password
    /// page (doc 11 P14 step 3); the server enforces it independently of whether the client listens.
    /// </summary>
    public sealed record Success(
        int UserId,
        string Email,
        string FirstName,
        string? LastName,
        bool GrantsAllPermissions,
        IReadOnlyCollection<string> Permissions,
        string SecurityStamp,
        bool MustChangePassword,
        bool TwoFactorEnabled = false) : AdminLoginResult
    {
        /// <summary>
        /// A GrantsAll holder without 2FA (doc 08 §3.1 N14): the client sends them to the enrolment page; the API
        /// refuses everything else until they enrol (<c>TwoFactorEnrolmentCompletedRequirement</c>).
        /// </summary>
        public bool TwoFactorEnrolmentRequired => TwoFactorPolicy.IsRequired(GrantsAllPermissions) && !TwoFactorEnabled;
    }

    /// <summary>Unknown email, wrong password, a non-staff or inactive account. Deliberately generic.</summary>
    public sealed record InvalidCredentials : AdminLoginResult;

    /// <summary>Doc 08 §3.1 N14: automatic, time-boxed lockout after repeated failures.</summary>
    public sealed record LockedOut(DateTimeOffset LockoutEndUtc) : AdminLoginResult;

    /// <summary>
    /// The password was right and the account has 2FA on (W1-10). No session is issued: the endpoint stores these
    /// in a short-lived pending cookie and the user completes sign-in with a code.
    /// </summary>
    public sealed record TwoFactorRequired(int UserId, string SecurityStamp) : AdminLoginResult;

    /// <summary>The authenticator or recovery code was wrong. The pending sign-in stays open for another try.</summary>
    public sealed record InvalidTwoFactorCode : AdminLoginResult;

    /// <summary>The pending sign-in is stale (the account changed in between): start again from the password.</summary>
    public sealed record TwoFactorSessionExpired : AdminLoginResult;
}
