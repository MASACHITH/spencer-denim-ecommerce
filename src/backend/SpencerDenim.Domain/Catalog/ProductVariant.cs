using SpencerDenim.Domain.Common;
using SpencerDenim.Domain.Inventory;

namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// <c>catalog.ProductVariants</c> (doc 08 §3.2) [A] [RV]: one buyable colour × size combination.
/// A variant that has been ordered is deactivated, never deleted.
/// </summary>
public sealed class ProductVariant : IAuditable
{
    public int Id { get; set; }

    public int ProductId { get; set; }

    public Product? Product { get; set; }

    /// <summary>
    /// Composite FK <c>(ProductColourId, ProductId)</c> → <c>ProductColours (Id, ProductId)</c>, so a
    /// variant can never point at another product's colour (doc 08 §3.2, §10.1).
    /// </summary>
    public int ProductColourId { get; set; }

    public ProductColour? ProductColour { get; set; }

    public int SizeId { get; set; }

    public Size? Size { get; set; }

    public string Sku { get; set; } = string.Empty;

    public string? Barcode { get; set; }

    /// <summary>Overrides <see cref="Product.BasePrice"/> for this variant. CK NULL or &gt; 0.</summary>
    public decimal? PriceOverride { get; set; }

    /// <summary>Overrides <see cref="Product.CompareAtPrice"/>. CK NULL or &gt; 0.</summary>
    public decimal? CompareAtPriceOverride { get; set; }

    public bool IsActive { get; set; } = true;

    public short SortOrder { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }

    public byte[]? RowVersion { get; set; }

    public StockLevel? StockLevel { get; set; }
}
