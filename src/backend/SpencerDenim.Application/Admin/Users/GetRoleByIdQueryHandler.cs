using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Application.Admin.Users.Contracts;

namespace SpencerDenim.Application.Admin.Users;

/// <summary>
/// One role's detail. For a viewer without GrantsAll, the GrantsAll role comes back as <c>null</c>, the same
/// answer as an id that does not exist. See <see cref="GetRolesQueryHandler"/>.
/// </summary>
public sealed class GetRoleByIdQueryHandler(IRoleDirectoryReader reader, IEffectivePermissionsProvider permissionsProvider)
    : IQueryHandler<GetRoleByIdQuery, RoleDetailDto?>
{
    public async Task<RoleDetailDto?> HandleAsync(GetRoleByIdQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var viewer = await permissionsProvider.GetEffectivePermissionsAsync(query.ActorUserId, cancellationToken);
        return await reader.GetByIdAsync(query.RoleId, includeGrantsAllHolders: viewer.GrantsAllPermissions, cancellationToken);
    }
}
