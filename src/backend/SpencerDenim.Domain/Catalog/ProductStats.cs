namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// <c>catalog.ProductStats</c> (doc 08 §3.2): 1:1 derived data behind the listing filters and sorts.
/// No rowversion, because nothing edits it by hand.
/// <para>
/// Stock is deliberately <b>not</b> stored here (DB6): in-stock filtering and the Low Stock badge are
/// computed at read time from <c>inventory.StockLevels</c>, so checkout never writes this table.
/// </para>
/// </summary>
public sealed class ProductStats
{
    public int ProductId { get; set; }

    public Product? Product { get; set; }

    public decimal MinListPrice { get; set; }

    public decimal MaxListPrice { get; set; }

    /// <summary>After the best active campaign (doc 08 §6). Equal to the list price with no campaign.</summary>
    public decimal MinSalePrice { get; set; }

    public decimal MaxSalePrice { get; set; }

    /// <summary><c>max(original, list)</c>, so a displayed discount is never negative.</summary>
    public decimal MaxOriginalPrice { get; set; }

    public decimal MaxDiscountPercent { get; set; }

    public bool HasDiscount { get; set; }

    /// <summary>Average of approved reviews. NULL until a product has one.</summary>
    public decimal? RatingAverage { get; set; }

    public int RatingCount { get; set; }

    /// <summary>Nightly, from paid order lines.</summary>
    public int SoldQty30d { get; set; }

    public int SoldQtyTotal { get; set; }

    public DateTime PricingRefreshedAtUtc { get; set; }

    public DateTime StatsRefreshedAtUtc { get; set; }
}
