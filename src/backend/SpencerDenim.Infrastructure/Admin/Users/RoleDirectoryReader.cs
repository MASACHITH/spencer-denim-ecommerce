using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Application.Admin.Users.Contracts;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Configurations.Identity;

namespace SpencerDenim.Infrastructure.Admin.Users;

/// <summary>
/// The role list/detail read model (ADM-02). Role management is read-only in this slice (see
/// <c>AppRole</c>'s remarks), so this reader is the only place role data is projected for display.
/// </summary>
internal sealed class RoleDirectoryReader(AppDbContext dbContext) : IRoleDirectoryReader
{
    public async Task<IReadOnlyList<RoleSummaryDto>> ListAsync(CancellationToken cancellationToken)
    {
        var roles = await dbContext.Roles.AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => new
            {
                r.Id,
                r.Name,
                r.Description,
                r.IsSystem,
                r.GrantsAllPermissions,
                PermissionCount = dbContext.RoleClaims.Count(rc => rc.RoleId == r.Id && rc.ClaimType == RoleClaimConfiguration.PermissionClaimType),
                UserCount = dbContext.UserRoles.Count(ur => ur.RoleId == r.Id),
            })
            .ToListAsync(cancellationToken);

        return roles
            .ConvertAll(r => new RoleSummaryDto(r.Id, r.Name ?? string.Empty, r.Description, r.IsSystem, r.GrantsAllPermissions, r.PermissionCount, r.UserCount));
    }

    public async Task<RoleDetailDto?> GetByIdAsync(int roleId, CancellationToken cancellationToken)
    {
        var role = await dbContext.Roles.AsNoTracking()
            .Where(r => r.Id == roleId)
            .Select(r => new { r.Id, r.Name, r.Description, r.IsSystem, r.GrantsAllPermissions })
            .SingleOrDefaultAsync(cancellationToken);

        if (role is null)
        {
            return null;
        }

        var permissions = await dbContext.RoleClaims.AsNoTracking()
            .Where(rc => rc.RoleId == roleId && rc.ClaimType == RoleClaimConfiguration.PermissionClaimType)
            .Select(rc => rc.ClaimValue!)
            .OrderBy(value => value)
            .ToListAsync(cancellationToken);

        var userCount = await dbContext.UserRoles.AsNoTracking().CountAsync(ur => ur.RoleId == roleId, cancellationToken);

        return new RoleDetailDto(role.Id, role.Name ?? string.Empty, role.Description, role.IsSystem, role.GrantsAllPermissions, permissions, userCount);
    }
}
