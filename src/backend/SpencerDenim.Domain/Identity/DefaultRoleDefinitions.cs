namespace SpencerDenim.Domain.Identity;

/// <summary>
/// The three default staff roles doc 08 §9's "Default roles (RoleClaims <c>perm</c>)" table asks for, beyond
/// the separately-seeded Super Admin (<see cref="SpencerDenim.Domain.Identity"/> has no framework dependency,
/// so this is where the permission *lists* live; the actual seeding — creating <c>identity.Roles</c> /
/// <c>RoleClaims</c> rows — is Infrastructure's job, in <c>Seeding/AdminSeedHostedService</c>).
/// <para>
/// Framework-free and reflection-free on purpose: every permission named here is a compile-time reference to a
/// <see cref="Permissions"/> constant, so a typo or a renamed permission is a build break, not a silent gap in
/// a seeded role. This is also why the lists are hand-written rather than computed by excluding a few constants
/// from <see cref="PermissionCatalog.All"/> — "every permission except these four" would silently start
/// granting a fifth module's permissions to Store Manager the day someone adds one, without anyone deciding
/// that on purpose.
/// </para>
/// </summary>
public static class DefaultRoleDefinitions
{
    // Adding a permission to the catalogue means asking, for each of these three roles, whether doc 08 section 9's
    // wording ("Module.*", "every Module.Action except ...") now covers it. These lists are written out rather than
    // computed by exclusion on purpose (a new permission must be granted deliberately, never by default), which is
    // exactly why Categories.Create/Delete and Media.Create/Edit, added by the catalogue admin work (W1-12, W1-04),
    // had to be added here by hand. The unit test for Store Manager walks the whole catalogue and fails on a miss.
    //
    // **And a permission added here reaches a database that already exists only through
    // `DefaultRoleSeedVersions`** (issue #122, doc 08 §9 / N13): the seeder creates a role only when the role is
    // absent, so adding a permission below and nothing else leaves every existing database without it. Declare it
    // as a new increment there. `DefaultRoleSeedVersionsTests` fails the build if you do not.

    /// <summary>Doc 08 §9's "Store Manager". The role name is the seeder's only key, so it is a constant.</summary>
    public const string StoreManagerName = "Store Manager";

    /// <summary>Doc 08 §9's "Order Staff".</summary>
    public const string OrderStaffName = "Order Staff";

    /// <summary>Doc 08 §9's "Content Editor".</summary>
    public const string ContentEditorName = "Content Editor";

    /// <summary>
    /// Doc 08 §9: "Every Module.Action except Users.*, Settings.Payments and Settings.Security (the last
    /// can't be granted to any role). Includes Orders.Refund, Settings.View/Edit, AuditLog.View,
    /// Customers.Delete."
    /// </summary>
    public static readonly RoleDefinition StoreManager = new(
        Name: StoreManagerName,
        Description: "Every module except Users management and payment/security settings (doc 08 §9).",
        Permissions: new HashSet<string>
        {
            Identity.Permissions.Dashboard.View,
            Identity.Permissions.Orders.View,
            Identity.Permissions.Orders.Edit,
            Identity.Permissions.Orders.Approve,
            Identity.Permissions.Orders.Refund,
            Identity.Permissions.Products.View,
            Identity.Permissions.Products.Create,
            Identity.Permissions.Products.Edit,
            Identity.Permissions.Products.Delete,
            Identity.Permissions.Categories.View,
            Identity.Permissions.Categories.Create,
            Identity.Permissions.Categories.Edit,
            Identity.Permissions.Categories.Delete,
            Identity.Permissions.Inventory.View,
            Identity.Permissions.Inventory.Edit,
            Identity.Permissions.Customers.View,
            Identity.Permissions.Customers.Edit,
            Identity.Permissions.Customers.Delete,
            Identity.Permissions.Reviews.View,
            Identity.Permissions.Reviews.Edit,
            Identity.Permissions.Reviews.Approve,
            Identity.Permissions.Reviews.Delete,
            Identity.Permissions.Promotions.View,
            Identity.Permissions.Promotions.Edit,
            Identity.Permissions.Content.View,
            Identity.Permissions.Content.Create,
            Identity.Permissions.Content.Edit,
            Identity.Permissions.Content.Delete,
            Identity.Permissions.Media.View,
            Identity.Permissions.Media.Create,
            Identity.Permissions.Media.Edit,
            Identity.Permissions.Media.Delete,
            Identity.Permissions.Reports.View,
            Identity.Permissions.Reports.Export,
            Identity.Permissions.Traffic.View,
            Identity.Permissions.Newsletter.View,
            Identity.Permissions.Newsletter.Export,
            Identity.Permissions.Settings.View,
            Identity.Permissions.Settings.Edit,
            Identity.Permissions.AuditLog.View,
        });

    /// <summary>Doc 08 §9: "Dashboard.View, Orders.View/Edit/Approve, Customers.View, Inventory.View. No Orders.Refund".</summary>
    public static readonly RoleDefinition OrderStaff = new(
        Name: OrderStaffName,
        Description: "Order processing without refunds (doc 08 §9).",
        Permissions: new HashSet<string>
        {
            Identity.Permissions.Dashboard.View,
            Identity.Permissions.Orders.View,
            Identity.Permissions.Orders.Edit,
            Identity.Permissions.Orders.Approve,
            Identity.Permissions.Customers.View,
            Identity.Permissions.Inventory.View,
        });

    /// <summary>Doc 08 §9: "Products.*, Categories.*, Content.*, Media.*, Reviews.View/Edit/Approve/Delete".</summary>
    public static readonly RoleDefinition ContentEditor = new(
        Name: ContentEditorName,
        Description: "Catalogue content, media and reviews (doc 08 §9).",
        Permissions: new HashSet<string>
        {
            Identity.Permissions.Products.View,
            Identity.Permissions.Products.Create,
            Identity.Permissions.Products.Edit,
            Identity.Permissions.Products.Delete,
            Identity.Permissions.Categories.View,
            Identity.Permissions.Categories.Create,
            Identity.Permissions.Categories.Edit,
            Identity.Permissions.Categories.Delete,
            Identity.Permissions.Content.View,
            Identity.Permissions.Content.Create,
            Identity.Permissions.Content.Edit,
            Identity.Permissions.Content.Delete,
            Identity.Permissions.Media.View,
            Identity.Permissions.Media.Create,
            Identity.Permissions.Media.Edit,
            Identity.Permissions.Media.Delete,
            Identity.Permissions.Reviews.View,
            Identity.Permissions.Reviews.Edit,
            Identity.Permissions.Reviews.Approve,
            Identity.Permissions.Reviews.Delete,
        });

    /// <summary>All three, in the order doc 08 §9 lists them (Super Admin is seeded separately, not here).</summary>
    public static IReadOnlyList<RoleDefinition> All { get; } = [StoreManager, OrderStaff, ContentEditor];
}

/// <summary>One default role's name, description and permission set, ready for an idempotent-by-name seeder.</summary>
public sealed record RoleDefinition(string Name, string Description, IReadOnlySet<string> Permissions);
