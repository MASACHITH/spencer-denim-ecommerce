namespace SpencerDenim.Application.Admin.CurrentUser;

/// <summary>
/// Port for computing a user's effective permissions (doc 08 §3.1 R4; doc 03 §3a: "cached, invalidated when a
/// role changes"). Implemented in Infrastructure, cached per <c>SecurityStamp</c> so a role or permission
/// change (which bumps the stamp, doc 08 §3.1 DB4 rule 5) is picked up automatically.
/// </summary>
public interface IEffectivePermissionsProvider
{
    Task<EffectivePermissions> GetEffectivePermissionsAsync(int userId, CancellationToken cancellationToken);
}
