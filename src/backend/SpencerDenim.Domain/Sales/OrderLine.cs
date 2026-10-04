using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Domain.Sales;

/// <summary>
/// <c>sales.OrderLines</c> (doc 08 §3.5, CHK-01 / ADM-07 / ADM-09): one variant on one order, as a
/// <b>snapshot</b>, and immutable once written.
/// <para>
/// <b>Why every descriptive column is duplicated here</b> rather than joined from the catalogue: doc 08
/// calls the row a snapshot, and an order confirmation, an invoice and a refund all have to keep showing
/// the product name, colour, size and price that the customer actually bought — after the product has
/// been renamed, repriced, recoloured or soft-deleted. The FKs stay so reporting can still group by
/// product, department and category; they are not what the line <i>reads</i>.
/// </para>
/// <para>
/// <b>Not audited and no rowversion</b> (doc 08 §3.5 marks it with neither): it is never edited. The one
/// column that changes is <see cref="RefundedQuantity"/>, written by the refund transition under the
/// order's own guards.
/// </para>
/// </summary>
public sealed class OrderLine
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    public Order? Order { get; set; }

    /// <summary>1-based position, unique within the order (<c>UX_OrderLines_Order_Line</c>).</summary>
    public short LineNumber { get; set; }

    public int ProductId { get; set; }

    public Product? Product { get; set; }

    public int VariantId { get; set; }

    public ProductVariant? Variant { get; set; }

    /// <summary>Snapshot of the primary category's department, for reporting that must not move.</summary>
    public int DepartmentId { get; set; }

    public Department? Department { get; set; }

    /// <summary>Snapshot of the primary category (doc 08 §3.5).</summary>
    public int CategoryId { get; set; }

    public Category? Category { get; set; }

    public string ProductName { get; set; } = string.Empty;

    public string ProductCode { get; set; } = string.Empty;

    public string Sku { get; set; } = string.Empty;

    public string ColourName { get; set; } = string.Empty;

    public string SizeLabel { get; set; } = string.Empty;

    /// <summary>
    /// The thumbnail's storage key, copied rather than linked. Nullable: a product may have had no image.
    /// There is no FK to <c>media.MediaAssets</c> in doc 08's column list, and that is deliberate — the
    /// media delete guard works off <c>MediaUsages</c>, and an order must not be able to block a purge.
    /// </summary>
    public string? ThumbnailStorageKey { get; set; }

    /// <summary>CK &gt; 0. A zero-quantity line is not a line.</summary>
    public short Quantity { get; set; }

    /// <summary><c>max(original, list)</c> at placement (doc 08 §3.5). CK ≥ <see cref="UnitListPrice"/>.</summary>
    public decimal UnitOriginalPrice { get; set; }

    public decimal UnitListPrice { get; set; }

    /// <summary>What was actually charged per unit. CK &gt; 0 and ≤ <see cref="UnitListPrice"/>.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// FK <c>marketing.SaleCampaigns</c> in doc 08 §3.5. <b>The column is here, the constraint is not</b>:
    /// <c>marketing.SaleCampaigns</c> is ADM-14 and no migration has created it.
    /// </summary>
    public int? SaleCampaignId { get; set; }

    /// <summary>CK <c>= UnitPrice × Quantity</c>, so a rounding bug in the totals code cannot be persisted.</summary>
    public decimal LineSubtotal { get; set; }

    /// <summary>This line's share of the order's promo discount. CK ≥ 0.</summary>
    public decimal PromoDiscountAllocated { get; set; }

    /// <summary>CK <c>= LineSubtotal − PromoDiscountAllocated</c> <b>and ≥ 0</b> (DB17).</summary>
    public decimal LineTotal { get; set; }

    /// <summary>
    /// DF 0, CK ≤ <see cref="Quantity"/>. v1 refunds whole payments only (D6); the column is what lets a
    /// later partial refund be recorded per line without a schema change.
    /// </summary>
    public short RefundedQuantity { get; set; }
}
