using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Admin.Reporting.Contracts;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Inventory.Abstractions;
using SpencerDenim.Application.Inventory.Contracts;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Inventory;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Inventory;

/// <summary>
/// The inventory screen's reads (ADM-12, doc 11 P2 step 1): the list, its filter choices, and one variant's
/// movement history.
/// <para>
/// <b>The status rule is doc 11 P2 step 9's</b>, the same as the dashboard's low-stock widget and the
/// inventory report: out of stock at 0 available (or no stock row), low at 1 to the variant's own threshold
/// or the shop default, in stock above. Out of stock is <b>never</b> low.
/// </para>
/// </summary>
internal sealed class InventoryReader(AppDbContext dbContext, IOptions<CatalogOptions> options) : IInventoryReader
{
    private readonly int _defaultThreshold = options.Value.DefaultLowStockThreshold;

    public async Task<InventoryListPage> ListAsync(InventoryListCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var threshold = _defaultThreshold;
        var scoped = await ScopeAsync(criteria, cancellationToken);

        var counts = new InventoryStatusCounts(
            await scoped.Where(InStock(threshold)).CountAsync(cancellationToken),
            await scoped.Where(Low(threshold)).CountAsync(cancellationToken),
            await scoped.Where(Out).CountAsync(cancellationToken));

        var matching = criteria.Status switch
        {
            InventoryStockFilter.InStock => scoped.Where(InStock(threshold)),
            InventoryStockFilter.LowStock => scoped.Where(Low(threshold)),
            InventoryStockFilter.OutOfStock => scoped.Where(Out),
            _ => scoped,
        };

        var total = await matching.CountAsync(cancellationToken);

        var rows = total == 0
            ? []
            : await Project(Order(matching, criteria.Sort, criteria.Direction)
                    .Skip((criteria.Page - 1) * criteria.PageSize)
                    .Take(criteria.PageSize))
                .ToListAsync(cancellationToken);

        return new InventoryListPage(
            rows.Select(ToRow).ToList(),
            criteria.Page,
            criteria.PageSize,
            total,
            threshold,
            counts);
    }

    public async Task<InventoryFilterOptions> FilterOptionsAsync(CancellationToken cancellationToken)
    {
        var departments = await dbContext.Departments
            .AsNoTracking()
            .OrderBy(department => department.SortOrder)
            .ThenBy(department => department.Name)
            .Select(department => new InventoryDepartmentOption(department.Id, department.Name))
            .ToListAsync(cancellationToken);

        // Deleted categories are left out by the context's soft-delete filter. Ordered by path, so each
        // category is followed by the ones below it and the screen only has to indent by depth.
        var categories = await dbContext.Categories
            .AsNoTracking()
            .OrderBy(category => category.DepartmentId)
            .ThenBy(category => category.Path)
            .Select(category => new InventoryCategoryOption(category.Id, category.DepartmentId, category.Name, category.Depth))
            .ToListAsync(cancellationToken);

        return new InventoryFilterOptions(_defaultThreshold, departments, categories);
    }

    public async Task<InventoryRow?> FindAsync(int variantId, CancellationToken cancellationToken)
    {
        var row = await Project(AnyVariant().Where(variant => variant.Id == variantId))
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : ToRow(row);
    }

    public async Task<InventoryHistoryPage?> HistoryAsync(
        int variantId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (await FindAsync(variantId, cancellationToken) is not { } variant)
        {
            return null;
        }

        var history = HistoryQuery(variantId);
        var total = await history.CountAsync(cancellationToken);

        var rows = total == 0
            ? []
            : await history
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(movement => new
                {
                    movement.Id,
                    movement.MovementType,
                    movement.OnHandDelta,
                    movement.ReservedDelta,
                    movement.OnHandAfter,
                    movement.ReservedAfter,
                    OrderNumber = movement.Order == null ? null : movement.Order.OrderNumber,
                    movement.BatchId,
                    movement.Reason,
                    CreatedBy = dbContext.Users
                        .Where(user => user.Id == movement.CreatedByUserId)
                        .Select(user => user.Email)
                        .FirstOrDefault(),
                    movement.CreatedAtUtc,
                })
                .ToListAsync(cancellationToken);

        return new InventoryHistoryPage(
            variant,
            rows.Select(row => new InventoryMovementRow(
                    row.Id,
                    row.MovementType.ToString(),
                    row.OnHandDelta,
                    row.ReservedDelta,
                    row.OnHandAfter,
                    row.ReservedAfter,
                    row.OrderNumber,
                    row.BatchId,
                    row.Reason,
                    row.CreatedBy,
                    row.CreatedAtUtc))
                .ToList(),
            page,
            pageSize,
            total);
    }

    /// <summary>
    /// One variant's movements, newest first: the order <c>IX_InventoryMovements_Variant_Created</c> stores
    /// them in, so the page is a seek and a range read with no sort. Internal for the plan test.
    /// </summary>
    internal IQueryable<InventoryMovement> HistoryQuery(int variantId) =>
        dbContext.InventoryMovements
            .AsNoTracking()
            .Where(movement => movement.VariantId == variantId)
            .OrderByDescending(movement => movement.CreatedAtUtc)
            .ThenByDescending(movement => movement.Id);

    /// <summary>Active variants of products that are not deleted, narrowed by search, department and category.</summary>
    private async Task<IQueryable<ProductVariant>> ScopeAsync(InventoryListCriteria criteria, CancellationToken cancellationToken)
    {
        var scoped = dbContext.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.IsActive && !variant.Product!.IsDeleted);

        if (criteria.NormalisedSearch is { } search)
        {
            scoped = scoped.Where(variant =>
                variant.Sku.StartsWith(search)
                || variant.Product!.ProductCode.StartsWith(search)
                || variant.Product.Name.Contains(search));
        }

        if (criteria.DepartmentId is { } departmentId)
        {
            scoped = scoped.Where(variant => variant.Product!.DepartmentId == departmentId);
        }

        if (criteria.CategoryId is { } categoryId)
        {
            // The category and everything below it: its materialised path (/12/34/) is a prefix of each
            // descendant's. An unknown or deleted category matches nothing rather than everything.
            var path = await dbContext.Categories
                .AsNoTracking()
                .Where(category => category.Id == categoryId)
                .Select(category => category.Path)
                .FirstOrDefaultAsync(cancellationToken);

            if (path is null)
            {
                return scoped.Where(_ => false);
            }

            var below = dbContext.Categories.Where(category => category.Path.StartsWith(path)).Select(category => category.Id);
            scoped = scoped.Where(variant => below.Contains(variant.Product!.PrimaryCategoryId));
        }

        return scoped;
    }

    /// <summary>Every variant, active or not, of a product that is not deleted: what a single row may show.</summary>
    private IQueryable<ProductVariant> AnyVariant() =>
        dbContext.ProductVariants
            .AsNoTracking()
            .Where(variant => !variant.Product!.IsDeleted);

    private static IOrderedQueryable<ProductVariant> Order(
        IQueryable<ProductVariant> variants,
        InventorySort sort,
        InventorySortDirection direction)
    {
        var descending = direction == InventorySortDirection.Desc;

        // Every order ends on the variant id, so a page boundary never falls between two equal rows
        // differently from one request to the next.
        return sort switch
        {
            InventorySort.Sku => By(variants, variant => variant.Sku, descending).ThenBy(variant => variant.Id),
            InventorySort.OnHand => By(variants, variant => variant.StockLevel == null ? 0 : variant.StockLevel.OnHand, descending)
                .ThenBy(variant => variant.Id),
            InventorySort.Reserved => By(variants, variant => variant.StockLevel == null ? 0 : variant.StockLevel.Reserved, descending)
                .ThenBy(variant => variant.Id),
            InventorySort.Available => By(variants, variant => variant.StockLevel == null ? 0 : variant.StockLevel.Available, descending)
                .ThenBy(variant => variant.Id),
            _ => By(variants, variant => variant.Product!.Name, descending)
                .ThenBy(variant => variant.ProductId)
                .ThenBy(variant => variant.SortOrder)
                .ThenBy(variant => variant.Id),
        };
    }

    private static IOrderedQueryable<ProductVariant> By<TKey>(
        IQueryable<ProductVariant> variants,
        Expression<Func<ProductVariant, TKey>> key,
        bool descending) =>
        descending ? variants.OrderByDescending(key) : variants.OrderBy(key);

    private static IQueryable<VariantStock> Project(IQueryable<ProductVariant> variants) =>
        variants.Select(variant => new VariantStock(
            variant.Id,
            variant.ProductId,
            variant.Product!.Name,
            variant.Product.ProductCode,
            variant.Product.Department!.Name,
            variant.Product.PrimaryCategory == null ? null : variant.Product.PrimaryCategory.Name,
            variant.Sku,
            variant.ProductColour!.Name,
            variant.Size!.Label,
            variant.StockLevel != null,
            variant.StockLevel == null ? 0 : variant.StockLevel.OnHand,
            variant.StockLevel == null ? 0 : variant.StockLevel.Reserved,
            variant.StockLevel == null ? 0 : variant.StockLevel.Available,
            variant.StockLevel == null ? null : variant.StockLevel.LowStockThreshold,
            variant.StockLevel == null ? null : variant.StockLevel.UpdatedAtUtc));

    private InventoryRow ToRow(VariantStock row)
    {
        var applied = StockAvailability.ResolveThreshold(row.OwnThreshold, _defaultThreshold);

        return new InventoryRow(
            row.VariantId,
            row.ProductId,
            row.ProductName,
            row.ProductCode,
            row.DepartmentName,
            row.CategoryName,
            row.Sku,
            row.Colour,
            row.Size,
            row.HasStockRecord,
            row.OnHand,
            row.Reserved,
            row.Available,
            applied,
            row.OwnThreshold,
            StockAvailability.ForVariant(row.Available, row.OwnThreshold, _defaultThreshold).ToString(),
            row.UpdatedAtUtc);
    }

    // The three statuses as SQL predicates, which must agree with StockAvailability.ForVariant above:
    // InventoryStatusRuleTests holds them to the same rows. A variant with no stock row has nothing available.
    internal static Expression<Func<ProductVariant, bool>> Low(int threshold) =>
        variant => variant.StockLevel != null
            && variant.StockLevel.Available > 0
            && variant.StockLevel.Available <= (variant.StockLevel.LowStockThreshold ?? threshold);

    internal static Expression<Func<ProductVariant, bool>> InStock(int threshold) =>
        variant => variant.StockLevel != null
            && variant.StockLevel.Available > 0
            && variant.StockLevel.Available > (variant.StockLevel.LowStockThreshold ?? threshold);

    internal static readonly Expression<Func<ProductVariant, bool>> Out =
        variant => variant.StockLevel == null || variant.StockLevel.Available <= 0;

    private sealed record VariantStock(
        int VariantId,
        int ProductId,
        string ProductName,
        string ProductCode,
        string DepartmentName,
        string? CategoryName,
        string Sku,
        string Colour,
        string Size,
        bool HasStockRecord,
        int OnHand,
        int Reserved,
        int Available,
        int? OwnThreshold,
        DateTime? UpdatedAtUtc);
}
