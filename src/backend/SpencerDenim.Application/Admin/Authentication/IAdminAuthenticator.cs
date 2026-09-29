namespace SpencerDenim.Application.Admin.Authentication;

/// <summary>
/// Port that checks staff credentials against ASP.NET Core Identity (doc 08 §3.1 DB10). Implemented in
/// Infrastructure using <c>UserManager&lt;AppUser&gt;</c> / <c>SignInManager&lt;AppUser&gt;</c>, which own
/// lockout bookkeeping. This port never issues the sign-in cookie itself — building the
/// <see cref="System.Security.Claims.ClaimsPrincipal"/> and calling <c>HttpContext.SignInAsync</c> is the Api
/// endpoint's job (a framework/HTTP concern, kept out of Application).
/// </summary>
public interface IAdminAuthenticator
{
    /// <summary>
    /// The password step. For an account with 2FA on, a right password gives
    /// <see cref="AdminAuthenticationStatus.TwoFactorRequired"/>, never <see cref="AdminAuthenticationStatus.Succeeded"/>,
    /// and does not reset the failed-attempt count (so a known password cannot be used to keep guessing codes).
    /// </summary>
    Task<AdminAuthenticationOutcome> AuthenticateAsync(string email, string password, CancellationToken cancellationToken);

    /// <summary>
    /// The second step (W1-10): checks an authenticator code, or redeems a recovery code (each works once), for
    /// the user the password step was passed for. Wrong codes count towards the same time-boxed lockout as wrong
    /// passwords (doc 08 §3.1 N14).
    /// </summary>
    /// <param name="pendingSecurityStamp">The stamp at the password step; a different current stamp ends the attempt.</param>
    Task<AdminAuthenticationOutcome> CompleteTwoFactorSignInAsync(
        int userId,
        string pendingSecurityStamp,
        string code,
        bool isRecoveryCode,
        CancellationToken cancellationToken);
}
