using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Payments.PayPal;

/// <summary>
/// The persistence behind PayPal's payment lifecycle (doc 08 §4.2, §4.3; doc 11 P8) — every statement that
/// moves a PayPal payment or its order <b>other than</b> "the money is ours", which is
/// <see cref="IOrderSettlementStore"/> and is shared with every provider.
/// <para>
/// <b>Each method is one short transaction in the lock order of doc 08 §5.3 and §4.2 N6: the order row
/// first, then the payment row</b>, and each is a conditional <c>UPDATE</c> whose row count is the answer —
/// the mechanism every money path here uses. Nothing calls PayPal inside one: the capture call is made
/// between <see cref="ClaimCaptureAsync"/> and the apply, never within either (doc 08 §4.2 step 2).
/// </para>
/// <para>
/// <b>Every method is pinned to the PayPal provider</b> in the statement itself, not only in the caller's
/// read, for the reason <c>OrderSettlementEffects</c> records: a guard that lives in the atomic statement
/// cannot be defeated by a bug in a read.
/// </para>
/// </summary>
public interface IPayPalPaymentStore
{
    /// <summary>The order behind this link and its PayPal payments, or null when there is no such order.</summary>
    Task<PayPalOrderView?> FindOrderAsync(Guid orderPublicId, CancellationToken cancellationToken);

    /// <summary>The same, by the number a customer quotes. For the order page's description of a payment.</summary>
    Task<PayPalOrderView?> FindOrderByNumberAsync(string orderNumber, CancellationToken cancellationToken);

    /// <summary>One PayPal payment by id, or null when there is none (or it belongs to another provider).</summary>
    Task<PayPalPaymentRef?> FindPaymentAsync(int paymentId, CancellationToken cancellationToken);

    /// <summary>
    /// The payment a webhook is about, found by whichever of the identifiers it carries, in the order of how
    /// sure each is: the capture id already recorded, PayPal's order id, then our own <c>custom_id</c>.
    /// Null when none matches a PayPal payment.
    /// </summary>
    Task<PayPalPaymentRef?> FindPaymentForEventAsync(
        string? captureId,
        string? paypalOrderId,
        int? paymentIdFromCustomId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds or makes the one open payment for an order that is about to be paid (doc 11 P8 step 9) —
    /// <b>the retry rule, in one transaction</b>.
    /// <para>
    /// The order row is locked first (a conditional <c>UPDATE</c> that also proves it is PendingPayment,
    /// within its deadline and not under a live capture lock). Then, and only then, the payments are read:
    /// a <b>Capturing</b> payment blocks the retry whatever the lock says, and a <b>Pending</b> one blocks it
    /// because it is still open (<c>UX_Payments_OneOpen</c> would refuse a second open row, and the doc says
    /// the customer waits for PayPal to resolve it); an <b>Initiated</b> one is reused as it is, at the rate
    /// it was opened with; only with none of those is a new Initiated row written, at the rate in
    /// <paramref name="initiation"/>. Because the claim locks the same order row first, a retry and a claim
    /// serialise, and the index is never asked to refuse an insert.
    /// </para>
    /// </summary>
    Task<OpenAttemptResult> OpenAttemptAsync(
        int orderId,
        PaymentInitiation? initiation,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores PayPal's order id on an Initiated payment that has none. Returns false when the payment
    /// already has one (a concurrent start won; the caller re-reads) or is no longer Initiated.
    /// </summary>
    Task<bool> SetProviderReferenceAsync(
        int paymentId,
        string providerReference,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// The buyer backed out at PayPal: Initiated → Cancelled (doc 08 §4.3, "buyer cancel / retry"). Only an
    /// Initiated payment moves; a captured, pending or capturing one is untouched. Returns whether it moved.
    /// </summary>
    Task<bool> CancelInitiatedAsync(
        int orderId,
        int paymentId,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Doc 08 §4.2 step 1, in one short transaction: the order's <c>CaptureLockedUntilUtc</c> becomes
    /// <c>now + lock</c> where the order is PendingPayment and holds no live lock, <b>and</b> the payment
    /// goes Initiated → Capturing with <c>CaptureStartedAtUtc</c>. Both must hit one row or neither happens
    /// and nothing is captured.
    /// </summary>
    Task<ClaimResult> ClaimCaptureAsync(
        int orderId,
        int paymentId,
        DateTime nowUtc,
        TimeSpan lockFor,
        CancellationToken cancellationToken);

    /// <summary>
    /// A claim whose capture PayPal refused because the buyer had not approved: Capturing → Initiated again
    /// and the lock cleared, so the buyer can go back and approve. Returns whether it moved.
    /// </summary>
    Task<bool> ReleaseClaimAsync(int orderId, int paymentId, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Doc 08 §4.2 step 4, PENDING: the payment goes to Pending, the order's lock is cleared and its hold is
    /// extended to <c>max(PaymentDueAtUtc, PlacedAtUtc + pendingMaxDays)</c> (N16) — extended only while the
    /// order is still PendingPayment.
    /// </summary>
    Task<PayPalMoveResult> RecordPendingAsync(
        int orderId,
        int paymentId,
        string? captureId,
        int pendingMaxDays,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Doc 08 §4.2 step 4, DENIED: the payment goes to Failed with <paramref name="failureCode"/> and the
    /// order's lock is cleared. <b>If the payment was Pending</b> (R2) the order's extended hold is cut back
    /// to <c>min(PaymentDueAtUtc, now + expiryMinutes)</c>, decided inside the transaction from the row's own
    /// status so two racing events cannot both cut or neither.
    /// </summary>
    Task<PayPalMoveResult> RecordDeclinedAsync(
        int orderId,
        int paymentId,
        string failureCode,
        string? failureMessage,
        int expiryMinutes,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// A payment that cannot be captured — ORDER_NOT_PAYABLE, VOIDED, NOT_FOUND, RECOVERY_TIMEOUT: an open
    /// payment goes to Failed and the order's lock is cleared. <paramref name="alert"/>, when given, is
    /// written in the same transaction (the <c>CaptureRecoveryFailed</c> banner for a timeout).
    /// </summary>
    Task<PayPalMoveResult> RecordFailedAsync(
        int orderId,
        int paymentId,
        string failureCode,
        string? failureMessage,
        DateTime nowUtc,
        AdminAlert? alert,
        CancellationToken cancellationToken);

    /// <summary>
    /// The payments stuck in Capturing since before <paramref name="startedBeforeUtc"/>, oldest first, up to
    /// <paramref name="take"/> — the recovery job's read, served by <c>IX_Payments_Capturing</c>.
    /// </summary>
    Task<IReadOnlyList<CapturingPayment>> FindCapturingAsync(
        DateTime startedBeforeUtc,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stamps <c>UpdatedAtUtc</c> on a Capturing payment: "PayPal was unreachable at this time", which
    /// <see cref="PayPalRecoverySchedule"/> reads to space the next attempt.
    /// </summary>
    Task<bool> RecordRecoveryAttemptAsync(int paymentId, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Doc 08 §4.2 step 5, "APPROVED, order payable": renews the order's lock (<c>WHERE Status = 0</c>) before
    /// the capture is asked for again. False when the order is no longer PendingPayment.
    /// </summary>
    Task<bool> ReclaimOrderLockAsync(
        int orderId,
        DateTime nowUtc,
        TimeSpan lockFor,
        CancellationToken cancellationToken);
}

/// <summary>An order and its PayPal payments, as far as the customer-facing flow needs to know.</summary>
/// <param name="OrderId"><c>sales.Orders.Id</c>.</param>
/// <param name="PublicId">The order link's token.</param>
/// <param name="OrderNumber">What a customer quotes.</param>
/// <param name="Status">The order's status.</param>
/// <param name="PlacedAtUtc">When it was placed.</param>
/// <param name="PaymentDueAtUtc">The payment deadline (extended while a capture is Pending).</param>
/// <param name="CaptureLockedUntilUtc">The capture lock, if any (it may have lapsed).</param>
/// <param name="GrandTotal">The order's total in LKR, which is the payment's <c>Amount</c>.</param>
/// <param name="PaymentProviderCode">The provider the customer chose.</param>
/// <param name="Open">The newest PayPal payment still Initiated, Pending or Capturing, or null.</param>
/// <param name="Latest">The newest PayPal payment of any status, or null when the order has none.</param>
/// <param name="HasUnsettledCapture">
/// Whether any PayPal payment of the order is <c>CapturedRequiresRefund</c>: money that arrived and could not
/// settle the order, which staff have to deal with. While it is true no new attempt is opened.
/// </param>
public sealed record PayPalOrderView(
    int OrderId,
    Guid PublicId,
    string OrderNumber,
    OrderStatus Status,
    DateTime PlacedAtUtc,
    DateTime PaymentDueAtUtc,
    DateTime? CaptureLockedUntilUtc,
    decimal GrandTotal,
    string PaymentProviderCode,
    PayPalPaymentView? Open,
    PayPalPaymentView? Latest,
    bool HasUnsettledCapture = false);

/// <summary>One PayPal payment row, as far as describing it needs.</summary>
public sealed record PayPalPaymentView(
    int Id,
    PaymentStatus Status,
    string? ProviderReference,
    decimal? ProviderAmount,
    string? ProviderCurrency,
    decimal? ExchangeRate,
    decimal? FxBufferPercent,
    string? FailureCode,
    DateTime? CaptureStartedAtUtc);

/// <summary>A PayPal payment and the order it belongs to, for applying a result to it.</summary>
public sealed record PayPalPaymentRef(
    int PaymentId,
    int OrderId,
    PaymentStatus Status,
    string? ProviderReference,
    string? ProviderTransactionId);

/// <summary>A payment stuck in Capturing, as the recovery job reads it.</summary>
/// <param name="PaymentId"><c>sales.Payments.Id</c>.</param>
/// <param name="OrderId"><c>sales.Orders.Id</c>.</param>
/// <param name="OrderPublicId">For the alert's related entity.</param>
/// <param name="OrderNumber">For the alert's title.</param>
/// <param name="ProviderReference">PayPal's order id; null would mean the payment could never have been claimed.</param>
/// <param name="CaptureStartedAtUtc">When the capture was claimed.</param>
/// <param name="LastAttemptAtUtc">When recovery last found PayPal unreachable for it, or null.</param>
public sealed record CapturingPayment(
    int PaymentId,
    int OrderId,
    Guid OrderPublicId,
    string OrderNumber,
    string? ProviderReference,
    DateTime CaptureStartedAtUtc,
    DateTime? LastAttemptAtUtc);

/// <summary>Why a payment could not be opened or claimed.</summary>
public enum PayPalBlock
{
    /// <summary>
    /// Another attempt is in flight: the order holds a live capture lock, or a payment is Capturing. The
    /// customer is told the payment is being processed (doc 08 §4.2 step 1, N17).
    /// </summary>
    BeingProcessed = 0,

    /// <summary>A payment is Pending with PayPal; a new attempt waits for it to resolve (doc 11 P8 step 9).</summary>
    PendingWithPayPal = 1,

    /// <summary>
    /// Anything else: the order has expired, been cancelled or paid, is not a PayPal order, or is past its
    /// deadline. "This order can no longer be paid."
    /// </summary>
    NotPayable = 2,

    /// <summary>
    /// A payment of this order has been captured and could not settle it (<c>CapturedRequiresRefund</c>:
    /// the amount differed, or the order closed first). Staff are dealing with that money, so a new attempt
    /// is not opened beside it: "We received your payment, our team will contact you" (doc 11 P8 step 6).
    /// </summary>
    CapturedAwaitingStaff = 3,
}

/// <summary>The answer to <see cref="IPayPalPaymentStore.OpenAttemptAsync"/>.</summary>
public abstract record OpenAttemptResult
{
    private OpenAttemptResult()
    {
    }

    /// <summary>An Initiated payment already existed and is used as it is, at the rate it was opened with.</summary>
    public sealed record Reused(PayPalPaymentView Payment) : OpenAttemptResult;

    /// <summary>A new Initiated payment was written.</summary>
    public sealed record Created(PayPalPaymentView Payment) : OpenAttemptResult;

    /// <summary>No attempt can be opened or reused.</summary>
    public sealed record Blocked(PayPalBlock Reason) : OpenAttemptResult;

    /// <summary>
    /// The order is payable and has no open attempt, and no <c>initiation</c> was supplied to make one with
    /// — the caller decides whether PayPal can be offered (a stale rate) and says so.
    /// </summary>
    public sealed record NeedsInitiation : OpenAttemptResult;
}

/// <summary>The answer to <see cref="IPayPalPaymentStore.ClaimCaptureAsync"/> (doc 08 §4.2 step 1).</summary>
public abstract record ClaimResult
{
    private ClaimResult()
    {
    }

    /// <summary>Both rows moved: the caller now holds the capture.</summary>
    public sealed record Claimed : ClaimResult;

    /// <summary>Not claimed; the reason decides which of the two customer messages is shown (N17).</summary>
    public sealed record Refused(PayPalBlock Reason) : ClaimResult;
}

/// <summary>What a Pending / Declined / Failed transition did.</summary>
public enum PayPalMoveResult
{
    /// <summary>The payment moved.</summary>
    Applied = 0,

    /// <summary>
    /// The payment was not in a status this transition applies from (a duplicate, an event that arrived out of
    /// order, a concurrent path that got there first). Nothing was changed.
    /// </summary>
    NotApplicable = 1,
}
