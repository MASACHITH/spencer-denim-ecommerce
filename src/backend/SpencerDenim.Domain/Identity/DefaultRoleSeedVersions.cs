namespace SpencerDenim.Domain.Identity;

/// <summary>
/// One version's worth of permissions <b>added</b> to one default role since the previous version
/// (doc 08 §9: "A later permission reaches default roles only through an explicit, idempotent, versioned
/// seeder step (recorded in the setting <c>Seed.Version</c>), never by overwriting admin edits (N13)").
/// </summary>
/// <param name="Version">
/// The <see cref="DefaultRoleSeedVersions"/> version that introduced these permissions. A database whose
/// recorded <c>Seed.Version</c> is below this number has never been offered them.
/// </param>
/// <param name="RoleName">The default role's name, as <see cref="DefaultRoleDefinitions"/> spells it.</param>
/// <param name="AddedPermissions">
/// The permissions this version adds to that role, and <b>only</b> the additions. Never the role's whole
/// set: a step that applied the whole set would reinstate a permission the client deliberately removed,
/// which is the one thing N13 forbids.
/// </param>
public sealed record DefaultRoleSeedIncrement(int Version, string RoleName, IReadOnlySet<string> AddedPermissions);

/// <summary>
/// The version history of <see cref="DefaultRoleDefinitions"/>, which is what makes "a permission added
/// since this database was seeded" a decidable question (issue #122, doc 08 §9 / N13).
/// <para>
/// <b>The problem it solves.</b> The seeder creates a default role only when the role is absent, so a
/// permission added to a role's shipped definition after a database was created never reaches that
/// database. On a fresh database the role is created complete; on an existing one a Store Manager simply
/// does not have the new permission, with no error and no log line.
/// </para>
/// <para>
/// <b>Why only the additions are safe, and a reconciliation is not.</b> In general you cannot distinguish
/// "the client removed this permission from the role" from "the role never had it" — both are an absent
/// <c>RoleClaims</c> row. But a permission value that is <i>new in this version</i> cannot have been
/// removed by anyone whose database predates the version, because it did not exist for them to remove. So
/// a step that applies only the increments above a database's recorded version is safe by construction,
/// and no step here ever removes a claim or compares a role against its whole shipped definition. Anything
/// that reset a role to the shipped defaults would silently undo the client's own edits.
/// </para>
/// <para>
/// <b><see cref="BaselinePermissions"/> is frozen and <see cref="All"/> grows.</b> Together they must
/// reproduce <see cref="DefaultRoleDefinitions"/> exactly, and a unit test asserts it — so a permission
/// added to a role's definition without a matching increment fails the build rather than quietly becoming
/// the next instance of #122. That test is the part that stops this recurring.
/// </para>
/// </summary>
public static class DefaultRoleSeedVersions
{
    /// <summary>
    /// The version of the first released definitions, and what a database with no recorded
    /// <c>Seed.Version</c> is assumed to hold.
    /// <para>
    /// <b>The assumption is sound only because no database predates the increments below.</b> A database
    /// with no marker could in principle be one seeded at the current version whose client then removed a
    /// permission — and this step would put it back. That cannot have happened here: nothing has been
    /// deployed, the three permissions of version 2 were added on 2026-10-07 (commit 31f3b27), and the
    /// only databases without a marker are development and test ones. Every database created from this
    /// version on carries a marker, so the ambiguity does not recur.
    /// </para>
    /// </summary>
    public const int Baseline = 1;

    /// <summary>
    /// The version this build ships. Recorded in <c>ops.Settings</c> under <c>Seed.Version</c> once every
    /// increment up to it has been offered to the default roles that exist.
    /// </summary>
    public const int Current = 3;

    /// <summary>
    /// Every increment, in version order.
    /// <para>
    /// <b>A released increment is never edited.</b> Its whole meaning is "these permissions were added in
    /// version N", and changing one would change what a database at version N − 1 is told it is missing.
    /// A correction is a new increment.
    /// </para>
    /// </summary>
    public static IReadOnlyList<DefaultRoleSeedIncrement> All { get; } =
    [
        // Version 2 — W2-11 / GLB-10 (commit 31f3b27, 2026-10-07), which is the diff of this file between
        // 4a32705 and 31f3b27: three permission values across two roles, five role-permission pairs.
        // `Customers.Edit` exists because a contact enquiry's status sits behind it (doc 08 §3.7 G-06) and
        // doc 08 §9 grants Store Manager every Module.Action bar the three it names; `Content.Create` and
        // `Content.Delete` because §9 grants both roles `Content.*`.
        new(
            Version: 2,
            RoleName: DefaultRoleDefinitions.StoreManagerName,
            AddedPermissions: new HashSet<string>(StringComparer.Ordinal)
            {
                Permissions.Customers.Edit,
                Permissions.Content.Create,
                Permissions.Content.Delete,
            }),

        new(
            Version: 2,
            RoleName: DefaultRoleDefinitions.ContentEditorName,
            AddedPermissions: new HashSet<string>(StringComparer.Ordinal)
            {
                Permissions.Content.Create,
                Permissions.Content.Delete,
            }),

        // Version 3 — W3-05 / ADM-13, the promo-code screen. `Promotions.Create` and `Promotions.Delete`
        // did not exist until this task: doc 11 P3's permission table names them for the promo-code and
        // sale-campaign screens, and doc 08 §9 grants Store Manager every Module.Action bar `Users.*`,
        // `Settings.Payments` and `Settings.Security`. Content Editor gets neither, because §9 grants that
        // role `Products.*`, `Categories.*`, `Content.*`, `Media.*` and four `Reviews.*` and no promotions.
        new(
            Version: 3,
            RoleName: DefaultRoleDefinitions.StoreManagerName,
            AddedPermissions: new HashSet<string>(StringComparer.Ordinal)
            {
                Permissions.Promotions.Create,
                Permissions.Promotions.Delete,
            }),
    ];

    /// <summary>
    /// What each default role was created with in version <see cref="Baseline"/> — the first released
    /// definitions, from commit 4a32705 (W1-08).
    /// <para>
    /// <b>Frozen. Never edit a set here.</b> It is a record of what some database somewhere already holds,
    /// not a statement about what a role should hold today; <see cref="DefaultRoleDefinitions"/> is that.
    /// A new permission belongs in a new <see cref="DefaultRoleSeedIncrement"/>, and the convention test
    /// over these two is what refuses the alternative.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> BaselinePermissions { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [DefaultRoleDefinitions.StoreManagerName] = new HashSet<string>(StringComparer.Ordinal)
            {
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
            },

            [DefaultRoleDefinitions.OrderStaffName] = new HashSet<string>(StringComparer.Ordinal)
            {
                Permissions.Dashboard.View,
                Permissions.Orders.View,
                Permissions.Orders.Edit,
                Permissions.Orders.Approve,
                Permissions.Customers.View,
                Permissions.Inventory.View,
            },

            [DefaultRoleDefinitions.ContentEditorName] = new HashSet<string>(StringComparer.Ordinal)
            {
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
            },
        };

    /// <summary>
    /// The increments a database at <paramref name="recordedVersion"/> has never been offered, in version
    /// order. Empty when the database is at or above <see cref="Current"/>.
    /// </summary>
    public static IReadOnlyList<DefaultRoleSeedIncrement> Since(int recordedVersion) =>
        All.Where(increment => increment.Version > recordedVersion)
            .OrderBy(increment => increment.Version)
            .ToList();

    /// <summary>
    /// What <see cref="BaselinePermissions"/> plus every increment says a role of
    /// <paramref name="roleName"/> seeded at <see cref="Baseline"/> and upgraded to
    /// <see cref="Current"/> would hold. Used only by the convention test, which compares it with
    /// <see cref="DefaultRoleDefinitions"/>.
    /// </summary>
    public static IReadOnlySet<string> ReconstructCurrent(string roleName)
    {
        var permissions = BaselinePermissions.TryGetValue(roleName, out var baseline)
            ? new HashSet<string>(baseline, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        foreach (var increment in All.Where(candidate =>
            string.Equals(candidate.RoleName, roleName, StringComparison.Ordinal)))
        {
            permissions.UnionWith(increment.AddedPermissions);
        }

        return permissions;
    }
}
