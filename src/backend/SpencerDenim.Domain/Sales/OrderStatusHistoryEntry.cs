namespace SpencerDenim.Domain.Sales;

/// <summary>
/// <c>sales.OrderStatusHistory</c> (doc 08 §3.5, ADM-09 / ACC-02): one row per order status change,
/// append-only.
/// <para>
/// Its key is a <c>bigint IDENTITY</c> because doc 08 §1.3 puts this table on the append-only,
/// high-volume list alongside <c>PaymentEvents</c> and <c>InventoryMovements</c>.
/// </para>
/// <para>
/// <b>It is not the audit log.</b> <c>ops.AuditLogs</c> records who changed which column of what, from
/// where; this records the order's own lifecycle, including the changes <i>no user</i> made — doc 08 §4
/// writes a history row for every System transition (expiry, auto-deliver, a verified webhook), which is
/// why <see cref="ChangedByUserId"/> is nullable and <see cref="Source"/> is not.
/// </para>
/// <para>
/// <b>Equal <see cref="FromStatus"/> and <see cref="ToStatus"/> mean "an event on this order that did not
/// move it", and the <see cref="Note"/> says which.</b> A reviewer was right to raise an eyebrow at a
/// "status history" row where no status changed, so the convention is written down rather than left to be
/// inferred: a rejected bank transfer (doc 11 P7 step 6b) is required to appear in the order's history and
/// deliberately leaves the order <c>PendingPayment</c>, because the customer may transfer again before the
/// deadline. The alternatives were worse. <see langword="null"/> <see cref="FromStatus"/> means "came from
/// nowhere" and belongs to placement's first row; <c>sales.PaymentEvents</c> is the <i>provider</i> event
/// log, with an idempotency key and a verbatim payload, and a staff decision is not a provider event;
/// writing no row at all would lose the one place an order's own timeline can be read.
/// </para>
/// </summary>
public sealed class OrderStatusHistoryEntry
{
    /// <summary>
    /// Doc 08 §3.5: <see cref="Note"/> is <c>nvarchar(1000)</c>.
    /// <para>
    /// A constant because a writer has to know it. A status change carries a staff member's own words
    /// (a rejection reason, an approval note), and a transition that threw on its way to the database
    /// because a note was two characters too long would roll back a decision about money for a reason
    /// nobody would guess. Callers clip; <c>OrderStatusHistoryEntryConfiguration</c> uses the same value,
    /// so the two cannot drift.
    /// </para>
    /// </summary>
    public const int NoteMaxLength = 1000;

    public long Id { get; set; }

    public int OrderId { get; set; }

    public Order? Order { get; set; }

    /// <summary><see langword="null"/> for the first row: placement has no previous status.</summary>
    public OrderStatus? FromStatus { get; set; }

    public OrderStatus ToStatus { get; set; }

    /// <summary>CK 1–4 (doc 08 §3.5). Doc 08 §4: "System" carries no staff permission.</summary>
    public OrderStatusChangeSource Source { get; set; }

    /// <summary>
    /// The staff member or customer who did it, or <see langword="null"/> for System. <b>No FK</b>, the
    /// same convention doc 08 §1.5 gives the audit columns: users are anonymised, never hard-deleted, so
    /// a constraint would buy nothing and a cascade path would cost something.
    /// </summary>
    public int? ChangedByUserId { get; set; }

    /// <summary>Free text, e.g. doc 08 §4.1's "late payment accepted".</summary>
    public string? Note { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}
