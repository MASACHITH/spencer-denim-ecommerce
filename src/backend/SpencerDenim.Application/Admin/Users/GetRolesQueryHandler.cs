using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Application.Admin.Users.Contracts;

namespace SpencerDenim.Application.Admin.Users;

public sealed class GetRolesQueryHandler(IRoleDirectoryReader reader)
    : IQueryHandler<GetRolesQuery, IReadOnlyList<RoleSummaryDto>>
{
    public Task<IReadOnlyList<RoleSummaryDto>> HandleAsync(GetRolesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return reader.ListAsync(cancellationToken);
    }
}
