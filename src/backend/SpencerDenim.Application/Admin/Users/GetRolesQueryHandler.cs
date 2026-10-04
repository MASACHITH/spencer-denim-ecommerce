using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Application.Admin.Users.Contracts;

namespace SpencerDenim.Application.Admin.Users;

/// <summary>
/// Lists roles. A viewer without GrantsAll is not shown the GrantsAll role, and the user counts leave GrantsAll
/// holders out. See <see cref="GetStaffUsersQueryHandler"/> for why. They could not assign that role anyway
/// (<c>RoleAssignmentPolicy</c> blocks it), so offering it would only be a button that always fails.
/// </summary>
public sealed class GetRolesQueryHandler(IRoleDirectoryReader reader, IEffectivePermissionsProvider permissionsProvider)
    : IQueryHandler<GetRolesQuery, IReadOnlyList<RoleSummaryDto>>
{
    public async Task<IReadOnlyList<RoleSummaryDto>> HandleAsync(GetRolesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var viewer = await permissionsProvider.GetEffectivePermissionsAsync(query.ActorUserId, cancellationToken);
        return await reader.ListAsync(includeGrantsAllHolders: viewer.GrantsAllPermissions, cancellationToken);
    }
}
