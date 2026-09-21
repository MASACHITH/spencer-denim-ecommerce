using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Admin.Authentication;
using SpencerDenim.Application.Admin.CurrentUser;
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
            .ValidateOnStart();

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
            .AddSignInManager()
            .AddDefaultTokenProviders();

        services.AddOptions<IdentityOptions>()
            .Configure<IOptions<AdminSecurityOptions>>((identityOptions, adminSecurity) =>
            {
                var security = adminSecurity.Value;
                identityOptions.Lockout.AllowedForNewUsers = true;
                identityOptions.Lockout.MaxFailedAccessAttempts = security.LockoutMaxFailedAttempts;
                identityOptions.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(security.LockoutMinutes);
                identityOptions.User.RequireUniqueEmail = true;
                identityOptions.SignIn.RequireConfirmedAccount = false;
            });

        services.AddAuthentication(AdminAuthenticationDefaults.Scheme)
            .AddCookie(AdminAuthenticationDefaults.Scheme);

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
                cookieOptions.Events.OnValidatePrincipal = context =>
                {
                    var userTypeClaim = context.Principal?.FindFirstValue(AdminAuthenticationDefaults.UserTypeClaimType);
                    if (userTypeClaim != ((byte)UserType.Staff).ToString(System.Globalization.CultureInfo.InvariantCulture))
                    {
                        context.RejectPrincipal();
                    }

                    return Task.CompletedTask;
                };
            });

        services.AddAuthorizationBuilder();

        // Scoped, not singleton: it depends on IEffectivePermissionsProvider, which reads AppDbContext (scoped).
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        foreach (var permission in PermissionCatalog.All)
        {
            var policyName = PermissionPolicy.NameFor(permission);
            services.AddAuthorization(options =>
                options.AddPolicy(policyName, policy => policy.Requirements.Add(new PermissionRequirement(permission))));
        }

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

        services.AddScoped<IAdminAuthenticator, AdminAuthenticator>();
        services.AddScoped<IAdminUserLookup, AdminUserLookup>();
        services.AddScoped<IEffectivePermissionsProvider, EffectivePermissionsProvider>();
        services.AddScoped<IAuditLogWriter, AuditLogWriter>();

        services.AddHostedService<AdminSeedHostedService>();

        return services;
    }

    /// <summary>See <see cref="AdminSecurityOptions.RequireSecureCookies"/>.</summary>
    private static CookieSecurePolicy ResolveSecurePolicy(AdminSecurityOptions security) =>
        security.RequireSecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
}
