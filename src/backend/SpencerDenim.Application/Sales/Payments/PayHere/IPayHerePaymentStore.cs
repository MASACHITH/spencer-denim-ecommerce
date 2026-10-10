using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Payments.PayHere;

/// <summary>
/// The reads and the small state changes PayHere's flow needs that the shared settlement port does not cover
/// (doc 11 P8b). Everything that <i>pays an order</i> goes through <see cref="Abstractions.IOrderSettlementStore"/>;
/// this port holds the rest: finding the attempt a notification is about, the details of the order a checkout
/// form is built from, and the three status changes that are PayHere's own rule — pending, failed and the
/// replacement attempt a failure leaves behind.
/// <para>
/// <b>Why not part of <c>IOrderSettlementStore</c>.</b> That port's own remarks leave "pending, failed and
/// cancelled notifications" out on purpose: they only change a payment's status, and what they do to the
/// order's deadline is each provider's rule. PayHere's rule is here.
/// </para>
/// <para>
/// <b>Each state change is one transaction in the lock order of doc 08 §5.3</b> — the order row, then the
/// payment row — and a conditional <c>UPDATE</c> whose row count is the answer, so two deliveries of one
/// notification, or a notification racing the expiry job, produce exactly one winner and no deadlock.
/// </para>
/// </summary>
public interface IPayHerePaymentStore
{
    /// <summary>
    /// The PayHere payment <paramref name="paymentId"/> of the order numbered <paramref name="orderNumber"/>,
    /// or <see langword="null"/> when there is none: no such payment, a payment of another provider, or a
    /// payment that belongs to a different order. The three cases are not told apart, so a notification
    /// cannot be used to ask which payments exist.
    /// </summary>
    Task<PayHerePayment?> FindAsync(string orderNumber, int paymentId, CancellationToken cancellationToken);

    /// <summary>
    /// What the order page needs to describe payment <paramref name="paymentId"/> to the customer: the order's
    /// details, and the payment's status so the provider can tell "pay now" from "already pending". Only while
    /// the payment is PayHere's and its order is still <see cref="OrderStatus.PendingPayment"/>; anything else
    /// is <see langword="null"/> — there is nothing to pay.
    /// </summary>
    Task<PayHereCheckoutContext?> FindCheckoutContextAsync(int paymentId, CancellationToken cancellationToken);

    /// <summary>
    /// Writes PayHere's own id for the payment into <c>Payments.ProviderReference</c>, so a refund made in the
    /// PayHere portal can be matched to its payment. Idempotent; never changes a status.
    /// </summary>
    Task RecordReferenceAsync(
        int paymentId,
        string payHerePaymentId,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Status code 0: <see cref="PaymentStatus.Initiated"/> becomes <see cref="PaymentStatus.Pending"/> and the
    /// order's deadline moves to <c>max(due, placed + pendingMaxDays)</c>, together (doc 11 P8b step 4).
    /// </summary>
    Task<PayHereTransition> MarkPendingAsync(
        int paymentId,
        int orderId,
        string payHerePaymentId,
        int pendingMaxDays,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Status code −1 or −2: an open payment becomes <see cref="PaymentStatus.Failed"/>. If it had been
    /// Pending, the order's deadline is cut back to <c>min(due, now + expiryMinutes)</c>. If the order can
    /// still be paid, <b>a fresh Initiated attempt is opened in the same transaction</b>, so the order page
    /// offers the customer another go (doc 11 P8b step 6) without a request of their own.
    /// </summary>
    Task<PayHereFailureResult> MarkFailedAsync(
        PayHereFailure failure,
        CancellationToken cancellationToken);
}

/// <summary>A PayHere payment, as far as applying a notification needs to know.</summary>
/// <param name="PaymentId"><c>sales.Payments.Id</c>.</param>
/// <param name="OrderId"><c>sales.Orders.Id</c>.</param>
/// <param name="OrderPublicId">The order's token — what the admin order route takes, and what an alert names.</param>
/// <param name="OrderNumber">The number a customer quotes.</param>
/// <param name="PaymentStatus">The payment's status when it was read.</param>
/// <param name="OrderStatus">The order's status when it was read.</param>
/// <param name="Amount">The payment's LKR amount.</param>
/// <param name="ProviderReference">PayHere's id as last recorded, or null.</param>
/// <param name="ProviderTransactionId">The id of the money that settled or was routed, or null.</param>
public sealed record PayHerePayment(
    int PaymentId,
    int OrderId,
    Guid OrderPublicId,
    string OrderNumber,
    PaymentStatus PaymentStatus,
    OrderStatus OrderStatus,
    decimal Amount,
    string? ProviderReference,
    string? ProviderTransactionId);

/// <summary>
/// The order details a PayHere checkout form carries, copied off the order as it was placed. Personal data:
/// it is posted to PayHere by the customer's own browser, and kept nowhere else.
/// </summary>
/// <param name="PaymentId"><c>sales.Payments.Id</c>.</param>
/// <param name="Status">The payment's status: Initiated means the customer may still pay, Pending means they have.</param>
/// <param name="OrderNumber">The order number.</param>
/// <param name="OrderPublicId">The order's token, for the return and cancel addresses.</param>
/// <param name="AmountLkr">The payment's amount, which is the order's grand total.</param>
/// <param name="FirstName">The customer's first name.</param>
/// <param name="LastName">The customer's last name, or null.</param>
/// <param name="Email">The customer's email address.</param>
/// <param name="Phone">The telephone number on the order, or null.</param>
/// <param name="AddressLine">Where it is going: the delivery address, or the pickup store, or null.</param>
/// <param name="City">The delivery city, or the district for a pickup order, or null.</param>
public sealed record PayHereCheckoutContext(
    int PaymentId,
    PaymentStatus Status,
    string OrderNumber,
    Guid OrderPublicId,
    decimal AmountLkr,
    string FirstName,
    string? LastName,
    string Email,
    string? Phone,
    string? AddressLine,
    string? City);

/// <summary>What a status change did.</summary>
public enum PayHereTransition
{
    /// <summary>The payment moved and the transaction was committed.</summary>
    Applied,

    /// <summary>
    /// Nothing was written: the payment was no longer in a state this change applies to (already Pending,
    /// Failed or settled), or its order could no longer be paid. A duplicate or an out-of-order notification.
    /// </summary>
    NotApplicable,
}

/// <summary>A failed or cancelled payment.</summary>
/// <param name="PaymentId"><c>sales.Payments.Id</c>.</param>
/// <param name="OrderId"><c>sales.Orders.Id</c>.</param>
/// <param name="PayHerePaymentId">PayHere's id for the payment, kept as the provider reference.</param>
/// <param name="FailureCode"><c>CANCELLED</c> or <c>FAILED</c>.</param>
/// <param name="FailureMessage">PayHere's message, cleaned, or a plain sentence.</param>
/// <param name="ExpiryMinutes">The payment window the order's deadline is cut back to, if the payment was Pending.</param>
/// <param name="NowUtc">One instant for the transaction.</param>
public sealed record PayHereFailure(
    int PaymentId,
    int OrderId,
    string PayHerePaymentId,
    string FailureCode,
    string FailureMessage,
    int ExpiryMinutes,
    DateTime NowUtc);

/// <summary>What <see cref="IPayHerePaymentStore.MarkFailedAsync"/> did.</summary>
/// <param name="Transition">Whether the payment was moved.</param>
/// <param name="WasPending">Whether it had been Pending, so the deadline was cut back.</param>
/// <param name="ReplacementOpened">Whether a fresh Initiated attempt was opened for the order.</param>
public sealed record PayHereFailureResult(PayHereTransition Transition, bool WasPending, bool ReplacementOpened);

/// <summary>The <c>sales.Payments.FailureCode</c> values PayHere's flow writes.</summary>
public static class PayHereFailureCodes
{
    /// <summary>Status code −1: the customer cancelled at PayHere.</summary>
    public const string Cancelled = "CANCELLED";

    /// <summary>Status code −2: the payment failed.</summary>
    public const string Failed = "FAILED";
}
