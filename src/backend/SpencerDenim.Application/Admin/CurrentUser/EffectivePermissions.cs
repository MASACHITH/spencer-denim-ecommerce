namespace SpencerDenim.Application.Admin.CurrentUser;

/// <summary>
/// A user's effective permissions (doc 08 §3.1 R4): computed only from their roles' <c>RoleClaims</c> plus the
/// <c>GrantsAllPermissions</c> flag, never from claims carried on the principal or cookie.
/// </summary>
/// <param name="GrantsAllPermissions">True when any of the user's roles is the GrantsAll role.</param>
/// <param name="Permissions">
/// The user's individual permission grants. Meaningless (and not consulted) when <see cref="GrantsAllPermissions"/>
/// is true — a GrantsAll holder implicitly has every permission.
/// </param>
public sealed record EffectivePermissions(bool GrantsAllPermissions, IReadOnlyCollection<string> Permissions)
{
    public bool Has(string permission) => GrantsAllPermissions || Permissions.Contains(permission);
}
