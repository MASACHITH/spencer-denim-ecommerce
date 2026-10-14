namespace SpencerDenim.Domain.Identity;

/// <summary>
/// Permission code constants in the form <c>Module.Action</c> (doc 03 §3a). Stored as
/// <c>identity.RoleClaims.ClaimValue</c> with <c>ClaimType = "perm"</c> (doc 08 §3.1).
/// The base grid is modules × {View, Create, Edit, Delete, Approve, Export}. A handful of modules also carry a
/// special permission outside that grid (e.g. <see cref="Orders.Refund"/>, <see cref="Users.Manage"/>).
/// </summary>
/// <remarks>
/// This is the authoritative catalogue of valid permission values (doc 08 §3.1: "Code constants are the
/// catalogue of valid values; a startup check logs unknown values" — see <see cref="PermissionCatalog"/>).
/// Only modules and actions actually needed as of this slice are declared; more are added as each admin
/// feature is built, never invented ad hoc in a handler.
/// </remarks>
public static class Permissions
{
    public static class Dashboard
    {
        public const string View = "Dashboard.View";
    }

    public static class Orders
    {
        public const string View = "Orders.View";
        public const string Edit = "Orders.Edit";
        public const string Approve = "Orders.Approve";

        /// <summary>Never granted to Order Staff (doc 08 §3.1 "Special permissions").</summary>
        public const string Refund = "Orders.Refund";
    }

    public static class Products
    {
        public const string View = "Products.View";
        public const string Create = "Products.Create";
        public const string Edit = "Products.Edit";
        public const string Delete = "Products.Delete";
    }

    public static class Categories
    {
        public const string View = "Categories.View";

        /// <summary>
        /// Create a category (ADM-11, W1-12). Departments are deliberately absent: the nav is fixed
        /// (Q5, doc 08 §3.2), so there is no create or delete for one — only <see cref="Edit"/>.
        /// </summary>
        public const string Create = "Categories.Create";

        /// <summary>
        /// Edit a category or a department, reorder siblings, move a category to another parent and
        /// activate or deactivate one (ADM-11). Departments sit here by doc 11 §7 G-02's stated
        /// default, which doc 08 §3.1 does not name a module for.
        /// </summary>
        public const string Edit = "Categories.Edit";

        /// <summary>
        /// Remove a category (ADM-11: "create / edit / reorder / <b>remove</b>"). A soft delete, blocked
        /// while the category has live products or child categories (doc 08 §3.2, doc 11 P1 step 5).
        /// </summary>
        public const string Delete = "Categories.Delete";
    }

    public static class Inventory
    {
        public const string View = "Inventory.View";
        public const string Edit = "Inventory.Edit";
    }

    public static class Customers
    {
        public const string View = "Customers.View";

        /// <summary>
        /// Added for GLB-10 (W2-11): doc 08 §3.7's G-06 table and doc 11 P15's permission table both put a
        /// contact enquiry's <i>status</i> behind <c>Customers.Edit</c> — moving one to Read, Replied or
        /// Spam — and doc 08 §9 grants Store Manager every <c>Module.Action</c>, so the value has to
        /// exist. It is the Customers module and not <c>Content.*</c> because an enquiry is customer data
        /// that happens to arrive through a content page.
        /// </summary>
        public const string Edit = "Customers.Edit";

        public const string Delete = "Customers.Delete";
    }

    public static class Reviews
    {
        public const string View = "Reviews.View";

        /// <summary>
        /// Added for W1-08 (issue #8): doc 08 §9's default-role table grants Content Editor
        /// "Reviews.View/Edit/Approve/Delete", so Edit and Delete need to exist as real permission values —
        /// this module previously declared only View/Approve.
        /// </summary>
        public const string Edit = "Reviews.Edit";

        public const string Approve = "Reviews.Approve";

        /// <summary>See <see cref="Edit"/>.</summary>
        public const string Delete = "Reviews.Delete";
    }

    public static class Promotions
    {
        public const string View = "Promotions.View";

        /// <summary>
        /// Add a promo code or a sale campaign (ADM-13 / ADM-14, W3-05). Added here because doc 11 P3's
        /// own permission table names <c>Promotions.View/Create/Edit</c> for both the promo-code and the
        /// sale-campaign screens, and doc 08 §9 grants Store Manager every <c>Module.Action</c> bar
        /// <c>Users.*</c>, <c>Settings.Payments</c> and <c>Settings.Security</c> — so the value has to
        /// exist rather than a create route borrowing <see cref="Edit"/>.
        /// </summary>
        public const string Create = "Promotions.Create";

        public const string Edit = "Promotions.Edit";

        /// <summary>
        /// Doc 11 P3's permission table: "Remove campaign or code — <c>Promotions.Delete</c>". A soft
        /// delete, which releases the code string for reuse and leaves placed orders alone (doc 08 §3.6
        /// DB19). Both halves exist here rather than only the one this build happened to need first.
        /// </summary>
        public const string Delete = "Promotions.Delete";
    }

    public static class Content
    {
        public const string View = "Content.View";

        /// <summary>
        /// Create a <b>custom</b> content page (GLB-10, W2-11). The six system pages are created by the
        /// seeder and can be neither created nor deleted, so this permission only ever adds a seventh.
        /// </summary>
        public const string Create = "Content.Create";

        public const string Edit = "Content.Edit";

        /// <summary>
        /// Remove a custom content page (GLB-10, doc 11 P13: "System pages cannot be deleted"). A soft
        /// delete, which releases the slug it held. Doc 11 P13's own permission table names
        /// <c>Content.View/Create/Edit/Delete</c> for these screens, so both halves exist here rather than
        /// only the two this build happened to need first.
        /// </summary>
        public const string Delete = "Content.Delete";
    }

    public static class Media
    {
        public const string View = "Media.View";

        /// <summary>Upload a new asset to the library (ADM-18, W1-04).</summary>
        public const string Create = "Media.Create";

        /// <summary>Edit an asset's alt text and title (ADM-18).</summary>
        public const string Edit = "Media.Edit";

        public const string Delete = "Media.Delete";
    }

    public static class Reports
    {
        public const string View = "Reports.View";
        public const string Export = "Reports.Export";
    }

    public static class Traffic
    {
        public const string View = "Traffic.View";
    }

    public static class Newsletter
    {
        public const string View = "Newsletter.View";
        public const string Export = "Newsletter.Export";
    }

    public static class Users
    {
        public const string View = "Users.View";

        /// <summary>
        /// Manage staff users and role assignments, subject to the subset rule (doc 08 §3.1 DB4;
        /// see <see cref="RoleAssignmentPolicy"/>).
        /// </summary>
        public const string Manage = "Users.Manage";
    }

    public static class Settings
    {
        /// <summary>
        /// Added for W1-08 (issue #8): doc 08 §9's default-role table grants Store Manager
        /// "Settings.View/Edit", so View needs to exist as a real permission value alongside the
        /// already-declared Edit.
        /// </summary>
        public const string View = "Settings.View";

        public const string Edit = "Settings.Edit";

        /// <summary>Bank details, payment providers, PayPal and FX (doc 08 §3.1 DB2).</summary>
        public const string Payments = "Settings.Payments";

        /// <summary>
        /// GrantsAll-only. Email/SMTP, security and legal-retention settings. Never grantable to any role
        /// (<c>CK_RoleClaims_NoSecurity</c>, doc 08 §3.1) — see <see cref="PermissionCatalog.IsGrantableToRoles"/>.
        /// </summary>
        public const string Security = "Settings.Security";
    }

    public static class AuditLog
    {
        public const string View = "AuditLog.View";
    }
}
