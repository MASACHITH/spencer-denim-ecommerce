using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Inventory.Contracts;
using SpencerDenim.Domain.Inventory;

namespace SpencerDenim.Application.Inventory.Abstractions;

/// <summary>The inventory screen's reads (ADM-12, <c>Inventory.View</c>).</summary>
public interface IInventoryReader
{
    Task<InventoryListPage> ListAsync(InventoryListCriteria criteria, CancellationToken cancellationToken);

    Task<InventoryFilterOptions> FilterOptionsAsync(CancellationToken cancellationToken);

    /// <summary>One variant's row, or null when there is no such variant.</summary>
    Task<InventoryRow?> FindAsync(int variantId, CancellationToken cancellationToken);

    /// <summary>One variant's movements, newest first, or null when there is no such variant.</summary>
    Task<InventoryHistoryPage?> HistoryAsync(int variantId, int page, int pageSize, CancellationToken cancellationToken);
}

/// <summary>One validated change to one variant's on-hand figure.</summary>
/// <param name="ExpectedOnHand">For a SetCount only: the figure the screen was showing.</param>
public sealed record StockChangeRequest(int VariantId, StockChangeKind Kind, int Quantity, int? ExpectedOnHand);

/// <summary>A validated set of changes sharing one reason, applied in one transaction.</summary>
/// <param name="Rows">In the order the caller sent them; the store applies them in VariantId order.</param>
/// <param name="BatchId">Written on every applied row's movement when the request had more than one row.</param>
public sealed record StockChangeBatch(
    IReadOnlyList<StockChangeRequest> Rows,
    string Reason,
    Guid? BatchId,
    AdminActor Actor,
    DateTime NowUtc);

/// <summary>What became of a threshold change.</summary>
public enum ThresholdChangeOutcome
{
    Applied,

    /// <summary>The value was already this; nothing was written and nothing audited.</summary>
    Unchanged,

    /// <summary>No such variant, or it has no stock record.</summary>
    NotFound,
}

/// <summary>The inventory screen's writes (ADM-12, <c>Inventory.Edit</c>).</summary>
public interface IInventoryStore
{
    /// <summary>
    /// Applies each row with a conditional <c>UPDATE</c> (doc 08 §5.8), writing one movement and one audit
    /// row per applied change, all in one transaction. A row whose condition fails changes nothing and is
    /// reported; the others still apply.
    /// </summary>
    Task<StockChangeResultDto> ApplyAsync(StockChangeBatch batch, CancellationToken cancellationToken);

    /// <summary>Sets or clears (null = the shop default) one variant's low-stock threshold, audited.</summary>
    Task<ThresholdChangeOutcome> SetThresholdAsync(
        int variantId,
        int? threshold,
        AdminActor actor,
        DateTime nowUtc,
        CancellationToken cancellationToken);
}
