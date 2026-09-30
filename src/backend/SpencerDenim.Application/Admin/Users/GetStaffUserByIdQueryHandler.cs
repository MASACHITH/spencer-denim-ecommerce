using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Application.Admin.Users.Contracts;

namespace SpencerDenim.Application.Admin.Users;

public sealed class GetStaffUserByIdQueryHandler(IStaffUserDirectoryReader reader)
    : IQueryHandler<GetStaffUserByIdQuery, StaffUserDetailDto?>
{
    public Task<StaffUserDetailDto?> HandleAsync(GetStaffUserByIdQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return reader.GetByIdAsync(query.UserId, cancellationToken);
    }
}
