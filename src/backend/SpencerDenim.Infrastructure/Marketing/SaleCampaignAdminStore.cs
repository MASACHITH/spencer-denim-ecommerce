using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;
using SpencerDenim.Application.Marketing.Promotions.Contracts;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Catalog;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Interceptors;

namespace SpencerDenim.Infrastructure.Marketing;

/// <summary>
/// The EF Core side of <see cref="ISaleCampaignAdminReader"/> (ADM-14).
/// </summary>
/// <remarks>
/// <b>The two date columns are converted to Colombo wall-clock here, at the edge</b>, for the reason
/// <see cref="PromoCodeAdminReader"/> gives: doc 06 stores UTC and displays Colombo, the admin form types
/// a wall-clock time, and the conversion is in memory rather than in SQL because
/// <c>AT TIME ZONE 'Sri Lanka Standard Time'</c> depends on the host's time-zone database — the
/// portability problem <c>ColomboTime</c> exists to avoid.
/// <para>
/// <b>Each target carries the name of what it points at</b>, so the list can say "Women › Shirts &amp;
/// Blouses" rather than "category 12". The category's name is built from its own name and its
/// department's, not from <c>Categories.Path</c>: the path holds ids, and resolving it would be one query
/// per ancestor for a label.
/// </para>
/// </remarks>
internal sealed class SaleCampaignAdminReader(
    AppDbContext dbContext,
    IClock clock,
    IOptions<CatalogOptions> catalogOptions) : ISaleCampaignAdminReader
{
    public async Task<IReadOnlyList<AdminSaleCampaignDto>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await Project(
                Undeleted()

                    // Newest first: a staff member who has just created a campaign expects to see it, and
                    // the list is short enough that no paging is warranted (doc 02 line 119 scopes ADM-14
                    // to one screen).
                    .OrderByDescending(campaign => campaign.Id))
            .ToListAsync(cancellationToken);

        return await ToDtosAsync(rows, cancellationToken);
    }

    public async Task<AdminSaleCampaignDto?> FindAsync(int campaignId, CancellationToken cancellationToken)
    {
        var rows = await Project(Undeleted().Where(campaign => campaign.Id == campaignId))
            .ToListAsync(cancellationToken);

        var dtos = await ToDtosAsync(rows, cancellationToken);

        return dtos.Count == 0 ? null : dtos[0];
    }

    /// <summary>
    /// Every campaign a staff member may see.
    /// </summary>
    /// <remarks>
    /// <c>IsDeleted</c> is excluded both ways — the global soft-delete filter and the predicate written
    /// out here — for the reason <see cref="PromoCodeAdminReader"/> gives: the predicate is what survives
    /// an <c>IgnoreQueryFilters()</c> that somebody adds later for an unrelated reason.
    /// </remarks>
    private IQueryable<SaleCampaign> Undeleted() =>
        dbContext.SaleCampaigns.AsNoTracking().Where(campaign => !campaign.IsDeleted);

    /// <summary>
    /// The columns the screen uses, <b>without the targets</b>.
    /// </summary>
    /// <remarks>
    /// <b>Every filter and the ordering go in before this, on the entity, and that is not style.</b> The
    /// first version of this reader projected first and then wrote <c>.Where(row =&gt; row.Id == id)</c>
    /// over the projected record; EF Core could not translate it — "The LINQ expression
    /// 'DbSet&lt;SaleCampaign&gt;() … ).Id == @campaignId)' could not be translated" — and it arrived as
    /// a 500 from the create endpoint, because the create re-reads the row it has just written.
    /// </remarks>
    private static IQueryable<CampaignRow> Project(IQueryable<SaleCampaign> campaigns) =>
        campaigns
            .Select(campaign => new CampaignRow(
                campaign.Id,
                campaign.Name,
                campaign.Description,
                campaign.DiscountType,
                campaign.DiscountValue,
                campaign.StartsAtUtc,
                campaign.EndsAtUtc,
                campaign.IsActive,
                campaign.BannerAssetId,

                // The banner, only when the storefront would show it: ready, a public image, and not
                // deleted (the global filter turns a deleted asset's navigation into null). Anything
                // else reads as "no banner", which is what the Sale page will show.
                campaign.BannerAsset != null
                    && campaign.BannerAsset.Status == MediaAssetStatus.Ready
                    && campaign.BannerAsset.MediaType == MediaAssetType.Image
                    && campaign.BannerAsset.Visibility == MediaVisibility.Public
                    ? new BannerRow(
                        campaign.BannerAsset.PublicId,
                        campaign.BannerAsset.StorageKey,
                        campaign.BannerAsset.RenditionSetVersion,
                        campaign.BannerAsset.Width,
                        campaign.BannerAsset.AltText)
                    : null,
                campaign.RowVersion));

    /// <summary>
    /// The targets of these campaigns, with the name of whatever each one points at, in one query.
    /// </summary>
    /// <remarks>
    /// <b>A second query rather than a nested collection in the projection above, and that is not a
    /// preference.</b> The first version of this reader projected the targets inline, with each one's
    /// department, category and product names reached through navigations, and EF Core could not
    /// translate it: "The LINQ expression 'DbSet&lt;SaleCampaign&gt;()' … could not be translated",
    /// which arrived as a 500 from the create endpoint and nothing else. Two queries also keep the
    /// shape honest — a campaign list of ten rows is two round-trips whatever the targets do, instead of
    /// one query whose cost depends on how many products somebody listed.
    /// </remarks>
    private async Task<IReadOnlyList<AdminSaleCampaignDto>> ToDtosAsync(
        List<CampaignRow> campaigns,
        CancellationToken cancellationToken)
    {
        if (campaigns.Count == 0)
        {
            return [];
        }

        var ids = campaigns.Select(campaign => campaign.Id).ToList();

        var targets = await dbContext.SaleCampaignTargets
            .AsNoTracking()
            .Where(target => ids.Contains(target.SaleCampaignId))
            .OrderBy(target => target.Id)
            .Select(target => new TargetRow(
                target.SaleCampaignId,
                target.TargetType,
                target.DepartmentId,
                target.CategoryId,
                target.ProductId,
                target.IncludeSubcategories,
                target.Department == null ? null : target.Department.Name,
                target.Category == null ? null : target.Category.Name,
                target.Category == null || target.Category.Department == null
                    ? null
                    : target.Category.Department.Name,
                target.Product == null ? null : target.Product.Name))
            .ToListAsync(cancellationToken);

        var byCampaign = targets
            .GroupBy(target => target.SaleCampaignId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var now = clock.UtcNow.UtcDateTime;

        return campaigns
            .Select(campaign => ToDto(
                campaign,
                byCampaign.TryGetValue(campaign.Id, out var own) ? own : [],
                now))
            .ToList()
            .AsReadOnly();
    }

    private AdminSaleCampaignDto ToDto(
        CampaignRow row,
        List<TargetRow> targets,
        DateTime utcNow) =>
        new(
            row.Id,
            row.Name,
            row.Description,
            row.DiscountType,
            row.DiscountValue,
            CampaignWindow.ToColombo(row.StartsAtUtc),
            CampaignWindow.ToColombo(row.EndsAtUtc),
            row.IsActive,
            row.Banner is { } banner
                ? new AdminSaleCampaignBannerDto(
                    banner.PublicId,

                    // The `card` rendition: the form's preview is a few hundred pixels wide, and the
                    // full-width file is the storefront's to load, not the admin screen's.
                    MediaUrlBuilder.ToRenditionUrl(
                        catalogOptions.Value.MediaBaseUrl,
                        new MediaRef(banner.StorageKey, banner.PublicId, banner.RenditionSetVersion, banner.Width),
                        MediaRenditions.Card),
                    string.IsNullOrWhiteSpace(banner.AltText) ? null : banner.AltText)
                : null,

            // The column still names a picture, but not one the shop can show any more.
            row.Banner is null && row.BannerAssetId is not null,
            targets.Select(ToTargetDto).ToList().AsReadOnly(),
            CampaignWindow.StatusOf(row.IsActive, row.StartsAtUtc, row.EndsAtUtc, utcNow),
            row.RowVersion is { } bytes ? Convert.ToBase64String(bytes) : string.Empty);

    private static AdminSaleCampaignTargetDto ToTargetDto(TargetRow row) =>
        new(
            row.TargetType,
            row.DepartmentId,
            row.CategoryId,
            row.ProductId,
            row.IncludeSubcategories,

            // "Women", "Women › Shirts & Blouses", "Relaxed Linen Blend Shirt". The fallback is the one
            // case the navigation can be null in: a soft-deleted category or product, which the global
            // filter removes from the navigation while the target row survives — a campaign may legally
            // outlive a product it named, and the screen has to say so rather than render nothing.
            row.TargetType switch
            {
                SaleCampaignTargetType.Department => row.DepartmentName ?? Removed,
                SaleCampaignTargetType.Category => row.CategoryName is { } category
                    ? row.CategoryDepartmentName is { } department
                        ? $"{department} › {category}"
                        : category
                    : Removed,
                _ => row.ProductName ?? Removed,
            });

    /// <summary>
    /// What the list calls a target whose row has been soft-deleted out from under the campaign.
    /// </summary>
    /// <remarks>
    /// Said rather than left blank. A staff member looking at a campaign that is quietly discounting
    /// fewer products than they think needs to be told which line is the reason, and "(removed from the
    /// catalogue)" is actionable in a way an empty cell is not.
    /// </remarks>
    private const string Removed = "(removed from the catalogue)";

    private sealed record CampaignRow(
        int Id,
        string Name,
        string? Description,
        SaleDiscountType DiscountType,
        decimal DiscountValue,
        DateTime StartsAtUtc,
        DateTime EndsAtUtc,
        bool IsActive,
        int? BannerAssetId,
        BannerRow? Banner,
        byte[]? RowVersion);

    private sealed record BannerRow(
        Guid PublicId,
        string StorageKey,
        byte? RenditionSetVersion,
        int? Width,
        string? AltText);

    private sealed record TargetRow(
        int SaleCampaignId,
        SaleCampaignTargetType TargetType,
        int? DepartmentId,
        int? CategoryId,
        int? ProductId,
        bool IncludeSubcategories,
        string? DepartmentName,
        string? CategoryName,
        string? CategoryDepartmentName,
        string? ProductName);
}

/// <summary>
/// The EF Core side of <see cref="ISaleCampaignAdminStore"/> (ADM-14).
/// <para>
/// It hands out tracked entities and keeps one unit of work, so <c>SaleCampaignWriteHandlers</c> decides
/// what a change is and this class decides only how it reaches SQL Server. <b>The one judgement it makes
/// is the one the database alone can make: a row somebody else changed first.</b> There is no
/// duplicate-key branch, because doc 08 §3.6 gives <c>marketing.SaleCampaigns</c> no unique index — two
/// campaigns may share a name and may target the same thing, which "lowest resulting price wins" makes
/// legal rather than a conflict.
/// </para>
/// </summary>
internal sealed class SaleCampaignAdminStore(
    AppDbContext dbContext,
    AuditAndSoftDeleteInterceptor auditColumns) : ISaleCampaignAdminStore
{
    /// <inheritdoc />
    /// <remarks>
    /// The targets are loaded with the campaign, because the write path replaces the whole collection and
    /// a <c>Clear()</c> on a collection EF has not loaded deletes nothing.
    /// </remarks>
    public Task<SaleCampaign?> FindAsync(int campaignId, CancellationToken cancellationToken) =>
        dbContext.SaleCampaigns
            .Include(campaign => campaign.Targets)
            .FirstOrDefaultAsync(
                campaign => campaign.Id == campaignId && !campaign.IsDeleted,
                cancellationToken);

    public void Add(SaleCampaign campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        dbContext.SaleCampaigns.Add(campaign);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Three queries, not one per target.</b> A campaign with twenty product targets would otherwise
    /// cost twenty round-trips inside a validation pass. Departments have no soft delete, so only their
    /// <c>IsActive</c> is relevant — and it is deliberately <i>not</i> filtered on: a campaign may be set
    /// up for a department the client has temporarily switched off, and refusing that would be inventing
    /// a rule. Categories and products are filtered by the global soft-delete filter, which is the
    /// behaviour that matters: a target pointing at a removed row is a mistake worth a field error.
    /// </remarks>
    public async Task<SaleCampaignTargetExistence> WhichTargetsExistAsync(
        IReadOnlyCollection<int> departmentIds,
        IReadOnlyCollection<int> categoryIds,
        IReadOnlyCollection<int> productIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(departmentIds);
        ArgumentNullException.ThrowIfNull(categoryIds);
        ArgumentNullException.ThrowIfNull(productIds);

        var departments = departmentIds.Count == 0
            ? []
            : await dbContext.Departments
                .AsNoTracking()
                .Where(department => departmentIds.Contains(department.Id))
                .Select(department => department.Id)
                .ToListAsync(cancellationToken);

        var categories = categoryIds.Count == 0
            ? []
            : await dbContext.Categories
                .AsNoTracking()
                .Where(category => categoryIds.Contains(category.Id))
                .Select(category => category.Id)
                .ToListAsync(cancellationToken);

        var products = productIds.Count == 0
            ? []
            : await dbContext.Products
                .AsNoTracking()
                .Where(product => productIds.Contains(product.Id))
                .Select(product => product.Id)
                .ToListAsync(cancellationToken);

        return new SaleCampaignTargetExistence(
            departments.ToHashSet(),
            categories.ToHashSet(),
            products.ToHashSet());
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>IsDeleted</c> is excluded both ways, the global filter and the predicate written out, for the
    /// reason <see cref="SaleCampaignAdminReader"/> gives. The type and visibility conditions are the ones
    /// <c>CatalogStructureStore.ReadyImageAssetExistsAsync</c> applies to a category's banner.
    /// </remarks>
    public Task<SaleCampaignBannerCandidate?> FindBannerAsync(Guid publicId, CancellationToken cancellationToken) =>
        dbContext.MediaAssets
            .AsNoTracking()
            .Where(asset => asset.PublicId == publicId
                && !asset.IsDeleted
                && asset.Status == MediaAssetStatus.Ready
                && asset.MediaType == MediaAssetType.Image
                && asset.Visibility == MediaVisibility.Public)
            .Select(asset => new SaleCampaignBannerCandidate(asset.Id, asset.AltText))
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// <b>An edit and a delete are one <c>SaveChanges</c></b>: the campaign already has its id, so its usage
    /// row is enlisted beside the change and SQL Server applies the batch whole or not at all, including
    /// when the row version refuses it.
    /// <para>
    /// <b>A create is two, inside one transaction</b>, because <c>MediaUsages.OwnerId</c> is the campaign's
    /// id and the campaign has none until its insert. That is the arrangement
    /// <c>ProductWriteStore.AddProductAsync</c> uses, and for the same reason: a failure between the two
    /// would leave a banner nothing protects, which no screen would show as wrong until somebody deleted
    /// the picture the Sale page was drawing. <c>EnableRetryOnFailure</c> refuses a user-initiated
    /// transaction outside its execution strategy, so the <i>attempt</i> is what retries, and a retried
    /// attempt starts from a clean change tracker with the pending rows re-added and their generated keys
    /// cleared.
    /// </para>
    /// </remarks>
    public async Task<SaleCampaignSaveOutcome> SaveAsync(
        SaleCampaign campaign,
        int? actingUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        auditColumns.CurrentUserId = actingUserId;

        try
        {
            return campaign.Id != 0
                ? await UpdateAsync(campaign, cancellationToken)
                : await InsertAsync(campaign, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Nothing was written: the UPDATE's RowVersion predicate matched no row, so SQL Server
            // applied none of the batch. The caller reloads and re-applies (doc 08 §1.5).
            return SaleCampaignSaveOutcome.ConcurrencyConflict;
        }
    }

    /// <summary>
    /// An edit or a removal: the banner check under the asset lock, then one <c>SaveChanges</c>, in one
    /// transaction.
    /// </summary>
    /// <remarks>
    /// The usage rows are enlisted <i>before</i> the execution strategy, because a retried attempt that
    /// enlisted them again would add a second, duplicate row. Changes are accepted only after the commit,
    /// so a retried attempt still has every change to write.
    /// </remarks>
    private async Task<SaleCampaignSaveOutcome> UpdateAsync(SaleCampaign campaign, CancellationToken cancellationToken)
    {
        await SyncBannerUsageAsync(campaign, cancellationToken);

        var strategy = dbContext.Database.CreateExecutionStrategy();

        var outcome = await strategy.ExecuteAsync(
            async token =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(token);

                if (!await BannerStillUsableAsync(campaign, token))
                {
                    await transaction.RollbackAsync(token);
                    return SaleCampaignSaveOutcome.BannerUnavailable;
                }

                await dbContext.SaveChangesAsync(acceptAllChangesOnSuccess: false, token);
                await transaction.CommitAsync(token);

                return SaleCampaignSaveOutcome.Saved;
            },
            cancellationToken);

        if (outcome == SaleCampaignSaveOutcome.Saved)
        {
            dbContext.ChangeTracker.AcceptAllChanges();
        }

        return outcome;
    }

    /// <summary>
    /// <b>The banner is re-checked under an update lock on its <c>media.MediaAssets</c> row</b>, inside the
    /// save's transaction, and <c>MediaService.DeleteAsync</c> takes the same lock before its own checks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handler's earlier check (<see cref="FindBannerAsync"/>) is an RCSI read, so a library delete
    /// could commit between it and this save, and the campaign would then commit a usage row for a deleted
    /// picture. With both paths locking the asset row first, exactly one of them wins. If the save goes
    /// first, the delete waits, then finds the usage row and refuses with 409. If the delete goes first,
    /// this read waits, then sees the asset deleted and the save is refused.
    /// </para>
    /// <para>
    /// <c>READCOMMITTEDLOCK</c> with <c>UPDLOCK</c>, so the read takes the lock and waits for the delete's
    /// commit rather than reading the last committed version (doc 08 §5.2's RCSI rule). Lock order is
    /// "the asset row first" in both paths, and neither path takes a second lock the other already holds, so
    /// they cannot deadlock (doc 08 §5.3). A removed campaign, or one with no banner, locks nothing.
    /// </para>
    /// </remarks>
    private async Task<bool> BannerStillUsableAsync(SaleCampaign campaign, CancellationToken cancellationToken)
    {
        if (campaign.IsDeleted || campaign.BannerAssetId is not { } assetId)
        {
            return true;
        }

        var usable = await dbContext.Database
            .SqlQuery<int>(
                $"SELECT CASE WHEN a.IsDeleted = 0 AND a.Status = 1 AND a.MediaType = 1 AND a.Visibility = 1 THEN 1 ELSE 0 END AS Value FROM media.MediaAssets a WITH (UPDLOCK, READCOMMITTEDLOCK, ROWLOCK) WHERE a.Id = {assetId}")
            .ToListAsync(cancellationToken);

        return usable.Count == 1 && usable[0] == 1;
    }

    private async Task<SaleCampaignSaveOutcome> InsertAsync(SaleCampaign campaign, CancellationToken cancellationToken)
    {
        // What this unit of work is about to insert: the campaign, its targets and the audit row the
        // handler enlisted. Captured once, so a retried attempt can put exactly these back.
        var pending = dbContext.ChangeTracker
            .Entries()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity)
            .ToList();

        var strategy = dbContext.Database.CreateExecutionStrategy();
        var attempt = 0;

        return await strategy.ExecuteAsync(
            async token =>
            {
                if (attempt++ > 0)
                {
                    dbContext.ChangeTracker.Clear();

                    foreach (var entity in pending)
                    {
                        ResetGeneratedValues(entity);
                    }

                    foreach (var entity in pending)
                    {
                        dbContext.Add(entity);
                    }
                }

                await using var transaction = await dbContext.Database.BeginTransactionAsync(token);

                // First, before anything is written: see BannerStillUsableAsync.
                if (!await BannerStillUsableAsync(campaign, token))
                {
                    await transaction.RollbackAsync(token);
                    return SaleCampaignSaveOutcome.BannerUnavailable;
                }

                await dbContext.SaveChangesAsync(token);
                await SyncBannerUsageAsync(campaign, token);
                await dbContext.SaveChangesAsync(token);

                await transaction.CommitAsync(token);

                return SaleCampaignSaveOutcome.Saved;
            },
            cancellationToken);
    }

    /// <summary>
    /// Clears the values SQL Server generated on a failed attempt (identity keys, row versions, and the
    /// foreign keys fixed up from them), so the retry inserts rather than updates.
    /// </summary>
    private void ResetGeneratedValues(object entity)
    {
        var entityType = dbContext.Model.FindEntityType(entity.GetType());

        if (entityType is null)
        {
            return;
        }

        foreach (var property in entityType.GetProperties())
        {
            var generated = property.ValueGenerated != ValueGenerated.Never
                && (property.IsPrimaryKey() || property.IsConcurrencyToken);

            if ((generated || property.IsForeignKey())
                && property.PropertyInfo is { CanWrite: true } info
                && property.ClrType.IsValueType
                && Nullable.GetUnderlyingType(property.ClrType) is null)
            {
                info.SetValue(entity, Activator.CreateInstance(property.ClrType));
            }
            else if (generated && property.PropertyInfo is { CanWrite: true } reference)
            {
                reference.SetValue(entity, null);
            }
        }
    }

    /// <summary>
    /// Brings this campaign's <c>media.MediaUsages</c> rows in line with its banner (doc 08 §3.3).
    /// </summary>
    /// <remarks>
    /// <b>A removed campaign owns no usage row.</b> Its banner column is left as it was, so the audit trail
    /// and the purge still see it, but a campaign nobody can see or restore from the screen must not stop
    /// the library deleting the picture. That is the rule <c>MediaService.DeleteAsync</c> follows for the
    /// rows a soft-deleted asset owns. A row that is still wanted is left untouched, so its
    /// <c>CreatedAtUtc</c>, which the library shows as "attached on", does not move on every save.
    /// </remarks>
    private async Task SyncBannerUsageAsync(SaleCampaign campaign, CancellationToken cancellationToken)
    {
        var ownerId = campaign.Id.ToString(CultureInfo.InvariantCulture);

        var existing = await dbContext.MediaUsages
            .Where(usage => usage.OwnerType == MediaUsageOwnerTypes.SaleCampaign && usage.OwnerId == ownerId)
            .ToListAsync(cancellationToken);

        int? wanted = campaign.IsDeleted ? null : campaign.BannerAssetId;

        foreach (var gone in existing.Where(usage =>
            usage.MediaAssetId != wanted
            || !string.Equals(usage.UsageRole, MediaUsageRoles.Banner, StringComparison.Ordinal)))
        {
            dbContext.MediaUsages.Remove(gone);
        }

        if (wanted is { } assetId
            && !existing.Any(usage => usage.MediaAssetId == assetId
                && string.Equals(usage.UsageRole, MediaUsageRoles.Banner, StringComparison.Ordinal)))
        {
            dbContext.MediaUsages.Add(new MediaUsage
            {
                MediaAssetId = assetId,
                OwnerType = MediaUsageOwnerTypes.SaleCampaign,
                OwnerId = ownerId,
                UsageRole = MediaUsageRoles.Banner,
            });
        }
    }
}
