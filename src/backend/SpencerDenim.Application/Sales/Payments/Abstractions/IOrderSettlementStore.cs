using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Payments.Abstractions;

/// <summary>
/// What a provider that is confirmed by <b>the provider itself</b> — a verified webhook, a capture
/// response, a recovery run — asks of the shop when money has arrived: pay the order, or, when the order
/// can no longer take it, keep the money visible (doc 08 §4.2 step 3 and step 3.4, §4.1, doc 11 P8/P10).
/// <para>
/// <b>This is the part of PayPal's and PayHere's flow that is the same for both</b>, and it is the same
/// effects a staff member's Approve has. Those effects — order <c>PendingPayment → Paid</c>, stock reserved
/// to sold, a gapless invoice number, the history row, the <c>PaymentConfirmed</c> email — are written once,
/// in the infrastructure's <c>OrderSettlementEffects</c>, which <c>BankTransferReviewStore</c> also calls;
/// this port is the provider-facing transaction around them. (<see cref="IPaymentProvider"/>'s remarks have
/// always called it "<c>IOrderSettlementStore</c>, which is a transaction and not a provider"; this is it.)
/// </para>
/// <para>
/// <b>Each method is one transaction, in the lock order of doc 08 §5.3 and §4.2 N6:</b> the order row
/// first, then the payment row, then the stock rows by ascending <c>VariantId</c>, then the number series.
/// The claim on the order is a conditional <c>UPDATE</c> whose row count is the concurrency control — see
/// <c>BankTransferReviewStore</c>'s remarks for why that, and not an isolation level, is the mechanism —
/// so two deliveries of one notification, or a webhook racing a return page, settle exactly once.
/// </para>
/// <para>
/// <b>What it does not do.</b> It does not talk to the provider, verify a signature or record the event —
/// the caller does the first two before calling and <c>IPaymentEventStore</c> the third around it.
/// It does not handle a pending, failed or cancelled notification, which only change a payment's status
/// and are each provider's own rule. And it does not accept an exception payment on a staff member's
/// behalf (doc 08 §4.2 step 7); that is a later use case that will reuse the same effects.
/// </para>
/// </summary>
public interface IOrderSettlementStore
{
    /// <summary>
    /// Doc 08 §4.2 step 3 for a payment the provider reports as <b>completed</b>.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item><description>
    /// <b>Which payments accept it (R1):</b> any not already settled — Initiated, Pending, Capturing,
    /// Failed, Cancelled or Expired. A payment already Succeeded, CapturedRequiresRefund, PartiallyRefunded
    /// or Refunded answers <see cref="PaymentSettlementResult.AlreadyHandled"/> and nothing is written. A
    /// <b>late</b> success on a Failed or Cancelled payment is therefore <i>not</i> a duplicate: it pays the
    /// order if the order can still take it, and any newer open attempt is closed in the same transaction
    /// (doc 08 Appendix B1).
    /// </description></item>
    /// <item><description>
    /// <b>The amount and currency are checked first</b> against what the provider was asked for —
    /// <c>ProviderAmount</c>/<c>ProviderCurrency</c> when the payment was converted, the order's LKR
    /// amount otherwise. A mismatch is routed to the exception path with
    /// <see cref="PaymentFailureCodes.AmountMismatch"/> (R9): the payment can only be refunded, never
    /// accepted, and nothing is deducted.
    /// </description></item>
    /// <item><description>
    /// <b>Then the settlement</b>, behind a savepoint: claim the order (<c>Status = 0</c>,
    /// <c>StockState = 0</c>, no settlement yet, and the order is this provider's), then settle the payment
    /// (<c>Succeeded</c>, the settling payment). If the order claim affects no row — the order expired, was
    /// cancelled, was paid another way — or the payment update then affects none, the savepoint undoes the
    /// order update and the payment goes down the exception path. The whole transaction is never rolled
    /// back for that, so a retry loop cannot repeat it forever (Appendix B4).
    /// </description></item>
    /// <item><description>
    /// <b>The exception path</b> moves the payment to <see cref="PaymentStatus.CapturedRequiresRefund"/>
    /// with the settlement flag clear, writes a <c>PaymentException</c> admin alert in the same transaction,
    /// and leaves the order as it was. The money has arrived and is on record; a person decides.
    /// </description></item>
    /// </list>
    /// </remarks>
    Task<PaymentSettlementResult> SettleAsync(
        PaymentSettlement settlement,
        CancellationToken cancellationToken);

    /// <summary>
    /// The exception path on its own, for a caller that already knows the payment must not settle the
    /// order — it found the amount wrong, or the merchant not ours — without asking the store to try.
    /// </summary>
    /// <remarks>
    /// Same transaction shape and same guards as the fall-through inside <see cref="SettleAsync"/>: the
    /// payment moves to CapturedRequiresRefund only from a status that has not settled, an alert is written
    /// only if it moved, and a payment that has already settled or been routed answers
    /// <see cref="PaymentSettlementResult.AlreadyHandled"/>. Calling it twice raises one alert.
    /// </remarks>
    Task<PaymentSettlementResult> RouteToExceptionAsync(
        PaymentExceptionRouting routing,
        CancellationToken cancellationToken);
}

/// <summary>A completed payment, as the provider reports it.</summary>
/// <param name="PaymentId"><c>sales.Payments.Id</c> — the attempt the provider's notification is about.</param>
/// <param name="ProviderCode">
/// The provider confirming it. The payment <b>and</b> the order must belong to this provider, or the answer
/// is <see cref="PaymentSettlementResult.NotFound"/> (a bank-transfer order cannot be paid by a gateway's
/// notification, for the reason <c>BankTransferReviewStore</c> records in the other direction).
/// <para>
/// <b>The bank-transfer code itself is refused with an <see cref="ArgumentException"/></b>, not answered:
/// a bank transfer is confirmed by a person (the approver, the audit row and the <c>Orders.Approve</c>
/// permission are <c>IBankTransferReviewStore</c>'s), and a caller that reaches this store with that code
/// has a bug that "settled" would turn into an unapproved payment.
/// </para>
/// </param>
/// <param name="ConfirmedAmount">
/// The amount the provider says it took, in <paramref name="ConfirmedCurrency"/> — a decimal parsed by the
/// caller, not a string.
/// </param>
/// <param name="ConfirmedCurrency">The currency the provider says it took it in (ISO 4217).</param>
/// <param name="NowUtc">
/// One instant for the whole transaction, from <c>IClock</c> taken before it opens, so every timestamp it
/// writes agrees (the reason <c>BankTransferDecision</c> carries one).
/// </param>
/// <param name="ProviderTransactionId">
/// The provider's id for the money — a capture id, a payment id. Stored on the payment; the unique index
/// on <c>(ProviderCode, ProviderTransactionId)</c> is what stops one lot of money settling two payments.
/// </param>
/// <param name="PayerReference">The provider's id for the payer, if it gives one.</param>
/// <param name="Note">
/// The history row's note. Defaults to "Payment confirmed by <i>provider code</i>". No personal data.
/// </param>
public sealed record PaymentSettlement(
    int PaymentId,
    string ProviderCode,
    decimal ConfirmedAmount,
    string ConfirmedCurrency,
    DateTime NowUtc,
    string? ProviderTransactionId = null,
    string? PayerReference = null,
    string? Note = null);

/// <summary>A payment the caller has decided cannot settle its order.</summary>
/// <param name="PaymentId"><c>sales.Payments.Id</c>.</param>
/// <param name="ProviderCode">The provider; the payment must belong to it.</param>
/// <param name="Reason">One of <see cref="PaymentExceptionReasons"/>.</param>
/// <param name="NowUtc">One instant for the transaction.</param>
/// <param name="ConfirmedAmount">What the provider says it took, for the alert. Null if unknown.</param>
/// <param name="ConfirmedCurrency">In what currency. Null if unknown.</param>
/// <param name="ProviderTransactionId">The provider's id for the money, stored on the payment.</param>
/// <param name="PayerReference">The provider's id for the payer.</param>
public sealed record PaymentExceptionRouting(
    int PaymentId,
    string ProviderCode,
    string Reason,
    DateTime NowUtc,
    decimal? ConfirmedAmount = null,
    string? ConfirmedCurrency = null,
    string? ProviderTransactionId = null,
    string? PayerReference = null);

/// <summary>What one settlement call did, or why it did nothing.</summary>
public abstract record PaymentSettlementResult
{
    private PaymentSettlementResult()
    {
    }

    /// <summary>Committed: the order is Paid, the payment is the settling payment.</summary>
    /// <param name="OrderNumber">For the caller's log and the customer-facing page.</param>
    /// <param name="InvoiceNumber">The gapless number issued inside the transaction.</param>
    public sealed record Settled(string OrderNumber, string InvoiceNumber) : PaymentSettlementResult;

    /// <summary>
    /// Committed: the order could not take the money, so the payment is CapturedRequiresRefund and an
    /// alert has been raised. <b>Not an error</b> — the money is on record, which is the point.
    /// </summary>
    /// <param name="Reason">One of <see cref="PaymentExceptionReasons"/>.</param>
    /// <param name="OrderNumber">For the caller's log.</param>
    public sealed record CapturedRequiresRefund(string Reason, string OrderNumber) : PaymentSettlementResult;

    /// <summary>
    /// Nothing was written: the payment had already settled, been routed to the exception path, or been
    /// refunded. A duplicate delivery lands here; the caller marks its event Ignored.
    /// </summary>
    /// <param name="PaymentStatus">The payment's status when the call looked.</param>
    public sealed record AlreadyHandled(PaymentStatus PaymentStatus) : PaymentSettlementResult;

    /// <summary>
    /// Nothing was written: there is no such payment of this provider, or its order belongs to another
    /// provider. Also what a payment id from a different provider gets, so the answer cannot be used to
    /// ask which payments exist.
    /// </summary>
    public sealed record NotFound : PaymentSettlementResult;
}

/// <summary>
/// Why a completed payment could not settle its order — stable strings, not message text, because a
/// staff screen and a test branch on them.
/// </summary>
public static class PaymentExceptionReasons
{
    /// <summary>The order expired before the money arrived (doc 11 P8: "completed, but the order is no longer payable").</summary>
    public const string OrderExpired = "order_expired";

    /// <summary>The order was cancelled before the money arrived.</summary>
    public const string OrderCancelled = "order_cancelled";

    /// <summary>
    /// The order is already paid — by another payment, or by staff — so this is a duplicate payment
    /// (doc 08 §4.1: "an order that is already Paid and receives a duplicate capture keeps its status").
    /// </summary>
    public const string OrderAlreadyPaid = "order_already_paid";

    /// <summary>
    /// The order is not payable for some other reason: its stock is not reserved, it belongs to another
    /// provider, or it is not in a state this build knows how to settle.
    /// </summary>
    public const string OrderNotPayable = "order_not_payable";

    /// <summary>
    /// The amount or currency the provider took is not what it was asked for (R9). Carries
    /// <see cref="PaymentFailureCodes.AmountMismatch"/> on the payment.
    /// </summary>
    public const string AmountMismatch = "amount_mismatch";

    /// <summary>
    /// The provider's transaction id or payer reference cannot be stored as given — longer than its column,
    /// or (for the transaction id) not printable ASCII, which a <c>varchar</c> column in a unique index
    /// would fold into a collision. <b>The money has arrived and is kept as an exception rather than
    /// refused</b>: a provider anomaly must never be a reason for a payment to go unrecorded. The unusable
    /// id is not stored; the payment's <c>FailureMessage</c> says so and quotes the start of it.
    /// </summary>
    public const string ProviderDataInvalid = "provider_data_invalid";

    /// <summary>Every reason, for validation.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        OrderExpired,
        OrderCancelled,
        OrderAlreadyPaid,
        OrderNotPayable,
        AmountMismatch,
        ProviderDataInvalid,
    ];
}

/// <summary>
/// <c>sales.Payments.FailureCode</c> values that mean something to the rest of the system (doc 08 §3.5,
/// §4.2 step 7). <c>BankTransferFailureCodes</c> holds the bank flow's.
/// </summary>
public static class PaymentFailureCodes
{
    /// <summary>
    /// The amount or currency did not match (R9). <b>A payment carrying this can only be refunded, never
    /// accepted</b> — the staff use case that accepts an exception payment must refuse it.
    /// </summary>
    public const string AmountMismatch = "AMOUNT_MISMATCH";
}
