using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Configurations.Identity;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Identity;

/// <summary>
/// The three default staff roles doc 08 §9 asks for, beyond the separately-tested Super Admin seed (W1-08,
/// issue #8). Role seeding runs as soon as <c>Seed:Admin:Enabled</c> is true, before the bootstrap-admin-user
/// step checks for an existing holder — but <see cref="SeedAdminOptions"/>'s own <c>ValidateOnStart</c> still
/// requires <c>Seed:Admin:Email</c>/<c>Password</c> to be non-empty whenever <c>Enabled</c> is true, regardless
/// of whether a Super Admin already exists, so every host start here still needs a (unique, throwaway) pair.
/// <para>
/// If no Super Admin exists yet anywhere in this shared database at that moment, that throwaway pair really
/// does get seeded into the "Super Admin" role — <c>AdminSeedHostedService</c> has no way to know this caller
/// does not actually want one. Every test here removes that role membership again immediately afterwards (see
/// <see cref="RemoveFromSuperAdminRoleIfPresentAsync"/>), the same reason
/// <c>UserEndpointTests</c>/<c>StaffUserRoleManagementStoreTests</c> clean up their own GrantsAll holders:
/// <c>AdminAuthEndpointTests.Admin_seeding_creates_exactly_one_super_admin_who_can_then_sign_in</c> needs that
/// role to still have zero members the first time it happens to run, regardless of test order.
/// </para>
/// </summary>
public sealed class DefaultRoleSeedingTests(SqlServerDatabaseFixture sqlServer)
{
    private static (Dictionary<string, string?> Configuration, string Email) SeedConfiguration()
    {
        var email = $"role-seed-{Guid.NewGuid():N}@example.com";
        return (
            new Dictionary<string, string?>
            {
                ["Seed:Admin:Enabled"] = "true",
                ["Seed:Admin:Email"] = email,
                ["Seed:Admin:Password"] = "Correct-Horse-Battery-Staple-1!",
            },
            email);
    }

    /// <summary>See the class remarks. A no-op when nothing was actually seeded for this email (the common case, once any Super Admin already exists).</summary>
    private static async Task RemoveFromSuperAdminRoleIfPresentAsync(ApiFactory factory, string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            return;
        }

        var roles = await userManager.GetRolesAsync(user);
        if (roles.Count > 0)
        {
            await userManager.RemoveFromRolesAsync(user, roles);
        }
    }

    [Theory]
    [MemberData(nameof(DefaultRoles))]
    public async Task Each_default_role_is_seeded_with_exactly_its_documented_permission_set(string roleName, IReadOnlyList<string> expectedPermissions, bool expectedGrantsAll)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var (configuration, email) = SeedConfiguration();
        await using var factory = new ApiFactory(sqlServer.ConnectionString, additionalConfiguration: configuration);
        _ = factory.Services; // touching Services starts the host, running AdminSeedHostedService synchronously.

        await using var scope = factory.Services.CreateAsyncScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();

        var role = await roleManager.FindByNameAsync(roleName);
        Assert.NotNull(role);
        Assert.True(role.IsSystem);
        Assert.Equal(expectedGrantsAll, role.GrantsAllPermissions);

        var claims = await roleManager.GetClaimsAsync(role);
        var permissions = claims.Where(c => c.Type == RoleClaimConfiguration.PermissionClaimType).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(new HashSet<string>(expectedPermissions, StringComparer.Ordinal), permissions);

        await RemoveFromSuperAdminRoleIfPresentAsync(factory, email);
    }

    /// <summary>
    /// Doc 08 §9: "The seeder creates default roles and their claims only when the role doesn't exist." Proven
    /// here via the row identity staying the same across two host starts (never recreated, so never at risk of
    /// dropping an admin's own later edits to it) — deliberately without also adding then re-checking a claim
    /// on one of the three real default roles here, which would race
    /// <see cref="Each_default_role_is_seeded_with_exactly_its_documented_permission_set"/>'s exact-permission-
    /// set assertion for whichever of the two methods the test runner happens to execute second, given this
    /// suite's one-shared-database-for-the-whole-assembly design (doc 06 §4 / <c>AssemblyFixture</c>). The
    /// "never overwrites an admin's edit" half of that same doc 08 sentence follows directly from "only when the
    /// role doesn't exist" — proven here — and needs no separate scenario.
    /// </summary>
    [Fact]
    public async Task Seeding_twice_reuses_the_same_default_role_row_instead_of_recreating_it()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var (firstConfiguration, firstEmail) = SeedConfiguration();
        await using var firstFactory = new ApiFactory(sqlServer.ConnectionString, additionalConfiguration: firstConfiguration);
        _ = firstFactory.Services;

        int orderStaffId;
        await using (var scope = firstFactory.Services.CreateAsyncScope())
        {
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();
            var role = await roleManager.FindByNameAsync("Order Staff");
            Assert.NotNull(role);
            orderStaffId = role.Id;
        }

        await RemoveFromSuperAdminRoleIfPresentAsync(firstFactory, firstEmail);

        // A second host start with the same "seed roles" configuration must not create a duplicate "Order
        // Staff" row: the seeder only acts on a role that does not exist by name yet, so the row's Id is
        // unchanged. That is exactly what keeps any later admin edit to this role's claims safe — a seeder that
        // recreated the row would drop whatever an admin had already changed on it.
        var (secondConfiguration, secondEmail) = SeedConfiguration();
        await using var secondFactory = new ApiFactory(sqlServer.ConnectionString, additionalConfiguration: secondConfiguration);
        _ = secondFactory.Services;

        await using var secondScope = secondFactory.Services.CreateAsyncScope();
        var dbContext = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var matchingRoles = await dbContext.Roles.AsNoTracking()
            .Where(r => r.Name == "Order Staff")
            .ToListAsync(TestContext.Current.CancellationToken);
        var orderStaffRole = Assert.Single(matchingRoles);
        Assert.Equal(orderStaffId, orderStaffRole.Id);

        await RemoveFromSuperAdminRoleIfPresentAsync(secondFactory, secondEmail);
    }

    public static TheoryData<string, IReadOnlyList<string>, bool> DefaultRoles()
    {
        var data = new TheoryData<string, IReadOnlyList<string>, bool>();
        foreach (var definition in DefaultRoleDefinitions.All)
        {
            data.Add(definition.Name, [.. definition.Permissions], false);
        }

        return data;
    }
}
