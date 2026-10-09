using SpencerDenim.Domain.Identity;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// <b>The version history of the default roles reproduces the shipped definitions exactly</b>, which is
/// what makes doc 08 §9's versioned seeder step able to tell "added since this database was seeded" from
/// "the client removed it" (issue #122, N13).
/// <para>
/// <b>This is the test that stops #122 recurring, and it is the reason the baseline is written out rather
/// than computed.</b> A permission added to <see cref="DefaultRoleDefinitions"/> and nowhere else reaches
/// a fresh database and no existing one; nothing about that is visible at runtime until a staff user
/// presses a button and is refused. Here it is a red build, naming the permission and the role, before the
/// branch is pushed.
/// </para>
/// </summary>
public sealed class DefaultRoleSeedVersionsTests
{
    public static TheoryData<string> DefaultRoleNames()
    {
        var data = new TheoryData<string>();

        foreach (var definition in DefaultRoleDefinitions.All)
        {
            data.Add(definition.Name);
        }

        return data;
    }

    /// <summary>
    /// The frozen baseline plus every increment equals the role's current shipped definition — in both
    /// directions, so neither a permission added without an increment nor an increment for a permission
    /// that has since been withdrawn can pass.
    /// </summary>
    [Theory]
    [MemberData(nameof(DefaultRoleNames))]
    public void Baseline_plus_increments_reproduces_the_shipped_definition(string roleName)
    {
        var definition = DefaultRoleDefinitions.All.Single(candidate =>
            string.Equals(candidate.Name, roleName, StringComparison.Ordinal));

        var reconstructed = DefaultRoleSeedVersions.ReconstructCurrent(roleName);

        var missingFromHistory = definition.Permissions.Except(reconstructed, StringComparer.Ordinal)
            .OrderBy(permission => permission, StringComparer.Ordinal)
            .ToList();

        var missingFromDefinition = reconstructed.Except(definition.Permissions, StringComparer.Ordinal)
            .OrderBy(permission => permission, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missingFromHistory.Count == 0,
            $"'{roleName}' is granted {string.Join(", ", missingFromHistory)} by DefaultRoleDefinitions, "
            + "and the seed version history does not account for them — so they would reach a fresh "
            + "database and no existing one (issue #122). Add them to DefaultRoleSeedVersions as a new "
            + "increment with a new version number and raise DefaultRoleSeedVersions.Current. Do not add "
            + "them to BaselinePermissions: that set records what databases already hold.");

        Assert.True(
            missingFromDefinition.Count == 0,
            $"The seed version history grants '{roleName}' {string.Join(", ", missingFromDefinition)}, "
            + "which DefaultRoleDefinitions no longer does. A released increment must not be edited — its "
            + "meaning is 'these were added in version N' — so withdrawing a permission needs a decision "
            + "about the databases that already have it, not a deletion here.");
    }

    /// <summary>
    /// <see cref="DefaultRoleSeedVersions.Current"/> is the highest version any increment names, so a new
    /// increment cannot be declared and then never applied.
    /// </summary>
    [Fact]
    public void Current_is_the_highest_declared_increment_version()
    {
        var highest = DefaultRoleSeedVersions.All.Count == 0
            ? DefaultRoleSeedVersions.Baseline
            : DefaultRoleSeedVersions.All.Max(increment => increment.Version);

        Assert.Equal(DefaultRoleSeedVersions.Current, highest);
        Assert.True(
            DefaultRoleSeedVersions.Current >= DefaultRoleSeedVersions.Baseline,
            "The current seed version cannot be below the baseline.");
    }

    /// <summary>
    /// No increment is above the baseline by accident: every one is strictly above it, so there is no
    /// increment a database can never be offered.
    /// </summary>
    [Fact]
    public void Every_increment_is_above_the_baseline_version()
    {
        Assert.All(
            DefaultRoleSeedVersions.All,
            increment => Assert.True(
                increment.Version > DefaultRoleSeedVersions.Baseline,
                $"The increment for '{increment.RoleName}' is version {increment.Version}, which is not "
                + $"above the baseline {DefaultRoleSeedVersions.Baseline}, so no database would ever be "
                + "offered it."));
    }

    /// <summary>
    /// An increment never repeats a permission the baseline already granted that role. A permission in
    /// both would be re-granted to a database whose client had removed it, which is the N13 violation this
    /// whole mechanism exists to avoid.
    /// </summary>
    [Fact]
    public void No_increment_repeats_a_baseline_permission()
    {
        foreach (var increment in DefaultRoleSeedVersions.All)
        {
            if (!DefaultRoleSeedVersions.BaselinePermissions.TryGetValue(increment.RoleName, out var baseline))
            {
                continue;
            }

            var repeated = increment.AddedPermissions.Intersect(baseline, StringComparer.Ordinal).ToList();

            Assert.True(
                repeated.Count == 0,
                $"Version {increment.Version} claims to add {string.Join(", ", repeated)} to "
                + $"'{increment.RoleName}', but the baseline already granted them. A database at the "
                + "baseline whose client removed one would have it put back, which is what N13 forbids.");
        }
    }

    /// <summary>
    /// Every permission the history names is a real catalogue value and is one a role may hold.
    /// <c>Settings.Security</c> is never grantable (<c>CK_RoleClaims_NoSecurity</c>, doc 08 §3.1), so an
    /// increment naming it would be a seeder step that fails on a CHECK constraint at deploy time.
    /// </summary>
    [Fact]
    public void Every_permission_in_the_history_is_grantable_to_a_role()
    {
        var named = DefaultRoleSeedVersions.BaselinePermissions.Values
            .SelectMany(permissions => permissions)
            .Concat(DefaultRoleSeedVersions.All.SelectMany(increment => increment.AddedPermissions))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(named);

        foreach (var permission in named)
        {
            Assert.True(
                PermissionCatalog.All.Contains(permission),
                $"'{permission}' is not in PermissionCatalog, so no role claim could ever match it.");

            Assert.True(
                PermissionCatalog.IsGrantableToRoles(permission),
                $"'{permission}' cannot be granted to a role (doc 08 §3.1), so a seeder step that tried "
                + "would fail against CK_RoleClaims_NoSecurity on the client's database.");
        }
    }

    /// <summary>Every increment and every baseline entry names a role the seeder actually creates.</summary>
    [Fact]
    public void Every_named_role_is_a_default_role()
    {
        var known = DefaultRoleDefinitions.All
            .Select(definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(
            DefaultRoleSeedVersions.All,
            increment => Assert.Contains(increment.RoleName, known));

        Assert.All(
            DefaultRoleSeedVersions.BaselinePermissions.Keys,
            name => Assert.Contains(name, known));

        // And the other way: a default role with no baseline entry would be reconstructed as an empty set
        // and so would look as if every one of its permissions were a missing increment.
        Assert.All(
            known,
            name => Assert.True(
                DefaultRoleSeedVersions.BaselinePermissions.ContainsKey(name),
                $"'{name}' has no baseline entry, so its whole permission set would read as un-versioned."));
    }

    /// <summary><see cref="DefaultRoleSeedVersions.Since"/> is a strict "above this version" filter.</summary>
    [Fact]
    public void Since_returns_the_increments_above_the_recorded_version_and_no_others()
    {
        Assert.Equal(
            DefaultRoleSeedVersions.All.Count,
            DefaultRoleSeedVersions.Since(DefaultRoleSeedVersions.Baseline).Count);

        Assert.Empty(DefaultRoleSeedVersions.Since(DefaultRoleSeedVersions.Current));
        Assert.Empty(DefaultRoleSeedVersions.Since(DefaultRoleSeedVersions.Current + 1));

        // In version order, because the step applies them in sequence and a later increment may add a
        // permission an earlier one did.
        var versions = DefaultRoleSeedVersions.Since(DefaultRoleSeedVersions.Baseline)
            .Select(increment => increment.Version)
            .ToList();

        Assert.Equal(versions.OrderBy(version => version).ToList(), versions);
    }

    /// <summary>
    /// The three permissions issue #122 names are the ones version 2 grants, so this file is not merely
    /// self-consistent: it says what the issue said.
    /// </summary>
    [Fact]
    public void Version_two_grants_the_three_permissions_issue_122_names()
    {
        var added = DefaultRoleSeedVersions.All
            .Where(increment => increment.Version == 2)
            .SelectMany(increment => increment.AddedPermissions)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(permission => permission, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [Permissions.Content.Create, Permissions.Content.Delete, Permissions.Customers.Edit],
            added);
    }
}
