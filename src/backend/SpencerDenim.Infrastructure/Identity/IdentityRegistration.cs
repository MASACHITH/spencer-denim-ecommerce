using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Admin.Account;
using SpencerDenim.Application.Admin.Authentication;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity.Authorization;
using SpencerDenim.Infrastructure.Identity.Seeding;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// Wires ASP.NET Core Identity, the admin cookie scheme and the permission authorization framework (doc 03 §3a).
/// Called from <see cref="DependencyInjection.AddInfrastructure"/>, mirroring how the rest of that method
/// registers infrastructure adapters. Pipeline middleware (<c>UseAuthentication</c> / <c>UseAuthorization</c>)
/// still has to be added in Api's <c>Program.cs</c> — that ordering cannot live outside the composition root.
/// </summary>
public static class IdentityRegistration
{
    public static IServiceCollection AddAdminIdentity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMemoryCache();
        services.AddHttpContextAccessor();

        services.AddOptions<AdminSecurityOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection(AdminSecurityOptions.SectionName).Bind(options))
            .Validate(o => o.LockoutMaxFailedAttempts > 0, "AdminSecurity:LockoutMaxFailedAttempts must be greater than zero.")
            .Validate(o => o.LockoutMinutes > 0, "AdminSecurity:LockoutMinutes must be greater than zero.")
            .Validate(o => o.CookieLifetimeMinutes > 0, "AdminSecurity:CookieLifetimeMinutes must be greater than zero.")
            .Validate(o => o.SecurityStampValidationIntervalSeconds > 0, "AdminSecurity:SecurityStampValidationIntervalSeconds must be greater than zero.")
            .Validate(o => o.TwoFactorPendingSignInMinutes > 0, "AdminSecurity:TwoFactorPendingSignInMinutes must be greater than zero.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.TwoFactorIssuer) && !o.TwoFactorIssuer.Contains(':', StringComparison.Ordinal), "AdminSecurity:TwoFactorIssuer is required and must not contain ':'.")
            .Validate(o => o.TwoFactorRecoveryCodeCount is >= 5 and <= 20, "AdminSecurity:TwoFactorRecoveryCodeCount must be between 5 and 20.")
            .Validate(
                o => ConfiguredProtectedStaffAccounts.AllEntriesLookLikeEmails(o.ProtectedStaffEmails),
                "AdminSecurity:ProtectedStaffEmails must be email addresses separated by commas, semicolons or spaces. A mistyped address would leave the account unprotected, so the server refuses to start.")
            .Validate(
                o => ConfiguredProtectedStaffAccounts.AllEntriesLookLikeEmails(o.GuardedStaffEmails),
                "AdminSecurity:GuardedStaffEmails must be email addresses separated by commas, semicolons or spaces. A mistyped address would leave the account unguarded, so the server refuses to start.")
            .Validate(
                o => ConfiguredProtectedStaffAccounts.Parse(o.GuardedStaffEmails).Count == 0 || ConfiguredProtectedStaffAccounts.Parse(o.ProtectedStaffEmails).Count > 0,
                "AdminSecurity:GuardedStaffEmails needs AdminSecurity:ProtectedStaffEmails as well: only a protected account may change a guarded one, so with none named the guarded account could never be deactivated or reactivated.")
            .ValidateOnStart();

        services.AddSingleton<IProtectedStaffAccounts, ConfiguredProtectedStaffAccounts>();

        services.AddOptions<SeedAdminOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection(SeedAdminOptions.SectionName).Bind(options))
            .Validate(
                o => !o.Enabled || (!string.IsNullOrWhiteSpace(o.Email) && !string.IsNullOrWhiteSpace(o.Password)),
                "Seed:Admin:Email and Seed:Admin:Password are required when Seed:Admin:Enabled is true.")
            .ValidateOnStart();

        services.AddIdentityCore<AppUser>()
            .AddRoles<AppRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            // Issue #46: replaces the EF user store registered just above (the last IUserStore registration wins),
            // so a save of a stale user can never write an old lockout state back — see AppUserStore.
            .AddUserStore<AppUserStore>()
            .AddSignInManager()
            // Issue #200: a too-short password is refused with the policy's own sentence, on every path.
            .AddErrorDescriber<PasswordPolicyErrorDescriber>()
            .AddDefaultTokenProviders()
            // W1-10: registered after the defaults so it replaces Identity's authenticator provider under the same
            // name, and every authenticator check (sign-in and enrolment) refuses a code that was already used.
            .AddTokenProvider<ReplayProtectedAuthenticatorTokenProvider>(TokenOptions.DefaultAuthenticatorProvider);

        services.AddOptions<IdentityOptions>()
            .Configure<IOptions<AdminSecurityOptions>>((identityOptions, adminSecurity) =>
            {
                var security = adminSecurity.Value;
                identityOptions.Lockout.AllowedForNewUsers = true;
                identityOptions.Lockout.MaxFailedAccessAttempts = security.LockoutMaxFailedAttempts;
                identityOptions.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(security.LockoutMinutes);
                identityOptions.User.RequireUniqueEmail = true;
                identityOptions.SignIn.RequireConfirmedAccount = false;

                // Issue #200: the password rule for EVERY account, from the one place it is written. These options
                // are global, so this is the staff rule and the customer rule at once: creating a staff user, a
                // staff member's own change (and the forced first one), the seeded first Super Admin and a
                // customer's register / change / reset all end in UserManager's validators and are refused here.
                // Before, nothing set RequiredLength, so staff passwords sat at Identity's default of six while
                // customers (who had a separate pre-check) needed ten. The four character classes below are
                // Identity's defaults, stated so the sentence PasswordPolicy.Requirement promises them is a fact
                // of the configuration and not an accident of a default (PasswordRuleWiringTests pins them).
                identityOptions.Password.RequiredLength = PasswordPolicy.MinimumLength;
                identityOptions.Password.RequireDigit = true;
                identityOptions.Password.RequireLowercase = true;
                identityOptions.Password.RequireUppercase = true;
                identityOptions.Password.RequireNonAlphanumeric = true;
            });

        services.AddAuthentication(AdminAuthenticationDefaults.Scheme)
            .AddCookie(AdminAuthenticationDefaults.Scheme)
            .AddCookie(AdminAuthenticationDefaults.TwoFactorPendingScheme);

        // W1-10: "password accepted, code still needed". Short-lived and never sliding, scoped to the auth
        // endpoints, and never an admin session: nothing authorizes against this scheme; only the second sign-in
        // step reads it, and it is deleted as soon as that step succeeds or the account locks.
        services.AddOptions<CookieAuthenticationOptions>(AdminAuthenticationDefaults.TwoFactorPendingScheme)
            .Configure<IOptions<AdminSecurityOptions>>((cookieOptions, adminSecurity) =>
            {
                cookieOptions.Cookie.Name = AdminAuthenticationDefaults.TwoFactorPendingCookieName;
                cookieOptions.Cookie.Path = "/api/admin/auth";
                cookieOptions.Cookie.HttpOnly = true;
                cookieOptions.Cookie.SecurePolicy = ResolveSecurePolicy(adminSecurity.Value);
                cookieOptions.Cookie.SameSite = SameSiteMode.Strict;
                cookieOptions.SlidingExpiration = false;
                cookieOptions.ExpireTimeSpan = TimeSpan.FromMinutes(adminSecurity.Value.TwoFactorPendingSignInMinutes);
            });

        // Identity's "remember this browser for 2FA" scheme is deliberately not registered: SignInManager then
        // treats no browser as remembered, so staff give a code at every sign-in and a right password alone never
        // resets the failed-attempt count of a 2FA account.

        services.AddOptions<CookieAuthenticationOptions>(AdminAuthenticationDefaults.Scheme)
            .Configure<IOptions<AdminSecurityOptions>>((cookieOptions, adminSecurity) =>
            {
                cookieOptions.Cookie.Name = AdminAuthenticationDefaults.CookieName;
                cookieOptions.Cookie.HttpOnly = true;
                cookieOptions.Cookie.SecurePolicy = ResolveSecurePolicy(adminSecurity.Value);
                cookieOptions.Cookie.SameSite = SameSiteMode.Strict;
                cookieOptions.SlidingExpiration = true;
                cookieOptions.ExpireTimeSpan = TimeSpan.FromMinutes(adminSecurity.Value.CookieLifetimeMinutes);

                // This is an API consumed by the Angular admin SPA: a 302 redirect on an XHR is useless, so
                // unauthenticated / unauthorized requests get a plain status code instead (doc 03 §3a).
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

                // Doc 08 §3.1 DB10: "the admin scheme rejects Customer principals ... at sign-in." Nothing in
                // this slice can actually sign a Customer into this scheme, but this keeps that guarantee true
                // even after the customer scheme exists, without relying on every future call site remembering.
                //
                // Also doc 08 §3.1 DB10 / doc 11 P14: a deactivated or security-stamp-invalidated (password
                // reset) staff account must not keep a working session for the rest of the cookie's sliding
                // lifetime. Before this, `IsActive` was only checked at login and on /whoami — a deactivated
                // admin's cookie kept authenticating every other endpoint. This throttles the recheck to once
                // per `SecurityStampValidationIntervalSeconds` (a real database read on every request would work
                // too, but defeats the point of a cookie), using AuthenticationProperties (itself part of the
                // encrypted cookie) to remember when a principal was last revalidated.
                cookieOptions.Events.OnValidatePrincipal = async context => await ValidateAdminPrincipalAsync(context);
            });

        // Every admin policy also carries the account-state gates (forced password change, then 2FA enrolment for
        // GrantsAll holders): the default policy behind a bare RequireAuthorization() as well as each permission
        // policy.
        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireAdminAccountGates()
                .Build());

        // Scoped, not singleton: they depend on ports that read AppDbContext (scoped).
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, AnyPermissionAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, PasswordChangeCompletedHandler>();
        services.AddScoped<IAuthorizationHandler, TwoFactorEnrolmentCompletedHandler>();
        foreach (var permission in PermissionCatalog.All)
        {
            var policyName = PermissionPolicy.NameFor(permission);
            services.AddAuthorization(options =>
                options.AddPolicy(policyName, policy => policy
                    .AddRequirements(new PermissionRequirement(permission))
                    .RequireAdminAccountGates()));
        }

        // The one endpoint any settings permission opens: the settings read. Its payload filters per row
        // against the caller's own permissions, so the route guard only has to let a settings holder in at
        // all — and a caller holding just Settings.Payments (the Bank.* keys, W3-03) must be able to reach
        // the screen to read the one group that is theirs. ADM-22 recorded this as the widening its
        // successor owed; see AnyPermissionRequirement.
        services.AddAuthorization(options =>
            options.AddPolicy(PermissionPolicy.ReadAnySettingsGroup, policy => policy
                .AddRequirements(new AnyPermissionRequirement(
                    Permissions.Settings.View,
                    Permissions.Settings.Edit,
                    Permissions.Settings.Payments,
                    Permissions.Settings.Security))
                .RequireAdminAccountGates()));

        // The admin alert banner and its acknowledge (doc 08 §3.8 R10, W4-02a). Only the two order
        // permissions are named: a GrantsAll holder passes any AnyPermissionRequirement through the
        // bypass in EffectivePermissions.Has, and R10 gives nobody else a path to an alert. The read
        // model filters per type against the caller's own permissions — see AdminAlertVisibility.
        services.AddAuthorization(options =>
            options.AddPolicy(PermissionPolicy.ReadAnyAlertAudience, policy => policy
                .AddRequirements(new AnyPermissionRequirement(
                    Permissions.Orders.Approve,
                    Permissions.Orders.Refund))
                .RequireAdminAccountGates()));

        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "sd_admin_xsrf";
            options.HeaderName = "X-XSRF-TOKEN";
        });

        // Separate from the block above: AntiforgeryOptions.Cookie.SecurePolicy has to track the same
        // environment-aware policy as the auth cookie (symmetry) — see the RequireSecureCookies remarks.
        // Unlike cookie auth, the antiforgery middleware actively THROWS if SecurePolicy is Always and the
        // request isn't flagged HTTPS, so getting this wrong is a hard failure, not a silently-dropped cookie.
        services.AddOptions<AntiforgeryOptions>()
            .Configure<IOptions<AdminSecurityOptions>>((options, adminSecurity) =>
                options.Cookie.SecurePolicy = ResolveSecurePolicy(adminSecurity.Value));

        services.AddScoped<StaffAccountLockout>();
        services.AddScoped<IAdminAuthenticator, AdminAuthenticator>();
        services.AddScoped<IAdminPasswordChanger, AdminPasswordChanger>();
        services.AddScoped<IAdminTwoFactorManager, AdminTwoFactorManager>();
        services.AddScoped<IAdminUserLookup, AdminUserLookup>();
        services.AddScoped<IEffectivePermissionsProvider, EffectivePermissionsProvider>();
        services.AddScoped<IAuditLogWriter, AuditLogWriter>();

        services.AddHostedService<AdminSeedHostedService>();

        return services;
    }

    /// <summary>See <see cref="AdminSecurityOptions.RequireSecureCookies"/>.</summary>
    private static CookieSecurePolicy ResolveSecurePolicy(AdminSecurityOptions security) =>
        security.RequireSecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;

    /// <summary>Key under which the last revalidation instant is stashed in the cookie's own AuthenticationProperties.</summary>
    private const string LastSecurityStampValidationPropertyKey = "sd.LastSecurityStampValidationUtc";

    private static async Task ValidateAdminPrincipalAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var userTypeClaim = principal?.FindFirstValue(AdminAuthenticationDefaults.UserTypeClaimType);
        if (principal is null || userTypeClaim != ((byte)UserType.Staff).ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            context.RejectPrincipal();
            return;
        }

        var security = context.HttpContext.RequestServices.GetRequiredService<IOptions<AdminSecurityOptions>>().Value;
        var interval = TimeSpan.FromSeconds(security.SecurityStampValidationIntervalSeconds);
        var now = DateTimeOffset.UtcNow;

        var lastValidatedUtc = ReadLastValidatedUtc(context.Properties);
        if (lastValidatedUtc is not null && now - lastValidatedUtc.Value < interval)
        {
            // Not due yet: accept the cookie as-is, same as any other claims-based auth between checks.
            return;
        }

        var userIdClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var cookieSecurityStamp = principal.FindFirstValue(AdminAuthenticationDefaults.SecurityStampClaimType);
        if (!int.TryParse(userIdClaim, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var userId)
            || string.IsNullOrEmpty(cookieSecurityStamp))
        {
            context.RejectPrincipal();
            return;
        }

        var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<AppUser>>();
        var user = await userManager.FindByIdAsync(userId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // IsActive is checked directly (not only via a SecurityStamp bump) so deactivation ends the session on
        // its own, whether or not whatever deactivates the account also remembers to bump the stamp.
        if (user is null || user.UserType != UserType.Staff || !user.IsActive)
        {
            context.RejectPrincipal();
            return;
        }

        var currentSecurityStamp = await userManager.GetSecurityStampAsync(user);
        if (!string.Equals(currentSecurityStamp, cookieSecurityStamp, StringComparison.Ordinal))
        {
            // A password reset (UserManager bumps the stamp itself) or any future action that calls
            // UpdateSecurityStampAsync — this session's claims are stale, so end it rather than trust them further.
            context.RejectPrincipal();
            return;
        }

        WriteLastValidatedUtc(context.Properties, now);
        context.ShouldRenew = true;
    }

    private static DateTimeOffset? ReadLastValidatedUtc(AuthenticationProperties properties)
    {
        if (properties.Items.TryGetValue(LastSecurityStampValidationPropertyKey, out var raw)
            && DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static void WriteLastValidatedUtc(AuthenticationProperties properties, DateTimeOffset value) =>
        properties.Items[LastSecurityStampValidationPropertyKey] = value.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
}
