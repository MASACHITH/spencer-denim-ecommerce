using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Application.Ops.Email.Contracts;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Application.Sales.Orders.Contracts;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Orders;

/// <summary>
/// Doc 11 P10 flow A, step 3, as one transaction: order <b>→ Cancelled</b> with the reason and the closed
/// time; the open payment <b>→ Cancelled</b>; stock <b>released</b>; promo use returned; history with
/// source Customer or Staff.
/// <para>
/// <b>The claim is the lock.</b> One conditional <c>UPDATE</c> on <c>sales.Orders</c> carrying all four of
/// doc 11 P10 step 2's guards, and the row count is the concurrency control (doc 08 §4.1, §5.2). No
/// <c>sp_getapplock</c>, no <c>Serializable</c> and no rowversion — the two places doc 08 names an
/// application lock are the promo per-customer limit (§3.6) and the last-Super-Admin guard (§3.1 DB4
/// rule 4, <c>sp_getapplock 'rbac:grants-all'</c>), and neither is here. (§4.2 names none at all, and
/// there is no reference-data-seeder applock: this sentence said both, having been copied from
/// <c>IOrderFulfilmentStore</c> where it was already wrong.)
/// The transaction is <c>ReadCommitted</c>, the same as expiry's, the bank review's and fulfilment's.
/// </para>
/// <para>
/// <b>Racing the expiry sweep is the case this shape exists for.</b> A customer presses Cancel in the
/// second the job takes their order. Both run the same claim against the same row; SQL Server takes an
/// update lock, the loser waits, re-evaluates <c>Status = 0 AND StockState = 0</c> against the row as the
/// winner left it, affects 0 rows and stops — so the order closes once, with one reason, and the stock is
/// released exactly once. <c>OrderCancellationTests.Cancel_racing_the_expiry_job_…</c> runs it.
/// </para>
/// <para>
/// <b>The release is <see cref="OrderStockRelease"/>, which is the expiry job's own loop.</b> Doc 08 §4.1
/// gives this transition the expiry row's side effects verbatim ("As Expired, reason Cancelled"), so the
/// lock order <c>sales.Orders</c> → <c>sales.Payments</c> → <c>inventory.StockLevels</c> → promo (§5.3,
/// §5.6) is the same code rather than the same intention written twice.
/// </para>
/// <para>
/// <b>Nothing here refunds, and nothing here can.</b> Doc 11 P10's table says a paid order is never
/// cancelled — it is refunded — and the claim's <c>Status = 0</c> makes that structural: a Paid,
/// Processing, Shipped or Delivered order affects 0 rows and is refused. Refunds are not in this build
/// (owner decision, 2026-10-08: done by hand as a bank transfer and recorded in the order's internal
/// note), and this file is not the place they arrive.
/// </para>
/// <para>
/// <b>It now queues an <c>Order cancelled</c> email, and that is a scope increase rather than a gap being
/// filled.</b> Doc 11 §4.4 lists "order cancelled" among the events with <i>no</i> notification in v1 and
/// doc 08 §3.8's template list has no such code; doc 11 §7 <b>G-14</b> recommends adding one and leaves
/// the decision to the client. It is built on the owner's instruction for W3-06 — a shopper who cancels
/// an order and hears nothing at all is the gap it closes — and it is one template, one composer method
/// and the one call below, so removing it is a small change if the client says no. The full note is on
/// <c>EmailTemplateCodes.OrderCancelled</c>.
/// </para>
/// </summary>
internal sealed class OrderCancellationStore(
    AppDbContext dbContext,
    IAuditLogWriter auditLog,
    IOrderEmailOutbox emails)
    : IOrderCancellationStore
{
    public Task<CancelOrderOutcome> CancelAsync(
        OrderCancellation cancellation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cancellation);

        dbContext.ChangeTracker.Clear();

        var strategy = dbContext.Database.CreateExecutionStrategy();

        return strategy.ExecuteAsync(async () =>
        {
            // Cleared again inside the retry: a retried attempt must not inherit the inserts a failed one
            // added, or the history row would be written twice. OrderFulfilmentStore's own rule.
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

            try
            {
                var outcome = await ApplyAsync(cancellation, cancellationToken);

                if (outcome is CancelOrderOutcome.Applied)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                else
                {
                    // A refusal rolls back, which is what makes a refusal mean "nothing happened".
                    await transaction.RollbackAsync(cancellationToken);
                }

                return outcome;
            }
            finally
            {
                dbContext.ChangeTracker.Clear();
            }
        });
    }

    private async Task<CancelOrderOutcome> ApplyAsync(
        OrderCancellation cancellation,
        CancellationToken cancellationToken)
    {
        if (await ReadOrderAsync(cancellation.OrderPublicId, cancellationToken) is not { } snapshot)
        {
            return new CancelOrderOutcome.NotFound();
        }

        // **Doc 11 P10 step 2's PayPal branch, decided before the claim because it is not a state the
        // claim can express.** §4.1 R1: when a PayPal payment is Pending, customers and `Orders.Edit`
        // holders are both blocked, and only `Orders.Approve` may continue after an explicit warning. That
        // Approve branch is **not built** — it is W4-01's, and a warning about a refund is worth nothing
        // while there is no refund — so both of this build's callers are refused here.
        //
        // Unreachable today: nothing captures, and `PaymentProviders` seeds PayPal disabled (doc 08 §9),
        // so no order can hold a PayPal payment outside a fixture. The guard goes in now because it is the
        // half that protects money: without it, a shopper could cancel an order whose capture is about to
        // land and the shop would be holding a payment for a closed order.
        //
        // **And the capture-lock clause in the claim below does not cover this case**, which is why this
        // is a separate check rather than redundant with it: doc 08 §4.2 step 4 says a PENDING capture
        // *clears* the lock and extends `PaymentDueAtUtc` instead, so a PayPal PENDING order has no lock
        // for the claim to refuse.
        //
        // **It is a read-then-check and the claim is not**, which is the one place this file departs from
        // §5.2's rule, so it is said rather than left to be found. A PayPal payment appearing between this
        // read and the claim would not be refused. That cannot happen in this build — nothing creates
        // one — and the fix when W4-01 does is to add the clause to the claim as a `NOT EXISTS` over
        // `ProviderCode = 'PayPal' AND Status = 1`, plus a branch in `DiagnoseAsync` so the refusal still
        // names the right cause. Doing that now would be three places for a state nothing can produce.
        if (snapshot.HasPayPalPendingPayment)
        {
            return Refused(
                CancelOrderRefusalCodes.PayPalPending,
                "A PayPal payment on this order is still being confirmed, so it cannot be cancelled here. "
                + "Please get in touch and we will sort it out.",
                snapshot);
        }

        // Doc 11 P10 step 2 in full, and doc 08 §4.1's `PendingPayment → Cancelled` row: PendingPayment,
        // stock Reserved, no active capture lock, no settling payment.
        //
        // **All four are inside the statement and none is read-then-checked.** Doc 08 §5.2's argument is
        // the whole reason: a predicate evaluated under the row's own update lock cannot be overtaken
        // between the read and the write, and 0 rows is then a fact rather than a guess. The snapshot
        // above is for the *message*, never for the decision — which is `DiagnoseAsync`'s rule in
        // `OrderFulfilmentStore` and `BankTransferReviewStore` before it.
        //
        // **Measured, so the next reader does not have to guess: `Status` and `StockState` are mutually
        // redundant here and neither can be mutation-proved alone.** Deleting either one leaves
        // `OrderCancellationTests` at 15/15; deleting both turns 4 of the 15 red. That is because no
        // reachable state has one without the other — every path that moves `Status` out of
        // PendingPayment moves `StockState` in the same statement — so each is a sufficient guard by
        // itself. They both stay because doc 08 §4.1's table names both, and because the day a path
        // writes one without the other is exactly the day the surviving guard is the one that matters.
        // What is *not* claimed is that a test would catch their removal one at a time.
        //
        // The settlement guard is doc 11 P10 step 2's and doc 08 §4.1's own ("no settlement payment"),
        // and **no path in this build can reach the state it guards** — which is said plainly rather than
        // dressed up, because the first version of this comment claimed §4.2 step 3.3's savepoint
        // fall-through could produce it and that is false: that path ends with the payment in status 7
        // CapturedRequiresRefund, which is not a settlement. Checked against the schema:
        // `CK_Payments_IsOrderSettlement` allows the flag only on a payment in Succeeded,
        // PartiallyRefunded or Refunded, and every path that puts a payment in one of those moves the
        // order in the same transaction. So the reachable shape is a *Succeeded settlement beside a
        // PendingPayment order*, which nothing writes today.
        //
        // It stays for two reasons. It costs one `NOT EXISTS` on an indexed column, and it is the guard
        // that would matter the moment a provider settled a payment in a transaction separate from the
        // order's status — which is exactly what a webhook does. `OrderCancellationTests` reaches the
        // state with a fixture, so the guard is proved rather than assumed.
        var claimed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.Orders
               SET Status = {(byte)OrderStatus.Cancelled},
                   StockState = {(byte)OrderStockState.Released},
                   CancelReason = {cancellation.Reason},
                   ClosedAtUtc = {cancellation.NowUtc},
                   UpdatedAtUtc = {cancellation.NowUtc},
                   UpdatedByUserId = {cancellation.Actor?.UserId}
             WHERE Id = {snapshot.OrderId}
               AND Status = {(byte)OrderStatus.PendingPayment}
               AND StockState = {(byte)OrderStockState.Reserved}
               AND (CaptureLockedUntilUtc IS NULL OR CaptureLockedUntilUtc < {cancellation.NowUtc})
               AND NOT EXISTS (
                     SELECT 1
                       FROM sales.Payments AS settlement
                      WHERE settlement.OrderId = {snapshot.OrderId}
                        AND settlement.IsOrderSettlement = 1);
            """,
            cancellationToken);

        if (claimed != 1)
        {
            return await DiagnoseAsync(cancellation, cancellationToken);
        }

        // Doc 08 §4.3: "Cancelling an unpaid order closes its open payment (Pending → Cancelled or
        // Initiated → Cancelled)."
        //
        // **Set-based and plural, which is the expiry store's own choice and for its reason.**
        // `UX_Payments_OneOpen` allows at most one open row today, but the statement is written for the
        // rule rather than for the index, so a future provider that opens two cannot leave one behind. The
        // row leaves that index's filter, which is what lets a later Accept late payment settle it (doc 08
        // §4.3 N7) — the Cancelled → Paid edge #124's screen already reaches.
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.Payments
               SET Status = {(byte)PaymentStatus.Cancelled},
                   UpdatedAtUtc = {cancellation.NowUtc},
                   CompletedAtUtc = {cancellation.NowUtc}
             WHERE OrderId = {snapshot.OrderId}
               AND Status IN ({(byte)PaymentStatus.Initiated}, {(byte)PaymentStatus.Pending});
            """,
            cancellationToken);

        // Doc 08 §5.3's lock order: the order row first (above), then payments, then the stock rows, then
        // the promo. The same loop the expiry job runs, not a second one.
        await OrderStockRelease.ReleaseAsync(
            dbContext,
            snapshot.OrderId,
            cancellation.NowUtc,
            "cancellation",
            cancellationToken);

        // Doc 08 §5.6 step 4 and doc 11 line 500: the use goes back, in this transaction, so a cancelled
        // order's code can be used again. The same statement the expiry job runs, not a second one.
        await OrderPromoUse.ReturnAsync(dbContext, snapshot.OrderId, cancellationToken);

        WriteHistory(cancellation, snapshot);

        // Enlisted into this unit of work, so the message and the cancellation commit together or not at
        // all (doc 08 §3.8's transactional outbox). It is after the claim, so a refused cancel — an order
        // already paid, already cancelled, or holding a capture lock — returns above and queues nothing.
        //
        // **The address and the name come from the snapshot read before the claim, and that is sound
        // here.** Doc 11 P9 is explicit that after placement only the courier, the tracking number and
        // the internal note are editable, so an order's email address and name cannot have changed
        // between that read and this statement. Nothing *else* on the snapshot is used for a decision —
        // `DiagnoseAsync` re-reads for that, which is this file's own rule.
        emails.EnlistOrderCancelled(new CancelledOrderEmailFacts(
            new OrderEmailHeader(
                snapshot.OrderNumber,
                snapshot.PublicId,
                new OrderEmailAddressee(snapshot.Email, snapshot.FirstName, snapshot.LastName),
                cancellation.NowUtc),
            cancellation.CancelledBy == OrderCancelledBy.Customer));

        if (cancellation.CancelledBy == OrderCancelledBy.Staff && cancellation.Actor is { } actor)
        {
            EnlistAudit(cancellation, actor, snapshot);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return new CancelOrderOutcome.Applied(snapshot.OrderNumber);
    }

    /// <summary>
    /// Doc 11 P10 step 3's history row, which is the <b>only</b> record of who closed the order.
    /// </summary>
    /// <remarks>
    /// <b><see cref="OrderStatusChangeSource"/> comes from the caller and is not a constant.</b> Doc 11
    /// P10's "Expired vs Cancelled" rule turns on it — Expired is nobody, Cancelled is a person — and the
    /// two people it can be answer different questions for staff later: a customer who changed their mind
    /// is not the same event as a colleague closing an order after a telephone call. The customer row
    /// carries no user id, because there is no customer sign-in to have one (ACC-01 is W3-01) and the
    /// order's token is the whole credential.
    /// </remarks>
    private void WriteHistory(OrderCancellation cancellation, OrderSnapshot snapshot)
    {
        var who = cancellation.CancelledBy == OrderCancelledBy.Customer
            ? "Cancelled by the customer from their order link, so the items were released."
            : "Cancelled by the shop, so the items were released.";

        var note = cancellation.Reason is { Length: > 0 } reason
            ? $"{who} Reason: {reason}"
            : who;

        dbContext.OrderStatusHistory.Add(new OrderStatusHistoryEntry
        {
            OrderId = snapshot.OrderId,
            FromStatus = OrderStatus.PendingPayment,
            ToStatus = OrderStatus.Cancelled,
            Source = cancellation.CancelledBy == OrderCancelledBy.Customer
                ? OrderStatusChangeSource.Customer
                : OrderStatusChangeSource.Staff,
            ChangedByUserId = cancellation.Actor?.UserId,

            // Clipped, never refused, for OrderStatusHistoryEntry.NoteMaxLength's own reason: a
            // transition that threw on its way to the database because a sentence was two characters too
            // long would roll back a decision for a reason nobody would guess. The reason itself is
            // length-checked at the use case, where a staff member can be told.
            Note = note.Length <= OrderStatusHistoryEntry.NoteMaxLength
                ? note
                : note[..OrderStatusHistoryEntry.NoteMaxLength],
            OccurredAtUtc = cancellation.NowUtc,
        });
    }

    /// <summary>
    /// The <c>ops.AuditLogs</c> row, for a staff cancellation only.
    /// </summary>
    /// <remarks>
    /// Enlisted rather than written, so it commits with the decision it describes (issue #59): an audit
    /// trail of a cancellation that rolled back would be worse than none. Every statement above is raw
    /// SQL, so the <c>SaveChanges</c> interceptor never sees these changes and the row has to be written
    /// deliberately.
    /// <para>
    /// <b>No audit row for a customer, and that is not an omission.</b> <c>ops.AuditLogs</c> records what
    /// staff did (doc 08 §3.8) and every row has a user and an email on it; a row with both null reads as
    /// a system act, which a customer cancel is the opposite of. The customer's act is recorded where it
    /// belongs — the order's own history, source <c>Customer</c> — which is what doc 11 P10 step 3 asks
    /// for and what the admin timeline shows.
    /// </para>
    /// <para>
    /// <b>The reason is in <c>AfterJson</c>.</b> It is a staff member's statement about why an order was
    /// closed, not a secret and not a customer's words, and it is already readable on the order by
    /// <c>Orders.View</c> — unlike the internal note, which <c>OrderFulfilmentStore</c> deliberately keeps
    /// out of the audit row because it can carry the content of a telephone call.
    /// </para>
    /// </remarks>
    private void EnlistAudit(OrderCancellation cancellation, AdminActor actor, OrderSnapshot snapshot) =>
        auditLog.Enlist(new AuditLog
        {
            OccurredAtUtc = cancellation.NowUtc,
            UserId = actor.UserId,
            UserEmail = actor.Email,
            Action = AuditActions.StatusChange,
            EntityType = nameof(Order),
            EntityId = snapshot.PublicId.ToString("D", CultureInfo.InvariantCulture),
            AfterJson = $$"""{"transition":"cancelled","reason":{{Json(cancellation.Reason)}}}""",
            IpAddress = actor.IpAddress,
            UserAgent = actor.UserAgent,
            CorrelationId = actor.CorrelationId,
        });

    /// <summary>
    /// The order's state, re-read after a claim affected 0 rows, turned into a refusal that names where it
    /// actually is.
    /// </summary>
    /// <remarks>
    /// <b><c>BankTransferReviewStore.DiagnoseAsync</c>'s lesson.</b> The claim's 0 rows says only "not in
    /// the state I needed"; a staff member or a shopper told that learns nothing. The read is inside the
    /// transaction and therefore before the rollback, so under <c>ReadCommitted</c> it sees the winner's
    /// committed state — which is exactly the question: where is the order <i>now</i>.
    /// <para>
    /// <b>Four guards, three codes, and the order of the branches is the order of the causes.</b> The
    /// capture lock is checked before the status, because an order that is still <c>PendingPayment</c>
    /// with a live lock is a different thing to tell somebody from one that has moved. A settlement beside
    /// a <c>PendingPayment</c> order gets its own code for the same reason: "the money is in" and "the
    /// order expired" lead to different next actions.
    /// </para>
    /// </remarks>
    private async Task<CancelOrderOutcome> DiagnoseAsync(
        OrderCancellation cancellation,
        CancellationToken cancellationToken)
    {
        if (await ReadOrderAsync(cancellation.OrderPublicId, cancellationToken) is not { } snapshot)
        {
            return new CancelOrderOutcome.NotFound();
        }

        if (snapshot.Status == OrderStatus.PendingPayment
            && snapshot.CaptureLockedUntilUtc is { } lockedUntil
            && lockedUntil >= cancellation.NowUtc)
        {
            return Refused(
                CancelOrderRefusalCodes.CaptureInProgress,
                "A payment on this order is being processed, so it cannot be cancelled yet. Try again in "
                + "a few minutes.",
                snapshot);
        }

        if (snapshot.Status == OrderStatus.PendingPayment && snapshot.HasSettlementPayment)
        {
            return Refused(
                CancelOrderRefusalCodes.PaymentSettling,
                "A payment for this order has already gone through, so it cannot be cancelled.",
                snapshot);
        }

        var label = OrderStatusLabels.For(snapshot.Status, snapshot.IsPickup);

        // Phrased from where the order is, not from where we wanted it — and for the closed statuses it
        // says that cancel has already happened, which is what somebody who pressed twice needs to hear.
        var message = snapshot.Status switch
        {
            OrderStatus.Cancelled => "This order has already been cancelled and its items are back on "
                + "sale.",

            OrderStatus.Expired => "The payment deadline for this order has passed, so it closed on its "
                + "own and its items are back on sale.",

            // Doc 11 P10's table: a paid order is never cancelled, it is refunded. Refunds are not in this
            // build, so the sentence says who to speak to rather than offering an action that does not
            // exist.
            OrderStatus.Paid or OrderStatus.Processing or OrderStatus.Shipped or OrderStatus.Delivered =>
                "This order has been paid, so it cannot be cancelled. A paid order is refunded instead — "
                + $"it is {label.ToLower(CultureInfo.InvariantCulture)}, so get in touch and we will "
                + "arrange it.",

            OrderStatus.Refunded => "This order has been refunded, so there is nothing to cancel.",

            _ => $"This order is {label.ToLower(CultureInfo.InvariantCulture)}, so it cannot be cancelled. "
                + "Reload the page to see its current state.",
        };

        return new CancelOrderOutcome.Refused(
            CancelOrderRefusalCodes.WrongStatus,
            message,
            snapshot.Status,
            label);
    }

    private static CancelOrderOutcome.Refused Refused(string code, string message, OrderSnapshot snapshot) =>
        new(code, message, snapshot.Status, OrderStatusLabels.For(snapshot.Status, snapshot.IsPickup));

    /// <summary>
    /// The order behind one token, or <see langword="null"/>. A seek on <c>UX_Orders_PublicId</c>.
    /// </summary>
    private async Task<OrderSnapshot?> ReadOrderAsync(
        Guid orderPublicId,
        CancellationToken cancellationToken) =>
        await dbContext.Orders
            .AsNoTracking()
            .Where(order => order.PublicId == orderPublicId)
            .Select(order => new OrderSnapshot(
                order.Id,
                order.OrderNumber,
                order.PublicId,
                order.Status,
                order.Delivery.DeliveryMethodCode == DeliveryMethod.PickupCode,
                order.CaptureLockedUntilUtc,
                order.Payments.Any(payment => payment.IsOrderSettlement),
                order.Payments.Any(payment =>
                    payment.ProviderCode == PaymentProviderCodes.PayPal
                    && payment.Status == PaymentStatus.Pending),

                // For the email only (W3-06). Three more columns on a projection that already reads this
                // row, rather than a second query inside the transaction.
                order.Email,
                order.FirstName,
                order.LastName))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>A JSON string literal, or <c>null</c>, for the audit row's <c>AfterJson</c> (CK ISJSON).</summary>
    private static string Json(string? value) =>
        value is null ? "null" : System.Text.Json.JsonSerializer.Serialize(value);

    /// <summary>
    /// The order, as a read answers it. <b>A record class and deliberately not a <c>record struct</c>.</b>
    /// </summary>
    /// <remarks>
    /// <c>FirstOrDefaultAsync</c> over a projection to a value type cannot return null — it returns
    /// <c>default</c> — so <c>OrderSnapshot?</c> would come back with <c>HasValue = true</c> and every
    /// field zeroed whenever the query matched nothing, and <c>Status = PendingPayment</c> (0) with no
    /// payments. <c>BankTransferReviewStore</c> records what that cost there: a PayPal order was approved
    /// and paid by the bank flow. Here it would be a cancel reported as applied against an order that does
    /// not exist.
    /// </remarks>
    private sealed record OrderSnapshot(
        int OrderId,
        string OrderNumber,
        Guid PublicId,
        OrderStatus Status,
        bool IsPickup,
        DateTime? CaptureLockedUntilUtc,
        bool HasSettlementPayment,
        bool HasPayPalPendingPayment,
        string Email,
        string FirstName,
        string? LastName);
}
