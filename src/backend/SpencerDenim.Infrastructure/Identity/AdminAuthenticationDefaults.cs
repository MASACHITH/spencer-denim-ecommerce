namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// Doc 03 §3a / doc 08 §3.1 DB10: a separate admin cookie scheme, never the future customer scheme. The admin
/// scheme rejects <see cref="Domain.Identity.UserType.Customer"/> principals at sign-in.
/// </summary>
public static class AdminAuthenticationDefaults
{
    public const string Scheme = "AdminCookie";

    public const string CookieName = "sd_admin_auth";

    /// <summary>Custom claim carrying <see cref="Domain.Identity.UserType"/> so the scheme can reject customers.</summary>
    public const string UserTypeClaimType = "sd_user_type";

    /// <summary>
    /// Custom claim carrying the <c>AppUser.SecurityStamp</c> value at sign-in time, compared against the current
    /// database value on each periodic revalidation (see <see cref="IdentityRegistration"/>'s
    /// <c>OnValidatePrincipal</c>) so a stamp bump — a password reset, today; a deactivation once that also bumps
    /// it — ends the session promptly instead of waiting out the sliding cookie lifetime (doc 08 §3.1 DB10).
    /// </summary>
    public const string SecurityStampClaimType = "sd_security_stamp";
}
