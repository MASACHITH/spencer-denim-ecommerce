namespace SpencerDenim.Application.Admin.Users;

/// <summary>
/// A staff user's current state as needed to evaluate the RBAC safety rules (doc 08 §3.1 DB4) before a
/// command handler decides whether to allow a change: their roles, and the permissions those roles
/// aggregate to. Loaded fresh for every command — never cached — because these are exactly the values an
/// authorization decision must never work from stale data.
/// </summary>
public sealed record StaffUserSnapshot(
    int Id,
    string Email,
    string FirstName,
    string? LastName,
    bool IsActive,
    string SecurityStamp,
    IReadOnlyList<RoleGrantSnapshot> Roles)
{
    public bool GrantsAllPermissions => Roles.Any(role => role.GrantsAllPermissions);

    /// <summary>
    /// The union of every held role's permissions. Meaningless (and never consulted) when
    /// <see cref="GrantsAllPermissions"/> is true, matching <c>EffectivePermissions.Has</c>'s contract.
    /// </summary>
    public IReadOnlySet<string> EffectivePermissions =>
        new HashSet<string>(Roles.SelectMany(role => role.Permissions));
}
