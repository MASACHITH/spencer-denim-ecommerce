using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Domain.Inventory;

/// <summary>
/// <c>inventory.StockLevels</c> (doc 08 §3.4): 1:1 with a variant and the hottest row in the system.
/// No rowversion: stock moves with conditional UPDATEs and row-count checks (doc 08 §5.2).
/// </summary>
public sealed class StockLevel
{
    public int VariantId { get; set; }

    public ProductVariant? Variant { get; set; }

    /// <summary>CK ≥ 0.</summary>
    public int OnHand { get; set; }

    /// <summary>Held by orders in StockState Reserved. CK ≥ 0 and ≤ <see cref="OnHand"/>.</summary>
    public int Reserved { get; set; }

    /// <summary>
    /// Computed and persisted in the database as <c>OnHand - Reserved</c>, so it can be indexed
    /// (doc 08 §3.4). Never assigned by the application.
    /// </summary>
    public int Available { get; private set; }

    /// <summary>NULL falls back to the <c>Inventory.DefaultLowStockThreshold</c> setting.</summary>
    public int? LowStockThreshold { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
