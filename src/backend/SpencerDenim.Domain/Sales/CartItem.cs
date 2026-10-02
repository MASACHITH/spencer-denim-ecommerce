using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Domain.Sales;

/// <summary>
/// <c>sales.CartItems</c> (doc 08 §3.5, CART-01): one variant in one bag.
/// <para>
/// A pure child row, so it is one of the cascades doc 08 §1.6 permits: deleting a cart deletes its
/// items. Its other foreign key, to <c>catalog.ProductVariants</c>, is <c>NO ACTION</c> like every
/// other — a variant that has been ordered is deactivated, never deleted.
/// </para>
/// </summary>
public sealed class CartItem
{
    /// <summary>How many of one variant a single bag may hold (doc 08 §3.5: <c>CK Quantity 1–10</c>).</summary>
    public const short MaxQuantity = 10;

    /// <summary>The smallest quantity a row may carry. Zero is a removal, not a quantity.</summary>
    public const short MinQuantity = 1;

    public int Id { get; set; }

    public int CartId { get; set; }

    public Cart? Cart { get; set; }

    public int VariantId { get; set; }

    public ProductVariant? Variant { get; set; }

    /// <summary>CK 1–10. Clamped by the upsert itself, not by the caller (doc 08 §3.5, DB22).</summary>
    public short Quantity { get; set; }

    /// <summary>
    /// What one of these cost when it was first put in the bag.
    /// <para>
    /// <b>It is never used to price the bag.</b> Doc 08 §3.5 says it is there to show "price changed",
    /// and §6 says prices are computed on every cart read. The subtotal is always built from today's
    /// catalogue price, so a price that moved after the item went in cannot be charged or displayed.
    /// </para>
    /// </summary>
    public decimal UnitPriceAtAdd { get; set; }

    public DateTime AddedAtUtc { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }
}
