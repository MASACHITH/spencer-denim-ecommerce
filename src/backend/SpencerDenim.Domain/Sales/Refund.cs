namespace SpencerDenim.Domain.Sales;

/// <summary>
/// <c>sales.Refunds</c> (doc 08 §3.5, ADM-09; permission <c>Orders.Refund</c>): money sent back, one row
/// per refund.
/// <para>
/// <b>v1's UI refunds a full payment only (D6)</b>, but the table is already shaped for partial refunds —
/// several rows per payment, and <c>OrderLines.RefundedQuantity</c> alongside. That is deliberate in doc
/// 08 and costs nothing now.
/// </para>
/// <para>
/// <b>Refunding a payment with <c>IsOrderSettlement = 0</c> does not touch <c>Orders.RefundedAmount</c></b>
/// (doc 08 §3.5): nothing was ever settled, so there is nothing to un-settle. That is the G-13 case —
/// money physically received for an order that expired — and without this table it could not be recorded
/// in the system at all.
/// </para>
/// </summary>
public sealed class Refund
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    public Order? Order { get; set; }

    public int PaymentId { get; set; }

    public Payment? Payment { get; set; }

    /// <summary>LKR, CK &gt; 0. A zero refund is not an event worth a row.</summary>
    public decimal Amount { get; set; }

    /// <summary><c>char(3)</c>, CK <c>LKR</c>.</summary>
    public string Currency { get; set; } = SalesCurrency.Lkr;

    /// <summary>The USD that actually went back for a PayPal refund (D1), or <see langword="null"/>.</summary>
    public decimal? ProviderAmount { get; set; }

    /// <summary>CK 1–3. How the money physically travelled — see <see cref="RefundMethod"/>.</summary>
    public RefundMethod Method { get; set; }

    /// <summary>
    /// The provider's refund id, filtered unique. It is what makes the <c>PAYMENT.CAPTURE.REFUNDED</c>
    /// webhook idempotent at this level too (G-23): a refund we already recorded is matched by this id
    /// instead of being written twice.
    /// </summary>
    public string? ProviderRefundId { get; set; }

    /// <summary>CK 0–2.</summary>
    public RefundStatus Status { get; set; }

    /// <summary>
    /// <b>NOT NULL.</b> Doc 08 §4.1 (G-13) makes the reason mandatory, and for a manual bank transfer it is
    /// also where the outgoing transfer's bank reference is recorded.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// <b>Input to the Refunded transition (R6)</b>, read when that transition runs — which may be later,
    /// from a webhook. Only a whole-order restock is guarded (<c>StockState 1 → 3</c>, N8); a future
    /// partial restock would need its own per-line guard.
    /// </summary>
    public bool RestockItems { get; set; }

    /// <summary>
    /// <b>Nullable, and that nullability is load-bearing (G-23).</b> <see langword="null"/> means System: a
    /// refund made in the PayPal dashboard reaches us only as <c>PAYMENT.CAPTURE.REFUNDED</c>, with no
    /// staff user behind it, so a <c>NOT NULL</c> column would make that row impossible to persist and the
    /// refund invisible in the system. The same convention as
    /// <c>InventoryMovements.CreatedByUserId</c>. <b>No FK</b> (doc 08 §1.5's rule for user columns).
    /// </summary>
    public int? CreatedByUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
}
