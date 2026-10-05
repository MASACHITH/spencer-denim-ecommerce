namespace SpencerDenim.Application.Admin.Users;

/// <summary>One role's identity and permission grant, as loaded from <c>identity.Roles</c>/<c>RoleClaims</c> for an RBAC decision (doc 08 §3.1 DB4).</summary>
public sealed record RoleGrantSnapshot(int Id, string Name, bool GrantsAllPermissions, IReadOnlySet<string> Permissions);
