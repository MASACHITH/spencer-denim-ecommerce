using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Identity;

/// <summary>
/// Doc 08 §3.1 DB4 rule 4, the last-Super-Admin guard, exercised directly against
/// <see cref="IStaffUserRoleManagementStore"/> against a real SQL Server database (so the real
/// <c>sp_getapplock</c> transaction runs, not a mock). This deliberately bypasses the command handler's
/// actor/self-edit authorization layer — see the remarks on <c>UserEndpointTests
/// .A_lone_active_super_admin_cannot_be_deactivated_by_anyone_reachable_through_this_api</c> for why that
/// layer makes "the literal sole remaining Super Admin" scenario unreachable through the HTTP surface, even
/// though the store must still refuse it on its own merits (defence in depth, and because nothing but this
/// class actually enforces the guard).
/// <para>
/// <c>UX_Roles_GrantsAll</c> (doc 08 §3.1) allows at most one <c>GrantsAllPermissions = 1</c> role in the whole
/// database, and this suite shares one database for the whole test assembly (doc 06 §4, <c>AssemblyFixture</c>),
/// so every test here reuses the single existing GrantsAll role (by flag, creating it only if truly none
/// exists yet) instead of creating its own, and explicitly deactivates any other active holder left behind by
/// an earlier test before asserting a guard outcome that depends on an exact headcount.
/// </para>
/// </summary>
public sealed class StaffUserRoleManagementStoreTests(SqlServerDatabaseFixture sqlServer)
{
    [Fact]
    public async Task Deactivating_the_sole_active_GrantsAll_holder_is_rejected()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();

        var roleName = await GetOrCreateGrantsAllRoleNameAsync(scope.ServiceProvider);
        var soleAdminId = await CreateActiveGrantsAllUserAsync(scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(), roleName, "sole");
        await DeactivateAllOtherActiveGrantsAllHoldersAsync(scope.ServiceProvider, soleAdminId);

        var store = scope.ServiceProvider.GetRequiredService<IStaffUserRoleManagementStore>();
        var result = await store.SetActiveAsync(soleAdminId, active: false, TestContext.Current.CancellationToken);

        Assert.IsType<SetActiveStoreResult.LastSuperAdminGuardTriggered>(result);

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stillActive = await dbContext.Users.AsNoTracking().Where(u => u.Id == soleAdminId).Select(u => u.IsActive).SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(stillActive);

        await RemoveFromGrantsAllRoleAsync(scope.ServiceProvider, soleAdminId);
    }

    [Fact]
    public async Task Unassigning_the_GrantsAll_role_from_the_sole_holder_is_rejected()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();

        var roleName = await GetOrCreateGrantsAllRoleNameAsync(scope.ServiceProvider);
        var soleAdminId = await CreateActiveGrantsAllUserAsync(scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(), roleName, "sole");
        await DeactivateAllOtherActiveGrantsAllHoldersAsync(scope.ServiceProvider, soleAdminId);

        var store = scope.ServiceProvider.GetRequiredService<IStaffUserRoleManagementStore>();
        var result = await store.ReplaceRolesAsync(soleAdminId, [], TestContext.Current.CancellationToken);

        Assert.IsType<ReplaceRolesStoreResult.LastSuperAdminGuardTriggered>(result);

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await userManager.FindByIdAsync(soleAdminId.ToString(CultureInfo.InvariantCulture));
        Assert.NotNull(user);
        var roles = await userManager.GetRolesAsync(user);
        Assert.NotEmpty(roles);

        await RemoveFromGrantsAllRoleAsync(scope.ServiceProvider, soleAdminId);
    }

    [Fact]
    public async Task Deactivating_one_of_two_active_GrantsAll_holders_is_allowed()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roleName = await GetOrCreateGrantsAllRoleNameAsync(scope.ServiceProvider);

        var firstId = await CreateActiveGrantsAllUserAsync(userManager, roleName, "first");
        var secondId = await CreateActiveGrantsAllUserAsync(userManager, roleName, "second");
        await DeactivateAllOtherActiveGrantsAllHoldersAsync(scope.ServiceProvider, firstId, secondId);

        var store = scope.ServiceProvider.GetRequiredService<IStaffUserRoleManagementStore>();
        var result = await store.SetActiveAsync(firstId, active: false, TestContext.Current.CancellationToken);

        Assert.IsType<SetActiveStoreResult.Success>(result);

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var secondStillActive = await dbContext.Users.AsNoTracking().Where(u => u.Id == secondId).Select(u => u.IsActive).SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(secondStillActive);

        await RemoveFromGrantsAllRoleAsync(scope.ServiceProvider, firstId, secondId);
    }

    /// <summary>Finds the one existing GrantsAll role by its flag, or creates it (named "Super Admin", matching the real seeder) only if truly none exists yet.</summary>
    private static async Task<string> GetOrCreateGrantsAllRoleNameAsync(IServiceProvider services)
    {
        var dbContext = services.GetRequiredService<AppDbContext>();
        var existingName = await dbContext.Roles.AsNoTracking()
            .Where(r => r.GrantsAllPermissions)
            .Select(r => r.Name)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        if (existingName is not null)
        {
            return existingName;
        }

        var roleManager = services.GetRequiredService<RoleManager<AppRole>>();
        var role = new AppRole { Name = "Super Admin", GrantsAllPermissions = true, IsSystem = true };
        var roleResult = await roleManager.CreateAsync(role);
        Assert.True(roleResult.Succeeded, string.Join("; ", roleResult.Errors.Select(e => e.Description)));
        return role.Name!;
    }

    /// <summary>See the class remarks: keeps a guard test's headcount precondition exact regardless of what earlier tests in this shared database left active.</summary>
    private static async Task DeactivateAllOtherActiveGrantsAllHoldersAsync(IServiceProvider services, params int[] exceptUserIds)
    {
        var dbContext = services.GetRequiredService<AppDbContext>();
        var keep = exceptUserIds.ToHashSet();

        var others = await dbContext.Users
            .Where(u => u.IsActive
                && !keep.Contains(u.Id)
                && dbContext.UserRoles.Any(ur => ur.UserId == u.Id && dbContext.Roles.Any(r => r.Id == ur.RoleId && r.GrantsAllPermissions)))
            .ToListAsync(TestContext.Current.CancellationToken);

        foreach (var user in others)
        {
            user.IsActive = false;
        }

        if (others.Count > 0)
        {
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// See the class remarks: restores the shared "Super Admin" role's membership to what it was before this
    /// test, so <c>AdminAuthEndpointTests.Admin_seeding_creates_exactly_one_super_admin_who_can_then_sign_in</c>
    /// still finds it empty whenever it happens to run.
    /// </summary>
    private static async Task RemoveFromGrantsAllRoleAsync(IServiceProvider services, params int[] userIds)
    {
        var userManager = services.GetRequiredService<UserManager<AppUser>>();

        foreach (var userId in userIds)
        {
            var user = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));
            if (user is null)
            {
                continue;
            }

            var roles = await userManager.GetRolesAsync(user);
            if (roles.Count > 0)
            {
                await userManager.RemoveFromRolesAsync(user, roles);
            }
        }
    }

    private static async Task<int> CreateActiveGrantsAllUserAsync(UserManager<AppUser> userManager, string roleName, string label)
    {
        var email = $"{label}-{Guid.NewGuid():N}@example.com";
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            UserType = UserType.Staff,
            FirstName = "Test",
            LastName = "Admin",
            IsActive = true,
        };

        var createResult = await userManager.CreateAsync(user, "Correct-Password-1!");
        Assert.True(createResult.Succeeded, string.Join("; ", createResult.Errors.Select(e => e.Description)));

        var addResult = await userManager.AddToRoleAsync(user, roleName);
        Assert.True(addResult.Succeeded, string.Join("; ", addResult.Errors.Select(e => e.Description)));

        return user.Id;
    }
}
