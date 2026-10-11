using SpencerDenim.Domain.Identity;

namespace SpencerDenim.UnitTests.Domain.Identity;

/// <summary>
/// The rule that stops issue #122 coming back: <b>a permission added to a default role must be given a seed
/// version</b>, or it reaches a fresh database and never an existing one (doc 08 §9, N13). The seeder does not
/// touch a role that already exists, so on the client's live database the only thing that delivers a new
/// permission to Store Manager, Order Staff or Content Editor is an entry in
/// <see cref="DefaultRoleSeedManifest.Introductions"/>.
/// <para>
/// Every failure message here says what to do, because the person reading it is mid-way through a different task
/// (a new admin screen) and has just added a permission.
/// </para>
/// </summary>
public sealed class DefaultRoleSeedManifestTests
{
    // ------------------------------------------------------------------------------------------------------
    // The frozen history. These two blocks are a record of what has SHIPPED, and they are deliberately written
    // out rather than derived from the code under test: a test that computed its expectation from the manifest
    // would pass whatever the manifest said.
    // ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// What the default roles held when they first shipped in W1-08 (#102, 2026-10-05) — the baseline, version 1.
    /// Never edit a list here to make a test pass: a permission added later is a manifest entry, not a line in
    /// this block.
    /// </summary>
    private static readonly Dictionary<string, string[]> Version1 = new(StringComparer.Ordinal)
    {
        ["Store Manager"] =
        [
            Permissions.Dashboard.View,
            Permissions.Orders.View,
            Permissions.Orders.Edit,
            Permissions.Orders.Approve,
            Permissions.Orders.Refund,
            Permissions.Products.View,
            Permissions.Products.Create,
            Permissions.Products.Edit,
            Permissions.Products.Delete,
            Permissions.Categories.View,
            Permissions.Categories.Create,
            Permissions.Categories.Edit,
            Permissions.Categories.Delete,
            Permissions.Inventory.View,
            Permissions.Inventory.Edit,
            Permissions.Customers.View,
            Permissions.Customers.Delete,
            Permissions.Reviews.View,
            Permissions.Reviews.Edit,
            Permissions.Reviews.Approve,
            Permissions.Reviews.Delete,
            Permissions.Promotions.View,
            Permissions.Promotions.Edit,
            Permissions.Content.View,
            Permissions.Content.Edit,
            Permissions.Media.View,
            Permissions.Media.Create,
            Permissions.Media.Edit,
            Permissions.Media.Delete,
            Permissions.Reports.View,
            Permissions.Reports.Export,
            Permissions.Traffic.View,
            Permissions.Newsletter.View,
            Permissions.Newsletter.Export,
            Permissions.Settings.View,
            Permissions.Settings.Edit,
            Permissions.AuditLog.View,
        ],
        ["Order Staff"] =
        [
            Permissions.Dashboard.View,
            Permissions.Orders.View,
            Permissions.Orders.Edit,
            Permissions.Orders.Approve,
            Permissions.Customers.View,
            Permissions.Inventory.View,
        ],
        ["Content Editor"] =
        [
            Permissions.Products.View,
            Permissions.Products.Create,
            Permissions.Products.Edit,
            Permissions.Products.Delete,
            Permissions.Categories.View,
            Permissions.Categories.Create,
            Permissions.Categories.Edit,
            Permissions.Categories.Delete,
            Permissions.Content.View,
            Permissions.Content.Edit,
            Permissions.Media.View,
            Permissions.Media.Create,
            Permissions.Media.Edit,
            Permissions.Media.Delete,
            Permissions.Reviews.View,
            Permissions.Reviews.Edit,
            Permissions.Reviews.Approve,
            Permissions.Reviews.Delete,
        ],
    };

    /// <summary>
    /// What version 2 (W2-11, #127) added on top of the baseline. A shipped version is never edited: the next
    /// permission is version 3.
    /// </summary>
    private static readonly Dictionary<string, string[]> Version2Additions = new(StringComparer.Ordinal)
    {
        ["Store Manager"] = [Permissions.Customers.Edit, Permissions.Content.Create, Permissions.Content.Delete],
        ["Content Editor"] = [Permissions.Content.Create, Permissions.Content.Delete],
    };

    public static TheoryData<string> DefaultRoleNames()
    {
        var data = new TheoryData<string>();
        foreach (var definition in DefaultRoleDefinitions.All)
        {
            data.Add(definition.Name);
        }

        return data;
    }

    // ------------------------------------------------------------------------------------------------------
    // The rule.
    // ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>The test that fails when a permission is added to a default role without a seed version.</b>
    /// Every permission a default role ships has an introduced-in version between the baseline and the current
    /// version, and one that no release introduced must be in the frozen version-1 record — otherwise it is new,
    /// and nothing would ever deliver it to an existing database.
    /// </summary>
    [Theory]
    [MemberData(nameof(DefaultRoleNames))]
    public void Every_permission_a_default_role_holds_has_a_seed_version(string roleName)
    {
        var definition = DefaultRoleDefinitions.All.Single(role => role.Name == roleName);

        var unversioned = new List<string>();

        foreach (var permission in definition.Permissions)
        {
            var version = DefaultRoleSeedManifest.IntroducedIn(roleName, permission);

            Assert.InRange(version, DefaultRoleSeedManifest.BaselineVersion, DefaultRoleSeedManifest.CurrentVersion);

            if (version == DefaultRoleSeedManifest.BaselineVersion && !Version1[roleName].Contains(permission))
            {
                unversioned.Add(permission);
            }
        }

        Assert.True(
            unversioned.Count == 0,
            $"{roleName} ships {string.Join(", ", unversioned.Order(StringComparer.Ordinal))} without a default-role seed version. "
            + "A permission added to DefaultRoleDefinitions reaches a fresh database only: the seeder never touches a role that "
            + "already exists (doc 08 §9, N13), so the client's staff would silently lack it (issue #122). Add a "
            + $"DefaultRolePermissionIntroduction to DefaultRoleSeedManifest.Introductions at version {DefaultRoleSeedManifest.CurrentVersion + 1} "
            + "(the next one) for each. If the role should NOT get it, remove it from DefaultRoleDefinitions instead.");
    }

    /// <summary>The current version is what the highest introduction says, so bumping it cannot be forgotten.</summary>
    [Fact]
    public void The_current_version_is_the_highest_version_in_the_manifest()
    {
        var highest = DefaultRoleSeedManifest.Introductions.Max(introduction => introduction.Version);

        Assert.Equal(highest, DefaultRoleSeedManifest.CurrentVersion);
        Assert.True(DefaultRoleSeedManifest.CurrentVersion > DefaultRoleSeedManifest.BaselineVersion);
    }

    /// <summary>
    /// Versions are consecutive from the baseline, so a version that introduces nothing (or a typo'd 20) is
    /// noticed — an applied version only means something if every number below it was a real release.
    /// </summary>
    [Fact]
    public void Versions_run_from_just_above_the_baseline_to_current_without_a_gap()
    {
        var versions = DefaultRoleSeedManifest.Introductions
            .Select(introduction => introduction.Version)
            .Distinct()
            .Order()
            .ToList();

        Assert.Equal(
            Enumerable.Range(DefaultRoleSeedManifest.BaselineVersion + 1, DefaultRoleSeedManifest.CurrentVersion - DefaultRoleSeedManifest.BaselineVersion),
            versions);
    }

    [Fact]
    public void The_baseline_is_version_one()
    {
        // A database with no recorded version is assumed to be at this one. Changing it changes what the first
        // start on every existing database grants, so it is pinned.
        Assert.Equal(1, DefaultRoleSeedManifest.BaselineVersion);
    }

    /// <summary>An entry that names a permission the role no longer ships would grant something the build does not intend.</summary>
    [Fact]
    public void Every_introduction_names_a_default_role_that_ships_that_permission()
    {
        foreach (var introduction in DefaultRoleSeedManifest.Introductions)
        {
            var role = DefaultRoleDefinitions.All.SingleOrDefault(candidate => candidate.Name == introduction.RoleName);

            Assert.True(role is not null, $"'{introduction.RoleName}' is not a default role.");
            Assert.True(
                role.Permissions.Contains(introduction.Permission),
                $"The manifest introduces {introduction.Permission} to {introduction.RoleName} at version {introduction.Version}, "
                + "but DefaultRoleDefinitions does not give that role the permission. Add it there, or delete the entry.");
            Assert.Contains(introduction.Permission, PermissionCatalog.All);
            Assert.False(string.IsNullOrWhiteSpace(introduction.Origin), "An introduction says which task added it.");
        }
    }

    [Fact]
    public void A_role_is_introduced_to_the_same_permission_once()
    {
        var duplicates = DefaultRoleSeedManifest.Introductions
            .GroupBy(introduction => (introduction.RoleName, introduction.Permission))
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key.RoleName}/{group.Key.Permission}")
            .ToList();

        Assert.Empty(duplicates);
    }

    /// <summary>
    /// Nothing is introduced at the baseline: a version-1 permission is one that no release added, and listing it
    /// would let an edit of the shipped baseline pass for a new release.
    /// </summary>
    [Fact]
    public void No_introduction_is_at_or_below_the_baseline()
    {
        Assert.All(
            DefaultRoleSeedManifest.Introductions,
            introduction => Assert.True(introduction.Version > DefaultRoleSeedManifest.BaselineVersion, $"{introduction.RoleName}/{introduction.Permission}"));
    }

    /// <summary>
    /// A permission that can never be granted to a role (<c>Settings.Security</c>, <c>CK_RoleClaims_NoSecurity</c>)
    /// must never be in the manifest: the database would refuse the insert and the whole start-up step with it.
    /// </summary>
    [Fact]
    public void Nothing_that_cannot_be_granted_to_a_role_is_introduced()
    {
        Assert.All(
            DefaultRoleSeedManifest.Introductions,
            introduction => Assert.True(PermissionCatalog.IsGrantableToRoles(introduction.Permission), introduction.Permission));
    }

    // ------------------------------------------------------------------------------------------------------
    // The history, pinned.
    // ------------------------------------------------------------------------------------------------------

    /// <summary>Version 2 is exactly the five grants W2-11 added. A shipped version is never edited.</summary>
    [Theory]
    [MemberData(nameof(DefaultRoleNames))]
    public void Version_2_is_exactly_what_w2_11_added(string roleName)
    {
        var expected = Version2Additions.GetValueOrDefault(roleName) ?? [];

        var actual = DefaultRoleSeedManifest.Introductions
            .Where(introduction => introduction.RoleName == roleName && introduction.Version == 2)
            .Select(introduction => introduction.Permission);

        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Order Staff gains nothing in W2-11: doc 08 §9 gives it <c>Customers.View</c> and nothing else from that
    /// module, and the manifest must not widen a role the document narrowed.
    /// </summary>
    [Fact]
    public void Order_Staff_is_introduced_to_nothing()
    {
        Assert.Empty(DefaultRoleSeedManifest.IntroducedAfter(DefaultRoleDefinitions.OrderStaff.Name, 0));
    }

    // ------------------------------------------------------------------------------------------------------
    // The lookups the start-up step relies on.
    // ------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_permission_no_release_added_is_a_baseline_permission()
    {
        Assert.Equal(
            DefaultRoleSeedManifest.BaselineVersion,
            DefaultRoleSeedManifest.IntroducedIn(DefaultRoleDefinitions.StoreManager.Name, Permissions.Orders.Refund));
    }

    [Fact]
    public void A_permission_a_release_added_reports_that_release()
    {
        Assert.Equal(2, DefaultRoleSeedManifest.IntroducedIn("Store Manager", Permissions.Customers.Edit));
        Assert.Equal(2, DefaultRoleSeedManifest.IntroducedIn("Content Editor", Permissions.Content.Create));
    }

    [Fact]
    public void A_permission_is_introduced_to_a_role_and_not_to_every_role_that_holds_it()
    {
        // Content.Create was added to Content Editor and to Store Manager in the same release, but Order Staff
        // never held it and so is not "introduced" to it.
        Assert.Equal(
            DefaultRoleSeedManifest.BaselineVersion,
            DefaultRoleSeedManifest.IntroducedIn("Order Staff", Permissions.Content.Create));
    }

    [Fact]
    public void Role_names_match_the_way_the_database_does()
    {
        // identity.Roles is case-insensitive (Latin1_General_100_CI_AS_SC).
        Assert.Equal(
            DefaultRoleSeedManifest.IntroducedAfter("Store Manager", 1).Count,
            DefaultRoleSeedManifest.IntroducedAfter("store manager", 1).Count);
    }

    [Fact]
    public void A_database_at_the_baseline_is_owed_every_introduction_and_one_at_current_is_owed_none()
    {
        foreach (var definition in DefaultRoleDefinitions.All)
        {
            Assert.Equal(
                DefaultRoleSeedManifest.Introductions.Count(introduction => introduction.RoleName == definition.Name),
                DefaultRoleSeedManifest.IntroducedAfter(definition.Name, DefaultRoleSeedManifest.BaselineVersion).Count);

            Assert.Empty(DefaultRoleSeedManifest.IntroducedAfter(definition.Name, DefaultRoleSeedManifest.CurrentVersion));
        }
    }
}
