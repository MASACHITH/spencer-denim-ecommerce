using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Payments.Abstractions;

/// <summary>
/// The three staff decisions of doc 11 P7 step 6 and step 8, each in <b>one transaction</b> with exactly
/// the effects the document lists (doc 08 §4.1, §4.3, §5.5, §5.7).
/// <para>
/// <b>Why they are a store and not three use cases' worth of EF.</b> Every one of them is a sequence of
/// conditional <c>UPDATE</c>s whose row counts <i>are</i> the concurrency control (doc 08 §4, §5.2): the
/// claim on the order row is the atomic act that decides who won, and EF's change tracker cannot express
/// "update this row only if its status is still what I read". Putting all three behind one port keeps
/// those statements in one file, where the lock order of doc 08 §5.3 and §4.2 N6 — <b>order row, then the
/// payment row, then the stock rows in ascending <c>VariantId</c>, then the promo row</b> — can be seen to
/// be the same in all of them.
/// </para>
/// <para>
/// <b>The payment row is chosen inside the transaction, never by the caller.</b> An admin request carries
/// an order token and nothing else. A payment id on the wire would be a stale value by the time it
/// arrived: a rejection closes a row and a resubmission opens a new one, so "approve payment 41" can
/// arrive after 41 was rejected and 42 created. Resolving it under the order's own lock makes that class
/// of mistake unrepresentable rather than guarded.
/// </para>
/// <para>
/// <b>Nothing here is on <c>IPaymentProvider</c>.</b> Approving a bank transfer is a staff decision about
/// a bank statement; no gateway has an equivalent, and PayPal's settlement arrives from a verified
/// webhook. What the two genuinely share is the <i>Paid effects</i> — stock reserved to sold, a gapless
/// invoice number, <c>PaidAtUtc</c>, the history row and the confirmation email — and those are written once,
/// in the infrastructure's <c>OrderSettlementEffects</c>, which this store calls and which
/// <see cref="IOrderSettlementStore"/> (the provider-facing transaction around them) calls too.
/// </para>
/// </summary>
public interface IBankTransferReviewStore
{
    /// <summary>
    /// Doc 11 P7 step 6a. Order <c>PendingPayment</c> to <c>Paid</c>, payment <c>Pending</c> to
    /// <c>Succeeded</c> as the settling payment with the approver and the time, stock reserved to sold, a
    /// gapless invoice number, and a history row with source <c>Staff</c>.
    /// </summary>
    Task<BankTransferDecisionResult> ApproveAsync(
        BankTransferDecision decision,
        CancellationToken cancellationToken);

    /// <summary>
    /// Doc 11 P7 step 6b. Payment <c>Pending</c> to <c>Failed</c> with the reviewer, the time and the note
    /// — and <b>the order stays <c>PendingPayment</c></b> until its due time, so the customer can transfer
    /// again.
    /// </summary>
    Task<BankTransferDecisionResult> RejectAsync(
        BankTransferDecision decision,
        CancellationToken cancellationToken);

    /// <summary>
    /// Doc 11 P7 step 8 and doc 08 §5.7. <c>Expired</c> or <c>Cancelled</c> to <c>Paid</c> with the same
    /// effects as an approval, after <b>re-checking stock for every line</b>; if any line is short the
    /// whole acceptance fails and <see cref="BankTransferDecisionResult.ShortStock"/> names every short
    /// line.
    /// </summary>
    Task<BankTransferDecisionResult> AcceptLateAsync(
        BankTransferDecision decision,
        CancellationToken cancellationToken);
}

/// <summary>
/// One staff decision about one order's bank payment.
/// </summary>
/// <param name="OrderId">
/// The internal order id, resolved by <see cref="IBankTransferReader"/> from the public token the admin
/// API carries. Internal because every statement joins on it.
/// </param>
/// <param name="Actor">
/// The <c>Orders.Approve</c> holder. Their id goes to <c>Payments.ApprovedByUserId</c> and to the history
/// row — a business fact, not an audit column (doc 08 §3.5 marks <c>Payments</c> <c>[RV]</c> and not
/// <c>[A]</c>) — and the rest of them goes to the <c>ops.AuditLogs</c> row this store writes <b>inside the
/// same transaction</b>, so doc 11 P7's "every approve and reject is audited" cannot be true of a decision
/// that rolled back, or false of one that committed.
/// </param>
/// <param name="Note">
/// The staff note. Optional on an approval, <b>required</b> on a rejection (P7 step 6b: "enters a
/// reason"), and the use case — not this store — is what enforces that.
/// </param>
/// <param name="NowUtc">
/// One instant for the whole transaction, taken from <c>IClock</c> before it opens. Every timestamp the
/// decision writes is this value, so <c>PaidAtUtc</c>, <c>ApprovedAtUtc</c>, <c>CompletedAtUtc</c>, the
/// invoice's issue time and the history row cannot disagree by a few milliseconds and make one event read
/// as several.
/// </param>
public sealed record BankTransferDecision(int OrderId, AdminActor Actor, string? Note, DateTime NowUtc);

/// <summary>What one decision did, or why it could not be made.</summary>
public abstract record BankTransferDecisionResult
{
    private BankTransferDecisionResult()
    {
    }

    /// <summary>Committed.</summary>
    /// <param name="OrderNumber">For the staff message and the audit row.</param>
    /// <param name="InvoiceNumber">
    /// The number issued inside this transaction, or <see langword="null"/> for a rejection, which issues
    /// none.
    /// </param>
    public sealed record Applied(string OrderNumber, string? InvoiceNumber) : BankTransferDecisionResult;

    /// <summary>
    /// The order is not there, or it is not a bank-transfer order. Also the answer when a non-bank order
    /// is aimed at this flow, so the admin API's 404 cannot be used to ask whether an order exists.
    /// </summary>
    public sealed record NotFound : BankTransferDecisionResult;

    /// <summary>
    /// Somebody or something else acted first, and the decision was not applied.
    /// <para>
    /// <b>This is the answer doc 11 P7's business rules ask for by name</b> — "if the expiry job or
    /// another staff member acted first, the approver is told and the screen reloads" — which is why it
    /// carries the order's and the payment's <i>current</i> state rather than only a sentence. The screen
    /// can then say "this order expired while you were looking at it; accept the late payment" instead of
    /// "something went wrong".
    /// </para>
    /// </summary>
    public sealed record Refused(
        string Code,
        string Message,
        OrderStatus OrderStatus,
        OrderStockState StockState,
        PaymentStatus? PaymentStatus) : BankTransferDecisionResult;

    /// <summary>
    /// Doc 11 P7 step 8: a late acceptance whose stock re-check failed, with <b>every</b> short line and
    /// not only the first one found. Nothing was written.
    /// </summary>
    /// <param name="Lines">Every line that could not be re-acquired.</param>
    /// <param name="OrderStatus">
    /// The order's status, which is <c>Expired</c> or <c>Cancelled</c> — the claim above only accepts those
    /// two.
    /// <para>
    /// <b>It is here because a client cannot otherwise tell that the order is closed, and one could not.</b>
    /// This was the only 409 in the flow carrying no status: the queue screen, having been refused with
    /// <c>short_stock</c>, forgot that expiry had won and offered <b>Approve</b> again on an order that can
    /// never be approved — an oscillation steering staff at an action that cannot succeed. The state has to
    /// travel with the refusal, because the refusal is the only thing the screen is told.
    /// </para>
    /// </param>
    public sealed record ShortStock(
        IReadOnlyList<ShortStockLine> Lines,
        OrderStatus OrderStatus) : BankTransferDecisionResult;
}

/// <summary>
/// One line a late acceptance could not re-acquire, in terms a staff member can act on.
/// </summary>
/// <remarks>
/// <b>Named, not numbered.</b> The first version carried variant id to quantity available, and a refusal
/// then read "variant 470 (0 left)" — an internal id, on a screen with no order-detail page to look it up
/// in, to somebody deciding whether to refund a customer's money. Every field here is already on
/// <c>sales.OrderLines</c> as a placement-time snapshot, so naming the item costs nothing extra: no join,
/// and the name shown is the name the customer bought under rather than whatever the catalogue says today.
/// <para>
/// <see cref="Needed"/> as well as <see cref="Available"/>, because "1 left" is not actionable on its own —
/// one of two is short and one of one is not.
/// </para>
/// </remarks>
public sealed record ShortStockLine(
    int VariantId,
    string Sku,
    string ProductName,
    string ColourName,
    string SizeLabel,
    int Needed,
    int Available);

/// <summary>The refusal codes a client may branch on. Stable strings, not message text.</summary>
public static class BankTransferRefusalCodes
{
    /// <summary>The order is no longer <c>PendingPayment</c> with its stock reserved.</summary>
    public const string OrderNotPending = "order_not_pending";

    /// <summary>The order is already paid — another staff member approved it, or a capture settled it.</summary>
    public const string AlreadyPaid = "order_already_paid";

    /// <summary>
    /// The order expired before the decision landed. The screen offers <b>Accept late payment</b>, which
    /// is doc 11 P7 step 8's whole purpose.
    /// </summary>
    public const string OrderExpired = "order_expired";

    /// <summary>The order is not <c>Expired</c> or <c>Cancelled</c>, so there is no late payment to accept.</summary>
    public const string OrderNotClosed = "order_not_closed";

    /// <summary>
    /// There is no bank payment on this order in a state this decision can act on: already approved,
    /// already rejected and not resubmitted, or expired.
    /// </summary>
    public const string PaymentNotReviewable = "payment_not_reviewable";

    /// <summary>Another payment already settles this order, so a second one must not (doc 08 §4.2).</summary>
    public const string AlreadySettled = "order_already_settled";
}
