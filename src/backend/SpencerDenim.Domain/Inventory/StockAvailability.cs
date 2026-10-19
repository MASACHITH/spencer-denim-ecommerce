namespace SpencerDenim.Domain.Inventory;

/// <summary>What the storefront shows for a product or a selected variant (CARD-01, PDP-02).</summary>
public enum StockStatus : byte
{
    OutOfStock = 0,
    LowStock = 1,
    InStock = 2,
}

/// <summary>
/// The read-time stock rules from doc 08 §3.2 and §7.1. Stock is never denormalised into
/// <c>ProductStats</c> (DB6), so these run over the page's rows at query time.
/// </summary>
public static class StockAvailability
{
    /// <summary>
    /// Doc 08 §3.2 / N11: a product is Low Stock when <c>0 &lt; Σ Available ≤ Σ threshold</c> over its
    /// active variants, using <b>each variant's own</b> threshold and falling back to the
    /// <c>Inventory.DefaultLowStockThreshold</c> setting where a variant has none.
    /// </summary>
    /// <param name="totalAvailable">Σ <c>Available</c> over the active variants.</param>
    /// <param name="totalThreshold">Σ <c>COALESCE(LowStockThreshold, default)</c> over the same variants.</param>
    public static StockStatus Evaluate(int totalAvailable, int totalThreshold)
    {
        if (totalAvailable <= 0)
        {
            return StockStatus.OutOfStock;
        }

        return totalAvailable <= totalThreshold ? StockStatus.LowStock : StockStatus.InStock;
    }

    /// <summary>
    /// One variant's status on the inventory screen (ADM-12): out of stock at 0 available or below, low at
    /// 1 to its threshold, in stock above. Out of stock is <b>never</b> low (doc 11 P2 step 9, P16).
    /// </summary>
    public static StockStatus ForVariant(int available, int? ownThreshold, int defaultThreshold) =>
        Evaluate(available, ResolveThreshold(ownThreshold, defaultThreshold));

    /// <summary>Resolves one variant's threshold against the store-wide default.</summary>
    public static int ResolveThreshold(int? variantThreshold, int defaultThreshold) =>
        variantThreshold ?? defaultThreshold;

    /// <summary>True when at least one active variant can be added to the bag.</summary>
    public static bool IsInStock(int totalAvailable) => totalAvailable > 0;
}
