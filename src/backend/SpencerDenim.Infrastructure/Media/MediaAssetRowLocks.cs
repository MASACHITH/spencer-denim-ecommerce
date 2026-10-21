using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Catalog.Admin.Abstractions;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Media;

/// <summary>
/// The catalogue side of doc 08 §5.3's media lock order (#223), shared by the product, category and
/// department saves: the owner's parent row (when there is one), then every <c>media.MediaAssets</c> row the
/// save will point at <b>by ascending id</b>, each with <c>UPDLOCK, READCOMMITTEDLOCK, ROWLOCK</c>, then the
/// writes. <c>MediaService.DeleteAsync</c> takes the same lock on its one asset row before it checks
/// anything, so a save and a delete of the same picture serialise and exactly one wins; nothing locks an
/// asset row and then a department row, so no cycle is possible.
/// <para>
/// One statement per asset rather than one <c>IN (…)</c> list: the order in which a single scan takes its
/// row locks is the plan's business, and the order is the whole point.
/// </para>
/// <para>
/// Every method here must run inside the caller's transaction, or the locks are released as soon as each
/// statement ends.
/// </para>
/// </summary>
internal static class MediaAssetRowLocks
{
    /// <summary>
    /// Locks the department row first when <see cref="CatalogMediaClaim.LockDepartmentId"/> is set, then each
    /// claimed asset in ascending id order, and answers the slots whose asset failed the re-check: missing,
    /// deleted, not ready, not public, or not of the slot's kind.
    /// </summary>
    public static async Task<IReadOnlyList<CatalogMediaSlot>> LockAndRecheckAsync(
        AppDbContext dbContext,
        CatalogMediaClaim claim,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(claim);

        if (claim.LockDepartmentId is { } departmentId)
        {
            await dbContext.Database
                .SqlQuery<int>(
                    $"SELECT 1 AS Value FROM catalog.Departments WITH (UPDLOCK, READCOMMITTEDLOCK, ROWLOCK) WHERE Id = {departmentId}")
                .ToListAsync(cancellationToken);
        }

        var unavailable = new List<CatalogMediaSlot>();

        foreach (var group in claim.Slots.GroupBy(slot => slot.MediaAssetId).OrderBy(group => group.Key))
        {
            var assetId = group.Key;
            var rows = await dbContext.Database
                .SqlQuery<LockedAsset>(
                    $"SELECT a.IsDeleted, a.Status, a.Visibility, a.MediaType FROM media.MediaAssets a WITH (UPDLOCK, READCOMMITTEDLOCK, ROWLOCK) WHERE a.Id = {assetId}")
                .ToListAsync(cancellationToken);

            foreach (var slot in group)
            {
                var usable = rows.Count == 1
                    && !rows[0].IsDeleted
                    && rows[0].Status == MediaAssetStatus.Ready
                    && rows[0].Visibility == MediaVisibility.Public
                    && rows[0].MediaType == slot.ExpectedType;

                if (!usable)
                {
                    unavailable.Add(slot);
                }
            }
        }

        return unavailable;
    }

    /// <summary>
    /// Rewrites the usage rows of one owner to exactly <paramref name="slots"/> (doc 08 §3.3: "an owner's save
    /// rewrites its usage rows in the same transaction"). A row still wanted is left alone, so its
    /// <c>CreatedAtUtc</c> — the "attached on" date of the library's "used in…" panel — does not move.
    /// Tracked changes only; the caller's <c>SaveChangesAsync</c> writes them.
    /// </summary>
    public static async Task SyncUsagesAsync(
        AppDbContext dbContext,
        string ownerType,
        int ownerId,
        IEnumerable<CatalogMediaSlot> slots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrEmpty(ownerType);
        ArgumentNullException.ThrowIfNull(slots);

        var owner = ownerId.ToString(CultureInfo.InvariantCulture);
        var wanted = slots.Select(slot => (slot.MediaAssetId, slot.UsageRole)).ToHashSet();

        var existing = await dbContext.MediaUsages
            .Where(usage => usage.OwnerType == ownerType && usage.OwnerId == owner)
            .ToListAsync(cancellationToken);

        foreach (var gone in existing.Where(usage => !wanted.Contains((usage.MediaAssetId, usage.UsageRole))))
        {
            dbContext.MediaUsages.Remove(gone);
        }

        var held = existing.Select(usage => (usage.MediaAssetId, usage.UsageRole)).ToHashSet();

        // A retried attempt (the execution strategy re-runs the whole transaction) finds the rows the failed
        // attempt added still tracked as Added: they are not in the table, so the query above did not return
        // them, and adding them again would trip UX_MediaUsages_Asset_Owner_Role. Keep the ones still wanted.
        foreach (var pending in dbContext.ChangeTracker.Entries<MediaUsage>()
            .Where(entry => entry.State == EntityState.Added
                && entry.Entity.OwnerType == ownerType
                && entry.Entity.OwnerId == owner)
            .ToList())
        {
            if (!wanted.Contains((pending.Entity.MediaAssetId, pending.Entity.UsageRole))
                || !held.Add((pending.Entity.MediaAssetId, pending.Entity.UsageRole)))
            {
                pending.State = EntityState.Detached;
            }
        }

        foreach (var (assetId, role) in wanted.Where(usage => !held.Contains(usage)))
        {
            dbContext.MediaUsages.Add(new MediaUsage
            {
                MediaAssetId = assetId,
                OwnerType = ownerType,
                OwnerId = owner,
                UsageRole = role,
            });
        }
    }

    /// <summary>One asset row as the locked re-check reads it.</summary>
    private sealed record LockedAsset(
        bool IsDeleted,
        MediaAssetStatus Status,
        MediaVisibility Visibility,
        MediaAssetType MediaType);
}
