using SpencerDenim.Domain.Identity;

namespace SpencerDenim.UnitTests.Domain.Identity;

/// <summary>Doc 08 §3.1 DB4 "RBAC safety rules", especially the N3 subset rule for <c>Users.Manage</c>.</summary>
public sealed class RoleAssignmentPolicyTests
{
    private static readonly HashSet<string> OrderStaffPermissions = [Permissions.Orders.View, Permissions.Orders.Edit];
    private static readonly HashSet<string> ContentEditorPermissions = [Permissions.Content.View, Permissions.Content.Edit];

    [Fact]
    public void CanManageStaffUser_allows_when_target_permissions_are_a_subset_before_and_after()
    {
        var actorPermissions = new HashSet<string>(OrderStaffPermissions) { Permissions.Users.Manage, Permissions.Content.View };

        var canManage = RoleAssignmentPolicy.CanManageStaffUser(
            actorHasGrantsAll: false,
            actorHasUsersManage: true,
            actorEffectivePermissions: actorPermissions,
            targetHasGrantsAllBefore: false,
            targetPermissionsBefore: OrderStaffPermissions,
            targetHasGrantsAllAfter: false,
            targetPermissionsAfter: new HashSet<string> { Permissions.Content.View });

        Assert.True(canManage);
    }

    [Fact]
    public void CanManageStaffUser_denies_when_the_after_state_exceeds_the_actors_own_permissions()
    {
        var actorPermissions = new HashSet<string>(OrderStaffPermissions) { Permissions.Users.Manage };

        var canManage = RoleAssignmentPolicy.CanManageStaffUser(
            actorHasGrantsAll: false,
            actorHasUsersManage: true,
            actorEffectivePermissions: actorPermissions,
            targetHasGrantsAllBefore: false,
            targetPermissionsBefore: OrderStaffPermissions,
            targetHasGrantsAllAfter: false,
            targetPermissionsAfter: ContentEditorPermissions);

        Assert.False(canManage);
    }

    [Fact]
    public void CanManageStaffUser_denies_a_grants_all_target_even_to_a_users_manage_holder()
    {
        var canManage = RoleAssignmentPolicy.CanManageStaffUser(
            actorHasGrantsAll: false,
            actorHasUsersManage: true,
            actorEffectivePermissions: PermissionCatalog.All,
            targetHasGrantsAllBefore: true,
            targetPermissionsBefore: new HashSet<string>(),
            targetHasGrantsAllAfter: false,
            targetPermissionsAfter: new HashSet<string>());

        Assert.False(canManage);
    }

    [Fact]
    public void CanManageStaffUser_allows_grants_all_actor_regardless_of_target()
    {
        var canManage = RoleAssignmentPolicy.CanManageStaffUser(
            actorHasGrantsAll: true,
            actorHasUsersManage: false,
            actorEffectivePermissions: new HashSet<string>(),
            targetHasGrantsAllBefore: true,
            targetPermissionsBefore: new HashSet<string>(),
            targetHasGrantsAllAfter: true,
            targetPermissionsAfter: new HashSet<string>());

        Assert.True(canManage);
    }

    [Fact]
    public void Only_grants_all_can_change_a_staff_email()
    {
        Assert.True(RoleAssignmentPolicy.CanChangeStaffEmail(actorHasGrantsAll: true));
        Assert.False(RoleAssignmentPolicy.CanChangeStaffEmail(actorHasGrantsAll: false));
    }

    [Fact]
    public void Self_role_edit_is_always_blocked()
    {
        Assert.True(RoleAssignmentPolicy.IsSelfRoleEditBlocked(actorUserId: 7, targetUserId: 7));
        Assert.False(RoleAssignmentPolicy.IsSelfRoleEditBlocked(actorUserId: 7, targetUserId: 8));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void Last_super_admin_guard_requires_at_least_one_other_active_holder(int othersExcludingTarget, bool expected)
    {
        Assert.Equal(expected, RoleAssignmentPolicy.CanReduceActiveGrantsAllHolders(othersExcludingTarget));
    }

    [Fact]
    public void Settings_security_can_never_be_granted_to_a_role()
    {
        Assert.False(PermissionCatalog.IsGrantableToRoles(Permissions.Settings.Security));
        Assert.True(PermissionCatalog.IsGrantableToRoles(Permissions.Settings.Payments));
    }

    [Fact]
    public void Permission_catalog_contains_every_declared_constant()
    {
        Assert.Contains(Permissions.Orders.Refund, PermissionCatalog.All);
        Assert.Contains(Permissions.Users.Manage, PermissionCatalog.All);
        Assert.Contains(Permissions.Settings.Security, PermissionCatalog.All);
    }
}
