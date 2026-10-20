using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Interceptors;

namespace SpencerDenim.Infrastructure.Content;

/// <summary>
/// The EF Core side of the ADM-17 homepage, announcement, size-guide (#192) and mega-menu card (#215) writes.
/// <para>
/// The arrangement <see cref="ContentPageStore"/> established — tracked entities out, one unit of work in —
/// plus the one thing these rows need that a content page does not: <c>media.MediaUsages</c>. Doc 08
/// §3.3 says "an owner's save rewrites its usage rows in the same transaction", and a new item has no id
/// to name its usage rows by until it is inserted, so a save is two statements inside one transaction,
/// itself inside the execution strategy (a retrying strategy refuses a bare user transaction — see
/// <c>ContactEnquiryStore.DeleteAsync</c>).
/// </para>
/// </summary>
internal sealed class ContentAdminStores(
    AppDbContext dbContext,
    AuditAndSoftDeleteInterceptor auditColumns,
    IClock clock)
    : IHomeContentAdminStore, IAnnouncementAdminStore, ISizeGuideAdminStore, IMegaMenuCardAdminStore
{
    // ------------------------------------------------------------------ shared

    public Task<ContentMediaAsset?> FindMediaAsync(Guid publicId, CancellationToken cancellationToken) =>
        dbContext.MediaAssets
            .AsNoTracking()
            .Where(asset => asset.PublicId == publicId)
            .Select(asset => new ContentMediaAsset(asset.Id, asset.MediaType, asset.Status, asset.Visibility, asset.AltText))
            .FirstOrDefaultAsync(cancellationToken);

    // ------------------------------------------------------------------ homepage

    public Task<HomeSection?> FindSectionAsync(int sectionId, CancellationToken cancellationToken) =>
        dbContext.HomeSections
            .Include(section => section.Items)
            .FirstOrDefaultAsync(section => section.Id == sectionId, cancellationToken);

    public async Task<HomeSection?> FindSectionOfItemAsync(int itemId, CancellationToken cancellationToken)
    {
        var sectionId = await dbContext.HomeSectionItems
            .Where(item => item.Id == itemId)
            .Select(item => (int?)item.HomeSectionId)
            .FirstOrDefaultAsync(cancellationToken);

        return sectionId is { } id ? await FindSectionAsync(id, cancellationToken) : null;
    }

    public Task<bool> DepartmentExistsAsync(int departmentId, CancellationToken cancellationToken) =>
        dbContext.Departments.AnyAsync(department => department.Id == departmentId, cancellationToken);

    public Task<int?> FindProductIdAsync(string slug, CancellationToken cancellationToken) =>
        dbContext.Products
            .Where(product => product.Slug == slug)
            .Select(product => (int?)product.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public void AddItem(HomeSectionItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        dbContext.HomeSectionItems.Add(item);
    }

    public void RemoveItem(HomeSectionItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        dbContext.HomeSectionItems.Remove(item);
    }

    public void MarkChanged(HomeSection section)
    {
        ArgumentNullException.ThrowIfNull(section);

        // Marking one column is enough to make the row part of the UPDATE, and the UPDATE carries the
        // RowVersion predicate — so an item write is refused when somebody else changed the block first,
        // and moves the block's version when it lands. The interceptor stamps UpdatedAtUtc itself.
        dbContext.Entry(section).Property(candidate => candidate.UpdatedAtUtc).IsModified = true;
    }

    Task<ContentSaveOutcome> IHomeContentAdminStore.SaveAsync(
        int? actingUserId,
        ContentMediaUsageChange? usage,
        CancellationToken cancellationToken) =>
        SaveWithUsageAsync(actingUserId, usage, cancellationToken);

    // ------------------------------------------------------------------ announcements

    Task<AnnouncementMessage?> IAnnouncementAdminStore.FindAsync(int messageId, CancellationToken cancellationToken) =>
        dbContext.AnnouncementMessages.FirstOrDefaultAsync(message => message.Id == messageId, cancellationToken);

    public Task<int> CountAsync(CancellationToken cancellationToken) =>
        dbContext.AnnouncementMessages.CountAsync(cancellationToken);

    public void Add(AnnouncementMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        dbContext.AnnouncementMessages.Add(message);
    }

    public void Remove(AnnouncementMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        dbContext.AnnouncementMessages.Remove(message);
    }

    async Task<ContentSaveOutcome> IAnnouncementAdminStore.SaveAsync(int? actingUserId, CancellationToken cancellationToken)
    {
        auditColumns.CurrentUserId = actingUserId;

        var kept = dbContext.ChangeTracker.Entries<AnnouncementMessage>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified)
            .ToList();

        if (await SaveOnceAsync(cancellationToken) == ContentSaveOutcome.ConcurrencyConflict)
        {
            return ContentSaveOutcome.ConcurrencyConflict;
        }

        // Read back, because the version token is built from UpdatedAtUtc and the column is datetime2(3):
        // the value the interceptor stamped in memory carries ticks the database rounded away, and the
        // token handed to the screen must match the row it will read next.
        foreach (var entry in kept)
        {
            await entry.ReloadAsync(cancellationToken);
        }

        return ContentSaveOutcome.Saved;
    }

    // ------------------------------------------------------------------ size guides

    Task<SizeGuide?> ISizeGuideAdminStore.FindAsync(int sizeGuideId, CancellationToken cancellationToken) =>
        dbContext.SizeGuides.FirstOrDefaultAsync(guide => guide.Id == sizeGuideId, cancellationToken);

    public async Task<(int Categories, int Products)> UsageAsync(int sizeGuideId, CancellationToken cancellationToken)
    {
        var categories = await dbContext.Categories.CountAsync(category => category.SizeGuideId == sizeGuideId, cancellationToken);
        var products = await dbContext.Products.CountAsync(product => product.SizeGuideId == sizeGuideId, cancellationToken);

        return (categories, products);
    }

    public void Add(SizeGuide sizeGuide)
    {
        ArgumentNullException.ThrowIfNull(sizeGuide);
        dbContext.SizeGuides.Add(sizeGuide);
    }

    Task<ContentSaveOutcome> ISizeGuideAdminStore.SaveAsync(
        int? actingUserId,
        ContentMediaUsageChange? usage,
        CancellationToken cancellationToken) =>
        SaveWithUsageAsync(actingUserId, usage, cancellationToken);

    // ------------------------------------------------------------------ mega-menu cards (#215)

    Task<MegaMenuPromoCard?> IMegaMenuCardAdminStore.FindAsync(int cardId, CancellationToken cancellationToken) =>
        dbContext.MegaMenuPromoCards.FirstOrDefaultAsync(card => card.Id == cardId, cancellationToken);

    public Task<int> CountForDepartmentAsync(int departmentId, CancellationToken cancellationToken) =>
        dbContext.MegaMenuPromoCards.CountAsync(card => card.DepartmentId == departmentId, cancellationToken);

    public void Add(MegaMenuPromoCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        dbContext.MegaMenuPromoCards.Add(card);
    }

    public void Remove(MegaMenuPromoCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        dbContext.MegaMenuPromoCards.Remove(card);
    }

    Task<ContentSaveOutcome> IMegaMenuCardAdminStore.SaveAsync(
        int? actingUserId,
        ContentMediaUsageChange? usage,
        CancellationToken cancellationToken) =>
        SaveWithUsageAsync(actingUserId, usage, cancellationToken);

    // ------------------------------------------------------------------ the save

    private async Task<ContentSaveOutcome> SaveWithUsageAsync(
        int? actingUserId,
        ContentMediaUsageChange? usage,
        CancellationToken cancellationToken)
    {
        auditColumns.CurrentUserId = actingUserId;

        if (usage is null)
        {
            return await SaveOnceAsync(cancellationToken);
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            if (await SaveOnceAsync(cancellationToken) == ContentSaveOutcome.ConcurrencyConflict)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ContentSaveOutcome.ConcurrencyConflict;
            }

            await ReplaceUsagesAsync(usage, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return ContentSaveOutcome.Saved;
        });
    }

    private async Task<ContentSaveOutcome> SaveOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return ContentSaveOutcome.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Nothing was written: the RowVersion predicate matched no row, so SQL Server applied none of
            // the batch. The tracked entities still hold the caller's edits, which is why the caller
            // answers 409 and the request ends here.
            return ContentSaveOutcome.ConcurrencyConflict;
        }
    }

    /// <summary>
    /// Makes the owner's usage rows exactly <see cref="ContentMediaUsageChange.Desired"/>: rows no longer
    /// wanted are removed, missing ones added, the rest left alone (so their <c>CreatedAtUtc</c> keeps
    /// saying when the picture first went there).
    /// </summary>
    private async Task ReplaceUsagesAsync(ContentMediaUsageChange usage, CancellationToken cancellationToken)
    {
        var ownerId = usage.OwnerId().ToString(CultureInfo.InvariantCulture);

        var existing = await dbContext.MediaUsages
            .Where(row => row.OwnerType == usage.OwnerType && row.OwnerId == ownerId)
            .ToListAsync(cancellationToken);

        foreach (var row in existing)
        {
            if (!usage.Desired.Contains(new ContentMediaUsage(row.MediaAssetId, row.UsageRole)))
            {
                dbContext.MediaUsages.Remove(row);
            }
        }

        var utcNow = clock.UtcNow.UtcDateTime;

        foreach (var wanted in usage.Desired)
        {
            if (existing.Exists(row => row.MediaAssetId == wanted.MediaAssetId
                && string.Equals(row.UsageRole, wanted.UsageRole, StringComparison.Ordinal)))
            {
                continue;
            }

            dbContext.MediaUsages.Add(new MediaUsage
            {
                MediaAssetId = wanted.MediaAssetId,
                OwnerType = usage.OwnerType,
                OwnerId = ownerId,
                UsageRole = wanted.UsageRole,
                CreatedAtUtc = utcNow,
            });
        }
    }
}
