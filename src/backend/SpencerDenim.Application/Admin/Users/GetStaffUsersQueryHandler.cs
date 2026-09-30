using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Application.Admin.Users.Contracts;
using SpencerDenim.Application.Catalog.Contracts;

namespace SpencerDenim.Application.Admin.Users;

public sealed class GetStaffUsersQueryHandler(IStaffUserDirectoryReader reader)
    : IQueryHandler<GetStaffUsersQuery, PagedResult<StaffUserSummaryDto>>
{
    public Task<PagedResult<StaffUserSummaryDto>> HandleAsync(GetStaffUsersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return reader.ListAsync(query, cancellationToken);
    }
}
