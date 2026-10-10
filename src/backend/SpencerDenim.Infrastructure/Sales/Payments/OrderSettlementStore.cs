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
        ArgumentException.ThrowIfNullOrWhiteSpace(settlement.ConfirmedCurrency);

        // Trimmed once, here: a notification handler that passes " PayPal" should not be told the payment
        // does not exist. Nothing else about the spelling is forgiven; see ResolvePaymentAsync.
        var trimmed = settlement with { ProviderCode = RequireGatewayProvider(settlement.ProviderCode) };

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

        if (!PaymentExceptionReasons.All.Contains(routing.Reason))
        {
            throw new ArgumentException(
                $"'{routing.Reason}' is not one of PaymentExceptionReasons.",
                nameof(routing));
        }

        var trimmed = routing with { ProviderCode = RequireGatewayProvider(routing.ProviderCode) };

        return await SettlementTransaction.RunAsync(
            dbContext,
            () => RouteInTransactionAsync(trimmed, cancellationToken),
            IsCommitted,
            cancellationToken);
    }

    /// <summary>
    /// The caller's provider code, trimmed — refused if it is the bank transfer's. The first of two guards;
    /// <see cref="ResolvePaymentAsync"/> is the one that cannot be spelled around.
    /// </summary>
    private static string RequireGatewayProvider(string providerCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerCode);

        var trimmed = providerCode.Trim();

        if (IsBankTransfer(trimmed))
        {
            throw BankTransferIsNotSettledHere(nameof(providerCode));
        }

        return trimmed;
    }

    private static bool IsBankTransfer(string providerCode) =>
        string.Equals(providerCode.Trim(), PaymentProviderCodes.BankTransfer, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A bank transfer is confirmed by a person, not by a notification: the approver, the audit row and the
    /// <c>Orders.Approve</c> permission all belong to <see cref="BankTransferReviewStore"/>. Reaching this
    /// store for one is a programming error in a caller, so it is refused loudly rather than answered — an
    /// answer of "settled" would be a bank order paid with nobody's say-so.
    /// </summary>
    private static ArgumentException BankTransferIsNotSettledHere(string parameterName) =>
        new(
            "A bank transfer is settled by staff through IBankTransferReviewStore, never by a "
            + "provider's notification.",
            parameterName);

    /// <summary>
    /// The payment, decided on **the stored row's provider code** and not on how the caller spelled it.
    /// </summary>
    /// <remarks>
    /// <b>The first version looked the payment up with <c>ProviderCode = @code</c> and so let the database
    /// compare the caller's text.</b> The column is <c>varchar</c> in a <c>Latin1_General_100_CI_AS_SC</c>
    /// database — case-insensitive and <i>width</i>-insensitive — so a full-width "ＢａｎｋＴｒａｎｓｆｅｒ"
    /// found the bank payment, passed a guard that compared the same text in C# and found it different, and
    /// paid a bank order with no approver. Reading the row by its id and deciding in C# on what is
    /// <i>stored</i> closes that for any spelling the collation might ever fold:
    /// <list type="number">
    /// <item><description>
    /// A row whose code is the bank transfer's is refused with an <see cref="ArgumentException"/>,
    /// whatever the caller said — the same refusal as passing the bank code itself.
    /// </description></item>
    /// <item><description>
    /// Otherwise the caller's code must equal the row's <b>ordinally</b>. Anything else — another provider,
    /// a different case, a look-alike — answers <see langword="null"/> (not found), the same as a payment
    /// that does not exist, so the call cannot be used to ask which payments exist.
    /// </description></item>
    /// </list>
    /// Every later statement uses the row's own code, so what is written is what is stored, never the
    /// caller's spelling.
    /// </remarks>
    private async Task<PaymentRow?> ResolvePaymentAsync(
        int paymentId,
        string callerProviderCode,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.Payments
            .AsNoTracking()
            .Where(payment => payment.Id == paymentId)
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

        if (row is null)
        {
            return null;
        }

        if (IsBankTransfer(row.ProviderCode))
        {
            throw BankTransferIsNotSettledHere(nameof(callerProviderCode));
        }

        return string.Equals(row.ProviderCode, callerProviderCode, StringComparison.Ordinal) ? row : null;
    }

    /// <summary>Kept: the order was paid, or the money was put on record. Everything else changed nothing.</summary>
    private static bool IsCommitted(PaymentSettlementResult result) =>
        result is PaymentSettlementResult.Settled or PaymentSettlementResult.CapturedRequiresRefund;

    private async Task<PaymentSettlementResult> SettleInTransactionAsync(
        PaymentSettlement settlement,
        CancellationToken cancellationToken)
    {
        var payment = await ResolvePaymentAsync(settlement.PaymentId, settlement.ProviderCode, cancellationToken);

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

        // What the provider sent has to fit the columns it will be written to. A payable order paid with an
        // id that does not is **kept as an exception, not refused and not truncated into a settling payment**:
        // the money has arrived, so it must be on record, and a transaction id that was cut or folded would
        // sit in a unique index as a falsified reference on the row that settles the order.
        var transactionId = CleanTransactionId(settlement.ProviderTransactionId);
        var payerReference = CleanPayerReference(settlement.PayerReference);

        if (transactionId.Unusable || payerReference.Unusable)
        {
            return await RecordExceptionAsync(
                payment,
                order,
                PaymentExceptionReasons.ProviderDataInvalid,
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

        var settled = await SettlePaymentAsync(
            payment,
            settlement.NowUtc,
            transactionId.Value,
            payerReference.Value,
            cancellationToken);

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

        // Doc 08 Appendix B1 — closes something only after a late success on an older attempt, or a
        // capture that completes after the customer retried. A payment that is itself the one open attempt
        // (Initiated, Pending) has nothing beside it, so the ordinary success asks the database nothing: see
        // CanHaveOtherOpenAttempt, and CloseOtherOpenAttemptsAsync for why this must not run needlessly
        // while this transaction holds a row lock.
        if (OrderSettlementEffects.CanHaveOtherOpenAttempt(payment.Status))
        {
            await effects.CloseOtherOpenAttemptsAsync(
                payment.OrderId,
                payment.Id,
                settlement.NowUtc,
                cancellationToken);
        }

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
        var payment = await ResolvePaymentAsync(routing.PaymentId, routing.ProviderCode, cancellationToken);

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
        DateTime nowUtc,
        string? transactionId,
        string? payerReference,
        CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.Payments
               SET Status = {(byte)PaymentStatus.Succeeded},
                   IsOrderSettlement = 1,
                   ProviderTransactionId = COALESCE({transactionId}, ProviderTransactionId),
                   PayerReference = COALESCE({payerReference}, PayerReference),
                   FailureCode = NULL,
                   FailureMessage = NULL,
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

        // **A provider anomaly must never prevent the exception being recorded.** Whatever the provider
        // sent is made to fit the columns here — cut, or dropped with a note — instead of thrown on, because
        // a long id would otherwise turn "money held for refund" into "String or binary data would be
        // truncated" and the payment would be recorded nowhere.
        var cleanTransactionId = CleanTransactionId(providerTransactionId);
        var cleanPayerReference = CleanPayerReference(payerReference);

        var failureCode = isMismatch ? PaymentFailureCodes.AmountMismatch : null;
        var failureMessage = FailureMessage(reason, cleanTransactionId, cleanPayerReference);

        var routed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.Payments
               SET Status = {(byte)PaymentStatus.CapturedRequiresRefund},
                   IsOrderSettlement = 0,
                   ProviderTransactionId = COALESCE({cleanTransactionId.Value}, ProviderTransactionId),
                   PayerReference = COALESCE({cleanPayerReference.Value}, PayerReference),
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
            PaymentExceptionReasons.ProviderDataInvalid =>
                "Payment received, but the provider's references could not be stored as given, so it was not applied.",
            _ => "Payment received for an order that could not take payment.",
        };

    /// <summary>
    /// The reason, plus a note for each reference that was not stored as given — what staff need to find the
    /// money at the provider when the row cannot carry the id.
    /// </summary>
    private static string FailureMessage(string reason, CleanedText transactionId, CleanedText payerReference)
    {
        var message = Describe(reason);

        if (transactionId.Unusable)
        {
            message += " The provider's transaction id could not be stored as given; it begins: "
                + Excerpt(transactionId.Original) + ".";
        }

        if (payerReference.Unusable)
        {
            message += " The payer reference was longer than " + Payment.PayerReferenceMaxLength
                .ToString(System.Globalization.CultureInfo.InvariantCulture) + " characters and was cut.";
        }

        return message.Length <= Payment.FailureMessageMaxLength
            ? message
            : message[..Payment.FailureMessageMaxLength];
    }

    /// <summary>
    /// <c>Payments.ProviderTransactionId</c> as it can be stored: <c>varchar(100)</c> and part of a unique
    /// index, so it must be short and printable ASCII — which every real gateway's id is. Anything else is
    /// <b>dropped, not repaired</b>: a truncated id could collide with another payment's, and a folded one
    /// (non-ASCII text becomes <c>?</c> in a <c>varchar</c>) already does.
    /// </summary>
    private static CleanedText CleanTransactionId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new CleanedText(null, Unusable: false, Original: null);
        }

        var text = raw.Trim();

        return text.Length <= Payment.ProviderTransactionIdMaxLength && text.All(IsPrintableAscii)
            ? new CleanedText(text, Unusable: false, text)
            : new CleanedText(null, Unusable: true, raw);
    }

    /// <summary>
    /// <c>Payments.PayerReference</c> as it can be stored: <c>nvarchar(100)</c>, so any text, cut to length.
    /// <c>Unusable</c> says it was cut; the settlement path treats that as a provider anomaly and the
    /// exception path just stores the cut value.
    /// </summary>
    private static CleanedText CleanPayerReference(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new CleanedText(null, Unusable: false, Original: null);
        }

        var text = raw.Trim();

        return text.Length <= Payment.PayerReferenceMaxLength
            ? new CleanedText(text, Unusable: false, text)
            : new CleanedText(text[..Payment.PayerReferenceMaxLength], Unusable: true, raw);
    }

    private static bool IsPrintableAscii(char c) => c is >= '!' and <= '~';

    /// <summary>The start of an unusable id, safe to print: at most 60 characters, anything else shown as <c>?</c>.</summary>
    private static string Excerpt(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        var head = text.Length <= 60 ? text : text[..60];

        return new string(head.Select(c => IsPrintableAscii(c) ? c : '?').ToArray());
    }

    /// <summary>A provider-sent reference after it has been made to fit its column.</summary>
    /// <param name="Value">What to store, or null for nothing.</param>
    /// <param name="Unusable">True when what was sent could not be stored as given.</param>
    /// <param name="Original">What was sent, for the note staff read.</param>
    private readonly record struct CleanedText(string? Value, bool Unusable, string? Original);

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
