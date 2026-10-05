using SpencerDenim.Domain.Identity;

namespace SpencerDenim.UnitTests.Domain.Identity;

/// <summary>Doc 08 §9 "Default roles (RoleClaims perm)" — the exact permission list per default role (W1-08, issue #8).</summary>
public sealed class DefaultRoleDefinitionsTests
{
    [Fact]
    public void Every_permission_named_by_every_default_role_is_a_real_declared_permission()
    {
        foreach (var role in DefaultRoleDefinitions.All)
        {
            foreach (var permission in role.Permissions)
            {
                Assert.Contains(permission, PermissionCatalog.All);
            }
        }
    }

    [Fact]
    public void Settings_Security_is_never_granted_to_any_default_role()
    {
        // CK_RoleClaims_NoSecurity (doc 08 §3.1): Settings.Security is GrantsAll-only, never grantable to a role.
        foreach (var role in DefaultRoleDefinitions.All)
        {
            Assert.DoesNotContain(Permissions.Settings.Security, role.Permissions);
        }
    }

    [Fact]
    public void Store_Manager_has_every_permission_except_UsersStar_and_the_GrantsAll_only_settings()
    {
        var storeManager = DefaultRoleDefinitions.StoreManager;

        foreach (var permission in PermissionCatalog.All)
        {
            var shouldBeExcluded = permission is Permissions.Users.View or Permissions.Users.Manage
                or Permissions.Settings.Payments or Permissions.Settings.Security;

            Assert.Equal(!shouldBeExcluded, storeManager.Permissions.Contains(permission));
        }

        // Doc 08 §9 explicitly calls these out as included, so a regression that dropped one would not be
        // caught by "everything except the four exclusions" alone if the catalogue itself ever shrank.
        Assert.Contains(Permissions.Orders.Refund, storeManager.Permissions);
        Assert.Contains(Permissions.Settings.View, storeManager.Permissions);
        Assert.Contains(Permissions.Settings.Edit, storeManager.Permissions);
        Assert.Contains(Permissions.AuditLog.View, storeManager.Permissions);
        Assert.Contains(Permissions.Customers.Delete, storeManager.Permissions);
    }

    [Fact]
    public void Order_Staff_has_exactly_the_documented_set_and_never_Orders_Refund()
    {
        var orderStaff = DefaultRoleDefinitions.OrderStaff;

        var expected = new HashSet<string>
        {
            Permissions.Dashboard.View,
            Permissions.Orders.View,
            Permissions.Orders.Edit,
            Permissions.Orders.Approve,
            Permissions.Customers.View,
            Permissions.Inventory.View,
        };

        Assert.Equal(expected, orderStaff.Permissions);
        Assert.DoesNotContain(Permissions.Orders.Refund, orderStaff.Permissions);
    }

    [Fact]
    public void Content_Editor_has_exactly_the_documented_set()
    {
        var contentEditor = DefaultRoleDefinitions.ContentEditor;

        var expected = new HashSet<string>
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
        };

        Assert.Equal(expected, contentEditor.Permissions);
    }

    [Fact]
    public void All_lists_exactly_the_three_non_super_admin_default_roles_in_doc_order()
    {
        Assert.Equal(
            ["Store Manager", "Order Staff", "Content Editor"],
            DefaultRoleDefinitions.All.Select(r => r.Name));
    }
}
