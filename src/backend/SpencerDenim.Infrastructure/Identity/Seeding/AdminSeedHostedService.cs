using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpencerDenim.Domain.Identity;

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
}
