using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// Issues and reads the admin cookies (doc 03 §3a): the admin session itself, re-issued with the new security stamp
/// after any change that rotates it (password change, 2FA on or off), and the short-lived pending cookie between
/// the two steps of a 2FA sign-in (W1-10). Shared by the auth and the 2FA endpoints so both build the principal
/// the same way.
/// </summary>
internal static class AdminSessionCookies
{
    /// <summary>The fields the admin cookie principal is built from.</summary>
    public sealed record SessionIdentity(int UserId, string Email, string FirstName, string? LastName, string SecurityStamp);

    /// <summary>The user and security stamp a pending 2FA sign-in belongs to.</summary>
    public sealed record PendingTwoFactorSignIn(int UserId, string SecurityStamp);

    public static Task SignInAsync(HttpContext httpContext, SessionIdentity session)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, session.UserId.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Email, session.Email),
            new(ClaimTypes.GivenName, session.FirstName),
            new(AdminAuthenticationDefaults.UserTypeClaimType, ((byte)UserType.Staff).ToString(CultureInfo.InvariantCulture)),
            new(AdminAuthenticationDefaults.SecurityStampClaimType, session.SecurityStamp),
        };

        if (!string.IsNullOrWhiteSpace(session.LastName))
        {
            claims.Add(new Claim(ClaimTypes.Surname, session.LastName));
        }

        var identity = new ClaimsIdentity(claims, AdminAuthenticationDefaults.Scheme);
        return httpContext.SignInAsync(AdminAuthenticationDefaults.Scheme, new ClaimsPrincipal(identity));
    }

    /// <summary>
    /// Starts a pending 2FA sign-in. Any admin session already in this browser is ended first, so a half-finished
    /// sign-in as one account never sits next to a live session of another.
    /// </summary>
    public static async Task BeginTwoFactorSignInAsync(HttpContext httpContext, PendingTwoFactorSignIn pending)
    {
        await httpContext.SignOutAsync(AdminAuthenticationDefaults.Scheme);

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, pending.UserId.ToString(CultureInfo.InvariantCulture)),
                new Claim(AdminAuthenticationDefaults.SecurityStampClaimType, pending.SecurityStamp),
            ],
            AdminAuthenticationDefaults.TwoFactorPendingScheme);

        await httpContext.SignInAsync(AdminAuthenticationDefaults.TwoFactorPendingScheme, new ClaimsPrincipal(identity));
    }

    /// <summary>The pending 2FA sign-in carried by this request's cookie, or null when there is none or it expired.</summary>
    public static async Task<PendingTwoFactorSignIn?> ReadTwoFactorSignInAsync(HttpContext httpContext)
    {
        var result = await httpContext.AuthenticateAsync(AdminAuthenticationDefaults.TwoFactorPendingScheme);
        if (!result.Succeeded)
        {
            return null;
        }

        var userIdValue = result.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var stamp = result.Principal.FindFirstValue(AdminAuthenticationDefaults.SecurityStampClaimType);
        return int.TryParse(userIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId) && !string.IsNullOrEmpty(stamp)
            ? new PendingTwoFactorSignIn(userId, stamp)
            : null;
    }

    public static Task EndTwoFactorSignInAsync(HttpContext httpContext) =>
        httpContext.SignOutAsync(AdminAuthenticationDefaults.TwoFactorPendingScheme);

    /// <summary>The signed-in admin's user id from the session principal (never client input).</summary>
    public static bool TryGetUserId(ClaimsPrincipal user, out int userId) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.Integer, CultureInfo.InvariantCulture, out userId);

    public static async Task<bool> IsAntiforgeryTokenValidAsync(HttpContext httpContext, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    public static BadRequest<AdminLoginErrorResponse> InvalidAntiforgeryToken() =>
        TypedResults.BadRequest(new AdminLoginErrorResponse("invalid_antiforgery_token", "Missing or invalid antiforgery token.", null));

    /// <summary>
    /// Keys, recovery codes and freshly issued sessions must not be kept by a browser or proxy cache.
    /// </summary>
    public static void DoNotStore(HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers.Pragma = "no-cache";
    }
}
