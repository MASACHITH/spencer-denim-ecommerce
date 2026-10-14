using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Accounts.Abstractions;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Infrastructure.Identity.Customers;

/// <summary>
/// Wires the customer cookie scheme and the account use cases (ACC-01, ACC-02, doc 03 §3a).
/// </summary>
/// <remarks>
/// <b>A separate method from <c>IdentityRegistration.AddAdminIdentity</c>, and it deliberately adds no
/// Identity core services of its own.</b> <c>AddIdentityCore&lt;AppUser&gt;</c> is already called there
/// and there is one user store (doc 08 D7), so calling it twice would register a second
/// <c>UserManager</c>. What this adds is the second <i>cookie scheme</i> doc 08 §3.1 DB10 asks for and
/// the ports behind it — which is also why the admin method must keep being called: its
/// <c>AddAuthentication(AdminAuthenticationDefaults.Scheme)</c> establishes the default scheme, and a
/// customer endpoint always names its own.
/// <para>
/// <b>Nothing here touches <c>IdentityOptions</c>.</b> Those options are global, so they are the staff
/// rules too, and Tharanga has three open PRs against that startup path (#142, #144, #146). The customer
/// lockout reads <see cref="CustomerSecurityOptions"/> and counts for itself
/// (<see cref="CustomerAccountLockout"/>); the customer password rule is one extra check beside
/// Identity's own validators (<see cref="CustomerPasswordPolicy"/>). Both consequences are in the task
/// report.
/// </para>
/// </remarks>
public static class CustomerIdentityRegistration
{
    public static IServiceCollection AddCustomerIdentity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<CustomerSecurityOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection(CustomerSecurityOptions.SectionName).Bind(options))
            .Validate(
                o => o.LockoutMaxFailedAttempts > 0,
                "CustomerSecurity:LockoutMaxFailedAttempts must be greater than zero: a threshold of "
                + "zero or less would lock an account out on its first correct password.")
            .Validate(
                o => o.LockoutMinutes > 0,
                "CustomerSecurity:LockoutMinutes must be greater than zero: a lockout that has already "
                + "expired when it is set is not a lockout.")
            .Validate(
                o => o.CookieLifetimeMinutes > 0,
                "CustomerSecurity:CookieLifetimeMinutes must be greater than zero.")
            .Validate(
                o => o.SecurityStampValidationIntervalSeconds > 0,
                "CustomerSecurity:SecurityStampValidationIntervalSeconds must be greater than zero.")
            .Validate(
                o => o.PasswordResetTokenHours > 0,
                "CustomerSecurity:PasswordResetTokenHours must be greater than zero: a reset link that "
                + "has expired before it is sent cannot be used.")
            .Validate(
                o => o.EmailConfirmationTokenHours > 0,
                "CustomerSecurity:EmailConfirmationTokenHours must be greater than zero.")
            .ValidateOnStart();

        // The second cookie scheme of doc 08 §3.1 DB10. Added to the existing authentication builder
        // rather than a new one: AddAuthentication is idempotent on the default scheme, and the admin
        // registration has already named its own as the default.
        services.AddAuthentication().AddCookie(CustomerAuthenticationDefaults.Scheme);

        services.AddOptions<CookieAuthenticationOptions>(CustomerAuthenticationDefaults.Scheme)
            .Configure<IOptions<CustomerSecurityOptions>>((cookieOptions, customerSecurity) =>
            {
                var security = customerSecurity.Value;

                cookieOptions.Cookie.Name = CustomerAuthenticationDefaults.CookieName;
                cookieOptions.Cookie.HttpOnly = true;
                cookieOptions.Cookie.SecurePolicy = security.RequireSecureCookies
                    ? CookieSecurePolicy.Always
                    : CookieSecurePolicy.SameAsRequest;

                // Lax, not Strict, and this is the one place the customer cookie differs from the admin
                // cookie on purpose. The admin app is reached by typing its address; the storefront is
                // reached from search results, from a shared link and — the case that decides it — from
                // the shop's own emails. `Strict` drops the cookie on every one of those top-level
                // cross-site navigations, so a customer following the order link in their own
                // confirmation email would arrive signed out. `Lax` still withholds the cookie from
                // cross-site POSTs and subresource requests, which is the CSRF surface; the storefront
                // additionally has no cookie-authenticated GET that changes anything.
                cookieOptions.Cookie.SameSite = SameSiteMode.Lax;

                // Scoped to nothing narrower than the site: the storefront's own pages are served from
                // `/` and the API from `/api`, and a path-scoped cookie would be withheld from one of
                // them.
                cookieOptions.Cookie.Path = "/";

                cookieOptions.SlidingExpiration = true;
                cookieOptions.ExpireTimeSpan = TimeSpan.FromMinutes(security.CookieLifetimeMinutes);

                // The storefront is a server-rendered Angular application that calls this API with
                // `fetch`. A 302 to a login page on an XHR is useless and a 302 on a server-side render
                // would be followed by the render process, so both answer a status code — the same
                // decision doc 03 §3a records for the admin API.
                cookieOptions.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                cookieOptions.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };

                cookieOptions.Events.OnValidatePrincipal = ValidateCustomerPrincipalAsync;
            });

        services.AddScoped<CustomerUserLookup>();
        services.AddScoped<CustomerAccountLockout>();
        services.AddScoped<ICustomerRegistration, CustomerRegistrationStore>();
        services.AddScoped<ICustomerAuthenticator, CustomerAuthenticator>();
        services.AddScoped<ICustomerPasswords, CustomerPasswordStore>();
        services.AddScoped<ICustomerEmailConfirmation, CustomerEmailConfirmationStore>();
        services.AddScoped<ICustomerProfileStore, CustomerProfileStore>();

        return services;
    }

    /// <summary>Where the last revalidation instant is stashed, inside the encrypted cookie itself.</summary>
    private const string LastValidatedPropertyKey = "sd.LastCustomerValidationUtc";

    /// <summary>
    /// Doc 08 §3.1 DB10: "the customer scheme rejects Staff principals … both at sign-in and on
    /// security-stamp revalidation".
    /// </summary>
    /// <remarks>
    /// <b>Three checks, and each one has a reason the other two do not cover.</b> The user-type claim is
    /// checked first and costs nothing, so a staff cookie replayed at this scheme is rejected without a
    /// database read. <c>IsActive</c> is checked directly rather than only through a stamp bump, so a
    /// deactivated or anonymised account's session ends whether or not whatever deactivated it remembered
    /// to rotate the stamp — <c>IdentityRegistration</c> makes the same note for the admin scheme, where
    /// it was a real defect. And the stamp comparison is what ends every <i>other</i> session after a
    /// password reset (doc 11 P11 step C.9).
    /// <para>
    /// <b>Throttled to <c>SecurityStampValidationIntervalSeconds</c>.</b> A database read on every
    /// storefront request defeats the point of a cookie; between checks a request is accepted on the
    /// cookie alone, which is what every claims-based scheme does.
    /// </para>
    /// <para>
    /// <b>The confirmed-email claim is refreshed on each real check</b>, so the banner G-24 asks for stops
    /// showing within one interval of the customer confirming — rather than until they next sign in.
    /// </para>
    /// </remarks>
    private static async Task ValidateCustomerPrincipalAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var userTypeClaim = principal?.FindFirstValue(CustomerAuthenticationDefaults.UserTypeClaimType);

        if (principal is null
            || userTypeClaim != ((byte)UserType.Customer).ToString(CultureInfo.InvariantCulture))
        {
            context.RejectPrincipal();
            return;
        }

        var security = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<CustomerSecurityOptions>>().Value;

        var interval = TimeSpan.FromSeconds(security.SecurityStampValidationIntervalSeconds);
        var now = DateTimeOffset.UtcNow;

        if (ReadLastValidated(context.Properties) is { } last && now - last < interval)
        {
            return;
        }

        var userIdClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var cookieStamp = principal.FindFirstValue(CustomerAuthenticationDefaults.SecurityStampClaimType);

        if (!int.TryParse(userIdClaim, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId)
            || string.IsNullOrEmpty(cookieStamp))
        {
            context.RejectPrincipal();
            return;
        }

        var authenticator = context.HttpContext.RequestServices.GetRequiredService<ICustomerAuthenticator>();
        var session = await authenticator.FindSessionAsync(userId, context.HttpContext.RequestAborted);

        if (session is null || !string.Equals(session.SecurityStamp, cookieStamp, StringComparison.Ordinal))
        {
            context.RejectPrincipal();
            return;
        }

        RefreshEmailConfirmedClaim(context, session.EmailConfirmed);
        WriteLastValidated(context.Properties, now);
        context.ShouldRenew = true;
    }

    /// <summary>
    /// Puts the current confirmed state on the principal, replacing a stale value.
    /// </summary>
    /// <remarks>
    /// The claim is a hint for the banner and never a gate (see
    /// <see cref="CustomerAuthenticationDefaults.EmailConfirmedClaimType"/>), so this is a convenience and
    /// not a security control. It is done here rather than left to the next sign-in because a customer
    /// who has just confirmed and is still being told to confirm reads it as the link not having worked.
    /// </remarks>
    private static void RefreshEmailConfirmedClaim(CookieValidatePrincipalContext context, bool emailConfirmed)
    {
        if (context.Principal?.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        var value = emailConfirmed ? "1" : "0";
        var existing = identity.FindFirst(CustomerAuthenticationDefaults.EmailConfirmedClaimType);

        if (existing is not null)
        {
            if (string.Equals(existing.Value, value, StringComparison.Ordinal))
            {
                return;
            }

            identity.RemoveClaim(existing);
        }

        identity.AddClaim(new Claim(CustomerAuthenticationDefaults.EmailConfirmedClaimType, value));
    }

    private static DateTimeOffset? ReadLastValidated(AuthenticationProperties properties) =>
        properties.Items.TryGetValue(LastValidatedPropertyKey, out var raw)
            && DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
            ? parsed
            : null;

    private static void WriteLastValidated(AuthenticationProperties properties, DateTimeOffset value) =>
        properties.Items[LastValidatedPropertyKey] = value.ToString("o", CultureInfo.InvariantCulture);
}
