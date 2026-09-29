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
