using SpencerDenim.Domain.Branding;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// Admin authentication settings, bound from environment variables (<c>AdminSecurity__*</c>). Doc 08 §3.8 keeps
/// these as live-editable <c>ops.Settings</c> rows (<c>Security.StaffLockoutMaxFailures</c> /
/// <c>StaffLockoutMinutes</c>) once the Settings feature (ADM-22) exists; until then the defaults below match
/// doc 08's seed values exactly and are overridable per environment the same way <c>DatabaseOptions</c> is.
/// </summary>
public sealed class AdminSecurityOptions
{
    public const string SectionName = "AdminSecurity";

    /// <summary>Doc 08 §3.8 <c>Security.StaffLockoutMaxFailures</c> default.</summary>
    public int LockoutMaxFailedAttempts { get; set; } = 5;

    /// <summary>Doc 08 §3.8 <c>Security.StaffLockoutMinutes</c> default.</summary>
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>Admin cookie lifetime (sliding).</summary>
    public int CookieLifetimeMinutes { get; set; } = 60;

    /// <summary>
    /// How often an already-signed-in request re-checks the staff account's <c>IsActive</c> flag and
    /// <c>SecurityStamp</c> against the database (doc 08 §3.1 DB10, doc 11 P14 "sessions end within 1 minute").
    /// Between checks, a request is accepted on the cookie alone, same as any other claims-based auth. Kept
    /// configurable (rather than a literal 60 in <see cref="IdentityRegistration"/>) so an integration test can
    /// shrink it and prove the rejection without waiting out a real minute.
    /// </summary>
    public int SecurityStampValidationIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// W1-10: how long the password step of a 2FA sign-in stays open for the code (the short-lived pending
    /// cookie's lifetime, not sliding). Five minutes covers finding the phone without leaving a half-signed-in
    /// browser around for long.
    /// </summary>
    public int TwoFactorPendingSignInMinutes { get; set; } = 5;

    /// <summary>
    /// W1-10: the issuer name authenticator apps show next to the account (the <c>otpauth://</c> URI's label and
    /// <c>issuer</c> parameter). Staff-facing, so the store's own name rather than the vendor's.
    /// </summary>
    public string TwoFactorIssuer { get; set; } = StoreBrand.AdminName;

    /// <summary>W1-10: how many single-use recovery codes are issued at enrolment and on each regeneration.</summary>
    public int TwoFactorRecoveryCodeCount { get; set; } = 10;

    /// <summary>
    /// Staff accounts that can never be deactivated, as email addresses separated by commas, semicolons or
    /// spaces (<c>AdminSecurity__ProtectedStaffEmails</c>; on a server, <c>SD_PROTECTED_STAFF_EMAILS</c> in
    /// <c>app.env</c>). Empty by default: nothing is protected until a server names an account. The address is
    /// configuration, never a literal in code (doc 03: configuration comes from the environment). See
    /// <see cref="SpencerDenim.Application.Admin.Users.Abstractions.IProtectedStaffAccounts"/>.
    /// </summary>
    public string ProtectedStaffEmails { get; set; } = string.Empty;

    /// <summary>
    /// Staff accounts that only a protected account may deactivate, reactivate or rename, in the same format
    /// (<c>AdminSecurity__GuardedStaffEmails</c>; on a server, <c>SD_GUARDED_STAFF_EMAILS</c> in <c>app.env</c>).
    /// Empty by default. Requires <see cref="ProtectedStaffEmails"/>: with nobody protected, nobody could ever
    /// change a guarded account, so the server refuses to start rather than lock it.
    /// </summary>
    public string GuardedStaffEmails { get; set; } = string.Empty;

    /// <summary>
    /// Whether the admin auth cookie and the antiforgery cookie require HTTPS
    /// (<see cref="Microsoft.AspNetCore.Http.CookieSecurePolicy.Always"/> vs <c>SameAsRequest</c>).
    /// True (secure-by-default) everywhere: doc 03 §4's deployment topology always terminates TLS at Caddy, and
    /// <c>UseForwardedHeaders</c> (Api Program.cs) restores <c>HttpContext.Request.IsHttps</c> from the client's
    /// real scheme even though Caddy's own hop to the API container is plain HTTP. Set
    /// <c>AdminSecurity__RequireSecureCookies=false</c> only where there is genuinely no TLS anywhere in the
    /// chain — already done in <c>appsettings.Development.json</c> for local <c>dotnet run</c>, where
    /// <c>CookieSecurePolicy.Always</c> would otherwise make the auth cookie never get set and make the
    /// antiforgery middleware throw (doc 03 §3a; this was a real regression, not a hypothetical).
    /// </summary>
    public bool RequireSecureCookies { get; set; } = true;
}
