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

        var admin = new AppUser
        {
            UserName = options.Email,
            Email = options.Email,
            EmailConfirmed = true,
            UserType = UserType.Staff,
            FirstName = options.FirstName,
            LastName = options.LastName,
            IsActive = true,
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
