using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Configurations.Identity;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// Computes effective permissions straight from <c>identity.RoleClaims</c> and <c>Roles.GrantsAllPermissions</c>
/// (doc 08 §3.1 R4), never from the principal. Cached per <c>(userId, SecurityStamp)</c>: a role or permission
/// change bumps the affected users' <c>SecurityStamp</c> (doc 08 §3.1 DB4 rule 5), which changes the cache key
/// and forces a fresh read — this is the "cached, invalidated when a role changes" behaviour doc 03 §3a asks for,
/// without needing an explicit cache-eviction call from the (out-of-scope) role management endpoints.
/// </summary>
public sealed class EffectivePermissionsProvider(AppDbContext dbContext, IMemoryCache cache) : IEffectivePermissionsProvider
{
    /// <summary>Upper bound on how long a stale entry can live if its key is never revisited (memory hygiene only).</summary>
    private static readonly TimeSpan CacheEntryTtl = TimeSpan.FromMinutes(5);

    public async Task<EffectivePermissions> GetEffectivePermissionsAsync(int userId, CancellationToken cancellationToken)
    {
        var securityStamp = await dbContext.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.SecurityStamp)
            .FirstOrDefaultAsync(cancellationToken);

        if (securityStamp is null)
        {
            return new EffectivePermissions(false, []);
        }

        var cacheKey = (nameof(EffectivePermissionsProvider), userId, securityStamp);
        if (cache.TryGetValue(cacheKey, out EffectivePermissions? cached) && cached is not null)
        {
            return cached;
        }

        var roles = await dbContext.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Join(dbContext.Roles.AsNoTracking(), ur => ur.RoleId, r => r.Id, (_, role) => role)
            .Select(role => new { role.Id, role.GrantsAllPermissions })
            .ToListAsync(cancellationToken);

        var grantsAll = roles.Exists(role => role.GrantsAllPermissions);
        List<string> permissions = [];

        if (!grantsAll && roles.Count > 0)
        {
            var roleIds = roles.ConvertAll(role => role.Id);
            permissions = await dbContext.RoleClaims.AsNoTracking()
                .Where(rc => roleIds.Contains(rc.RoleId) && rc.ClaimType == RoleClaimConfiguration.PermissionClaimType)
                .Select(rc => rc.ClaimValue!)
                .Distinct()
                .ToListAsync(cancellationToken);
        }

        var result = new EffectivePermissions(grantsAll, permissions);
        cache.Set(cacheKey, result, CacheEntryTtl);
        return result;
    }
}
