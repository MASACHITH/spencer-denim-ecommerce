using SpencerDenim.Application.Admin.Users.Contracts;

namespace SpencerDenim.Application.Admin.Users.Abstractions;

/// <summary>
/// Read port for the role list/detail screens (ADM-02). Role management is read-only in this slice — see
/// <c>AppRole</c>'s remarks — so this is the only role port besides the write-side snapshot lookups
/// <see cref="IStaffUserRoleManagementStore"/> needs to evaluate the RBAC safety rules.
/// </summary>
public interface IRoleDirectoryReader
{
    Task<IReadOnlyList<RoleSummaryDto>> ListAsync(CancellationToken cancellationToken);

    Task<RoleDetailDto?> GetByIdAsync(int roleId, CancellationToken cancellationToken);
}
