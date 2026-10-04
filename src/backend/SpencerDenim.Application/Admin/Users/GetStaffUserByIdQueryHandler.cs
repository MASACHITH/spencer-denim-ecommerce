using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Application.Admin.Users.Contracts;

namespace SpencerDenim.Application.Admin.Users;

/// <summary>
/// One staff user's detail. For a viewer without GrantsAll, a GrantsAll holder comes back as <c>null</c>, the same
/// answer as an id that does not exist. See <see cref="GetStaffUsersQueryHandler"/> for why.
/// </summary>
public sealed class GetStaffUserByIdQueryHandler(IStaffUserDirectoryReader reader, IEffectivePermissionsProvider permissionsProvider)
    : IQueryHandler<GetStaffUserByIdQuery, StaffUserDetailDto?>
{
    public async Task<StaffUserDetailDto?> HandleAsync(GetStaffUserByIdQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var viewer = await permissionsProvider.GetEffectivePermissionsAsync(query.ActorUserId, cancellationToken);
        return await reader.GetByIdAsync(query.UserId, includeGrantsAllHolders: viewer.GrantsAllPermissions, cancellationToken);
    }
}
