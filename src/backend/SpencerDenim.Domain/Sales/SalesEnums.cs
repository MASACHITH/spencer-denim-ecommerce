namespace SpencerDenim.Domain.Sales;

/// <summary>
/// <c>sales.Orders.Status</c> (doc 08 §3.5, §4.1). Doc 08 §1.4: an enum is <c>tinyint</c> plus a CK, so
/// the numbers here are the contract with the database and must never be renumbered — a stored 3 means
/// Shipped for as long as the rows live.
/// </summary>
public enum OrderStatus : byte
{
    /// <summary>Placed, stock reserved, money not yet taken.</summary>
    PendingPayment = 0,

    /// <summary>Settled: the invoice number is issued and stock is committed.</summary>
    Paid = 1,

    Processing = 2,

    Shipped = 3,

    Delivered = 4,

    Cancelled = 5,

    /// <summary>The payment due time passed with no capture lock in force (§4.1).</summary>
    Expired = 6,

    Refunded = 7,
}

/// <summary>
/// <c>sales.Orders.StockState</c> (doc 08 §3.5, §5). It is tracked separately from
/// <see cref="OrderStatus"/> because the conditional UPDATEs of §4.1 guard on <i>both</i>: a transition
/// is only safe when the stock is in the state that transition expects.
/// </summary>
public enum OrderStockState : byte
{
    /// <summary>Reserved at placement (<c>Reserved += q</c>).</summary>
    Reserved = 0,

    /// <summary>Deducted from <c>OnHand</c> at Paid.</summary>
    Committed = 1,

    /// <summary>Reservation given back at expiry or cancel.</summary>
    Released = 2,

    /// <summary>
    /// N8: set <b>only</b> inside the conditional transition to Refunded with restock. It exists so a
    /// repeated refund finds no row to update and cannot add the goods back twice.
    /// </summary>
    Restocked = 3,
}

/// <summary>
/// <c>sales.OrderStatusHistory.Source</c> (doc 08 §3.5). Doc 08 §4: "System" carries no staff
/// permission and cannot be called from the admin API, so who moved an order is part of its history.
/// </summary>
public enum OrderStatusChangeSource : byte
{
    Customer = 1,

    Staff = 2,

    /// <summary>A background job (expiry, auto-deliver, capture recovery).</summary>
    SystemJob = 3,

    /// <summary>A signature-verified provider event (doc 08 §4.2 step 3).</summary>
    PaymentProvider = 4,
}

/// <summary>
/// <c>sales.Payments.Status</c> (doc 08 §3.5, §4.3). The gaps in the obvious ordering are deliberate:
/// 7 <see cref="CapturedRequiresRefund"/> is money we hold for an order we cannot settle, which is a
/// different thing from a failure.
/// </summary>
public enum PaymentStatus : byte
{
    /// <summary>PayPal order created, buyer has not approved yet.</summary>
    Initiated = 0,

    /// <summary>Bank transfer awaiting review, or a PayPal eCheck capture that came back PENDING.</summary>
    Pending = 1,

    /// <summary>
    /// A capture is claimed and in flight (N1). Deliberately <b>outside</b> <c>UX_Payments_OneOpen</c>,
    /// so the index can never block capture recovery or a customer retry.
    /// </summary>
    Capturing = 2,

    Succeeded = 3,

    Failed = 4,

    Cancelled = 5,

    Expired = 6,

    /// <summary>
    /// Captured money for an order that could not be settled (expired, cancelled, or already settled by
    /// another payment). It is never the settlement payment and must be refunded (§4.2 step 4).
    /// </summary>
    CapturedRequiresRefund = 7,

    PartiallyRefunded = 8,

    Refunded = 9,
}

/// <summary>
/// <c>sales.PaymentEvents.EventSource</c> (doc 08 §3.5, DB13). The distinction is a security boundary,
/// not bookkeeping: only a verified webhook and an API response of ours may change state, and a return
/// redirect — which the buyer's browser controls — never may.
/// </summary>
public enum PaymentEventSource : byte
{
    /// <summary>A webhook whose signature was verified before anything was stored.</summary>
    VerifiedWebhook = 1,

    /// <summary>Our own create / capture / refund API call's response.</summary>
    ApiResponse = 2,

    /// <summary>The buyer's return redirect. Informational, never acted on.</summary>
    ReturnRedirect = 3,
}

/// <summary><c>sales.PaymentEvents.ProcessingStatus</c> (doc 08 §3.5, DB13).</summary>
public enum PaymentEventProcessingStatus : byte
{
    Received = 0,

    Processed = 1,

    /// <summary>Recognised and deliberately not acted on, e.g. a duplicate capture (§4.2 step 6).</summary>
    Ignored = 2,

    /// <summary>Processing threw; the retry job picks it up from <c>IX_PaymentEvents_Retry</c>.</summary>
    Failed = 3,
}

/// <summary>
/// <c>sales.Refunds.Method</c> (doc 08 §3.5). It records how the money physically went back, which is
/// what tells staff whether anything outside the system still has to happen.
/// </summary>
public enum RefundMethod : byte
{
    /// <summary>Through the provider's refund API.</summary>
    ProviderApi = 1,

    /// <summary>A manual outgoing bank transfer (the G-13 path, and every bank-transfer refund).</summary>
    ManualBankTransfer = 2,

    /// <summary>Anything else, including a refund made in the PayPal dashboard (G-23).</summary>
    ManualOther = 3,
}

/// <summary><c>sales.Refunds.Status</c> (doc 08 §3.5).</summary>
public enum RefundStatus : byte
{
    Pending = 0,

    Completed = 1,

    Failed = 2,
}

/// <summary>
/// The delivery zone a rate is quoted for (doc 08 §3.5 <c>DeliveryMethodZoneRates.DeliveryZone</c>,
/// N4). Doc 08 says it carries "the same CK as <c>ref.Districts.DeliveryZone</c>".
/// <para>
/// <b>It lives in <c>Sales</c> today because <c>ref.Districts</c> does not exist yet</b> (no migration
/// has created the <c>ref</c> schema). When GLB-06 builds it, this enum is the one both tables must
/// share, and moving it to a <c>Ref</c> namespace is a rename, not a redefinition.
/// </para>
/// </summary>
public enum DeliveryZone : byte
{
    Colombo = 1,

    Western = 2,

    IslandWide = 3,
}
