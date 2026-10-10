using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Payments;

/// <summary>
/// Doc 11 P7 steps 6a, 6b and 8, each as one transaction (doc 08 §4.1, §4.3, §5.5, §5.7).
/// <para>
/// <b>The claim is the lock.</b> Every path that changes an order starts with one conditional
/// <c>UPDATE</c> on <c>sales.Orders</c> whose <c>WHERE</c> carries the states that transition is allowed
/// from. Doc 08 §5.2's argument applies to it exactly as it does to the stock reservation: SQL Server
/// takes an update lock on the qualifying row, a second writer waits for it, and then <b>re-evaluates the
/// predicate against the row as it now is</b>. So two staff members approving at once do not both see
/// "PendingPayment": the second waits, finds <c>Status = 1</c>, affects 0 rows and is told the order has
/// already been paid. The same statement is what makes approve-versus-expiry a coin toss with two correct
/// outcomes rather than a race with a wrong one.
/// </para>
/// <para>
/// <b>There is no <c>sp_getapplock</c> here and no <c>Serializable</c>, deliberately.</b> Doc 08 names an
/// application lock in two places — the promo per-customer limit (§3.6) and the last-Super-Admin guard
/// (§3.1 DB4 rule 4, <c>sp_getapplock 'rbac:grants-all'</c>) — and neither is on this path. (It used to
/// say "the reference-data seeder", which holds no applock anywhere; corrected by W3-04b's tester.)
/// §4.1 and §5.5 specify conditional <c>UPDATE</c>s and row counts, and §5.2 explains at length why
/// that is stronger here than an isolation level. An applock on the order
/// would add a second, weaker gate beside the one the database already enforces, and <c>Serializable</c>
/// would turn the lost race from "0 rows, here is why" into a deadlock to retry. Every transaction below
/// is <c>ReadCommitted</c>, the same as checkout's.
/// </para>
/// <para>
/// <b>Why the payment row is resolved in here.</b> A rejection closes a row and a resubmission opens a new
/// one (doc 08 §3.5 N7), so a payment id that was correct when a screen rendered can name a rejected row
/// by the time Approve is pressed. The caller passes an order and this store asks, under that order's own
/// lock, which payment is currently reviewable.
/// </para>
/// <para>
/// <b>The Paid effects are not in this file.</b> Stock reserved to sold, the gapless invoice number, the
/// history row and the <c>PaymentConfirmed</c> email are <see cref="OrderSettlementEffects"/>, which this
/// store and <c>OrderSettlementStore</c> (the provider-facing settlement) both call; its remarks
/// carry what is deliberately not written there (no <c>InventoryMovements</c> row, no promo restore, no
/// seller snapshot). What stays here is the bank flow's own: the staff actor, the review of a
/// <i>transfer</i>, the refusals worded for an approver, and the audit row.
/// </para>
/// <para>
/// <b>What is not written, and why it is not quietly skipped.</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>No email on a rejection, and that is doc 11's instruction rather than an omission.</b> P7's
/// notification list says "No email on rejection" and §7 G-14 leaves the question with the client. So
/// <c>ApproveAsync</c> and <c>AcceptLateAsync</c> queue <c>PaymentConfirmed</c> (W3-06) and
/// <c>RejectAsync</c> queues nothing — the order is still PendingPayment with time left on it, and the
/// customer finds out by looking at their order or by trying again, which is what P7 step 6b describes.
/// </description></item>
/// </list>
/// </summary>
internal sealed class BankTransferReviewStore(
    AppDbContext dbContext,
    OrderSettlementEffects effects,
    IAuditLogWriter auditLog) : IBankTransferReviewStore
{
    public async Task<BankTransferDecisionResult> ApproveAsync(
        BankTransferDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return await InTransactionAsync(
            async () =>
            {
                var order = await ReadOrderAsync(decision.OrderId, cancellationToken);
                if (order is not { } snapshot)
                {
                    return new BankTransferDecisionResult.NotFound();
                }

                // Doc 08 §4.1 "PendingPayment -> Paid": Status = 0 AND StockState = 0, plus §4.2 step
                // 3.2's NOT EXISTS so a second settlement is impossible even if the payment update below
                // were wrong, and the bank-transfer pin. The statement — and the comment on why the
                // provider code and the literal 1 are in it — is OrderSettlementEffects's, because the
                // provider-facing settlement claims the order with exactly the same one.
                var claimed = await effects.ClaimPendingOrderPaidAsync(
                    decision.OrderId,
                    PaymentProviderCodes.BankTransfer,
                    decision.NowUtc,
                    decision.Actor.UserId,
                    cancellationToken);

                if (claimed != 1)
                {
                    return await DiagnoseAsync(decision.OrderId, Expecting.Pending, cancellationToken);
                }

                var payment = await FindReviewablePaymentAsync(
                    decision.OrderId,
                    [PaymentStatus.Pending],
                    cancellationToken);

                if (payment is not { } open)
                {
                    return NoReviewablePayment(snapshot);
                }

                var settled = await SettleAsync(open.Id, open.Status, decision, cancellationToken);
                if (settled != 1)
                {
                    // Something changed the payment between the read above and this statement — a second
                    // staff member rejecting it is the realistic one. The order claim is undone with the
                    // rollback, so the order is still PendingPayment when the screen reloads.
                    return await DiagnoseAsync(decision.OrderId, Expecting.Pending, cancellationToken);
                }

                // **Stock first, then the number series — the same order the late acceptance uses.** The
                // first version allocated the invoice number here and deducted afterwards, so the two
                // paths took `sales.NumberSeries` and `inventory.StockLevels` in opposite orders: two
                // decisions in flight on a shared variant could each hold the row the other wanted, which
                // is a 1205 that `EnableRetryOnFailure` would absorb silently rather than a deadlock
                // anybody would see. Doc 08 §5.3 fixes an order precisely so this cannot arise, and the
                // series belongs at the end of it.
                //
                // It is also the safer end: allocating last means a statement that throws between the two
                // consumes no invoice number, which is what keeps the series gapless.
                await effects.DeductStockAsync(decision.OrderId, decision.NowUtc, cancellationToken);

                var invoiceNumber = await effects.IssueInvoiceAsync(
                    decision.OrderId,
                    decision.NowUtc,
                    cancellationToken);

                RecordStaffStatusChange(
                    decision,
                    OrderStatus.PendingPayment,
                    OrderStatus.Paid,
                    Note("Bank transfer approved", decision.Note));

                EnlistAudit(
                    decision,
                    snapshot,
                    AuditActions.Approve,
                    $$"""{"decision":"approve","payment":{{open.Id}},"invoiceNumber":{{Json(invoiceNumber)}}}""");

                // Doc 11 P7 step 6a: "**Payment confirmed** email queued". Enlisted into this unit of
                // work, so it commits with the approval — and with the invoice number that
                // `IssueInvoiceAsync` allocated above, inside this same transaction, which would
                // otherwise be a figure the email claimed and the database had rolled back.
                //
                // (This named a distance — "four statements ago" — where the journal's own narrative said
                // three and the count is three. A distance in a comment is wrong the first time anything
                // is inserted between the two lines, so it names the call instead. W3-06's tester.)
                effects.EnlistPaymentConfirmed(snapshot, invoiceNumber, decision.NowUtc, wasLatePayment: false);

                await dbContext.SaveChangesAsync(cancellationToken);

                return new BankTransferDecisionResult.Applied(snapshot.OrderNumber, invoiceNumber);
            },
            cancellationToken);
    }

    public async Task<BankTransferDecisionResult> RejectAsync(
        BankTransferDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return await InTransactionAsync(
            async () =>
            {
                var order = await ReadOrderAsync(decision.OrderId, cancellationToken);
                if (order is not { } snapshot)
                {
                    return new BankTransferDecisionResult.NotFound();
                }

                // **The order row is claimed even though a rejection does not move the order**, and this
                // is doc 08 §5.3's instruction rather than caution: "paths that start from an existing
                // order claim the order row first". A rejection starts from one.
                //
                // **What the claim buys, stated accurately — an earlier version of this comment overstated
                // it and a tester was right to take it apart.** It said a losing rejection would set the
                // settling payment to Failed and violate `CK_Payments_IsOrderSettlement`, giving a 500.
                // It would not: that constraint is `IsOrderSettlement = 0 OR Status IN (3, 8, 9)`, the
                // rejection's UPDATE never touches the flag, and it matches only a row still at Pending —
                // a row at Pending already has the flag clear, or it would be violating the constraint
                // where it sits. The statement's own `AND Status = Pending` is the protection, and it is
                // sufficient for correctness: the loser simply updates nothing.
                //
                // What the claim adds is smaller and still worth having. It is doc 08 §5.3's instruction
                // — "paths that start from an existing order claim the order row first", and a rejection
                // starts from one — so all three decisions take the same locks in the same order, which
                // is how 1205s are avoided rather than retried. And it makes the loser *wait* for the
                // winner's commit instead of racing it, so the refusal it reports is diagnosed from
                // settled state: "this order has already been paid" rather than an answer derived from a
                // read that was already stale. A 0-row update with a stale diagnosis is not a wrong write,
                // but it is a wrong sentence on a screen about somebody's money.
                //
                // The only thing it writes is the stamp — the status and the stock state are deliberately
                // untouched (doc 11 P7 step 6b: "the order stays PendingPayment until its due time"), and
                // the order genuinely did change, because its payment history did.
                var claimed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE sales.Orders
                       SET UpdatedAtUtc = {decision.NowUtc},
                           UpdatedByUserId = {decision.Actor.UserId}
                     WHERE Id = {decision.OrderId}
                       AND PaymentProviderCode = {PaymentProviderCodes.BankTransfer}
                       AND Status = {(byte)OrderStatus.PendingPayment}
                       AND StockState = {(byte)OrderStockState.Reserved};
                    """,
                    cancellationToken);

                if (claimed != 1)
                {
                    return await DiagnoseAsync(decision.OrderId, Expecting.Pending, cancellationToken);
                }

                var payment = await FindReviewablePaymentAsync(
                    decision.OrderId,
                    [PaymentStatus.Pending],
                    cancellationToken);

                if (payment is not { } open)
                {
                    return NoReviewablePayment(snapshot);
                }

                var rejected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE sales.Payments
                       SET Status = {(byte)PaymentStatus.Failed},
                           FailureCode = {BankTransferFailureCodes.Rejected},
                           FailureMessage = {decision.Note},
                           ApprovedByUserId = {decision.Actor.UserId},
                           ApprovedAtUtc = {decision.NowUtc},
                           ApprovalNote = {decision.Note},
                           CompletedAtUtc = {decision.NowUtc},
                           UpdatedAtUtc = {decision.NowUtc}
                     WHERE Id = {open.Id}
                       AND OrderId = {decision.OrderId}
                       AND ProviderCode = {PaymentProviderCodes.BankTransfer}
                       AND Status = {(byte)PaymentStatus.Pending};
                    """,
                    cancellationToken);

                if (rejected != 1)
                {
                    return await DiagnoseAsync(decision.OrderId, Expecting.Pending, cancellationToken);
                }

                // **FromStatus and ToStatus are both PendingPayment, and that is the honest encoding.**
                // Doc 11 P7 requires a rejection to appear in the order's history, and the order did not
                // move. Null would mean "came from nowhere", which is what placement's first row uses it
                // for; two equal values read as "stayed where it was", and the note says why.
                RecordStaffStatusChange(
                    decision,
                    OrderStatus.PendingPayment,
                    OrderStatus.PendingPayment,
                    Note("Bank transfer rejected", decision.Note));

                EnlistAudit(
                    decision,
                    snapshot,
                    AuditActions.StatusChange,
                    $$"""{"decision":"reject","payment":{{open.Id}}}""");

                await dbContext.SaveChangesAsync(cancellationToken);

                return new BankTransferDecisionResult.Applied(snapshot.OrderNumber, null);
            },
            cancellationToken);
    }

    public async Task<BankTransferDecisionResult> AcceptLateAsync(
        BankTransferDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return await InTransactionAsync(
            async () =>
            {
                var order = await ReadOrderAsync(decision.OrderId, cancellationToken);
                if (order is not { } snapshot)
                {
                    return new BankTransferDecisionResult.NotFound();
                }

                // Doc 08 §4.1 "Expired / Cancelled -> Paid" (DB8): Status IN (5,6) AND StockState = 2,
                // with the same NOT EXISTS settlement guard and the same literal 1.
                var claimed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE sales.Orders
                       SET Status = {(byte)OrderStatus.Paid},
                           StockState = {(byte)OrderStockState.Committed},
                           PaidAtUtc = {decision.NowUtc},
                           ClosedAtUtc = NULL,
                           UpdatedAtUtc = {decision.NowUtc},
                           UpdatedByUserId = {decision.Actor.UserId}
                     WHERE Id = {decision.OrderId}
                       AND PaymentProviderCode = {PaymentProviderCodes.BankTransfer}
                       AND Status IN ({(byte)OrderStatus.Cancelled}, {(byte)OrderStatus.Expired})
                       AND StockState = {(byte)OrderStockState.Released}
                       AND NOT EXISTS (
                             SELECT 1 FROM sales.Payments s
                              WHERE s.OrderId = {decision.OrderId} AND s.IsOrderSettlement = 1);
                    """,
                    cancellationToken);

                if (claimed != 1)
                {
                    return await DiagnoseAsync(decision.OrderId, Expecting.Closed, cancellationToken);
                }

                // Doc 08 §4.3 N7: the Expired row itself settles, or a Pending row the customer created by
                // resubmitting after a rejection. Latest first, because a resubmission is the later row and
                // is the one the money arrived against.
                //
                // **`Cancelled` is in the list because the claim above accepts a cancelled order.** Doc 08
                // §4.1 specifies `Cancelled → Paid` and doc 11 P10 is where it comes from: a customer
                // cancels, their transfer arrives anyway, and staff choose to honour it. Without this
                // status the claim would succeed, no payment would be found, and the whole transaction
                // would roll back with `payment_not_reviewable` — a specified path answering "there is
                // nothing to review" about an order with a payment row sitting right there. Nothing writes
                // a `Cancelled` payment yet (customer cancel is W3-04), so this is latent today and would
                // have been silently wrong the day it landed.
                var payment = await FindReviewablePaymentAsync(
                    decision.OrderId,
                    [PaymentStatus.Pending, PaymentStatus.Expired, PaymentStatus.Cancelled],
                    cancellationToken);

                if (payment is not { } latest)
                {
                    return NoReviewablePayment(snapshot);
                }

                var settled = await SettleAsync(latest.Id, latest.Status, decision, cancellationToken);

                if (settled != 1)
                {
                    return await DiagnoseAsync(decision.OrderId, Expecting.Closed, cancellationToken);
                }

                // Doc 08 §5.7: re-acquire the stock with a conditional deduct per line. Reserved is *not*
                // touched — the reservation was given back when the order expired or was cancelled — so
                // this takes from OnHand against what is available now, competing with live shoppers.
                if (await effects.ReacquireStockAsync(decision.OrderId, decision.NowUtc, cancellationToken)
                    is { } shortLines)
                {
                    // The status travels with the refusal: it is the only thing the screen is told, and
                    // without it the screen cannot know the order is closed. See ShortStock's own remarks.
                    return new BankTransferDecisionResult.ShortStock(shortLines, snapshot.Status);
                }

                var invoiceNumber = await effects.IssueInvoiceAsync(
                    decision.OrderId,
                    decision.NowUtc,
                    cancellationToken);

                RecordStaffStatusChange(
                    decision,
                    snapshot.Status,
                    OrderStatus.Paid,
                    Note("Late payment accepted", decision.Note));

                EnlistAudit(
                    decision,
                    snapshot,
                    AuditActions.Approve,
                    $$"""{"decision":"acceptLate","payment":{{latest.Id}},"invoiceNumber":{{Json(invoiceNumber)}}}""");

                // Doc 11 P7 step 8 and P10 step 11: a late acceptance has "the same effects as step 6a",
                // and §4.1 lists `PaymentConfirmed` for "approval, PayPal capture, late acceptance". The
                // message says so in different words, because the customer's last message from us said
                // the order had expired — see `PaymentConfirmedEmailFacts.WasLatePayment`.
                effects.EnlistPaymentConfirmed(snapshot, invoiceNumber, decision.NowUtc, wasLatePayment: true);

                await dbContext.SaveChangesAsync(cancellationToken);

                return new BankTransferDecisionResult.Applied(snapshot.OrderNumber, invoiceNumber);
            },
            cancellationToken);
    }

    /// <summary>
    /// One <c>ReadCommitted</c> transaction inside EF's execution strategy, rolled back unless the body
    /// produced an <see cref="BankTransferDecisionResult.Applied"/>.
    /// </summary>
    /// <remarks>
    /// <b>Rolling back on every non-<c>Applied</c> answer is the point.</b> A refusal discovered half way
    /// through — the payment changed under us, a line is short — has usually already issued statements, and
    /// the only safe thing is for none of them to have happened. Writing that once here rather than at
    /// every <c>return</c> is what stops the next person adding a branch that forgets.
    /// <para>
    /// <see cref="DbContext.ChangeTracker"/> is cleared on every exit, successful or not, for the two
    /// reasons <c>OrderPlacementStore</c> records: a retry by the execution strategy must not re-send the
    /// rolled-back inserts, and the request's later reads must come from the database rather than from a
    /// tracker holding what we meant to write.
    /// </para>
    /// <para>
    /// The transaction itself is <see cref="SettlementTransaction"/>, shared with the provider-facing
    /// settlement; what is this flow's own is the rule for when to keep it.
    /// </para>
    /// </remarks>
    private Task<BankTransferDecisionResult> InTransactionAsync(
        Func<Task<BankTransferDecisionResult>> body,
        CancellationToken cancellationToken) =>
        SettlementTransaction.RunAsync(
            dbContext,
            body,
            result => result is BankTransferDecisionResult.Applied,
            cancellationToken);

    /// <summary>
    /// Doc 08 §4.1's Paid effects on the payment row: <c>Succeeded</c>, flagged as the settlement, with
    /// the approver, the time and the note.
    /// </summary>
    /// <remarks>
    /// <b>The guard is the status this very transaction just read, not a list of acceptable ones.</b>
    /// <paramref name="expected"/> comes from <see cref="FindReviewablePaymentAsync"/> a few statements
    /// earlier, so the condition reads "update this row only if it is still exactly what I saw" — which is
    /// optimistic concurrency on the payment, stated once and correct for all three decisions without
    /// either of them widening what the others allow. It is also what keeps this statement a parameterised
    /// <c>ExecuteSqlInterpolated</c>: a variable-length <c>IN</c> list would have to be concatenated into
    /// the SQL text, which is dynamic SQL in a file that handles money.
    /// <para>
    /// <c>IsOrderSettlement = 1</c> is allowed only because <c>Status</c> becomes 3 in the same statement:
    /// <c>CK_Payments_IsOrderSettlement</c> permits the flag for statuses 3, 8 and 9 only, so setting it on
    /// a row that stayed <c>Pending</c> could not commit at all. <c>UX_Payments_OneSettlement</c> is the
    /// backstop behind the order claim's <c>NOT EXISTS</c>, and doc 08 §4.2 is explicit that the design
    /// never relies on catching its violation.
    /// </para>
    /// </remarks>
    private async Task<int> SettleAsync(
        int paymentId,
        PaymentStatus expected,
        BankTransferDecision decision,
        CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.Payments
               SET Status = {(byte)PaymentStatus.Succeeded},
                   IsOrderSettlement = 1,
                   ApprovedByUserId = {decision.Actor.UserId},
                   ApprovedAtUtc = {decision.NowUtc},
                   ApprovalNote = {decision.Note},
                   CompletedAtUtc = {decision.NowUtc},
                   UpdatedAtUtc = {decision.NowUtc}
             WHERE Id = {paymentId}
               AND OrderId = {decision.OrderId}
               AND ProviderCode = {PaymentProviderCodes.BankTransfer}
               AND Status = {(byte)expected};
            """,
            cancellationToken);

    /// <summary>The order's number and current state, or <see langword="null"/> when it is not a bank order.</summary>
    /// <remarks>
    /// <b>Filtered to bank transfers here rather than at the endpoint</b>, so there is one place that
    /// decides what this flow may touch. A PayPal order aimed at Approve answers
    /// <see cref="BankTransferDecisionResult.NotFound"/> — the same answer as a token that names nothing —
    /// because a different one would let a caller enumerate which orders exist.
    /// </remarks>
    private async Task<OrderSnapshot?> ReadOrderAsync(int orderId, CancellationToken cancellationToken) =>
        await dbContext.Orders
            .AsNoTracking()
            .Where(order =>
                order.Id == orderId
                && order.PaymentProviderCode == PaymentProviderCodes.BankTransfer)
            .Select(OrderSnapshot.FromOrder)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The bank payment this decision acts on: the newest row in one of <paramref name="statuses"/>.
    /// </summary>
    /// <remarks>
    /// Newest first because doc 08 §4.3 N7 makes the newest the live one — a rejection closes a row and a
    /// resubmission opens another, so an order can hold several bank rows and only the last of them is
    /// about money that may still arrive. For an approval <paramref name="statuses"/> is
    /// <c>Pending</c> alone, and <c>UX_Payments_OneOpen</c> then guarantees there is at most one, so the
    /// ordering is only load-bearing for a late acceptance.
    /// </remarks>
    private async Task<PaymentRow?> FindReviewablePaymentAsync(
        int orderId,
        PaymentStatus[] statuses,
        CancellationToken cancellationToken) =>
        await dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.OrderId == orderId
                && payment.ProviderCode == PaymentProviderCodes.BankTransfer
                && statuses.Contains(payment.Status))
            .OrderByDescending(payment => payment.Id)
            .Select(payment => new PaymentRow(payment.Id, payment.Status))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The order's current state, read after a claim affected 0 rows, turned into the refusal doc 11 P7
    /// asks for.
    /// </summary>
    /// <remarks>
    /// <b>This is checkout's <c>RefuseUnlessAlreadyPlacedAsync</c> lesson applied to a different
    /// question.</b> That method re-asks the index before answering a refusal, because the first answer
    /// described the database rather than the request. Here the claim's 0 rows says only "not in the state
    /// I needed", and a staff member told that learns nothing: whether to press Accept late payment instead
    /// depends entirely on <i>which</i> state it is in. So the row is read again and the refusal names it.
    /// <para>
    /// The read is inside the transaction and therefore <i>before</i> the rollback. Under
    /// <c>ReadCommitted</c> it sees the winner's committed state, which is exactly what is wanted — the
    /// question is what the order is now, not what it was when we started.
    /// </para>
    /// </remarks>
    private async Task<BankTransferDecisionResult> DiagnoseAsync(
        int orderId,
        Expecting expecting,
        CancellationToken cancellationToken)
    {
        var order = await ReadOrderAsync(orderId, cancellationToken);

        if (order is not { } snapshot)
        {
            return new BankTransferDecisionResult.NotFound();
        }

        var payment = await dbContext.Payments
            .AsNoTracking()
            .Where(candidate =>
                candidate.OrderId == orderId
                && candidate.ProviderCode == PaymentProviderCodes.BankTransfer)
            .OrderByDescending(candidate => candidate.Id)
            .Select(candidate => (PaymentStatus?)candidate.Status)
            .FirstOrDefaultAsync(cancellationToken);

        var (code, message) = (snapshot.Status, expecting) switch
        {
            (OrderStatus.Paid, _) => (
                BankTransferRefusalCodes.AlreadyPaid,
                "This order has already been paid. Somebody else approved it, or the payment was confirmed "
                + "another way."),

            (OrderStatus.Expired, Expecting.Pending) => (
                BankTransferRefusalCodes.OrderExpired,
                "This order expired before the transfer was approved, so its items have been released. If "
                + "the money has arrived, use Accept late payment and the stock will be re-checked."),

            (OrderStatus.Cancelled, Expecting.Pending) => (
                BankTransferRefusalCodes.OrderNotPending,
                "This order has been cancelled, so a transfer can no longer be approved against it. If the "
                + "money has arrived, use Accept late payment and the stock will be re-checked."),

            (_, Expecting.Closed) when snapshot.Status is not (OrderStatus.Expired or OrderStatus.Cancelled)
                => (
                    BankTransferRefusalCodes.OrderNotClosed,
                    "This order is not expired or cancelled, so there is no late payment to accept. It is "
                    + "still awaiting payment, so Approve is the action."),

            _ => (
                BankTransferRefusalCodes.OrderNotPending,
                "This order is no longer awaiting a bank transfer, so nothing was changed. Reload the page "
                + "to see its current state."),
        };

        // A settlement that exists while the order is not Paid is the one shape worth naming separately:
        // it means the NOT EXISTS guard, not the status guard, is what refused us.
        if (payment is PaymentStatus.Succeeded && snapshot.Status != OrderStatus.Paid)
        {
            (code, message) = (
                BankTransferRefusalCodes.AlreadySettled,
                "A payment already settles this order, so a second one cannot. Reload the page.");
        }

        return new BankTransferDecisionResult.Refused(
            code,
            message,
            snapshot.Status,
            snapshot.StockState,
            payment);
    }

    private static BankTransferDecisionResult.Refused NoReviewablePayment(OrderSnapshot snapshot) =>
        new(
            BankTransferRefusalCodes.PaymentNotReviewable,
            "There is no bank transfer awaiting review on this order. It may already have been approved or "
            + "rejected. Reload the page to see its current state.",
            snapshot.Status,
            snapshot.StockState,
            null);

    /// <summary>
    /// The order's history row for a staff decision (doc 08 §3.5), added through EF so it joins the same
    /// unit of work as the audit row.
    /// </summary>
    private void RecordStaffStatusChange(
        BankTransferDecision decision,
        OrderStatus from,
        OrderStatus to,
        string note) =>
        effects.RecordStatusChange(
            decision.OrderId,
            from,
            to,

            // Doc 11 P7 step 6: source Staff. Not SystemJob, which is the expiry job's, and not
            // PaymentProvider, which doc 08 §4 reserves for a signature-verified webhook.
            OrderStatusChangeSource.Staff,
            decision.Actor.UserId,
            note,
            decision.NowUtc);

    /// <summary>
    /// Doc 11 P7: "every approve and reject is audited". <c>sales.Payments</c> is <c>[RV]</c> and not
    /// <c>[A]</c> (doc 08 §3.5), so the <c>SaveChanges</c> interceptor never sees these changes — every
    /// statement above is raw SQL — and the row has to be written deliberately.
    /// </summary>
    /// <remarks>
    /// Enlisted rather than written, so it commits with the decision it describes (issue #59): an audit
    /// trail of a rejection that rolled back would be worse than none.
    /// </remarks>
    private void EnlistAudit(
        BankTransferDecision decision,
        OrderSnapshot snapshot,
        string action,
        string afterJson) =>
        auditLog.Enlist(new AuditLog
        {
            OccurredAtUtc = decision.NowUtc,
            UserId = decision.Actor.UserId,
            UserEmail = decision.Actor.Email,
            Action = action,
            EntityType = nameof(Order),

            // The public token, not the internal id: it is what the admin screen and the staff member's
            // own link both carry, so an audit row can be matched to an order without a second lookup.
            EntityId = snapshot.PublicId.ToString("D", CultureInfo.InvariantCulture),
            AfterJson = afterJson,
            IpAddress = decision.Actor.IpAddress,
            UserAgent = decision.Actor.UserAgent,
            CorrelationId = decision.Actor.CorrelationId,
        });

    /// <summary>
    /// "Bank transfer approved" or "Bank transfer approved: <i>note</i>", clipped to the column's 1,000
    /// characters.
    /// </summary>
    /// <remarks>
    /// Clipped rather than refused: the note itself is validated at the use case, and a history row is not
    /// the place to discover that a message is two characters too long for the column once a transaction is
    /// open. The prefix is never lost, because it comes first.
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

    /// <summary>Which states a claim was expecting, so a refusal can be worded for the right action.</summary>
    private enum Expecting
    {
        /// <summary>Approve and reject: the order should be <c>PendingPayment</c> with stock reserved.</summary>
        Pending,

        /// <summary>Accept late: the order should be <c>Expired</c> or <c>Cancelled</c> with stock released.</summary>
        Closed,
    }

    /// <summary>One payment row. A class, for the same reason as <see cref="OrderSnapshot"/>.</summary>
    /// <remarks>
    /// As a struct, "no reviewable payment" came back as <c>Id = 0, Status = Initiated</c>, so the refusal
    /// path below was skipped and <see cref="SettleAsync"/> ran a statement against payment 0 — which
    /// affects no rows, so the outcome was still a refusal, but the <i>wrong</i> one: the staff member was
    /// told the order's state had changed rather than that there was no transfer to review.
    /// </remarks>
    private sealed record PaymentRow(int Id, PaymentStatus Status);
}

/// <summary>
/// <c>sales.Payments.FailureCode</c> values this flow writes (doc 08 §3.5 lists <c>REJECTED</c> first).
/// </summary>
public static class BankTransferFailureCodes
{
    /// <summary>Staff reviewed the transfer and refused it (doc 11 P7 step 6b).</summary>
    public const string Rejected = "REJECTED";
}
