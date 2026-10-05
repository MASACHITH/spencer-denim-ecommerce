using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Persistence.Configurations.Identity;

namespace SpencerDenim.Infrastructure.Identity.Seeding;

/// <summary>
/// Seeds exactly one Super Admin (GrantsAll) role and, when configured, one initial Super Admin user, so the
/// system has a way in before Users &amp; Roles management exists (see <see cref="SeedAdminOptions"/> for the
/// environment variables that gate this). Runs once at host startup, after the database schema is assumed to
/// already exist (this slice does not auto-apply EF Core migrations at startup — run
/// <c>dotnet ef database update</c> first).
/// </summary>
public sealed class AdminSeedHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<SeedAdminOptions> seedOptions,
    ILogger<AdminSeedHostedService> logger) : IHostedService
{
    public const string SuperAdminRoleName = "Super Admin";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = seedOptions.Value;
        if (!options.Enabled)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var superAdminRole = await EnsureSuperAdminRoleAsync(roleManager);

        // W1-08 (issue #8, doc 08 §9): the three default staff roles beyond Super Admin. Seeded here, behind
        // the same Seed:Admin:Enabled gate as the Super Admin role above, because that is the one flag this
        // slice already uses to mean "this host is allowed to create baseline RBAC rows" — see the final report
        // for the operational note on what that means once Enabled is turned back off after go-live.
        await EnsureDefaultRolesAsync(roleManager);

        // Idempotent gate: never create a second Super Admin automatically, no matter how many times this host
        // starts with Seed:Admin:Enabled left on.
        var existingHolders = await userManager.GetUsersInRoleAsync(SuperAdminRoleName);
        if (existingHolders.Count > 0)
        {
            logger.SeedingSkippedExistingHolders(existingHolders.Count);
            return;
        }

        if (string.IsNullOrWhiteSpace(options.Email) || string.IsNullOrWhiteSpace(options.Password))
        {
            logger.SeedingSkippedMissingCredentials();
            return;
        }

        // Doc 08 §9 / doc 11 P14: the bootstrap password comes from the server's .env, so it is known to whoever
        // can read that file and must not stay valid. MustChangePassword makes the API refuse every admin
        // endpoint except the change-password flow until it is replaced (PasswordChangeCompletedRequirement).
        //
        // EmailConfirmed stays true on purpose: the docs require only the forced password change (and 2FA,
        // W1-10) for this account, staff never go through the customer confirm-email flow (doc 08 §3.1 DB10),
        // and the address is typed in by the technical owner who also holds the server. Leaving it unconfirmed
        // would change nothing today (SignIn.RequireConfirmedAccount is false), and no flow exists that could
        // ever confirm a staff address afterwards.
        var admin = new AppUser
        {
            UserName = options.Email,
            Email = options.Email,
            EmailConfirmed = true,
            UserType = UserType.Staff,
            FirstName = options.FirstName,
            LastName = options.LastName,
            IsActive = true,
            MustChangePassword = true,
            CreatedAtUtc = DateTime.UtcNow,
        };

        var createResult = await userManager.CreateAsync(admin, options.Password);
        if (!createResult.Succeeded)
        {
            logger.SeedingFailed(options.Email, string.Join("; ", createResult.Errors.Select(e => e.Description)));
            return;
        }

        await userManager.AddToRoleAsync(admin, SuperAdminRoleName);
        logger.SeededAdmin(options.Email);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task<AppRole> EnsureSuperAdminRoleAsync(RoleManager<AppRole> roleManager)
    {
        var role = await roleManager.FindByNameAsync(SuperAdminRoleName);
        if (role is not null)
        {
            return role;
        }

        role = new AppRole
        {
            Name = SuperAdminRoleName,
            Description = "Full access to every module (doc 03 §3a).",
            IsSystem = true,
            GrantsAllPermissions = true,
            CreatedAtUtc = DateTime.UtcNow,
        };

        var result = await roleManager.CreateAsync(role);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not seed the {SuperAdminRoleName} role: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }

        return role;
    }

    /// <summary>
    /// Doc 08 §9: "The seeder creates default roles and their claims only when the role doesn't exist. A later
    /// permission reaches default roles only through an explicit, idempotent, versioned seeder step (recorded
    /// in the setting Seed.Version), never by overwriting admin edits." <c>ops.Settings</c> (where Seed.Version
    /// would live) does not exist in this codebase yet (ADM-22, W3-07 — see <c>CatalogOptions</c>'s remarks), so
    /// there is nothing to version against yet: this only ever creates a role that is missing by name, exactly
    /// like <see cref="EnsureSuperAdminRoleAsync"/> above, and never touches one that already exists — so it can
    /// never overwrite an admin's own edits to that role's claims. Adding the versioned upgrade path is a
    /// follow-up once a default role's permission list needs to change after its first seed.
    /// </summary>
    private static async Task EnsureDefaultRolesAsync(RoleManager<AppRole> roleManager)
    {
        foreach (var definition in DefaultRoleDefinitions.All)
        {
            var existing = await roleManager.FindByNameAsync(definition.Name);
            if (existing is not null)
            {
                continue;
            }

            var role = new AppRole
            {
                Name = definition.Name,
                Description = definition.Description,
                IsSystem = true,
                GrantsAllPermissions = false,
                CreatedAtUtc = DateTime.UtcNow,
            };

            var createResult = await roleManager.CreateAsync(role);
            if (!createResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Could not seed the {definition.Name} role: {string.Join("; ", createResult.Errors.Select(e => e.Description))}");
            }

            foreach (var permission in definition.Permissions)
            {
                var claimResult = await roleManager.AddClaimAsync(
                    role,
                    new Claim(RoleClaimConfiguration.PermissionClaimType, permission));
                if (!claimResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Could not seed permission '{permission}' on role {definition.Name}: " +
                        string.Join("; ", claimResult.Errors.Select(e => e.Description)));
                }
            }
        }
    }
}
