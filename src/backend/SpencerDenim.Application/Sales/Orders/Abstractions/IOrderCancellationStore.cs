using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Orders.Abstractions;

/// <summary>
/// Doc 11 P10 flow A, step 3: closing an <b>unpaid</b> order, in one transaction with exactly the effects
/// doc 08 §4.1's <c>PendingPayment → Cancelled</c> row lists.
/// <para>
/// <b>Why this is not a member of <see cref="IOrderFulfilmentStore"/>.</b> That interface's own remarks
/// say it touches no stock, no promo use and no number series, so doc 08 §5.3's lock order "has nothing to
/// order" there. Cancel touches stock, so it has an ordering obligation the fulfilment transitions do not,
/// and putting it beside them would make that remark false for one of five methods — the kind of true
/// sentence that quietly stops being true. It is also a different permission question (doc 11 P10's table,
/// not P9's) and has a second, non-staff caller.
/// </para>
/// <para>
/// <b>Cancelled is a closing state and nothing here can move an order out of one.</b> Doc 08 §4.1 allows
/// exactly one edge out of <c>Cancelled</c> — <c>Cancelled → Paid</c>, "accept late / exception payment",
/// which <c>IBankTransferReviewStore.AcceptLateAsync</c> already is (W3-03, issue #124) — and this
/// interface has no member that could express any other. There is no "uncancel".
/// </para>
/// <para>
/// <b>The claim is the lock, and there is nothing else.</b> One conditional <c>UPDATE</c> on
/// <c>sales.Orders</c> carrying all four of doc 11 P10 step 2's guards, and the row count is the
/// concurrency control (doc 08 §5.2). No <c>sp_getapplock</c>, no <c>Serializable</c> and no rowversion:
/// §5.2's argument is that SQL Server takes an update lock on the qualifying row, a second writer waits
/// and then <b>re-evaluates the predicate against the row as it now is</b> — which is what makes the
/// customer's Cancel and the expiry sweep taking the same order produce exactly one winner and exactly one
/// release. That race has its own integration test.
/// </para>
/// <para>
/// <b>No email.</b> <c>ops.EmailOutbox</c> is specified (doc 08 §3.8) and unbuilt; W3-06 owns it. Doc 11
/// P10's notification list says there is <b>no</b> email on cancellation in any case (§7 G-14), so nothing
/// here is owed one and nothing claims one was sent.
/// </para>
/// </summary>
public interface IOrderCancellationStore
{
    /// <summary>
    /// Doc 11 P10 flow A: <c>PendingPayment → Cancelled</c> with the reason and the closed time, the open
    /// payment <c>→ Cancelled</c>, the stock released, the promo use returned and a history row whose
    /// source is the caller.
    /// </summary>
    Task<CancelOrderOutcome> CancelAsync(
        OrderCancellation cancellation,
        CancellationToken cancellationToken);
}

/// <summary>
/// Who is cancelling, which decides the history row's source — and that row is the <b>only</b> record of
/// who closed the order.
/// </summary>
/// <remarks>
/// <b>Two members, and the System is deliberately not one of them.</b> Doc 08 §4.1 says it in as many
/// words: <c>PendingPayment → Cancelled</c> is "<c>Orders.Edit</c>; customer (own order). <b>Never the
/// System</b> (G-09)" — a denied PayPal capture only shortens <c>PaymentDueAtUtc</c> and the order then
/// reaches <b>Expired</b>, not Cancelled, because Expired means nobody paid in time and Cancelled means a
/// person decided. Leaving <c>SystemJob</c> out of this enum is what stops a future job expressing one.
/// </remarks>
public enum OrderCancelledBy
{
    /// <summary>The shopper, from their own order link (doc 11 P10 step 1, "account or order link").</summary>
    Customer = 1,

    /// <summary>A staff member holding <c>Orders.Edit</c>, from the order detail, with a reason.</summary>
    Staff = 2,
}

/// <summary>One cancellation of one order.</summary>
/// <param name="OrderPublicId">
/// The order's own unguessable token, which is what both callers hold: a guest's order link is the
/// shopper's only credential (doc 08 §3.3), and the admin screen carries the same token because the
/// internal id never leaves the server (GLB-11).
/// <para>
/// <b>Both callers pass the token and the store resolves it</b>, rather than the customer path resolving
/// it and the staff path passing an id. One shape means the four guards are evaluated against the same
/// row by the same statement for both; two shapes would be two chances for one of them to drift.
/// </para>
/// </param>
/// <param name="CancelledBy">
/// Customer or staff. It reaches <c>OrderStatusHistory.Source</c> unchanged, so the timeline says who
/// closed the order rather than what kind of request arrived.
/// </param>
/// <param name="Actor">
/// The staff member, for the history row's user id and the <c>ops.AuditLogs</c> row written inside the
/// same transaction. <see langword="null"/> for a customer: there is no customer sign-in in this build
/// (ACC-01 is W3-01), the order's token is the only credential there is, and <c>ops.AuditLogs</c> is the
/// staff audit trail (doc 08 §3.8) — a row with no user against it would read as a system act, which is
/// precisely what a customer cancel is not.
/// </param>
/// <param name="Reason">
/// Why, stored in <c>Orders.CancelReason</c> (<c>nvarchar(500)</c>). <b>Required for staff</b> (doc 11 P10
/// step 1: "clicks <i>Cancel</i> and enters a reason"), and <see langword="null"/> for a customer, who is
/// not asked for one — the history row's source already says they decided, and inventing a sentence to put
/// in a column the admin screen prints under "Why it was cancelled" would be putting words in a shopper's
/// mouth.
/// </param>
/// <param name="NowUtc">
/// One instant for the whole transaction, taken before it opens, so <c>ClosedAtUtc</c>, the payment's
/// <c>CompletedAtUtc</c>, the history row and the audit row cannot disagree by milliseconds and make one
/// event read as several.
/// </param>
public sealed record OrderCancellation(
    Guid OrderPublicId,
    OrderCancelledBy CancelledBy,
    AdminActor? Actor,
    string? Reason,
    DateTime NowUtc);

/// <summary>What one cancellation did, or why it could not be done.</summary>
public abstract record CancelOrderOutcome
{
    private CancelOrderOutcome()
    {
    }

    /// <summary>Cancelled. The stock is back and the open payment is closed.</summary>
    /// <param name="OrderNumber">For the staff message and the audit row.</param>
    public sealed record Applied(string OrderNumber) : CancelOrderOutcome;

    /// <summary>No such order.</summary>
    public sealed record NotFound : CancelOrderOutcome;

    /// <summary>
    /// The order cannot be cancelled, and nothing was changed.
    /// <para>
    /// It carries the order's current state as well as a sentence, which is W3-03's <c>ShortStock</c>
    /// lesson: a screen refused with no state went back to offering an action that could never succeed.
    /// </para>
    /// </summary>
    public sealed record Refused(
        string Code,
        string Message,
        OrderStatus Status,
        string StatusLabel) : CancelOrderOutcome;
}

/// <summary>The refusal codes a client may branch on. Stable strings, not message text.</summary>
public static class CancelOrderRefusalCodes
{
    /// <summary>
    /// The order is not awaiting payment, so there is nothing to cancel. The refusal names where it
    /// actually is.
    /// <para>
    /// <b>A paid order is this code and not a special one</b>, deliberately. Doc 11 P10's table is
    /// explicit that a paid order is <b>never</b> cancelled — it is refunded instead — and refunds are not
    /// in this build (owner decision, 2026-10-08: manual bank transfer, recorded in the order's internal
    /// note). So the honest answer is "this order is paid, cancel does not apply", and the screens say to
    /// use a refund; what must not happen is a cancel path that quietly becomes one.
    /// </para>
    /// </summary>
    public const string WrongStatus = "order_not_cancellable";

    /// <summary>
    /// A PayPal capture is in flight on this order (doc 08 §4.1's capture-lock guard, doc 11 P10's
    /// "customer cancels while a PayPal capture is in progress → blocked").
    /// </summary>
    public const string CaptureInProgress = "capture_in_progress";

    /// <summary>
    /// The order holds a PayPal payment in <b>PENDING</b>, so doc 11 P10's table gives the decision to
    /// <c>Orders.Approve</c> alone, after an explicit warning that a late capture will arrive as a payment
    /// exception needing a refund (doc 08 §4.1 R1).
    /// <para>
    /// <b>Unreachable in this build, and built anyway because the guard is the point.</b> Nothing captures
    /// — PayPal is W4-01 and its provider row is seeded disabled — so no payment can be a PayPal PENDING
    /// one outside a fixture. The <i>guard</i> is what stops a customer or an <c>Orders.Edit</c> holder
    /// cancelling an order whose money is about to arrive; the <c>Orders.Approve</c>-with-warning branch
    /// that may then continue is <b>not built</b> and is W4-01's, because a warning about a refund is
    /// worth nothing while there is no refund.
    /// </para>
    /// </summary>
    public const string PayPalPending = "paypal_pending";

    /// <summary>
    /// The same rule for any other gateway that confirms its own payments (PayHere's pending status is the
    /// first): a payment of a provider that is <b>not</b> a bank transfer is <c>Pending</c>, so the money may
    /// be on its way and the decision is not the customer's or an <c>Orders.Edit</c> holder's. Doc 11 P8b
    /// step 6 gives a Pending PayHere payment P8 step 9's treatment.
    /// <para>
    /// <b>A bank transfer's Pending is the ordinary state of an unpaid order and never blocks a cancel</b> -
    /// flow A exists to cancel exactly that - which is why the rule is "a gateway payment" and not "any
    /// Pending payment". <see cref="PayPalPending"/> keeps its own string so nothing that matches on it
    /// changes; this one is for everybody else.
    /// </para>
    /// </summary>
    public const string GatewayPaymentPending = "gateway_payment_pending";

    /// <summary>
    /// A payment on this order is already the one that settles it, so the money is in and cancel does not
    /// apply (doc 11 P10 step 2's "no settling payment").
    /// </summary>
    public const string PaymentSettling = "payment_settling";
}
