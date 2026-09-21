using SpencerDenim.Domain.Identity;

namespace SpencerDenim.UnitTests.Domain.Identity;

/// <summary>Doc 08 §3.1 DB4 "RBAC safety rules", especially the N3 subset rule for <c>Users.Manage</c>.</summary>
public sealed class RoleAssignmentPolicyTests
{
    private static readonly HashSet<string> OrderStaffPermissions = [Permissions.Orders.View, Permissions.Orders.Edit];
    private static readonly HashSet<string> ContentEditorPermissions = [Permissions.Content.View, Permissions.Content.Edit];

    [Fact]
    public void GrantsAll_actor_can_assign_any_non_grants_all_role()
    {
        var canAssign = RoleAssignmentPolicy.CanAssignRole(
            actorHasGrantsAll: true,
            actorHasUsersManage: false,
            actorEffectivePermissions: new HashSet<string>(),
            roleGrantsAllPermissions: false,
            rolePermissions: ContentEditorPermissions);

        Assert.True(canAssign);
    }

    [Fact]
    public void GrantsAll_actor_can_assign_the_grants_all_role()
    {
        var canAssign = RoleAssignmentPolicy.CanAssignRole(
            actorHasGrantsAll: true,
            actorHasUsersManage: false,
            actorEffectivePermissions: new HashSet<string>(),
            roleGrantsAllPermissions: true,
            rolePermissions: new HashSet<string>());

        Assert.True(canAssign);
    }

    [Fact]
    public void Non_grants_all_actor_can_never_assign_the_grants_all_role_even_with_users_manage()
    {
        var canAssign = RoleAssignmentPolicy.CanAssignRole(
            actorHasGrantsAll: false,
            actorHasUsersManage: true,
            actorEffectivePermissions: new HashSet<string> { Permissions.Users.Manage },
            roleGrantsAllPermissions: true,
            rolePermissions: new HashSet<string>());

        Assert.False(canAssign);
    }

    [Fact]
    public void UsersManage_holder_can_assign_a_role_that_is_a_subset_of_their_own_permissions()
    {
        var actorPermissions = new HashSet<string>(OrderStaffPermissions) { Permissions.Users.Manage };

        var canAssign = RoleAssignmentPolicy.CanAssignRole(
            actorHasGrantsAll: false,
            actorHasUsersManage: true,
            actorEffectivePermissions: actorPermissions,
            roleGrantsAllPermissions: false,
            rolePermissions: OrderStaffPermissions);

        Assert.True(canAssign);
    }

    [Fact]
    public void UsersManage_holder_cannot_assign_a_role_that_grants_a_permission_they_do_not_hold()
    {
        var actorPermissions = new HashSet<string>(OrderStaffPermissions) { Permissions.Users.Manage };

        // ContentEditorPermissions is not a subset of the actor's own permissions.
        var canAssign = RoleAssignmentPolicy.CanAssignRole(
            actorHasGrantsAll: false,
            actorHasUsersManage: true,
            actorEffectivePermissions: actorPermissions,
            roleGrantsAllPermissions: false,
            rolePermissions: ContentEditorPermissions);

        Assert.False(canAssign);
    }

    [Fact]
    public void Actor_without_UsersManage_cannot_assign_any_role()
    {
        var canAssign = RoleAssignmentPolicy.CanAssignRole(
            actorHasGrantsAll: false,
            actorHasUsersManage: false,
            actorEffectivePermissions: OrderStaffPermissions,
            roleGrantsAllPermissions: false,
            rolePermissions: OrderStaffPermissions);

        Assert.False(canAssign);
    }

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
