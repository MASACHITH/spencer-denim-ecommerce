using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Admin.Users;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Application.Admin.Users.Contracts;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Admin.Users;

/// <summary>
/// The staff user list/detail read model (ADM-02), implemented the same way <c>AuditLogReader</c> and
/// <c>ProductCatalogReader</c> are: predicates are composed only for the filters that are present, and every
/// query is <c>AsNoTracking</c> with a projection, so no EF entity leaves this layer (doc 08 §10.3).
/// <para>
/// <c>AppUser</c> declares no navigation to its roles (doc 08 §3.1 keeps <c>UserRoles</c>/<c>RoleClaims</c> as
/// plain Identity join tables), so a user's roles are loaded in one extra, batched query per page rather than
/// per row — exactly two round trips regardless of how many users are on the page, never N+1.
/// </para>
/// <para>
/// <b>Protected targets are filtered in SQL, not after the read.</b> When the viewer does not hold GrantsAll,
/// every GrantsAll holder is excluded by the <c>WHERE</c> clause itself (tester round 2, defect B). A by-id
/// lookup of a Super Admin therefore runs the same single query and gets the same empty result as an id that
/// does not exist, so neither the response nor its timing can tell the two apart.
/// </para>
/// </summary>
internal sealed class StaffUserDirectoryReader(AppDbContext dbContext, IProtectedStaffAccounts protectedAccounts) : IStaffUserDirectoryReader
{
    public async Task<PagedResult<StaffUserSummaryDto>> ListAsync(
        GetStaffUsersQuery query,
        bool includeGrantsAllHolders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var grantsAllHolders = GrantsAllHolderIds();
        var rows = ApplyFilters(
            dbContext.Users.AsNoTracking().Where(u => u.UserType == UserType.Staff && (includeGrantsAllHolders || !grantsAllHolders.Contains(u.Id))),
            query);

        var totalCount = await rows.CountAsync(cancellationToken);
        if (totalCount == 0)
        {
            return PagedResult.Empty<StaffUserSummaryDto>(query.Page, query.PageSize);
        }

        var page = await rows
            .OrderBy(u => u.Email)
            .ThenBy(u => u.Id)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(u => new
            {
                u.Id,
                Email = u.Email ?? string.Empty,
                u.FirstName,
                u.LastName,
                u.IsActive,
                u.MustChangePassword,
                u.LastLoginAtUtc,
            })
            .ToListAsync(cancellationToken);

        var rolesByUser = await LoadRolesByUserAsync(page.ConvertAll(u => u.Id), cancellationToken);

        var items = page
            .ConvertAll(u => new StaffUserSummaryDto(
                u.Id,
                u.Email,
                u.FirstName,
                u.LastName,
                u.IsActive,
                u.MustChangePassword,
                u.LastLoginAtUtc,
                rolesByUser.TryGetValue(u.Id, out var roles) ? roles : [],
                protectedAccounts.IsProtected(u.Email)));

        return new PagedResult<StaffUserSummaryDto>(items, query.Page, query.PageSize, totalCount);
    }

    public async Task<StaffUserDetailDto?> GetByIdAsync(int userId, bool includeGrantsAllHolders, CancellationToken cancellationToken)
    {
        var grantsAllHolders = GrantsAllHolderIds();
        var user = await dbContext.Users.AsNoTracking()
            .Where(u => u.Id == userId && u.UserType == UserType.Staff && (includeGrantsAllHolders || !grantsAllHolders.Contains(u.Id)))
            .Select(u => new
            {
                u.Id,
                Email = u.Email ?? string.Empty,
                u.FirstName,
                u.LastName,
                u.IsActive,
                u.MustChangePassword,
                u.LastLoginAtUtc,
                u.CreatedAtUtc,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (user is null)
        {
            return null;
        }

        var rolesByUser = await LoadRolesByUserAsync([user.Id], cancellationToken);
        var roles = rolesByUser.TryGetValue(user.Id, out var loadedRoles) ? loadedRoles : [];
        var grantsAll = roles.Any(role => role.GrantsAllPermissions);

        var permissions = grantsAll
            ? []
            : await LoadPermissionsForRolesAsync(roles.ConvertAll(role => role.Id), cancellationToken);

        return new StaffUserDetailDto(
            user.Id,
            user.Email,
            user.FirstName,
            user.LastName,
            user.IsActive,
            user.MustChangePassword,
            user.LastLoginAtUtc,
            user.CreatedAtUtc,
            grantsAll,
            roles,
            permissions,
            protectedAccounts.IsProtected(user.Email));
    }

    /// <summary>
    /// The ids of every user who holds a role that grants every permission. Held in a local and closed over by the
    /// caller's <c>Where</c> so EF composes it into the same SQL statement as a subquery: calling an ordinary method
    /// from inside the predicate would not translate. The same shape the store's last-Super-Admin count uses.
    /// </summary>
    private IQueryable<int> GrantsAllHolderIds() =>
        dbContext.UserRoles.AsNoTracking()
            .Where(ur => dbContext.Roles.Any(r => r.Id == ur.RoleId && r.GrantsAllPermissions))
            .Select(ur => ur.UserId);

    private IQueryable<AppUser> ApplyFilters(IQueryable<AppUser> rows, GetStaffUsersQuery query)
    {
        if (query.Search is { } search)
        {
            // Latin1_General_100_CI_AS_SC (doc 08 §1.1) is case-insensitive, so a plain Contains already
            // matches regardless of case — the same assumption ProductCatalogReader's search makes.
            rows = rows.Where(u =>
                (u.Email != null && u.Email.Contains(search))
                || u.FirstName.Contains(search)
                || (u.LastName != null && u.LastName.Contains(search)));
        }

        if (query.RoleId is { } roleId)
        {
            rows = rows.Where(u => dbContext.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == roleId));
        }

        if (query.IsActive is { } isActive)
        {
            rows = rows.Where(u => u.IsActive == isActive);
        }

        return rows;
    }

    private async Task<Dictionary<int, List<RoleRefDto>>> LoadRolesByUserAsync(List<int> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        var assignments = await dbContext.UserRoles.AsNoTracking()
            .Where(ur => userIds.Contains(ur.UserId))
            .Join(dbContext.Roles.AsNoTracking(), ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Id, r.Name, r.GrantsAllPermissions })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<int, List<RoleRefDto>>();
        foreach (var assignment in assignments)
        {
            if (!result.TryGetValue(assignment.UserId, out var roles))
            {
                roles = [];
                result[assignment.UserId] = roles;
            }

            roles.Add(new RoleRefDto(assignment.Id, assignment.Name ?? string.Empty, assignment.GrantsAllPermissions));
        }

        return result;
    }

    private async Task<IReadOnlyList<string>> LoadPermissionsForRolesAsync(List<int> roleIds, CancellationToken cancellationToken)
    {
        if (roleIds.Count == 0)
        {
            return [];
        }

        return await dbContext.RoleClaims.AsNoTracking()
            .Where(rc => roleIds.Contains(rc.RoleId) && rc.ClaimType == Persistence.Configurations.Identity.RoleClaimConfiguration.PermissionClaimType)
            .Select(rc => rc.ClaimValue!)
            .Distinct()
            .ToListAsync(cancellationToken);
    }
}
