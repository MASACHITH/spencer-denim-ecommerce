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
    Task<AdminAuthenticationOutcome> AuthenticateAsync(string email, string password, CancellationToken cancellationToken);
}
