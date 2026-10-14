using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Identity.Seeding;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Configurations.Identity;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Identity;

/// <summary>
/// <b>A permission added after a database was created reaches the default roles that database already
/// holds</b> (issue #122, doc 08 §9's versioned seeder step, N13).
/// <para>
/// <b>Every test here runs against a database of its own that it ages on purpose</b>, and that is the
/// whole point: the defect is invisible on a fresh database, because <c>AdminSeedHostedService</c> creates
/// each role from the whole current definition, and invisible on the shared fixture's database, because
/// the fixture seeded it with the current build. So each test creates the deployment doc 08 §10 describes,
/// starts the API once to get a correctly seeded database, then puts that database back into the state an
/// older build left it in — every increment's claims deleted and <c>Seed.Version</c> wound back — and starts
/// the API again. Without the ageing step none of these tests could fail, which is the shape this project
/// has shipped eleven times.
/// </para>
/// </summary>
public sealed class DefaultRoleSeedUpgradeTests(SqlServerDatabaseFixture sqlServer)
{
    /// <summary>
    /// Every role-and-permission pair <b>every</b> increment above the baseline adds, which is what a
    /// database created at the baseline is missing. Taken from <see cref="DefaultRoleSeedVersions"/>
    /// rather than retyped, so the assertions follow the declaration;
    /// <c>DefaultRoleSeedVersionsTests</c> is what pins the declaration to issue #122's list.
    /// </summary>
    /// <remarks>
    /// <b>It used to select version 2 alone, and that made this whole suite go quiet the moment a second
    /// increment was declared.</b> W3-05's version 3 adds <c>Promotions.Create</c> and
    /// <c>Promotions.Delete</c> to Store Manager, and with a version-2-only list none of it was asserted:
    /// the ageing step did not remove those two claims, so the database it produced was not the state any
    /// build ever left (a version-1 marker over version-3 claims), the step had nothing to grant for them,
    /// and <see cref="Running_the_step_again_grants_nothing_and_leaves_one_row_per_permission"/>'s count
    /// matched because the pairs it did not know about were already there. The mechanism merged in #152
    /// and its first real use would have shipped unverified.
    /// <para>
    /// Over every increment, the next one is covered by arriving — which is the property this suite is
    /// for. <see cref="DefaultRoleSeedVersionsTests"/> pins the baseline so a permission smuggled in
    /// there instead still goes red.
    /// </para>
    /// </remarks>
    private static List<(string RoleName, string Permission)> IncrementPairs =>
        DefaultRoleSeedVersions.All
            .Where(increment => increment.Version > DefaultRoleSeedVersions.Baseline)
            .SelectMany(increment => increment.AddedPermissions
                .Select(permission => (increment.RoleName, permission)))
            .ToList();

    private static Dictionary<string, string?> SeedConfiguration() =>
        new()
        {
            ["Seed:Admin:Enabled"] = "true",
            ["Seed:Admin:Email"] = $"seed-upgrade-{Guid.NewGuid():N}@example.com",
            ["Seed:Admin:Password"] = "Correct-Horse-Battery-Staple-1!",
        };

    /// <summary>
    /// <b>The defect, and the fix.</b> A database seeded at the baseline has none of the permission values
    /// any later increment adds on its default roles; starting this build's API grants every one of them,
    /// and leaves the marker at the current version. It covers version 2 and version 3 today, and the
    /// next increment by arriving.
    /// </summary>
    [Fact]
    public async Task A_permission_added_after_seeding_reaches_a_role_that_already_exists()
    {
        sqlServer.SkipIfUnavailable();

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var deployment = await FreshDeployment.CreateAsync(
            sqlServer.ConnectionString,
            cancellationToken);

        await StartHostAsync(deployment, cancellationToken);

        // The database is now correct for this build. Wind it back to what the previous build left.
        await AgeToTheBaselineAsync(deployment, cancellationToken);

        // **The premise, asserted.** Without this the test would pass against a step that does nothing,
        // because the host above already granted everything.
        foreach (var (roleName, permission) in IncrementPairs)
        {
            Assert.False(
                await HoldsAsync(deployment, roleName, permission, cancellationToken),
                $"The ageing step did not remove {permission} from '{roleName}', so this test cannot fail.");
        }

        // Step 3 of the deployment, again: start the API. No flag, no script, no manual grant.
        await StartHostAsync(deployment, cancellationToken);

        foreach (var (roleName, permission) in IncrementPairs)
        {
            Assert.True(
                await HoldsAsync(deployment, roleName, permission, cancellationToken),
                $"'{roleName}' still does not hold {permission} after a startup, so a staff user in that "
                + "role would be refused by a screen that is there (issue #122).");
        }

        // And the marker is back at the current version, written as the settings row doc 08 §9 names —
        // with a permission no role can hold and a group no screen shows, so nobody can edit it.
        await using var scope = ScopeOver(deployment);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var marker = await db.Settings
            .SingleAsync(setting => setting.Key == SettingKeys.Seed.Version, cancellationToken);

        Assert.Equal(DefaultRoleSeedVersions.Current.ToString(CultureInfo.InvariantCulture), marker.Value);
        Assert.Equal(SettingKeys.Seed.Group, marker.GroupKey);
        Assert.Equal(Permissions.Settings.Security, marker.RequiredPermission);
        Assert.Null(SettingCatalog.Find(SettingKeys.Seed.Version));
    }

    /// <summary>
    /// <b>A permission the client removed stays removed</b> (N13), <b>including on the startup that is
    /// busy granting a new one</b>.
    /// </summary>
    /// <remarks>
    /// <b>The second half is the half that matters, and the first version of this test did not have
    /// it.</b> With the marker already at the current version there is no pending increment at all, so the
    /// version gate alone keeps the role untouched — and a mutation that replaced
    /// <c>increment.AddedPermissions</c> with the role's whole shipped definition passed this test
    /// unchanged, because the loop it broke never ran. That is precisely the test-that-cannot-fail shape
    /// this project has shipped eleven times.
    /// <para>
    /// So the second half removes a baseline permission <i>and</i> winds the marker back, which is the
    /// state the client's database will really be in: an older seed version, the client's own edits on top
    /// of it. The step then has to grant the new permissions and leave the removed one alone in the same
    /// pass, which is the property "apply only the additions" actually buys.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_permission_the_client_removed_is_not_put_back()
    {
        sqlServer.SkipIfUnavailable();

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var deployment = await FreshDeployment.CreateAsync(
            sqlServer.ConnectionString,
            cancellationToken);

        await StartHostAsync(deployment, cancellationToken);

        // The client's own edit: a baseline permission taken off a default role, with the marker left at
        // the current version, which is exactly what removing it through a role screen would leave behind.
        await RemoveAsync(
            deployment,
            DefaultRoleDefinitions.StoreManagerName,
            Permissions.Orders.Refund,
            cancellationToken);

        Assert.False(
            await HoldsAsync(
                deployment,
                DefaultRoleDefinitions.StoreManagerName,
                Permissions.Orders.Refund,
                cancellationToken));

        await StartHostAsync(deployment, cancellationToken);

        Assert.False(
            await HoldsAsync(
                deployment,
                DefaultRoleDefinitions.StoreManagerName,
                Permissions.Orders.Refund,
                cancellationToken),
            "Orders.Refund was restored to Store Manager. The step must only apply permissions introduced "
            + "above the recorded version; reinstating one the client removed is what doc 08 N13 forbids.");

        // **Now with an increment pending**, so the step is doing work on this very role in this very
        // pass, and still must not touch anything but the additions.
        await AgeToTheBaselineAsync(deployment, cancellationToken);

        await StartHostAsync(deployment, cancellationToken);

        foreach (var (roleName, permission) in IncrementPairs)
        {
            Assert.True(
                await HoldsAsync(deployment, roleName, permission, cancellationToken),
                $"'{roleName}' did not get {permission}, so the second half of this test is not "
                + "exercising a pass that grants anything.");
        }

        Assert.False(
            await HoldsAsync(
                deployment,
                DefaultRoleDefinitions.StoreManagerName,
                Permissions.Orders.Refund,
                cancellationToken),
            "Orders.Refund came back on the startup that granted the pending increments' permissions. The step "
            + "must "
            + "apply the increment's additions and nothing else — not the role's whole shipped "
            + "definition, which would undo every edit the client has made (doc 08 N13).");
    }

    /// <summary>
    /// <b>The drift the step will not act on is reported by name</b>, which is the part of issue #122 that
    /// turns a silent gap into a line somebody reads.
    /// </summary>
    [Fact]
    public async Task The_step_reports_what_it_will_not_grant_by_name()
    {
        sqlServer.SkipIfUnavailable();

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var deployment = await FreshDeployment.CreateAsync(
            sqlServer.ConnectionString,
            cancellationToken);

        await using var factory = new ApiFactory(
            deployment.ConnectionString,
            additionalConfiguration: SeedConfiguration());
        _ = factory.Services;

        await RemoveAsync(
            deployment,
            DefaultRoleDefinitions.ContentEditorName,
            Permissions.Media.Delete,
            cancellationToken);

        await using var scope = factory.Services.CreateAsyncScope();
        var upgrader = scope.ServiceProvider.GetRequiredService<DefaultRoleSeedUpgrader>();

        var result = await upgrader.RunAsync(cancellationToken);

        Assert.Empty(result.Granted);

        var contentEditor = Assert.Single(
            result.MissingPermissions,
            role => role.RoleName == DefaultRoleDefinitions.ContentEditorName);

        Assert.Equal([Permissions.Media.Delete], contentEditor.Permissions);

        // And the roles that are correct are not reported, so the Warning is about something.
        Assert.DoesNotContain(
            DefaultRoleDefinitions.StoreManagerName,
            result.MissingPermissions.Select(role => role.RoleName));
    }

    /// <summary>
    /// <b>The step is idempotent against the row, not only against the version.</b> Running it twice over
    /// an aged database grants once: the second pass finds the claims and adds nothing, so there is no
    /// duplicate for <c>UX_RoleClaims_Role_Type_Value</c> to reject and no second row for a permission
    /// screen to show twice.
    /// </summary>
    [Fact]
    public async Task Running_the_step_again_grants_nothing_and_leaves_one_row_per_permission()
    {
        sqlServer.SkipIfUnavailable();

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var deployment = await FreshDeployment.CreateAsync(
            sqlServer.ConnectionString,
            cancellationToken);

        await using var factory = new ApiFactory(
            deployment.ConnectionString,
            additionalConfiguration: SeedConfiguration());
        _ = factory.Services;

        await AgeToTheBaselineAsync(deployment, cancellationToken);

        await using (var first = factory.Services.CreateAsyncScope())
        {
            var granted = await first.ServiceProvider
                .GetRequiredService<DefaultRoleSeedUpgrader>()
                .RunAsync(cancellationToken);

            Assert.Equal(IncrementPairs.Count, granted.Granted.Count);
        }

        await using (var second = factory.Services.CreateAsyncScope())
        {
            var again = await second.ServiceProvider
                .GetRequiredService<DefaultRoleSeedUpgrader>()
                .RunAsync(cancellationToken);

            Assert.Empty(again.Granted);
            Assert.Empty(again.MissingPermissions);
            Assert.Equal(DefaultRoleSeedVersions.Current, again.RecordedVersion);
        }

        foreach (var (roleName, permission) in IncrementPairs)
        {
            Assert.Equal(1, await ClaimRowCountAsync(deployment, roleName, permission, cancellationToken));
        }
    }

    /// <summary>
    /// <b>The marker is never moved down, and a marker that is not a number does not stop the step.</b>
    /// A database written by a newer build may hold permissions this build has never heard of, so lowering
    /// its marker would make a later upgrade re-offer increments the client may since have removed.
    /// </summary>
    [Fact]
    public async Task A_newer_marker_is_left_alone_and_an_unreadable_one_re_offers_every_increment()
    {
        sqlServer.SkipIfUnavailable();

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var deployment = await FreshDeployment.CreateAsync(
            sqlServer.ConnectionString,
            cancellationToken);

        await using var factory = new ApiFactory(
            deployment.ConnectionString,
            additionalConfiguration: SeedConfiguration());
        _ = factory.Services;

        var (roleName, permission) = IncrementPairs[0];

        // A newer build's marker, with one of this build's increments missing from the role.
        await RemoveAsync(deployment, roleName, permission, cancellationToken);
        await SetMarkerAsync(deployment, "99", cancellationToken);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider
                .GetRequiredService<DefaultRoleSeedUpgrader>()
                .RunAsync(cancellationToken);

            Assert.Empty(result.Granted);
            Assert.Equal(99, result.RecordedVersion);
        }

        Assert.Equal("99", await ReadMarkerAsync(deployment, cancellationToken));
        Assert.False(await HoldsAsync(deployment, roleName, permission, cancellationToken));

        // A value nobody can read is treated as the baseline: re-offering is idempotent, and refusing to
        // run would leave the roles short for as long as the bad value sat there.
        await SetMarkerAsync(deployment, "banana", cancellationToken);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider
                .GetRequiredService<DefaultRoleSeedUpgrader>()
                .RunAsync(cancellationToken);

            Assert.Contains(permission, result.Granted.Select(grant => grant.Permission));
        }

        Assert.Equal(
            DefaultRoleSeedVersions.Current.ToString(CultureInfo.InvariantCulture),
            await ReadMarkerAsync(deployment, cancellationToken));

        Assert.True(await HoldsAsync(deployment, roleName, permission, cancellationToken));
    }

    /// <summary>
    /// <b>Three hosts starting at once against an aged database all come up ready, and each permission is
    /// granted exactly once.</b> This is the deploy-time case the step has to survive: a rolling restart
    /// or a multi-replica start, with every instance reading the same old marker at the same moment.
    /// </summary>
    /// <remarks>
    /// The protection is the reference-data phase's <c>sp_getapplock</c> on
    /// <c>SpencerDenim:ReferenceDataSeed</c>, which the step runs inside, with
    /// <c>UX_RoleClaims_Role_Type_Value</c> and <c>PK_Settings</c> underneath it as the database's own
    /// answer — a unique violation there is contention, which <c>CatalogSeedHostedService</c> already
    /// retries rather than reporting as a broken shop.
    /// </remarks>
    [Fact]
    public async Task Three_hosts_starting_at_once_grant_each_permission_exactly_once()
    {
        sqlServer.SkipIfUnavailable();

        const int instances = 3;

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var deployment = await FreshDeployment.CreateAsync(
            sqlServer.ConnectionString,
            cancellationToken);

        await StartHostAsync(deployment, cancellationToken);
        await AgeToTheBaselineAsync(deployment, cancellationToken);

        var factories = Enumerable.Range(0, instances)
            .Select(_ => new ApiFactory(
                deployment.ConnectionString,
                additionalConfiguration: SeedConfiguration()))
            .ToList();

        try
        {
            using var gate = new SemaphoreSlim(0, instances);

            var starting = factories.Select(async factory =>
            {
                await gate.WaitAsync(cancellationToken);
                return factory.CreateClient();
            }).ToList();

            gate.Release(instances);

            var clients = await Task.WhenAll(starting);

            try
            {
                foreach (var client in clients)
                {
                    using var ready = await client.GetAsync(
                        new Uri("/health/ready", UriKind.Relative),
                        cancellationToken);

                    Assert.True(
                        ready.StatusCode == System.Net.HttpStatusCode.OK,
                        "An instance came up unready: "
                        + await ready.Content.ReadAsStringAsync(cancellationToken));
                }
            }
            finally
            {
                foreach (var client in clients)
                {
                    client.Dispose();
                }
            }

            foreach (var (roleName, permission) in IncrementPairs)
            {
                Assert.Equal(
                    1,
                    await ClaimRowCountAsync(deployment, roleName, permission, cancellationToken));
            }

            Assert.Equal(
                DefaultRoleSeedVersions.Current.ToString(CultureInfo.InvariantCulture),
                await ReadMarkerAsync(deployment, cancellationToken));
        }
        finally
        {
            foreach (var factory in factories)
            {
                await factory.DisposeAsync();
            }
        }
    }

    /// <summary>Starts a host over the deployment's database and disposes it again.</summary>
    private static async Task StartHostAsync(FreshDeployment deployment, CancellationToken cancellationToken)
    {
        await using var factory = new ApiFactory(
            deployment.ConnectionString,
            additionalConfiguration: SeedConfiguration());

        // Touching Services starts the host, which runs every hosted service's StartAsync to completion.
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync(
            new Uri("/health/ready", UriKind.Relative),
            cancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.OK, ready.StatusCode);
    }

    /// <summary>
    /// Puts the database back into the state a baseline build left: every increment's claims deleted and
    /// <c>Seed.Version</c> wound back to the baseline.
    /// </summary>
    private static async Task AgeToTheBaselineAsync(
        FreshDeployment deployment,
        CancellationToken cancellationToken)
    {
        foreach (var (roleName, permission) in IncrementPairs)
        {
            await RemoveAsync(deployment, roleName, permission, cancellationToken);
        }

        await SetMarkerAsync(
            deployment,
            DefaultRoleSeedVersions.Baseline.ToString(CultureInfo.InvariantCulture),
            cancellationToken);
    }

    private static async Task RemoveAsync(
        FreshDeployment deployment,
        string roleName,
        string permission,
        CancellationToken cancellationToken)
    {
        await using var scope = ScopeOver(deployment);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var roleId = await db.Roles
            .Where(role => role.Name == roleName)
            .Select(role => role.Id)
            .SingleAsync(cancellationToken);

        var removed = await db.RoleClaims
            .Where(claim =>
                claim.RoleId == roleId
                && claim.ClaimType == RoleClaimConfiguration.PermissionClaimType
                && claim.ClaimValue == permission)
            .ExecuteDeleteAsync(cancellationToken);

        Assert.True(removed > 0, $"'{roleName}' did not hold {permission}, so nothing was removed.");
    }

    private static async Task SetMarkerAsync(
        FreshDeployment deployment,
        string value,
        CancellationToken cancellationToken)
    {
        await using var scope = ScopeOver(deployment);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var updated = await db.Settings
            .Where(setting => setting.Key == SettingKeys.Seed.Version)
            .ExecuteUpdateAsync(setter => setter.SetProperty(setting => setting.Value, value), cancellationToken);

        Assert.Equal(1, updated);
    }

    private static async Task<string?> ReadMarkerAsync(
        FreshDeployment deployment,
        CancellationToken cancellationToken)
    {
        await using var scope = ScopeOver(deployment);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Settings
            .Where(setting => setting.Key == SettingKeys.Seed.Version)
            .Select(setting => setting.Value)
            .SingleAsync(cancellationToken);
    }

    private static async Task<bool> HoldsAsync(
        FreshDeployment deployment,
        string roleName,
        string permission,
        CancellationToken cancellationToken) =>
        await ClaimRowCountAsync(deployment, roleName, permission, cancellationToken) > 0;

    private static async Task<int> ClaimRowCountAsync(
        FreshDeployment deployment,
        string roleName,
        string permission,
        CancellationToken cancellationToken)
    {
        await using var scope = ScopeOver(deployment);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.RoleClaims
            .Join(
                db.Roles.Where(role => role.Name == roleName),
                claim => claim.RoleId,
                role => role.Id,
                (claim, _) => claim)
            .CountAsync(
                claim =>
                    claim.ClaimType == RoleClaimConfiguration.PermissionClaimType
                    && claim.ClaimValue == permission,
                cancellationToken);
    }

    private static AsyncServiceScope ScopeOver(FreshDeployment deployment) =>
        deployment.Services.CreateAsyncScope();
}
