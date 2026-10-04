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
    /// <param name="query">The validated listing query.</param>
    /// <param name="includeGrantsAllHolders">
    /// <c>false</c> removes every GrantsAll holder from the rows <i>before</i> they are counted and paged, so the
    /// total, the page and the paging all behave as if those accounts did not exist.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<PagedResult<StaffUserSummaryDto>> ListAsync(GetStaffUsersQuery query, bool includeGrantsAllHolders, CancellationToken cancellationToken);

    /// <summary>Returns <c>null</c> for a missing id, and also for a GrantsAll holder when <paramref name="includeGrantsAllHolders"/> is <c>false</c>.</summary>
    Task<StaffUserDetailDto?> GetByIdAsync(int userId, bool includeGrantsAllHolders, CancellationToken cancellationToken);
}
