using SpencerDenim.Application.Admin.Reporting.Contracts;

namespace SpencerDenim.Application.Inventory.Contracts;

// ---------------------------------------------------------------------------------------------------------
// The inventory screen (ADM-12; doc 11 P2). Reads need Inventory.View, writes Inventory.Edit.
// ---------------------------------------------------------------------------------------------------------

/// <summary>How the inventory list is ordered.</summary>
public enum InventorySort
{
    /// <summary>Product name, then the variant's own order on the product. The default.</summary>
    Product,

    Sku,

    OnHand,

    Reserved,

    Available,
}

/// <summary>Which way <see cref="InventorySort"/> runs.</summary>
public enum InventorySortDirection
{
    Asc,

    Desc,
}

/// <summary>The inventory list's filters, paging and order.</summary>
/// <param name="Search">Part of a product name, or the start of a SKU or product code; null for every variant.</param>
/// <param name="Status">In stock, low, out of stock, or all.</param>
/// <param name="DepartmentId">One department's products only, or null.</param>
/// <param name="CategoryId">Products whose main category is this one or below it, or null.</param>
public sealed record InventoryListCriteria(
    string? Search,
    InventoryStockFilter Status,
    int? DepartmentId,
    int? CategoryId,
    InventorySort Sort,
    InventorySortDirection Direction,
    int Page,
    int PageSize)
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;

    public const int MaxSearchLength = InventoryReportFilter.MaxSearchLength;

    /// <summary>The highest page honoured, so <c>(page - 1) * pageSize</c> cannot wrap negative.</summary>
    public const int MaxPage = int.MaxValue / MaxPageSize;

    /// <summary>The trimmed search, cut to <see cref="MaxSearchLength"/>, or null.</summary>
    public string? NormalisedSearch => new InventoryReportFilter(Search, Status).NormalisedSearch;
}

/// <summary>
/// One page of the inventory list: <b>active</b> variants of products that are not deleted, live.
/// </summary>
/// <param name="DefaultThreshold">The threshold a variant without its own uses.</param>
/// <param name="Counts">
/// In stock, low and out of stock over the search, department and category — <b>not</b> over the status
/// filter, so the counts say what each status choice would show.
/// </param>
public sealed record InventoryListPage(
    IReadOnlyList<InventoryRow> Rows,
    int Page,
    int PageSize,
    int TotalCount,
    int DefaultThreshold,
    InventoryStatusCounts Counts);

/// <summary>One variant's stock now.</summary>
/// <param name="CategoryName">The product's main category; null if that category has been removed.</param>
/// <param name="HasStockRecord">
/// False only for a variant with no <c>inventory.StockLevels</c> row, which nothing in this build creates
/// but a support script could. Its figures read 0 and it cannot be edited until the row exists.
/// </param>
/// <param name="Threshold">The threshold that applies: the variant's own, or the shop default.</param>
/// <param name="OwnThreshold">The variant's own threshold; null means it uses the shop default.</param>
/// <param name="StockStatus"><c>InStock</c>, <c>LowStock</c> or <c>OutOfStock</c>. Out of stock is never low.</param>
public sealed record InventoryRow(
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
    int Threshold,
    int? OwnThreshold,
    string StockStatus,
    DateTime? UpdatedAtUtc);

/// <summary>The choices the screen's department and category filters offer.</summary>
public sealed record InventoryFilterOptions(
    int DefaultThreshold,
    IReadOnlyList<InventoryDepartmentOption> Departments,
    IReadOnlyList<InventoryCategoryOption> Categories);

public sealed record InventoryDepartmentOption(int Id, string Name);

/// <param name="Depth">1 for a top-level category, so the screen can indent the ones below it.</param>
public sealed record InventoryCategoryOption(int Id, int DepartmentId, string Name, int Depth);

/// <summary>One variant's movement history, newest first, with its stock now.</summary>
public sealed record InventoryHistoryPage(
    InventoryRow Variant,
    IReadOnlyList<InventoryMovementRow> Rows,
    int Page,
    int PageSize,
    int TotalCount);

/// <summary>One ledger row.</summary>
/// <param name="MovementType">
/// <c>Initial</c>, <c>Receive</c>, <c>AdjustIn</c>, <c>AdjustOut</c>, <c>Reserve</c>, <c>Release</c>,
/// <c>Sale</c>, <c>Restock</c> or <c>CountCorrection</c>.
/// </param>
/// <param name="OrderNumber">The order that caused it, for the automatic movements; null for a manual one.</param>
/// <param name="CreatedBy">The staff member's email; null for the system.</param>
public sealed record InventoryMovementRow(
    long MovementId,
    string MovementType,
    int OnHandDelta,
    int ReservedDelta,
    int OnHandAfter,
    int ReservedAfter,
    string? OrderNumber,
    Guid? BatchId,
    string? Reason,
    string? CreatedBy,
    DateTime CreatedAtUtc);

/// <summary>What happened to one requested change.</summary>
public enum StockChangeOutcome
{
    /// <summary>Applied, with a movement row and an audit row.</summary>
    Applied,

    /// <summary>
    /// Refused because it would leave on hand below what unpaid orders hold, or above the ceiling. Nothing
    /// changed.
    /// </summary>
    Refused,

    /// <summary>A SetCount whose on-hand figure moved since the screen loaded it. Nothing changed; re-confirm.</summary>
    Changed,

    /// <summary>No such variant, or it has no stock record. Nothing changed.</summary>
    NotFound,
}

/// <summary>The answer to a stock change: one result per requested row, in the order they were sent.</summary>
/// <param name="BatchId">Shared by every applied row of a multi-row update; null for a single change.</param>
public sealed record StockChangeResultDto(
    Guid? BatchId,
    int AppliedCount,
    int NotAppliedCount,
    IReadOnlyList<StockChangeRowResult> Rows);

/// <summary>One row's result, with the variant's figures as they now are.</summary>
/// <param name="Outcome"><c>Applied</c>, <c>Refused</c>, <c>Changed</c> or <c>NotFound</c>.</param>
/// <param name="Message">Why it was not applied; null when it was.</param>
/// <param name="OnHandDelta">The change made to on hand; 0 when nothing was applied.</param>
/// <param name="OnHand">On hand now (after the change, or as found when refused); null for NotFound.</param>
public sealed record StockChangeRowResult(
    int VariantId,
    string Outcome,
    string? Message,
    int OnHandDelta,
    int? OnHand,
    int? Reserved,
    int? Available);

/// <summary>The answer to a threshold change: the variant as it now is.</summary>
public sealed record ThresholdChangeResultDto(InventoryRow Variant);
