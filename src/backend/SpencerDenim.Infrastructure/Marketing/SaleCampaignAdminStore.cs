using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;
using SpencerDenim.Application.Marketing.Promotions.Contracts;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Domain.Media;
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
internal sealed class SaleCampaignAdminReader(AppDbContext dbContext, IClock clock) : ISaleCampaignAdminReader
{
    public async Task<IReadOnlyList<AdminSaleCampaignDto>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await Projection()

            // Newest first: a staff member who has just created a campaign expects to see it, and the
            // list is short enough that no paging is warranted (doc 02 line 119 scopes ADM-14 to one
            // screen).
            .OrderByDescending(row => row.Id)
            .ToListAsync(cancellationToken);

        var now = clock.UtcNow.UtcDateTime;

        return rows.Select(row => ToDto(row, now)).ToList().AsReadOnly();
    }

    public async Task<AdminSaleCampaignDto?> FindAsync(int campaignId, CancellationToken cancellationToken)
    {
        var row = await Projection()
            .FirstOrDefaultAsync(candidate => candidate.Id == campaignId, cancellationToken);

        return row is null ? null : ToDto(row, clock.UtcNow.UtcDateTime);
    }

    /// <summary>
    /// Every undeleted campaign with its targets, projected to the columns the screen uses.
    /// </summary>
    /// <remarks>
    /// <c>IsDeleted</c> is excluded both ways — the global soft-delete filter and the predicate written
    /// out here — for the reason <see cref="PromoCodeAdminReader"/> gives: the predicate is what survives
    /// an <c>IgnoreQueryFilters()</c> that somebody adds later for an unrelated reason.
    /// </remarks>
    private IQueryable<CampaignRow> Projection() =>
        dbContext.SaleCampaigns
            .AsNoTracking()
            .Where(campaign => !campaign.IsDeleted)
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
                campaign.RowVersion,
                campaign.Targets
                    .Select(target => new TargetRow(
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
                    .ToList()));

    private static AdminSaleCampaignDto ToDto(CampaignRow row, DateTime utcNow) =>
        new(
            row.Id,
            row.Name,
            row.Description,
            row.DiscountType,
            row.DiscountValue,
            CampaignWindow.ToColombo(row.StartsAtUtc),
            CampaignWindow.ToColombo(row.EndsAtUtc),
            row.IsActive,
            row.BannerAssetId,
            row.Targets.Select(ToTargetDto).ToList().AsReadOnly(),
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
        byte[]? RowVersion,
        List<TargetRow> Targets);

    private sealed record TargetRow(
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
    /// Ready and not deleted, the same two conditions every other reader of a banner applies: a campaign
    /// pointing at an asset that is still uploading would render a broken image on the Sale page, and the
    /// foreign key alone would accept it.
    /// </remarks>
    public Task<bool> BannerExistsAsync(int mediaAssetId, CancellationToken cancellationToken) =>
        dbContext.MediaAssets.AnyAsync(
            asset => asset.Id == mediaAssetId
                && !asset.IsDeleted
                && asset.Status == MediaAssetStatus.Ready,
            cancellationToken);

    public async Task<SaleCampaignSaveOutcome> SaveAsync(
        int? actingUserId,
        CancellationToken cancellationToken)
    {
        auditColumns.CurrentUserId = actingUserId;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return SaleCampaignSaveOutcome.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Nothing was written: the UPDATE's RowVersion predicate matched no row, so SQL Server
            // applied none of the batch. The caller reloads and re-applies (doc 08 §1.5).
            return SaleCampaignSaveOutcome.ConcurrencyConflict;
        }
    }
}
