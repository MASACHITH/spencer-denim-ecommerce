using SpencerDenim.Application.Abstractions.Messaging;

namespace SpencerDenim.Application.Admin.CurrentUser;

/// <summary>
/// Answers "who am I": the endpoint resolves <see cref="WhoAmIQuery.UserId"/> from the authenticated admin
/// cookie principal, and this handler loads the current profile and effective permissions from the database
/// (never from the principal's own claims, per doc 08 §3.1 R4).
/// </summary>
public sealed class WhoAmIQueryHandler(IAdminUserLookup userLookup, IEffectivePermissionsProvider permissionsProvider)
    : IQueryHandler<WhoAmIQuery, WhoAmIResponse?>
{
    public async Task<WhoAmIResponse?> HandleAsync(WhoAmIQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var user = await userLookup.FindByIdAsync(query.UserId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return null;
        }

        var permissions = await permissionsProvider.GetEffectivePermissionsAsync(query.UserId, cancellationToken);

        return new WhoAmIResponse(
            user.UserId,
            user.Email,
            user.FirstName,
            user.LastName,
            permissions.GrantsAllPermissions,
            permissions.Permissions);
    }
}
