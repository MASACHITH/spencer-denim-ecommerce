using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Application.Sales.Orders.Contracts;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Orders;

/// <summary>
/// Doc 11 P9's fulfilment transitions, each as one transaction (doc 08 §4.1).
/// <para>
/// <b>The claim is the lock.</b> Every path starts with one conditional <c>UPDATE</c> on
/// <c>sales.Orders</c> whose <c>WHERE</c> carries the status that transition is allowed from, and the row
/// count is the concurrency control. Doc 08 §5.2's argument applies exactly: SQL Server takes an update
/// lock on the qualifying row, a second writer waits for it, and then <b>re-evaluates the predicate
/// against the row as it now is</b> — so two staff members pressing "Mark as shipped" at once do not both
/// see <c>Processing</c>, and a transition racing the auto-deliver job has one winner rather than a wrong
/// answer.
/// </para>
/// <para>
/// <b>No <c>sp_getapplock</c>, no <c>Serializable</c>, no rowversion.</b> Doc 08 §4.2 names neither of the
/// first two on this path (§3.6's promo limit and the reference-data seeder are the only two places an
/// application lock belongs), and §4.1's own note says the transitions deliberately do <i>not</i> rely on
/// the admin ETag: a stale admin edit is rejected by a token, a lost race between two transitions is
/// rejected by a row count, and those are different guarantees. Every transaction here is
/// <c>ReadCommitted</c>, the same as checkout's and the bank review's.
/// </para>
/// <para>
/// <b>No stock, no promo use, no number series</b> — doc 08 §4.1 gives these transitions none of those, so
/// §5.3's lock order has nothing to order here. The one path in this screen that does touch stock and the
/// series is accepting a late payment, which is <c>IBankTransferReviewStore.AcceptLateAsync</c> and is
/// reused rather than reimplemented.
/// </para>
/// <para>
/// <b>No email, and nothing claims one was sent.</b> <c>ops.EmailOutbox</c> does not exist (W3-06); doc 11
/// P9 step 5's <c>OrderShipped</c> is specified and unbuilt. The shopper's own order page already shows a
/// status timeline, which is the honest route.
/// </para>
/// </summary>
internal sealed class OrderFulfilmentStore(AppDbContext dbContext, IAuditLogWriter auditLog)
    : IOrderFulfilmentStore
{
    public Task<OrderTransitionResult> StartProcessingAsync(
        OrderTransition transition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transition);

        return InTransactionAsync(
            async () =>
            {
                if (await ReadOrderAsync(transition.OrderId, cancellationToken) is not { } snapshot)
                {
                    return new OrderTransitionResult.NotFound();
                }

                // Doc 08 §4.1 "Paid -> Processing": guard Status = 1, no other side effects. StockState is
                // deliberately *not* in the guard: §4.1's table names it for the money transitions and not
                // for this one, and adding a condition the document does not have would refuse a legitimate
                // transition on an order whose stock state a later milestone moves for another reason.
                var claimed = await ClaimAsync(
                    transition,
                    OrderStatus.Paid,
                    OrderStatus.Processing,
                    extraSet: null,
                    cancellationToken);

                if (claimed != 1)
                {
                    return await DiagnoseAsync(
                        transition.OrderId,
                        OrderStatus.Paid,
                        snapshot.IsPickup,
                        cancellationToken);
                }

                await WriteHistoryAsync(
                    transition,
                    OrderStatus.Paid,
                    OrderStatus.Processing,
                    Note("Processing started", transition.Note),
                    cancellationToken);

                EnlistAudit(transition, snapshot, AuditActions.StatusChange, "processing", null);

                await dbContext.SaveChangesAsync(cancellationToken);

                return new OrderTransitionResult.Applied(snapshot.OrderNumber, OrderStatus.Processing);
            },
            cancellationToken);
    }

    public Task<OrderTransitionResult> MarkShippedAsync(
        OrderTransition transition,
        OrderDispatchDetails? dispatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transition);

        return InTransactionAsync(
            async () =>
            {
                if (await ReadOrderAsync(transition.OrderId, cancellationToken) is not { } snapshot)
                {
                    return new OrderTransitionResult.NotFound();
                }

                // **Pickup-ness is re-read here and not taken from the caller.** The use case already
                // refused the obviously wrong combination, but the method a caller names and the order's
                // actual delivery method are two different facts, and the one that decides whether a
                // courier is written must be the order's.
                if (snapshot.IsPickup && dispatch is not null)
                {
                    return Refused(
                        OrderTransitionRefusalCodes.DispatchDetailsNotAllowed,
                        "This is a store pickup order, so it has no courier and no tracking number.",
                        snapshot);
                }

                if (!snapshot.IsPickup && dispatch is null)
                {
                    return Refused(
                        OrderTransitionRefusalCodes.DispatchDetailsRequired,
                        "This order is being delivered, so it needs a courier and a tracking number "
                        + "before it can be marked as shipped.",
                        snapshot);
                }

                string? courierName = null;

                if (dispatch is { } details)
                {
                    // Read inside the transaction, so a courier deactivated between the screen's read and
                    // this press cannot be written on to an order. The name is copied, which is doc 11
                    // P9's own instruction ("courier name is copied into the order") and what keeps a past
                    // dispatch note readable after a rename.
                    courierName = await dbContext.Couriers
                        .AsNoTracking()
                        .Where(courier => courier.Id == details.CourierId && courier.IsActive)
                        .Select(courier => courier.Name)
                        .FirstOrDefaultAsync(cancellationToken);

                    if (courierName is null)
                    {
                        return Refused(
                            OrderTransitionRefusalCodes.CourierUnavailable,
                            "That courier is no longer available. Reload the page and choose another.",
                            snapshot);
                    }
                }

                // Doc 08 §4.1 "Processing -> Shipped": guard Status = 2, side effect ShippedAtUtc. The
                // courier, its name and the tracking number are set in the *same* statement as the status,
                // so there is no instant at which an order is Shipped with no tracking number on it — a
                // second UPDATE afterwards would leave exactly that window open to the detail screen and
                // to the shopper's own order page.
                var claimed = await ClaimAsync(
                    transition,
                    OrderStatus.Processing,
                    OrderStatus.Shipped,
                    extraSet: $"""
                        , ShippedAtUtc = @nowUtc
                        , CourierId = @courierId
                        , CourierName = @courierName
                        , TrackingNumber = @trackingNumber
                        """,
                    cancellationToken,
                    dispatch?.CourierId,
                    courierName,
                    dispatch?.TrackingNumber);

                if (claimed != 1)
                {
                    return await DiagnoseAsync(
                        transition.OrderId,
                        OrderStatus.Processing,
                        snapshot.IsPickup,
                        cancellationToken);
                }

                await WriteHistoryAsync(
                    transition,
                    OrderStatus.Processing,
                    OrderStatus.Shipped,
                    Note(
                        snapshot.IsPickup
                            ? "Ready for collection"
                            : $"Shipped with {courierName}, tracking {dispatch!.TrackingNumber}",
                        transition.Note),
                    cancellationToken);

                EnlistAudit(
                    transition,
                    snapshot,
                    AuditActions.StatusChange,
                    snapshot.IsPickup ? "readyForCollection" : "shipped",
                    dispatch is null
                        ? null
                        : $$"""{"courierId":{{dispatch.CourierId}},"courierName":{{Json(courierName)}},"trackingNumber":{{Json(dispatch.TrackingNumber)}}}""");

                await dbContext.SaveChangesAsync(cancellationToken);

                return new OrderTransitionResult.Applied(snapshot.OrderNumber, OrderStatus.Shipped);
            },
            cancellationToken);
    }

    public Task<OrderTransitionResult> MarkDeliveredAsync(
        OrderTransition transition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transition);

        return InTransactionAsync(
            async () =>
            {
                if (await ReadOrderAsync(transition.OrderId, cancellationToken) is not { } snapshot)
                {
                    return new OrderTransitionResult.NotFound();
                }

                // Doc 08 §4.1 "Shipped -> Delivered", the staff path: guard Status = 3, side effect
                // DeliveredAtUtc. **There is no age condition and no pickup exclusion here**, and that is
                // the difference between this method and the job: §4.1 puts both of those on the *System*
                // path only. A staff member marking a pickup order collected is the documented way a
                // pickup order ever reaches Delivered (G-18), so excluding pickups here would make them
                // unclosable.
                var claimed = await ClaimAsync(
                    transition,
                    OrderStatus.Shipped,
                    OrderStatus.Delivered,
                    extraSet: ", DeliveredAtUtc = @nowUtc",
                    cancellationToken);

                if (claimed != 1)
                {
                    return await DiagnoseAsync(
                        transition.OrderId,
                        OrderStatus.Shipped,
                        snapshot.IsPickup,
                        cancellationToken);
                }

                await WriteHistoryAsync(
                    transition,
                    OrderStatus.Shipped,
                    OrderStatus.Delivered,
                    Note(snapshot.IsPickup ? "Collected by the customer" : "Delivered", transition.Note),
                    cancellationToken);

                EnlistAudit(
                    transition,
                    snapshot,
                    AuditActions.StatusChange,
                    snapshot.IsPickup ? "collected" : "delivered",
                    null);

                await dbContext.SaveChangesAsync(cancellationToken);

                return new OrderTransitionResult.Applied(snapshot.OrderNumber, OrderStatus.Delivered);
            },
            cancellationToken);
    }

    public Task<OrderTransitionResult> UpdateDispatchAsync(
        OrderTransition transition,
        OrderDispatchDetails dispatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(dispatch);

        return InTransactionAsync(
            async () =>
            {
                if (await ReadOrderAsync(transition.OrderId, cancellationToken) is not { } snapshot)
                {
                    return new OrderTransitionResult.NotFound();
                }

                if (snapshot.IsPickup)
                {
                    return Refused(
                        OrderTransitionRefusalCodes.DispatchDetailsNotAllowed,
                        "This is a store pickup order, so there is no courier or tracking number to "
                        + "correct. Use the internal note instead.",
                        snapshot);
                }

                var courierName = await dbContext.Couriers
                    .AsNoTracking()
                    .Where(courier => courier.Id == dispatch.CourierId && courier.IsActive)
                    .Select(courier => courier.Name)
                    .FirstOrDefaultAsync(cancellationToken);

                if (courierName is null)
                {
                    return Refused(
                        OrderTransitionRefusalCodes.CourierUnavailable,
                        "That courier is no longer available. Reload the page and choose another.",
                        snapshot);
                }

                // **The status is in the WHERE and not in the SET**, which is what makes this a correction
                // rather than a transition. Doc 11 P9 allows the courier and the tracking number to be
                // corrected on an order that has gone; Delivered is included because a wrong number is
                // usually noticed after the parcel arrives, and correcting the record then is still worth
                // doing. Anything earlier is refused: setting a tracking number on an order that has not
                // shipped would publish a link to the customer with no dispatch behind it.
                var updated = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE sales.Orders
                       SET CourierId = {dispatch.CourierId},
                           CourierName = {courierName},
                           TrackingNumber = {dispatch.TrackingNumber},
                           UpdatedAtUtc = {transition.NowUtc},
                           UpdatedByUserId = {transition.Actor.UserId}
                     WHERE Id = {transition.OrderId}
                       AND Status IN ({(byte)OrderStatus.Shipped}, {(byte)OrderStatus.Delivered});
                    """,
                    cancellationToken);

                if (updated != 1)
                {
                    return Refused(
                        OrderTransitionRefusalCodes.NotShipped,
                        "This order has not been dispatched, so there is no courier or tracking number to "
                        + "correct. Reload the page to see its current state.",
                        snapshot);
                }

                // Equal from and to statuses: "an event on this order that did not move it", the
                // convention OrderStatusHistoryEntry documents. The order's timeline is the only place a
                // correction is visible to the next person who reads it, and doc 11 P9 requires the
                // customer to be told by telephone — which the screen says and this note records.
                await WriteHistoryAsync(
                    transition,
                    snapshot.Status,
                    snapshot.Status,
                    Note(
                        $"Dispatch corrected to {courierName}, tracking {dispatch.TrackingNumber}. "
                        + "No new notice is sent to the customer",
                        transition.Note),
                    cancellationToken);

                EnlistAudit(
                    transition,
                    snapshot,
                    AuditActions.Update,
                    "dispatchCorrected",
                    $$"""{"courierId":{{dispatch.CourierId}},"courierName":{{Json(courierName)}},"trackingNumber":{{Json(dispatch.TrackingNumber)}}}""");

                await dbContext.SaveChangesAsync(cancellationToken);

                return new OrderTransitionResult.Applied(snapshot.OrderNumber, snapshot.Status);
            },
            cancellationToken);
    }

    public Task<OrderTransitionResult> SetInternalNoteAsync(
        OrderTransition transition,
        string? note,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transition);

        return InTransactionAsync(
            async () =>
            {
                if (await ReadOrderAsync(transition.OrderId, cancellationToken) is not { } snapshot)
                {
                    return new OrderTransitionResult.NotFound();
                }

                // **No status guard at all, and that is deliberate.** The internal note is the mechanism
                // doc 08 §4.1 gives for correcting a mistake on an order that can never move backwards, so
                // it has to be writable in every status — including Refunded and Expired, which are
                // precisely the ones somebody needs to explain. The *note* is not money and changes no
                // state.
                var updated = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE sales.Orders
                       SET InternalNote = {note},
                           UpdatedAtUtc = {transition.NowUtc},
                           UpdatedByUserId = {transition.Actor.UserId}
                     WHERE Id = {transition.OrderId};
                    """,
                    cancellationToken);

                if (updated != 1)
                {
                    // The order existed a statement ago, so this is the vanishingly rare case of it being
                    // deleted underneath us — which doc 08 §1.6 forbids. Reported rather than ignored.
                    return new OrderTransitionResult.NotFound();
                }

                await WriteHistoryAsync(
                    transition,
                    snapshot.Status,
                    snapshot.Status,
                    note is null ? "Internal note cleared" : Note("Internal note", note),
                    cancellationToken);

                // **The note's text is not in the audit row's AfterJson.** Doc 08 §3.8's redaction list is
                // about secrets, so this is a judgement rather than a rule: a staff member's internal note
                // can carry a customer's reason for a refund or a telephone conversation's content, and
                // ops.AuditLogs is kept for three years and exportable. That it changed, by whom and when
                // is the auditable fact; the order row holds the current text and the history row holds
                // what it was set to, both of which are readable by Orders.View.
                EnlistAudit(transition, snapshot, AuditActions.Update, "internalNote", null);

                await dbContext.SaveChangesAsync(cancellationToken);

                return new OrderTransitionResult.Applied(snapshot.OrderNumber, snapshot.Status);
            },
            cancellationToken);
    }

    /// <summary>
    /// Doc 08 §4.1's conditional UPDATE: move the order from <paramref name="from"/> to
    /// <paramref name="to"/> only if it is still in <paramref name="from"/>.
    /// </summary>
    /// <remarks>
    /// <b>The status values are interpolated as <c>byte</c>s, so EF sends them as parameters and not as
    /// literals.</b> That is the right choice here and the opposite of <c>BankTransferReader</c>'s
    /// deliberate literals: there is no filtered index on <c>Status</c> alone for the optimiser to have to
    /// prove a predicate against, so parameterisation costs nothing and buys plan reuse across the four
    /// transitions.
    /// <para>
    /// <paramref name="extraSet"/> is a fragment of <b>this file's own source</b> and never anything a
    /// caller supplies — every value it mentions arrives as one of the named parameters below. It is a
    /// string because <c>ExecuteSqlInterpolated</c> has no other way to vary a <c>SET</c> list, and the
    /// three parameters are declared unconditionally so that a fragment which does not use them is still
    /// valid SQL.
    /// </para>
    /// </remarks>
    private async Task<int> ClaimAsync(
        OrderTransition transition,
        OrderStatus from,
        OrderStatus to,
        string? extraSet,
        CancellationToken cancellationToken,
        int? courierId = null,
        string? courierName = null,
        string? trackingNumber = null)
    {
        var sql = $"""
            UPDATE sales.Orders
               SET Status = @to,
                   UpdatedAtUtc = @nowUtc,
                   UpdatedByUserId = @actorId
                   {extraSet}
             WHERE Id = @orderId
               AND Status = @from;
            """;

        return await dbContext.Database.ExecuteSqlRawAsync(
            sql,
            [
                new Microsoft.Data.SqlClient.SqlParameter("@to", (byte)to),
                new Microsoft.Data.SqlClient.SqlParameter("@from", (byte)from),
                new Microsoft.Data.SqlClient.SqlParameter("@orderId", transition.OrderId),
                new Microsoft.Data.SqlClient.SqlParameter("@nowUtc", transition.NowUtc),
                new Microsoft.Data.SqlClient.SqlParameter(
                    "@actorId",
                    transition.Actor.UserId ?? (object)DBNull.Value),
                new Microsoft.Data.SqlClient.SqlParameter("@courierId", courierId ?? (object)DBNull.Value),
                new Microsoft.Data.SqlClient.SqlParameter(
                    "@courierName",
                    courierName ?? (object)DBNull.Value),
                new Microsoft.Data.SqlClient.SqlParameter(
                    "@trackingNumber",
                    trackingNumber ?? (object)DBNull.Value),
            ],
            cancellationToken);
    }

    /// <summary>
    /// The order's state, read after a claim affected 0 rows, turned into a refusal that names where it
    /// actually is.
    /// </summary>
    /// <remarks>
    /// <b>This is <c>BankTransferReviewStore.DiagnoseAsync</c>'s lesson applied to fulfilment</b>, which is
    /// itself checkout's <c>RefuseUnlessAlreadyPlacedAsync</c> lesson: the claim's 0 rows says only "not in
    /// the state I needed", and a staff member told that learns nothing. So the row is read again and the
    /// refusal carries the status — which is what lets the screen redraw the action that <i>will</i> work
    /// instead of offering the one that just failed.
    /// <para>
    /// The read is inside the transaction and therefore before the rollback. Under <c>ReadCommitted</c> it
    /// sees the winner's committed state, which is exactly what is wanted: the question is where the order
    /// is now, not where it was when this attempt started.
    /// </para>
    /// </remarks>
    private async Task<OrderTransitionResult> DiagnoseAsync(
        int orderId,
        OrderStatus expected,
        bool isPickup,
        CancellationToken cancellationToken)
    {
        if (await ReadOrderAsync(orderId, cancellationToken) is not { } snapshot)
        {
            return new OrderTransitionResult.NotFound();
        }

        var label = OrderStatusLabels.For(snapshot.Status, isPickup);

        // Phrased from where the order is, not from where we wanted it. "Somebody has already started
        // processing this order" is actionable; "the order was not Paid" is a sentence about our own
        // predicate.
        var message = (expected, snapshot.Status) switch
        {
            (_, OrderStatus.Processing) when expected == OrderStatus.Paid =>
                "Somebody has already started processing this order.",

            (_, OrderStatus.Shipped) when expected == OrderStatus.Processing =>
                "This order has already been marked as shipped.",

            (_, OrderStatus.Delivered) when expected == OrderStatus.Shipped =>
                "This order is already recorded as delivered — by a colleague, or by the job that "
                + "closes orders nobody has confirmed.",

            (_, OrderStatus.Refunded) =>
                "This order has been refunded, so it cannot be moved on.",

            (_, OrderStatus.Expired or OrderStatus.Cancelled) =>
                $"This order is {label.ToLower(CultureInfo.InvariantCulture)} and its items have been "
                + "released, so it cannot be fulfilled.",

            _ => $"This order is {label.ToLower(CultureInfo.InvariantCulture)}, so that could not be "
                + "applied. Reload the page to see its current state.",
        };

        return new OrderTransitionResult.Refused(
            OrderTransitionRefusalCodes.WrongStatus,
            message,
            snapshot.Status,
            label);
    }

    private static OrderTransitionResult.Refused Refused(
        string code,
        string message,
        OrderSnapshot snapshot) =>
        new(code, message, snapshot.Status, OrderStatusLabels.For(snapshot.Status, snapshot.IsPickup));

    /// <summary>The order's number, status and delivery method, or <see langword="null"/> when there is none.</summary>
    private async Task<OrderSnapshot?> ReadOrderAsync(int orderId, CancellationToken cancellationToken) =>
        await dbContext.Orders
            .AsNoTracking()
            .Where(order => order.Id == orderId)
            .Select(order => new OrderSnapshot(
                order.OrderNumber,
                order.PublicId,
                order.Status,
                order.Delivery.DeliveryMethodCode == DeliveryMethod.PickupCode))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The order's history row (doc 08 §3.5), written through EF so it joins the same unit of work as the
    /// audit row.
    /// </summary>
    private async Task WriteHistoryAsync(
        OrderTransition transition,
        OrderStatus from,
        OrderStatus to,
        string note,
        CancellationToken cancellationToken)
    {
        dbContext.OrderStatusHistory.Add(new OrderStatusHistoryEntry
        {
            OrderId = transition.OrderId,
            FromStatus = from,
            ToStatus = to,

            // Doc 11 P9: source Staff. Not SystemJob, which is the auto-deliver job's, and not
            // PaymentProvider, which doc 08 §4 reserves for a signature-verified webhook.
            Source = OrderStatusChangeSource.Staff,
            ChangedByUserId = transition.Actor.UserId,
            Note = note,
            OccurredAtUtc = transition.NowUtc,
        });

        await Task.CompletedTask;
    }

    /// <summary>
    /// Doc 11 P9: "every transition writes status history and is audited".
    /// </summary>
    /// <remarks>
    /// Enlisted rather than written, so it commits with the transition it describes (issue #59): an audit
    /// trail of a dispatch that rolled back would be worse than none. Every statement above is raw SQL, so
    /// the <c>SaveChanges</c> interceptor never sees these changes and the row has to be written
    /// deliberately.
    /// </remarks>
    private void EnlistAudit(
        OrderTransition transition,
        OrderSnapshot snapshot,
        string action,
        string what,
        string? detailJson) =>
        auditLog.Enlist(new AuditLog
        {
            OccurredAtUtc = transition.NowUtc,
            UserId = transition.Actor.UserId,
            UserEmail = transition.Actor.Email,
            Action = action,
            EntityType = nameof(Order),

            // The public token, not the internal id: it is what the admin screen and the staff member's
            // own link both carry, so an audit row can be matched to an order without a second lookup.
            EntityId = snapshot.PublicId.ToString("D", CultureInfo.InvariantCulture),
            AfterJson = detailJson ?? $$"""{"transition":{{Json(what)}}}""",
            IpAddress = transition.Actor.IpAddress,
            UserAgent = transition.Actor.UserAgent,
            CorrelationId = transition.Actor.CorrelationId,
        });

    /// <summary>
    /// "Shipped with X" or "Shipped with X: <i>note</i>", clipped to the column's 1,000 characters.
    /// </summary>
    /// <remarks>
    /// Clipped rather than refused, and the prefix comes first so it is never the part that is lost: the
    /// note is validated at the use case, and a history row is not the place to discover that a sentence is
    /// two characters too long once a transaction is open.
    /// </remarks>
    private static string Note(string prefix, string? note)
    {
        var text = string.IsNullOrWhiteSpace(note) ? prefix : $"{prefix}: {note.Trim()}";

        return text.Length <= OrderStatusHistoryEntry.NoteMaxLength
            ? text
            : text[..OrderStatusHistoryEntry.NoteMaxLength];
    }

    /// <summary>A JSON string literal, or <c>null</c>, for the audit row's <c>AfterJson</c> (CK ISJSON).</summary>
    private static string Json(string? value) =>
        value is null ? "null" : System.Text.Json.JsonSerializer.Serialize(value);

    /// <summary>
    /// Runs <paramref name="act"/> in one <c>ReadCommitted</c> transaction, committing only an
    /// <see cref="OrderTransitionResult.Applied"/>.
    /// </summary>
    /// <remarks>
    /// <b>A refusal rolls back, which is what makes a refusal mean "nothing happened".</b> A claim that
    /// won and a later statement that refused would otherwise leave the order moved and the staff member
    /// told it was not — the shape <c>BankTransferReviewStore</c> guards against in the same way.
    /// <para>
    /// The change tracker is cleared first. These methods mix raw SQL with EF inserts, and a tracked entity
    /// left over from an earlier read in the same scope would be written back by <c>SaveChanges</c>
    /// alongside the history row — <c>OrderPlacementStore.DetachAll</c>'s own warning.
    /// </para>
    /// </remarks>
    private async Task<OrderTransitionResult> InTransactionAsync(
        Func<Task<OrderTransitionResult>> act,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();

        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            // Cleared again inside the retry: a retried attempt must not inherit the inserts the failed
            // one added, or the history row would be written twice.
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

            var result = await act();

            if (result is OrderTransitionResult.Applied)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return result;
        });
    }

    /// <summary>
    /// The order, as a read answers it. <b>A record class and deliberately not a <c>record struct</c>.</b>
    /// </summary>
    /// <remarks>
    /// <c>FirstOrDefaultAsync</c> over a projection to a value type cannot return null — it returns
    /// <c>default</c> — so <c>OrderSnapshot?</c> would come back with <c>HasValue = true</c> and every
    /// field zeroed whenever the query matched nothing, and <c>Status = PendingPayment</c> (0) with
    /// <c>IsPickup = false</c>. <c>BankTransferReviewStore</c> records what that cost there: a PayPal order
    /// was approved and paid by the bank flow.
    /// </remarks>
    private sealed record OrderSnapshot(
        string OrderNumber,
        Guid PublicId,
        OrderStatus Status,
        bool IsPickup);
}
