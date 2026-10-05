using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Application.Admin.Users.Contracts;
using SpencerDenim.Application.Catalog.Contracts;

namespace SpencerDenim.Application.Admin.Users;

/// <summary>
/// Lists staff users. A viewer who does not hold GrantsAll never sees a GrantsAll holder, in the page or in the
/// total, so the list cannot be used to learn which accounts are Super Admins (tester round 2, defect B). This is
/// the read-side half of the rule the write handlers already apply: doc 08 section 3.1 says GrantsAll holders are
/// never a subset of anyone else's permissions, so they are protected, and a protected target is
/// indistinguishable from one that does not exist.
/// </summary>
public sealed class GetStaffUsersQueryHandler(IStaffUserDirectoryReader reader, IEffectivePermissionsProvider permissionsProvider)
    : IQueryHandler<GetStaffUsersQuery, PagedResult<StaffUserSummaryDto>>
{
    public async Task<PagedResult<StaffUserSummaryDto>> HandleAsync(GetStaffUsersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var viewer = await permissionsProvider.GetEffectivePermissionsAsync(query.ActorUserId, cancellationToken);
        return await reader.ListAsync(query, includeGrantsAllHolders: viewer.GrantsAllPermissions, cancellationToken);
    }
}
