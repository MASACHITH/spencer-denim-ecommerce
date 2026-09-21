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
}
