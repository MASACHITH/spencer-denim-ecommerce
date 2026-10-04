using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Application.Admin.Users.Contracts;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Configurations.Identity;

namespace SpencerDenim.Infrastructure.Admin.Users;

/// <summary>
/// The role list/detail read model (ADM-02). Role management is read-only in this slice (see
/// <c>AppRole</c>'s remarks), so this reader is the only place role data is projected for display.
/// <para>
/// For a viewer without GrantsAll the GrantsAll role is excluded by the <c>WHERE</c> clause, and GrantsAll
/// holders are left out of every user count, so a role's headcount cannot reveal that a Super Admin also holds
/// it (tester round 2, defect B).
/// </para>
/// </summary>
internal sealed class RoleDirectoryReader(AppDbContext dbContext) : IRoleDirectoryReader
{
    public async Task<IReadOnlyList<RoleSummaryDto>> ListAsync(bool includeGrantsAllHolders, CancellationToken cancellationToken)
    {
        var grantsAllHolders = GrantsAllHolderIds();
        var roles = await dbContext.Roles.AsNoTracking()
            .Where(r => includeGrantsAllHolders || !r.GrantsAllPermissions)
            .OrderBy(r => r.Name)
            .Select(r => new
            {
                r.Id,
                r.Name,
                r.Description,
                r.IsSystem,
                r.GrantsAllPermissions,
                PermissionCount = dbContext.RoleClaims.Count(rc => rc.RoleId == r.Id && rc.ClaimType == RoleClaimConfiguration.PermissionClaimType),
                UserCount = dbContext.UserRoles.Count(ur => ur.RoleId == r.Id && (includeGrantsAllHolders || !grantsAllHolders.Contains(ur.UserId))),
            })
            .ToListAsync(cancellationToken);

        return roles
            .ConvertAll(r => new RoleSummaryDto(r.Id, r.Name ?? string.Empty, r.Description, r.IsSystem, r.GrantsAllPermissions, r.PermissionCount, r.UserCount));
    }

    public async Task<RoleDetailDto?> GetByIdAsync(int roleId, bool includeGrantsAllHolders, CancellationToken cancellationToken)
    {
        var role = await dbContext.Roles.AsNoTracking()
            .Where(r => r.Id == roleId && (includeGrantsAllHolders || !r.GrantsAllPermissions))
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

        var grantsAllHolders = GrantsAllHolderIds();
        var userCount = await dbContext.UserRoles.AsNoTracking()
            .CountAsync(ur => ur.RoleId == roleId && (includeGrantsAllHolders || !grantsAllHolders.Contains(ur.UserId)), cancellationToken);

        return new RoleDetailDto(role.Id, role.Name ?? string.Empty, role.Description, role.IsSystem, role.GrantsAllPermissions, permissions, userCount);
    }

    /// <summary>
    /// The ids of every user who holds a role that grants every permission, as a local the caller's lambdas close
    /// over so EF composes it into the same statement as a subquery (an ordinary method call would not translate).
    /// </summary>
    private IQueryable<int> GrantsAllHolderIds() =>
        dbContext.UserRoles.AsNoTracking()
            .Where(ur => dbContext.Roles.Any(r => r.Id == ur.RoleId && r.GrantsAllPermissions))
            .Select(ur => ur.UserId);
}
