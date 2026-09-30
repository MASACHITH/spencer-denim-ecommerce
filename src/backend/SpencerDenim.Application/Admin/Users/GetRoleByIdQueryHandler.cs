using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Application.Admin.Users.Contracts;

namespace SpencerDenim.Application.Admin.Users;

public sealed class GetRoleByIdQueryHandler(IRoleDirectoryReader reader)
    : IQueryHandler<GetRoleByIdQuery, RoleDetailDto?>
{
    public Task<RoleDetailDto?> HandleAsync(GetRoleByIdQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return reader.GetByIdAsync(query.RoleId, cancellationToken);
    }
}
