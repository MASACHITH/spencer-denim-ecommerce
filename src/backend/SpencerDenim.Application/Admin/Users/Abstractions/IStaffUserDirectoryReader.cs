using SpencerDenim.Application.Admin.Users.Contracts;
using SpencerDenim.Application.Catalog.Contracts;

namespace SpencerDenim.Application.Admin.Users.Abstractions;

/// <summary>
/// Read port for the staff user list/detail screens (ADM-02). Implemented in Infrastructure against
/// <c>identity.Users</c> / <c>UserRoles</c> / <c>Roles</c>, projecting straight to DTOs with
/// <c>AsNoTracking</c> so no EF entity leaves that layer (doc 08 §10.3) — the same shape
/// <c>IAuditLogReader</c> uses for the audit log viewer.
/// </summary>
public interface IStaffUserDirectoryReader
{
    Task<PagedResult<StaffUserSummaryDto>> ListAsync(GetStaffUsersQuery query, CancellationToken cancellationToken);

    Task<StaffUserDetailDto?> GetByIdAsync(int userId, CancellationToken cancellationToken);
}
