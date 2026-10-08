using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Orders.Abstractions;

/// <summary>
/// Doc 11 P9's fulfilment transitions and the internal note, each in <b>one transaction</b> with exactly
/// the effects doc 08 §4.1's table lists.
/// <para>
/// <b>The claim is the lock, and there is nothing else.</b> Every method here starts with one conditional
/// <c>UPDATE</c> on <c>sales.Orders</c> whose <c>WHERE</c> carries the status that transition is allowed
/// from. Doc 08 §5.2's argument is why that is enough and why it is stronger than an isolation level: SQL
/// Server takes an update lock on the qualifying row, a second writer waits, and then <b>re-evaluates the
/// predicate against the row as it now is</b> — so two staff members pressing "Mark as shipped" at once do
/// not both see <c>Processing</c>; the second affects 0 rows and is told where the order actually is.
/// </para>
/// <para>
/// <b>No <c>sp_getapplock</c> and no <c>Serializable</c>.</b> Doc 08 §4.2 names neither on this path, and
/// the two places doc 08 does name an application lock are the promo per-customer limit (§3.6) and the
/// last-Super-Admin guard (§3.1 DB4 rule 4, <c>sp_getapplock 'rbac:grants-all'</c>) — neither is here.
/// (Corrected by W3-04b's tester: this used to say "§4.2 names an application lock in exactly two
/// places", which §4.2 does not, and to name "the reference-data seeder", which holds no applock in doc 08
/// or in the code. The wording had been copied into three files before anybody checked it.)
/// An applock would add a second, weaker gate beside the one the database already enforces, and
/// <c>Serializable</c> would turn a lost race from "0 rows, here is why" into a deadlock to retry.
/// </para>
/// <para>
/// <b>Orders never move backwards and this interface cannot express it.</b> There is no
/// <c>MarkProcessingAgain</c>, no "undo shipped", no status argument. Doc 08 §4.1 and doc 11 P9 both say a
/// mistake is corrected with an internal note and a history note, so the only writer of a correction is
/// <see cref="SetInternalNoteAsync"/> — which writes a history entry whose from and to status are equal, a
/// convention <c>OrderStatusHistoryEntry</c> documents.
/// </para>
/// <para>
/// <b>No stock and no number series, so no lock-order concern.</b> Doc 08 §4.1 gives these four
/// transitions no stock effect (the stock was committed at Paid) and no invoice allocation, so none of them
/// touches <c>inventory.StockLevels</c> or <c>sales.NumberSeries</c> and doc 08 §5.3's ordering has nothing
/// to order. The one path in this task that <i>does</i> touch both — accepting a late payment on an expired
/// order — is <c>IBankTransferReviewStore.AcceptLateAsync</c>, already built, and is reused rather than
/// reimplemented.
/// </para>
/// <para>
/// <b>Nothing here queues an email and nothing claims one was sent.</b> <c>ops.EmailOutbox</c> is
/// specified (doc 08 §3.8) and unbuilt; W3-06 owns it. Doc 11 P9 step 5 queues <c>OrderShipped</c> with the
/// tracking link and step 8 queues nothing — so the one email this flow owes is the dispatch notice, and
/// the honest route today is the shopper's own order page — which shows <b>one sentence</b> beside
/// <c>Payment</c>, not a timeline. An earlier version of this remark said "a status timeline" and that was
/// simply wrong about another page; it is the wrong-comment class, and a confidently-stated fact about
/// somebody else's screen is the version of it that travels furthest. What that sentence now says is at
/// least correct for a pickup order, which it was not before this task.
/// </para>
/// </summary>
public interface IOrderFulfilmentStore
{
    /// <summary>
    /// Doc 11 P9 step 2. <c>Paid → Processing</c>, guard <c>Status = 1</c>, no side effects but the history
    /// row.
    /// </summary>
    Task<OrderTransitionResult> StartProcessingAsync(
        OrderTransition transition,
        CancellationToken cancellationToken);

    /// <summary>
    /// Doc 11 P9 steps 4 and 5, and the pickup variant's step 2. <c>Processing → Shipped</c>, guard
    /// <c>Status = 2</c>, setting <c>ShippedAtUtc</c>.
    /// </summary>
    /// <param name="transition">Who and when.</param>
    /// <param name="dispatch">
    /// The courier and the tracking number, or <see langword="null"/> for a pickup order — which needs
    /// neither (doc 08 §4.1: "CourierId and TrackingNumber required unless PICKUP").
    /// <para>
    /// <b>The pickup-ness is re-checked inside the transaction and not taken from this argument.</b> A
    /// caller that sent no dispatch for a delivery order must be refused rather than allowed to ship it
    /// blind, and a caller that sent one for a pickup order must not have a courier written on to an order
    /// nobody is couriering.
    /// </para>
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<OrderTransitionResult> MarkShippedAsync(
        OrderTransition transition,
        OrderDispatchDetails? dispatch,
        CancellationToken cancellationToken);

    /// <summary>
    /// Doc 11 P9 step 8, and the pickup variant's step 4 ("Mark as collected"). <c>Shipped → Delivered</c>,
    /// guard <c>Status = 3</c>, setting <c>DeliveredAtUtc</c>. Enables a "Verified buyer" review (P12).
    /// </summary>
    Task<OrderTransitionResult> MarkDeliveredAsync(
        OrderTransition transition,
        CancellationToken cancellationToken);

    /// <summary>
    /// Corrects the courier or the tracking number on an order that has already shipped (doc 11 P9's
    /// "wrong tracking number entered").
    /// </summary>
    /// <remarks>
    /// <b>Not a transition: the status does not move and must not.</b> Doc 11 P9 lists the courier, the
    /// tracking number and the internal note as the only three things editable after placement, and this
    /// is two of them. It writes a history row with equal from and to statuses so the correction is in the
    /// order's timeline, and <b>no second dispatch notice is sent</b> — P9 says so, and adds that whoever
    /// corrects it tells the customer by telephone or WhatsApp the same day, because the customer is
    /// holding a tracking link that does not work. The screen says that in words.
    /// </remarks>
    Task<OrderTransitionResult> UpdateDispatchAsync(
        OrderTransition transition,
        OrderDispatchDetails dispatch,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the staff-only internal note, writing a history row so the change is in the timeline.
    /// </summary>
    /// <param name="transition">Who and when.</param>
    /// <param name="note">
    /// The new note, or <see langword="null"/> to clear it.
    /// <para>
    /// <b>Null here means "clear it", and that is only safe because the use case can tell null from
    /// absent.</b> Four defects in this project were a save that did not mention a field silently clearing
    /// it (W1-12's category images, W1-03's mapper, GLB-10's audit snapshot), so the <i>endpoint</i> reads
    /// presence off the JSON and a request that does not carry <c>internalNote</c> never reaches this
    /// method at all.
    /// </para>
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<OrderTransitionResult> SetInternalNoteAsync(
        OrderTransition transition,
        string? note,
        CancellationToken cancellationToken);
}

/// <summary>
/// One staff act on one order.
/// </summary>
/// <param name="OrderId">
/// The internal order id, resolved by <see cref="IAdminOrderReader.FindTargetAsync"/> from the public
/// token the admin API carries.
/// </param>
/// <param name="Actor">
/// The <c>Orders.Edit</c> holder. Their id goes to the history row — a business fact, not an audit column
/// — and the rest of them to the <c>ops.AuditLogs</c> row the store writes <b>inside the same
/// transaction</b>, so doc 11 P9's "every transition is audited" cannot be true of one that rolled back,
/// or false of one that committed.
/// </param>
/// <param name="Note">
/// An optional staff note, appended to the history row's sentence. Clipped, never refused, for the reason
/// <c>OrderStatusHistoryEntry.NoteMaxLength</c> gives: a transition that threw on its way to the database
/// because a note was two characters too long would roll back a decision for a reason nobody would guess.
/// </param>
/// <param name="NowUtc">
/// One instant for the whole transaction, taken from <c>IClock</c> before it opens, so
/// <c>ShippedAtUtc</c>, the history row and the audit row cannot disagree by milliseconds and make one
/// event read as several.
/// </param>
public sealed record OrderTransition(int OrderId, AdminActor Actor, string? Note, DateTime NowUtc);

/// <summary>
/// The courier and tracking number a dispatch carries (doc 11 P9 step 4).
/// </summary>
/// <param name="CourierId">
/// Which courier, from <c>sales.Couriers</c>. Validated inside the transaction against the live table:
/// a courier deactivated between the screen's read and the press must not be written on to an order.
/// </param>
/// <param name="TrackingNumber">
/// What the courier gave. Required: doc 08 §4.1 makes both mandatory for a non-pickup dispatch, and
/// shipping without one would leave the customer with no way to follow the parcel and no row anywhere
/// saying so.
/// </param>
public sealed record OrderDispatchDetails(int CourierId, string TrackingNumber);

/// <summary>What one transition did, or why it could not be done.</summary>
public abstract record OrderTransitionResult
{
    private OrderTransitionResult()
    {
    }

    /// <summary>Committed.</summary>
    /// <param name="OrderNumber">For the staff message and the audit row.</param>
    /// <param name="Status">
    /// The order's status now. Returned so the screen can redraw from the answer instead of guessing, and
    /// so a correction — which does not move the status — reports the same value it started with rather
    /// than nothing.
    /// </param>
    public sealed record Applied(string OrderNumber, OrderStatus Status) : OrderTransitionResult;

    /// <summary>No such order. A bare 404.</summary>
    public sealed record NotFound : OrderTransitionResult;

    /// <summary>
    /// Somebody or something else acted first, and nothing was changed.
    /// <para>
    /// <b>It carries the order's current state, not only a sentence</b>, which is the lesson W3-03's
    /// <c>ShortStock</c> paid for: a screen refused with no state went back to offering an action that
    /// could never succeed. The <paramref name="Code"/> is what a client branches on;
    /// <paramref name="Message"/> is what a staff member reads.
    /// </para>
    /// </summary>
    public sealed record Refused(
        string Code,
        string Message,
        OrderStatus Status,
        string StatusLabel) : OrderTransitionResult;
}

/// <summary>The refusal codes a client may branch on. Stable strings, not message text.</summary>
public static class OrderTransitionRefusalCodes
{
    /// <summary>
    /// The order is not in the status this transition starts from. The refusal names where it actually is,
    /// which is the only thing that tells a staff member what to do next.
    /// </summary>
    public const string WrongStatus = "order_wrong_status";

    /// <summary>
    /// A delivery order was sent to Shipped with no courier or no tracking number (doc 08 §4.1).
    /// </summary>
    public const string DispatchDetailsRequired = "dispatch_details_required";

    /// <summary>
    /// A pickup order was sent a courier or a tracking number. Doc 11 P9's pickup variant needs neither,
    /// and writing one would put a courier on an order nobody is couriering.
    /// </summary>
    public const string DispatchDetailsNotAllowed = "dispatch_details_not_allowed";

    /// <summary>
    /// The courier named does not exist, or is no longer active. A separate code from
    /// <see cref="DispatchDetailsRequired"/> because the remedy differs: one is "fill the field in", the
    /// other is "that carrier has been retired, pick another".
    /// </summary>
    public const string CourierUnavailable = "courier_unavailable";

    /// <summary>
    /// The order has not shipped, so there is no dispatch to correct. Doc 11 P9 allows the courier and the
    /// tracking number to be corrected; it does not allow them to be set on an order that has not gone.
    /// </summary>
    public const string NotShipped = "order_not_shipped";
}
