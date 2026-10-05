using SpencerDenim.Domain.Common;

namespace SpencerDenim.Domain.Sales;

/// <summary>
/// <c>sales.Orders</c> (doc 08 §3.5, CHK-01..04 / ADM-09 / ACC-02) [A] [RV]. The invoice is folded in
/// (revision 2 removed the separate <c>Invoices</c> table), so <see cref="InvoiceNumber"/> and its
/// companions are columns here.
/// <para>
/// <b>Orders are never deleted</b> (doc 08 §1.6), which is why nothing cascades to or from this row and
/// why the money columns are snapshots rather than joins: an order has to keep reading the same after
/// the product, the promo code and the delivery rate have all moved on.
/// </para>
/// <para>
/// <b>No transition logic lives here yet.</b> Doc 08 §4 puts the order state machine in the Domain, but
/// this task is the schema only (step 1 of W3-03). What the schema has to do is make §4 <i>expressible</i>
/// and its guards enforceable: <see cref="Status"/> and <see cref="StockState"/> are the two columns
/// every conditional UPDATE in §4.1 keys on, and <see cref="CaptureLockedUntilUtc"/> is what stops the
/// expiry job from cancelling an order while PayPal is mid-capture.
/// </para>
/// </summary>
public sealed class Order : IAuditable
{
    public int Id { get; set; }

    /// <summary>
    /// <c>SD-2026-000123</c>, unique. Doc 08 §1.3: a natural key as a unique alternate key, never the
    /// clustered one. Allocated from <see cref="NumberSeries"/> in a short separate transaction before
    /// checkout, so gaps are allowed and two checkouts never serialise on the series row.
    /// </summary>
    public string OrderNumber { get; set; } = string.Empty;

    /// <summary>
    /// The unguessable token in a guest's order link. Doc 08 §1.3: a <c>uniqueidentifier</c> under a
    /// unique index and never a clustered key — a guest has no account, so this is the only credential
    /// the link can carry, and a sequential id would let anyone read the next person's order.
    /// </summary>
    public Guid PublicId { get; set; }

    /// <summary>
    /// Unique. The client's own key for "this checkout attempt", so a double-submitted form or a retried
    /// request produces one order rather than two with two stock reservations. The uniqueness is the
    /// mechanism, not a sanity check: the second insert fails and the caller returns the first order.
    /// </summary>
    public Guid CheckoutIdempotencyKey { get; set; }

    /// <summary>The account that placed it, or <see langword="null"/> for a guest order.</summary>
    public int? CustomerUserId { get; set; }

    /// <summary>CK 0–7 (doc 08 §3.5, §4.1).</summary>
    public OrderStatus Status { get; set; }

    /// <summary>
    /// CK 0–3. Tracked separately from <see cref="Status"/> because §4.1's conditional UPDATEs guard on
    /// both, and because a Refunded order's stock may or may not have come back (N8).
    /// </summary>
    public OrderStockState StockState { get; set; }

    /// <summary>
    /// DB1/N1: set to claim time + <c>Payments.PayPal.CaptureLockHours</c> (default 3 h) when a capture
    /// is claimed, and cleared when it resolves. Expiry and cancel skip an order while this is in the
    /// future, so an order whose capture is in flight is never expired out from under the money.
    /// </summary>
    public DateTime? CaptureLockedUntilUtc { get; set; }

    /// <summary>FK <c>sales.PaymentProviders</c> (doc 08 §3.5). Which provider the customer chose.</summary>
    public string PaymentProviderCode { get; set; } = string.Empty;

    public PaymentProvider? PaymentProvider { get; set; }

    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// The upper-cased form, indexed. It exists because the promo per-customer limit and the "link my
    /// past guest orders" flow both match on email, and the database collation must not be the thing
    /// deciding whether two addresses are the same.
    /// </summary>
    public string EmailNormalized { get; set; } = string.Empty;

    /// <summary><see langword="null"/> after PDPA anonymisation (doc 08 §3.1).</summary>
    public string? Phone { get; set; }

    /// <summary>Bill-to first name.</summary>
    public string FirstName { get; set; } = string.Empty;

    public string? LastName { get; set; }

    /// <summary>
    /// The delivery address's district, or the pickup location's (R7).
    /// <para>
    /// Doc 08 §3.5 calls it an "entity-level FK Districts". <b>The column is here and the constraint is
    /// not</b>: no migration has created <c>ref.Districts</c> (GLB-06), so there is nothing to point at.
    /// Dropping the column was not an option — doc 08 makes it <c>NOT NULL</c>, and
    /// <see cref="DeliveryZone"/> below is derived from it.
    /// </para>
    /// </summary>
    public int DeliveryDistrictId { get; set; }

    /// <summary>Snapshot of the district's name, so a renamed district does not rewrite old orders.</summary>
    public string DeliveryDistrictName { get; set; } = string.Empty;

    /// <summary>
    /// Snapshot of the district's zone at placement (N4). Snapshotted rather than joined because the zone
    /// is <b>admin-editable</b> (doc 08 §3.1): reassigning a district must not change what a past order
    /// was charged for delivery.
    /// </summary>
    public DeliveryZone DeliveryZone { get; set; }

    /// <summary>
    /// Required complex property (doc 08 §3.5): the <c>Ship_*</c> columns on this row. Every inner column
    /// is nullable and <c>CK_Orders_ShipAddress</c> enforces the real rule — see
    /// <see cref="AddressSnapshot"/>.
    /// </summary>
    public AddressSnapshot ShipAddress { get; set; } = new();

    /// <summary>FK <c>sales.DeliveryMethods</c>.</summary>
    public int DeliveryMethodId { get; set; }

    public DeliveryMethod? DeliveryMethod { get; set; }

    /// <summary>FK <c>sales.PickupLocations</c>, required by <c>CK_Orders_Pickup</c> for a PICKUP order.</summary>
    public int? PickupLocationId { get; set; }

    public PickupLocation? PickupLocation { get; set; }

    /// <summary>Required complex property (doc 08 §3.5). No FKs inside — see <see cref="DeliverySnapshot"/>.</summary>
    public DeliverySnapshot Delivery { get; set; } = new();

    /// <summary><c>char(3)</c>, CK <c>LKR</c> (doc 08 §1.4). See <see cref="SalesCurrency"/>.</summary>
    public string Currency { get; set; } = SalesCurrency.Lkr;

    /// <summary>Σ <c>UnitOriginalPrice × Quantity</c> over the lines.</summary>
    public decimal ItemsOriginalTotal { get; set; }

    /// <summary>Σ <c>UnitListPrice × Quantity</c>.</summary>
    public decimal ItemsListTotal { get; set; }

    /// <summary>Σ <c>LineSubtotal</c>, i.e. at the prices actually charged.</summary>
    public decimal ItemsSubtotal { get; set; }

    /// <summary>
    /// Original − List (DB17). It is stored rather than computed so the three discount kinds can be
    /// reported separately years later, and <c>CK_Orders_Totals</c> proves it still adds up.
    /// </summary>
    public decimal MarkdownDiscountTotal { get; set; }

    /// <summary>List − Subtotal (DB17): what a sale campaign took off.</summary>
    public decimal CampaignDiscountTotal { get; set; }

    /// <summary>
    /// FK <c>marketing.PromoCodes</c> in doc 08 §3.5. <b>The column is here, the constraint is not</b>:
    /// <c>marketing.PromoCodes</c> is ADM-13 and no migration has created it. <see cref="PromoCodeText"/>
    /// is what the order displays either way.
    /// </summary>
    public int? PromoCodeId { get; set; }

    /// <summary>The code as the customer typed it, kept even if the promo is later deleted.</summary>
    public string? PromoCodeText { get; set; }

    public decimal PromoDiscountAmount { get; set; }

    public decimal DeliveryFeeAmount { get; set; }

    /// <summary>A free-delivery threshold or a promo that paid the delivery fee.</summary>
    public decimal DeliveryDiscountAmount { get; set; }

    /// <summary>
    /// DF 0. VAT is <b>inclusive</b> in the shown price (D2), so this records the tax contained in the
    /// total and is not added to it — which is why <c>CK_Orders_Totals</c>'s <c>GrandTotal</c> formula
    /// does not mention it.
    /// </summary>
    public decimal TaxAmount { get; set; }

    public decimal GrandTotal { get; set; }

    /// <summary>
    /// DF 0. <b>Settlement-payment refunds only</b> (doc 08 §3.5): refunding an exception payment
    /// (<c>IsOrderSettlement = 0</c>) leaves it alone, because nothing was ever settled to refund.
    /// </summary>
    public decimal RefundedAmount { get; set; }

    public DateTime PlacedAtUtc { get; set; }

    /// <summary>
    /// Placement + the provider's expiry window. Extended once for a PayPal PENDING capture (N16) and cut
    /// back if that capture is later DENIED (R2). <c>IX_Orders_Pending_Due</c> is the expiry job's index.
    /// </summary>
    public DateTime PaymentDueAtUtc { get; set; }

    public DateTime? PaidAtUtc { get; set; }

    public DateTime? ShippedAtUtc { get; set; }

    public DateTime? DeliveredAtUtc { get; set; }

    /// <summary>When the order reached a terminal state. Cleared again if an expired order is revived (DB8).</summary>
    public DateTime? ClosedAtUtc { get; set; }

    public int? CourierId { get; set; }

    public Courier? Courier { get; set; }

    /// <summary>Snapshot, so a renamed or deleted courier does not change a past dispatch note.</summary>
    public string? CourierName { get; set; }

    public string? TrackingNumber { get; set; }

    /// <summary>
    /// <c>INV-2026-000045</c>. Filtered unique index <c>WHERE … IS NOT NULL</c>, set at Paid.
    /// <para>
    /// Allocated <b>inside</b> the Paid transaction so the series is gapless — a legal requirement an
    /// order number does not have, which is exactly why doc 08 keeps two series.
    /// </para>
    /// </summary>
    public string? InvoiceNumber { get; set; }

    public DateTime? InvoiceIssuedAtUtc { get; set; }

    /// <summary>
    /// The store's legal name, address and BR/VAT number as they stood when the invoice was issued, as
    /// JSON (<c>nvarchar(max)</c> with an <c>ISJSON</c> check, doc 08 §1.4/DB15). Snapshotted because a
    /// reissued PDF of a two-year-old invoice must not show this year's registration details.
    /// </summary>
    public string? InvoiceSellerSnapshotJson { get; set; }

    /// <summary>What the customer asked for. Shown to staff; never used as an instruction by the system.</summary>
    public string? CustomerNote { get; set; }

    /// <summary>Staff-only. Doc 08 §4.1: corrections use this plus a history note, because orders never move backwards.</summary>
    public string? InternalNote { get; set; }

    public string? CancelReason { get; set; }

    /// <summary><c>varchar(45)</c> (IPv6 fits), purged after 90 days.</summary>
    public string? PlacedIpAddress { get; set; }

    /// <summary>Set when a PDPA removal has overwritten the personal columns (doc 08 §3.1, N5).</summary>
    public DateTime? AnonymisedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }

    /// <summary>
    /// [RV]. The admin ETag (doc 08 §1.5). The §4 transitions do <b>not</b> rely on it — they use
    /// conditional UPDATEs on <see cref="Status"/> and <see cref="StockState"/>, which is a different and
    /// stronger guarantee: a stale admin edit is rejected by the token, and a lost race between two
    /// transitions is rejected by the row count.
    /// </summary>
    public byte[]? RowVersion { get; set; }

    public ICollection<OrderLine> Lines { get; } = [];

    public ICollection<OrderStatusHistoryEntry> StatusHistory { get; } = [];

    public ICollection<Payment> Payments { get; } = [];

    public ICollection<Refund> Refunds { get; } = [];
}
