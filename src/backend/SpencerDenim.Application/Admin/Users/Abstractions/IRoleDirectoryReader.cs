using SpencerDenim.Application.Admin.Users.Contracts;

namespace SpencerDenim.Application.Admin.Users.Abstractions;

/// <summary>
/// Read port for the role list/detail screens (ADM-02). Role management is read-only in this slice — see
/// <c>AppRole</c>'s remarks — so this is the only role port besides the write-side snapshot lookups
/// <see cref="IStaffUserRoleManagementStore"/> needs to evaluate the RBAC safety rules.
/// </summary>
public interface IRoleDirectoryReader
{
    /// <param name="includeGrantsAllHolders">
    /// <c>false</c> omits the GrantsAll role and leaves GrantsAll holders out of every role's user count.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<RoleSummaryDto>> ListAsync(bool includeGrantsAllHolders, CancellationToken cancellationToken);

    /// <summary>Returns <c>null</c> for a missing id, and also for the GrantsAll role when <paramref name="includeGrantsAllHolders"/> is <c>false</c>.</summary>
    Task<RoleDetailDto?> GetByIdAsync(int roleId, bool includeGrantsAllHolders, CancellationToken cancellationToken);
}
