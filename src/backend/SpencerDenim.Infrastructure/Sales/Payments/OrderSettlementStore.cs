using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Ops.Alerts.Abstractions;
using SpencerDenim.Application.Sales.Payments;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Payments;

/// <summary>
/// Doc 08 §4.2 step 3 and step 3.4 — the transaction a provider's confirmed payment is applied in, whoever
/// carries the news: a verified webhook, a capture response, a recovery run (PAY-02, PAY-03).
/// <para>
/// <b>The claim is the lock, exactly as in <see cref="BankTransferReviewStore"/></b> — see that class's
/// remarks for why a conditional <c>UPDATE</c> and its row count, and not an isolation level or an
/// application lock, is the mechanism. What this adds is the half a staff decision does not have: a payment
/// that arrives for an order that can no longer take it is not an error to refuse but money to account
/// for, so the failure of the claim leads somewhere (the exception path) instead of back to the caller.
/// </para>
/// <para>
/// <b>Lock order is doc 08 §4.2 N6 and §5.3:</b> the order row, then the payment row, then the other
/// payments of the order, then the stock rows by ascending <c>VariantId</c>, then the number series. The
/// exception path for an amount mismatch touches the order row (to clear a capture lock) <i>before</i> the
/// payment row, which is the reverse of the order doc 08 lists the statements in, because the lock order
/// is the rule and the listing order is a description.
/// </para>
/// <para>
/// <b>The savepoint is doc 08 Appendix B4's</b>: <c>CreateSavepoint</c> before the settlement,
/// <c>RollbackToSavepoint</c> when the payment update then affects no row, no <c>XACT_ABORT</c>. It undoes
/// only the order claim, so the transaction goes on to the exception path rather than being abandoned and
/// retried forever by the execution strategy.
/// </para>
/// <para>
/// <b>What is not written.</b> No <c>ops.AuditLogs</c> row: nobody acted, and the history row (source
/// <c>PaymentProvider</c>) and the event log are the record of a System payment — the audit trail is for a
/// person's decisions. No staff email on the exception path, because no staff email exists in this build
/// (see <see cref="PaymentExceptionAlert"/>). Nothing here calls the provider.
/// </para>
/// </summary>
internal sealed class OrderSettlementStore(
    AppDbContext dbContext,
    OrderSettlementEffects effects,
    IAdminAlertWriter alerts) : IOrderSettlementStore
{
    /// <summary>The savepoint's name, as doc 08 §4.2 step 3.2 calls it.</summary>
    private const string SavepointName = "BeforeSettle";

    /// <summary>
    /// Doc 08 §4.2 step 3.1 (R1): every status a COMPLETED payment is accepted from — "0 Initiated, 1
    /// Pending, 2 Capturing, 4 Failed, 5 Cancelled, 6 Expired". Only 3, 7, 8 and 9 count as duplicates.
    /// </summary>
    private static readonly PaymentStatus[] Settleable =
    [
        PaymentStatus.Initiated,
        PaymentStatus.Pending,
        PaymentStatus.Capturing,
        PaymentStatus.Failed,
        PaymentStatus.Cancelled,
        PaymentStatus.Expired,
    ];

    public async Task<PaymentSettlementResult> SettleAsync(
        PaymentSettlement settlement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        RequireGatewayProvider(settlement.ProviderCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(settlement.ConfirmedCurrency);

        // Trimmed once, here: the column compares equal case-insensitively but not across leading space,
        // and a notification handler that passes " PayPal" should not be told the payment does not exist.
        var trimmed = settlement with { ProviderCode = settlement.ProviderCode.Trim() };

        return await SettlementTransaction.RunAsync(
            dbContext,
            () => SettleInTransactionAsync(trimmed, cancellationToken),
            IsCommitted,
            cancellationToken);
    }

    public async Task<PaymentSettlementResult> RouteToExceptionAsync(
        PaymentExceptionRouting routing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(routing);
        RequireGatewayProvider(routing.ProviderCode);

        if (!PaymentExceptionReasons.All.Contains(routing.Reason))
        {
            throw new ArgumentException(
                $"'{routing.Reason}' is not one of PaymentExceptionReasons.",
                nameof(routing));
        }

        var trimmed = routing with { ProviderCode = routing.ProviderCode.Trim() };

        return await SettlementTransaction.RunAsync(
            dbContext,
            () => RouteInTransactionAsync(trimmed, cancellationToken),
            IsCommitted,
            cancellationToken);
    }

    /// <summary>
    /// A bank transfer is confirmed by a person, not by a notification: the approver, the audit row and the
    /// <c>Orders.Approve</c> permission all belong to <see cref="BankTransferReviewStore"/>. Reaching this
    /// store with that provider's code is a programming error in a caller, so it is refused loudly rather
    /// than answered — an answer of "settled" would be a bank order paid with nobody's say-so.
    /// </summary>
    private static void RequireGatewayProvider(string providerCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerCode);

        if (string.Equals(providerCode.Trim(), PaymentProviderCodes.BankTransfer, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "A bank transfer is settled by staff through IBankTransferReviewStore, never by a "
                + "provider's notification.",
                nameof(providerCode));
        }
    }

    /// <summary>Kept: the order was paid, or the money was put on record. Everything else changed nothing.</summary>
    private static bool IsCommitted(PaymentSettlementResult result) =>
        result is PaymentSettlementResult.Settled or PaymentSettlementResult.CapturedRequiresRefund;

    private async Task<PaymentSettlementResult> SettleInTransactionAsync(
        PaymentSettlement settlement,
        CancellationToken cancellationToken)
    {
        var payment = await ReadPaymentAsync(settlement.PaymentId, settlement.ProviderCode, cancellationToken);

        if (payment is null)
        {
            return new PaymentSettlementResult.NotFound();
        }

        var order = await ReadOrderAsync(payment.OrderId, cancellationToken);

        if (order is null)
        {
            // Unreachable while Payments.OrderId is a foreign key, and answered rather than assumed.
            return new PaymentSettlementResult.NotFound();
        }

        // Doc 08 §4.2 step 6: a payment already 3, 7, 8 or 9 is a duplicate. Read before any statement, so a
        // redelivery is answered without taking a single lock.
        if (!Settleable.Contains(payment.Status))
        {
            return new PaymentSettlementResult.AlreadyHandled(payment.Status);
        }

        // Doc 08 §4.2 step 3.1: "Before any statement, check that the captured amount and currency equal
        // Payments.ProviderAmount / ProviderCurrency." A payment that was never converted (PayHere, in
        // LKR) has neither, and is held to its LKR Amount and Currency — the contract test pins that the
        // three conversion columns are all set or all null, so there is no half-converted payment to
        // compare against.
        if (!AmountMatches(payment, settlement))
        {
            return await RecordExceptionAsync(
                payment,
                order,
                PaymentExceptionReasons.AmountMismatch,
                settlement.ConfirmedAmount,
                settlement.ConfirmedCurrency,
                settlement.ProviderTransactionId,
                settlement.PayerReference,
                settlement.NowUtc,
                cancellationToken);
        }

        var transaction = dbContext.Database.CurrentTransaction
            ?? throw new InvalidOperationException("SettleAsync must run inside its transaction.");

        // Doc 08 §4.2 step 3.2 and Appendix B4.
        await transaction.CreateSavepointAsync(SavepointName, cancellationToken);

        var claimed = await effects.ClaimPendingOrderPaidAsync(
            payment.OrderId,
            payment.ProviderCode,
            settlement.NowUtc,
            updatedByUserId: null,
            cancellationToken);

        if (claimed != 1)
        {
            // Step 3.4, "reached when 3.2 affects 0 rows (order expired, cancelled or already settled)".
            // Nothing was written by the claim, so there is nothing to roll back to the savepoint.
            return await RecordExceptionAsync(
                payment,
                order,
                await DiagnoseAsync(payment, cancellationToken),
                settlement.ConfirmedAmount,
                settlement.ConfirmedCurrency,
                settlement.ProviderTransactionId,
                settlement.PayerReference,
                settlement.NowUtc,
                cancellationToken);
        }

        var settled = await SettlePaymentAsync(payment, settlement, cancellationToken);

        if (settled != 1)
        {
            // Step 3.3, "0 rows (a concurrent path changed the payment): ROLLBACK TRANSACTION BeforeSettle
            // undoes only the order update, then fall through to 3.4 (R1). The whole transaction is never
            // rolled back, so the retry loop can't repeat it forever."
            await transaction.RollbackToSavepointAsync(SavepointName, cancellationToken);

            return await RecordExceptionAsync(
                payment,
                order,
                PaymentExceptionReasons.OrderNotPayable,
                settlement.ConfirmedAmount,
                settlement.ConfirmedCurrency,
                settlement.ProviderTransactionId,
                settlement.PayerReference,
                settlement.NowUtc,
                cancellationToken);
        }

        // Doc 08 Appendix B1 — only ever closes something after a late success on an older attempt.
        await effects.CloseOtherOpenAttemptsAsync(
            payment.OrderId,
            payment.Id,
            settlement.NowUtc,
            cancellationToken);

        // Stock first, then the number series — the order every Paid path uses; see
        // BankTransferReviewStore.ApproveAsync for the deadlock this ordering exists to prevent.
        await effects.DeductStockAsync(payment.OrderId, settlement.NowUtc, cancellationToken);

        var invoiceNumber = await effects.IssueInvoiceAsync(
            payment.OrderId,
            settlement.NowUtc,
            cancellationToken);

        effects.RecordStatusChange(
            payment.OrderId,
            OrderStatus.PendingPayment,
            OrderStatus.Paid,

            // Doc 08 §4: PaymentProvider is the source "reserved for a signature-verified webhook" — a
            // System payment, with nobody to name.
            OrderStatusChangeSource.PaymentProvider,
            changedByUserId: null,
            Clip(settlement.Note, $"Payment confirmed by {payment.ProviderCode}"),
            settlement.NowUtc);

        // Doc 11 P8 step 6: "Payment confirmed email". Enlisted, so it commits with the invoice number it
        // quotes and rolls back with it.
        effects.EnlistPaymentConfirmed(order, invoiceNumber, settlement.NowUtc, wasLatePayment: false);

        await dbContext.SaveChangesAsync(cancellationToken);

        return new PaymentSettlementResult.Settled(order.OrderNumber, invoiceNumber);
    }

    private async Task<PaymentSettlementResult> RouteInTransactionAsync(
        PaymentExceptionRouting routing,
        CancellationToken cancellationToken)
    {
        var payment = await ReadPaymentAsync(routing.PaymentId, routing.ProviderCode, cancellationToken);

        if (payment is null)
        {
            return new PaymentSettlementResult.NotFound();
        }

        var order = await ReadOrderAsync(payment.OrderId, cancellationToken);

        if (order is null)
        {
            return new PaymentSettlementResult.NotFound();
        }

        if (!Settleable.Contains(payment.Status))
        {
            return new PaymentSettlementResult.AlreadyHandled(payment.Status);
        }

        return await RecordExceptionAsync(
            payment,
            order,
            routing.Reason,
            routing.ConfirmedAmount,
            routing.ConfirmedCurrency,
            routing.ProviderTransactionId,
            routing.PayerReference,
            routing.NowUtc,
            cancellationToken);
    }

    /// <summary>
    /// Doc 08 §4.2 step 3.3: <c>Succeeded</c>, flagged as the settling payment, with the provider's
    /// transaction id.
    /// </summary>
    /// <remarks>
    /// <b>The guard is the statuses a COMPLETED payment is accepted from</b> (R1), not the status this
    /// transaction read, because the payment may legitimately have moved between Initiated and Failed
    /// since, and either way the money is real. What it must not match is 3, 7, 8 or 9, and a row at one of
    /// those affects 0 rows here.
    /// <para>
    /// <c>IsOrderSettlement = 1</c> is allowed only because <c>Status</c> becomes 3 in the same statement
    /// (<c>CK_Payments_IsOrderSettlement</c>); <c>UX_Payments_OneSettlement</c> is the backstop behind the
    /// order claim's <c>NOT EXISTS</c>, and doc 08 §4.2 is explicit that the design never relies on
    /// catching its violation. <c>UX_Payments_Provider_Txn</c> is the same kind of backstop for the
    /// transaction id: the same money cannot settle two payments, and if it ever tried this statement
    /// would throw and the event would be retried, which is the loud outcome that belongs to it.
    /// </para>
    /// <para>
    /// The failure columns are cleared: a late success on a Failed payment would otherwise leave
    /// <c>INSTRUMENT_DECLINED</c> on a row that is now the settling one, and the staff screen reads that
    /// column to decide what may be done with a payment.
    /// </para>
    /// </remarks>
    private async Task<int> SettlePaymentAsync(
        PaymentRow payment,
        PaymentSettlement settlement,
        CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.Payments
               SET Status = {(byte)PaymentStatus.Succeeded},
                   IsOrderSettlement = 1,
                   ProviderTransactionId = COALESCE({settlement.ProviderTransactionId}, ProviderTransactionId),
                   PayerReference = COALESCE({settlement.PayerReference}, PayerReference),
                   FailureCode = NULL,
                   FailureMessage = NULL,
                   CompletedAtUtc = {settlement.NowUtc},
                   UpdatedAtUtc = {settlement.NowUtc}
             WHERE Id = {payment.Id}
               AND OrderId = {payment.OrderId}
               AND ProviderCode = {payment.ProviderCode}
               AND Status IN (
                     {(byte)PaymentStatus.Initiated}, {(byte)PaymentStatus.Pending},
                     {(byte)PaymentStatus.Capturing}, {(byte)PaymentStatus.Failed},
                     {(byte)PaymentStatus.Cancelled}, {(byte)PaymentStatus.Expired});
            """,
            cancellationToken);

    /// <summary>
    /// Doc 08 §4.2 step 3.4: the payment becomes <see cref="PaymentStatus.CapturedRequiresRefund"/>, the
    /// order is left as it is, and a <c>PaymentException</c> alert is written — in this transaction.
    /// </summary>
    /// <remarks>
    /// <b>Once per payment, by construction.</b> The <c>UPDATE</c> only matches a payment still at a status
    /// that has not settled or been routed, so the second delivery of the same notification — or a second
    /// call to <see cref="RouteToExceptionAsync(PaymentExceptionRouting, CancellationToken)"/> — affects
    /// 0 rows and the answer is <see cref="PaymentSettlementResult.AlreadyHandled"/>: "0 rows means the
    /// payment is already 3, 7, 8 or 9, so the event is marked Ignored". The alert is written only after the
    /// update moved a row, so there is never a banner for a payment somebody else already routed.
    /// <para>
    /// <b>An amount mismatch (R9) also clears the order's capture lock</b>, and does it first: the order
    /// row precedes the payment row in the lock order. "A still-payable order stays PendingPayment, and no
    /// stock is deducted" — the customer can pay again, so the lock a capture claim set must not hold the
    /// order hostage for three hours because the amount was wrong. The statement is pinned to the
    /// payment's provider and does nothing to an order that holds no lock.
    /// </para>
    /// <para>
    /// <c>FailureCode</c> is written only for <c>AMOUNT_MISMATCH</c>, because that is the one code doc 08
    /// §4.2 step 7 makes load-bearing (an exception payment carrying it can only be refunded). Every
    /// reason is recorded in <c>FailureMessage</c> and in the alert's details.
    /// </para>
    /// </remarks>
    private async Task<PaymentSettlementResult> RecordExceptionAsync(
        PaymentRow payment,
        OrderSnapshot order,
        string reason,
        decimal? confirmedAmount,
        string? confirmedCurrency,
        string? providerTransactionId,
        string? payerReference,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var isMismatch = reason == PaymentExceptionReasons.AmountMismatch;

        if (isMismatch)
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE sales.Orders
                   SET CaptureLockedUntilUtc = NULL,
                       UpdatedAtUtc = {nowUtc}
                 WHERE Id = {payment.OrderId}
                   AND PaymentProviderCode = {payment.ProviderCode}
                   AND CaptureLockedUntilUtc IS NOT NULL;
                """,
                cancellationToken);
        }

        var failureCode = isMismatch ? PaymentFailureCodes.AmountMismatch : null;
        var failureMessage = Describe(reason);

        var routed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.Payments
               SET Status = {(byte)PaymentStatus.CapturedRequiresRefund},
                   IsOrderSettlement = 0,
                   ProviderTransactionId = COALESCE({providerTransactionId}, ProviderTransactionId),
                   PayerReference = COALESCE({payerReference}, PayerReference),
                   FailureCode = {failureCode},
                   FailureMessage = {failureMessage},
                   CompletedAtUtc = {nowUtc},
                   UpdatedAtUtc = {nowUtc}
             WHERE Id = {payment.Id}
               AND OrderId = {payment.OrderId}
               AND ProviderCode = {payment.ProviderCode}
               AND Status IN (
                     {(byte)PaymentStatus.Initiated}, {(byte)PaymentStatus.Pending},
                     {(byte)PaymentStatus.Capturing}, {(byte)PaymentStatus.Failed},
                     {(byte)PaymentStatus.Cancelled}, {(byte)PaymentStatus.Expired});
            """,
            cancellationToken);

        if (routed != 1)
        {
            var current = await dbContext.Payments
                .AsNoTracking()
                .Where(candidate => candidate.Id == payment.Id)
                .Select(candidate => candidate.Status)
                .SingleAsync(cancellationToken);

            return new PaymentSettlementResult.AlreadyHandled(current);
        }

        alerts.Enlist(PaymentExceptionAlert.For(
            new PaymentExceptionFacts(
                payment.Id,
                payment.ProviderCode,
                order.PublicId,
                order.OrderNumber,
                reason,
                payment.ProviderAmount ?? payment.Amount,
                payment.ProviderCurrency ?? payment.Currency,
                confirmedAmount,
                confirmedCurrency),
            nowUtc));

        await dbContext.SaveChangesAsync(cancellationToken);

        return new PaymentSettlementResult.CapturedRequiresRefund(reason, order.OrderNumber);
    }

    /// <summary>
    /// The order's state, read again after the claim affected 0 rows, turned into the reason the money has
    /// nowhere to go.
    /// </summary>
    /// <remarks>
    /// <b>Read inside the transaction, after the claim.</b> Under <c>ReadCommitted</c> it sees the
    /// winner's committed state, which is the question: what is the order <i>now</i>, not what it was when
    /// the notification arrived. The same lesson as <c>BankTransferReviewStore.DiagnoseAsync</c>, applied
    /// to a different answer.
    /// </remarks>
    private async Task<string> DiagnoseAsync(PaymentRow payment, CancellationToken cancellationToken)
    {
        var status = await dbContext.Orders
            .AsNoTracking()
            .Where(order => order.Id == payment.OrderId)
            .Select(order => (OrderStatus?)order.Status)
            .FirstOrDefaultAsync(cancellationToken);

        return status switch
        {
            OrderStatus.Expired => PaymentExceptionReasons.OrderExpired,
            OrderStatus.Cancelled => PaymentExceptionReasons.OrderCancelled,

            // Paid, or past it: the order took money before this notification arrived. A refunded order was
            // paid once, and this is a second payment for it.
            OrderStatus.Paid or OrderStatus.Processing or OrderStatus.Shipped or OrderStatus.Delivered
                or OrderStatus.Refunded => PaymentExceptionReasons.OrderAlreadyPaid,

            _ => PaymentExceptionReasons.OrderNotPayable,
        };
    }

    private static bool AmountMatches(PaymentRow payment, PaymentSettlement settlement) =>
        settlement.ConfirmedAmount == (payment.ProviderAmount ?? payment.Amount)
        && string.Equals(
            settlement.ConfirmedCurrency.Trim(),
            payment.ProviderCurrency ?? payment.Currency,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The payment, if it is this provider's. A payment of another provider answers the same as one that
    /// does not exist, so the call cannot be used to ask which payments exist.
    /// </summary>
    private async Task<PaymentRow?> ReadPaymentAsync(
        int paymentId,
        string providerCode,
        CancellationToken cancellationToken) =>
        await dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.Id == paymentId
                && payment.ProviderCode == providerCode)
            .Select(payment => new PaymentRow(
                payment.Id,
                payment.OrderId,
                payment.ProviderCode,
                payment.Status,
                payment.Amount,
                payment.Currency,
                payment.ProviderAmount,
                payment.ProviderCurrency))
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<OrderSnapshot?> ReadOrderAsync(int orderId, CancellationToken cancellationToken) =>
        await dbContext.Orders
            .AsNoTracking()
            .Where(order => order.Id == orderId)
            .Select(OrderSnapshot.FromOrder)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The reason as a sentence for <c>Payments.FailureMessage</c> (<c>nvarchar(500)</c>).</summary>
    private static string Describe(string reason) =>
        reason switch
        {
            PaymentExceptionReasons.OrderExpired => "Payment received after the order had expired.",
            PaymentExceptionReasons.OrderCancelled => "Payment received after the order had been cancelled.",
            PaymentExceptionReasons.OrderAlreadyPaid => "Payment received for an order that was already paid.",
            PaymentExceptionReasons.AmountMismatch =>
                "The amount or currency received did not match what was requested.",
            _ => "Payment received for an order that could not take payment.",
        };

    /// <summary>The caller's note, or the default, clipped to the history column.</summary>
    private static string Clip(string? note, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(note) ? fallback : note.Trim();

        return text.Length <= OrderStatusHistoryEntry.NoteMaxLength
            ? text
            : text[..OrderStatusHistoryEntry.NoteMaxLength];
    }

    /// <summary>One payment row, as far as settlement needs. A class: see <see cref="OrderSnapshot"/> for why.</summary>
    private sealed record PaymentRow(
        int Id,
        int OrderId,
        string ProviderCode,
        PaymentStatus Status,
        decimal Amount,
        string Currency,
        decimal? ProviderAmount,
        string? ProviderCurrency);
}
