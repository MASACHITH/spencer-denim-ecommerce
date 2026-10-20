using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Ops.Alerts.Abstractions;
using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Application.Sales.Payments.PayPal;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Payments.PayPal;

/// <summary>
/// <see cref="IPayPalPaymentStore"/> over <c>sales.Orders</c> and <c>sales.Payments</c> (doc 08 §4.2, §4.3).
/// <para>
/// <b>The claim is the lock, exactly as in <c>OrderSettlementStore</c> and <c>BankTransferReviewStore</c></b>:
/// every transition is a conditional <c>UPDATE</c> and its row count is the answer, in the lock order of
/// doc 08 §5.3 and §4.2 N6 — the order row first, then the payment row. The statements that take the order
/// row first even when they have no order column to change (<see cref="OpenAttemptAsync"/>) do it for the
/// same reason the claim does: it is what makes a customer's retry and a capture claim queue behind one
/// another instead of racing, so <c>UX_Payments_OneOpen</c> is never asked to refuse an insert.
/// </para>
/// <para>
/// <b>Every statement names the PayPal provider</b> in its own <c>WHERE</c>, so a bug in a caller's read
/// cannot move a bank-transfer payment — the lesson <c>OrderSnapshot</c> records.
/// </para>
/// <para>
/// <b>The index traps this class steers round</b> (the <c>PaymentConfiguration</c> remarks name them):
/// Capturing (2) is outside <c>UX_Payments_OneOpen</c>, but Pending (1) is inside it, so a capture that
/// resolves to Pending must not find a second open row beside it. That is prevented at the other end — a
/// retry is refused while a payment is Capturing <i>or</i> Pending — and not by catching error 2627, which
/// doc 08 §4.2 says the design never relies on.
/// </para>
/// </summary>
internal sealed class PayPalPaymentStore(AppDbContext dbContext, IAdminAlertWriter alerts) : IPayPalPaymentStore
{
    private const string PayPal = PaymentProviderCodes.PayPal;

    public async Task<PayPalOrderView?> FindOrderAsync(Guid orderPublicId, CancellationToken cancellationToken)
    {
        var order = await dbContext.Orders
            .AsNoTracking()
            .Where(candidate => candidate.PublicId == orderPublicId)
            .Select(candidate => new OrderRow(
                candidate.Id,
                candidate.PublicId,
                candidate.OrderNumber,
                candidate.Status,
                candidate.PlacedAtUtc,
                candidate.PaymentDueAtUtc,
                candidate.CaptureLockedUntilUtc,
                candidate.GrandTotal,
                candidate.PaymentProviderCode))
            .FirstOrDefaultAsync(cancellationToken);

        return order is null ? null : await WithPaymentsAsync(order, cancellationToken);
    }

    public async Task<PayPalOrderView?> FindOrderByNumberAsync(string orderNumber, CancellationToken cancellationToken)
    {
        var order = await dbContext.Orders
            .AsNoTracking()
            .Where(candidate => candidate.OrderNumber == orderNumber)
            .Select(candidate => new OrderRow(
                candidate.Id,
                candidate.PublicId,
                candidate.OrderNumber,
                candidate.Status,
                candidate.PlacedAtUtc,
                candidate.PaymentDueAtUtc,
                candidate.CaptureLockedUntilUtc,
                candidate.GrandTotal,
                candidate.PaymentProviderCode))
            .FirstOrDefaultAsync(cancellationToken);

        return order is null ? null : await WithPaymentsAsync(order, cancellationToken);
    }

    public async Task<PayPalPaymentRef?> FindPaymentAsync(int paymentId, CancellationToken cancellationToken) =>
        await dbContext.Payments
            .AsNoTracking()
            .Where(payment => payment.Id == paymentId && payment.ProviderCode == PayPal)
            .Select(payment => new PayPalPaymentRef(
                payment.Id,
                payment.OrderId,
                payment.Status,
                payment.ProviderReference,
                payment.ProviderTransactionId))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<PayPalPaymentRef?> FindPaymentForEventAsync(
        string? captureId,
        string? paypalOrderId,
        int? paymentIdFromCustomId,
        CancellationToken cancellationToken)
    {
        // In the order of how sure each is. A capture id we already recorded names the payment outright; a
        // PayPal order id names it because UX_Payments_Provider_Reference makes it unique per provider; our
        // own custom_id is the last resort and is checked against the provider like the others.
        if (!string.IsNullOrWhiteSpace(captureId)
            && await ProjectAsync(payment => payment.ProviderTransactionId == captureId, cancellationToken)
                is { } byCapture)
        {
            return byCapture;
        }

        if (!string.IsNullOrWhiteSpace(paypalOrderId)
            && await ProjectAsync(payment => payment.ProviderReference == paypalOrderId, cancellationToken)
                is { } byOrder)
        {
            return byOrder;
        }

        return paymentIdFromCustomId is { } id
            ? await ProjectAsync(payment => payment.Id == id, cancellationToken)
            : null;
    }

    public async Task<OpenAttemptResult> OpenAttemptAsync(
        int orderId,
        PaymentInitiation? initiation,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        await SettlementTransaction.RunAsync(
            dbContext,
            () => OpenAttemptInTransactionAsync(orderId, initiation, nowUtc, cancellationToken),

            // Only the insert is kept. Everything else rolls back, and with it the statement that took the
            // order row's lock, which changed nothing but UpdatedAtUtc.
            result => result is OpenAttemptResult.Created,
            cancellationToken);

    public async Task<bool> SetProviderReferenceAsync(
        int paymentId,
        string providerReference,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.Payments
               SET ProviderReference = {providerReference},
                   UpdatedAtUtc = {nowUtc}
             WHERE Id = {paymentId}
               AND ProviderCode = {PayPal}
               AND Status = {(byte)PaymentStatus.Initiated}
               AND ProviderReference IS NULL;
            """,
            cancellationToken) == 1;

    public async Task<bool> CancelInitiatedAsync(
        int orderId,
        int paymentId,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.Payments
               SET Status = {(byte)PaymentStatus.Cancelled},
                   CompletedAtUtc = {nowUtc},
                   UpdatedAtUtc = {nowUtc}
             WHERE Id = {paymentId}
               AND OrderId = {orderId}
               AND ProviderCode = {PayPal}
               AND Status = {(byte)PaymentStatus.Initiated};
            """,
            cancellationToken) == 1;

    public async Task<ClaimResult> ClaimCaptureAsync(
        int orderId,
        int paymentId,
        DateTime nowUtc,
        TimeSpan lockFor,
        CancellationToken cancellationToken) =>
        await SettlementTransaction.RunAsync(
            dbContext,
            () => ClaimInTransactionAsync(orderId, paymentId, nowUtc, nowUtc + lockFor, cancellationToken),
            result => result is ClaimResult.Claimed,
            cancellationToken);

    public async Task<bool> ReleaseClaimAsync(
        int orderId,
        int paymentId,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        await SettlementTransaction.RunAsync(
            dbContext,
            async () =>
            {
                var order = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE sales.Orders
                       SET CaptureLockedUntilUtc = NULL,
                           UpdatedAtUtc = {nowUtc}
                     WHERE Id = {orderId}
                       AND PaymentProviderCode = {PayPal}
                       AND Status = {(byte)OrderStatus.PendingPayment};
                    """,
                    cancellationToken);

                if (order != 1)
                {
                    return false;
                }

                return await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE sales.Payments
                       SET Status = {(byte)PaymentStatus.Initiated},
                           CaptureStartedAtUtc = NULL,
                           UpdatedAtUtc = {nowUtc}
                     WHERE Id = {paymentId}
                       AND OrderId = {orderId}
                       AND ProviderCode = {PayPal}
                       AND Status = {(byte)PaymentStatus.Capturing};
                    """,
                    cancellationToken) == 1;
            },
            released => released,
            cancellationToken);

    public async Task<PayPalMoveResult> RecordPendingAsync(
        int orderId,
        int paymentId,
        string? captureId,
        int pendingMaxDays,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        await SettlementTransaction.RunAsync(
            dbContext,
            async () =>
            {
                // The order first (N6). The lock is cleared whatever the order is, because the capture is no
                // longer in flight; the hold is extended only for an order that is still PendingPayment —
                // doc 08 §4.2 step 4: max(PaymentDueAtUtc, PlacedAtUtc + PendingCaptureMaxDays).
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE sales.Orders
                       SET CaptureLockedUntilUtc = NULL,
                           PaymentDueAtUtc = CASE
                               WHEN Status = {(byte)OrderStatus.PendingPayment}
                                AND PaymentDueAtUtc < DATEADD(DAY, {pendingMaxDays}, PlacedAtUtc)
                               THEN DATEADD(DAY, {pendingMaxDays}, PlacedAtUtc)
                               ELSE PaymentDueAtUtc END,
                           UpdatedAtUtc = {nowUtc}
                     WHERE Id = {orderId}
                       AND PaymentProviderCode = {PayPal};
                    """,
                    cancellationToken);

                var moved = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE sales.Payments
                       SET Status = {(byte)PaymentStatus.Pending},
                           ProviderTransactionId = COALESCE({captureId}, ProviderTransactionId),
                           UpdatedAtUtc = {nowUtc}
                     WHERE Id = {paymentId}
                       AND OrderId = {orderId}
                       AND ProviderCode = {PayPal}
                       AND Status IN ({(byte)PaymentStatus.Initiated}, {(byte)PaymentStatus.Capturing});
                    """,
                    cancellationToken);

                return moved == 1 ? PayPalMoveResult.Applied : PayPalMoveResult.NotApplicable;
            },

            // A duplicate or out-of-order PENDING must leave the order exactly as it found it.
            result => result == PayPalMoveResult.Applied,
            cancellationToken);

    public async Task<PayPalMoveResult> RecordDeclinedAsync(
        int orderId,
        int paymentId,
        string failureCode,
        string? failureMessage,
        int expiryMinutes,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        await SettlementTransaction.RunAsync(
            dbContext,
            async () =>
            {
                // Doc 08 §4.2 step 4 / R2: "if the payment was Pending, PaymentDueAtUtc = min(PaymentDueAtUtc,
                // now + Orders.PayPalExpiryMinutes)". Whether it was Pending is read in the same statement
                // that cuts the hold, behind the order row's lock, so two racing DENIED events cannot both
                // see Pending — and the second one's payment update then finds nothing to move and rolls the
                // whole thing back.
                //
                // **The `EXISTS` reads `sales.Payments` WITH (READCOMMITTEDLOCK) (issue #185).** Every database
                // runs READ_COMMITTED_SNAPSHOT since #163, where a plain read takes the last committed version
                // of a row instead of waiting for the transaction that holds it. Nothing backs this decision up:
                // an unhinted read of a payment whose move to Pending has not committed says "not Pending", and
                // the long hold is left in place after a decline although doc 08 §4.2 R2 cuts it. The hint makes
                // the read wait for that writer, in whatever order the plan reads the two tables.
                // `PaymentGuardLockTests` fails without it.
                var cutBack = nowUtc.AddMinutes(expiryMinutes);

                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE sales.Orders
                       SET CaptureLockedUntilUtc = NULL,
                           PaymentDueAtUtc = CASE
                               WHEN Status = {(byte)OrderStatus.PendingPayment}
                                AND PaymentDueAtUtc > {cutBack}
                                AND EXISTS (
                                      SELECT 1 FROM sales.Payments pending WITH (READCOMMITTEDLOCK)
                                       WHERE pending.Id = {paymentId}
                                         AND pending.Status = {(byte)PaymentStatus.Pending})
                               THEN {cutBack}
                               ELSE PaymentDueAtUtc END,
                           UpdatedAtUtc = {nowUtc}
                     WHERE Id = {orderId}
                       AND PaymentProviderCode = {PayPal};
                    """,
                    cancellationToken);

                var moved = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE sales.Payments
                       SET Status = {(byte)PaymentStatus.Failed},
                           FailureCode = {failureCode},
                           FailureMessage = {failureMessage},
                           CompletedAtUtc = {nowUtc},
                           UpdatedAtUtc = {nowUtc}
                     WHERE Id = {paymentId}
                       AND OrderId = {orderId}
                       AND ProviderCode = {PayPal}
                       AND Status IN (
                             {(byte)PaymentStatus.Initiated}, {(byte)PaymentStatus.Pending},
                             {(byte)PaymentStatus.Capturing});
                    """,
                    cancellationToken);

                return moved == 1 ? PayPalMoveResult.Applied : PayPalMoveResult.NotApplicable;
            },
            result => result == PayPalMoveResult.Applied,
            cancellationToken);

    public async Task<PayPalMoveResult> RecordFailedAsync(
        int orderId,
        int paymentId,
        string failureCode,
        string? failureMessage,
        DateTime nowUtc,
        AdminAlert? alert,
        CancellationToken cancellationToken) =>
        await SettlementTransaction.RunAsync(
            dbContext,
            async () =>
            {
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE sales.Orders
                       SET CaptureLockedUntilUtc = NULL,
                           UpdatedAtUtc = {nowUtc}
                     WHERE Id = {orderId}
                       AND PaymentProviderCode = {PayPal}
                       AND CaptureLockedUntilUtc IS NOT NULL;
                    """,
                    cancellationToken);

                var moved = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE sales.Payments
                       SET Status = {(byte)PaymentStatus.Failed},
                           FailureCode = {failureCode},
                           FailureMessage = {failureMessage},
                           CompletedAtUtc = {nowUtc},
                           UpdatedAtUtc = {nowUtc}
                     WHERE Id = {paymentId}
                       AND OrderId = {orderId}
                       AND ProviderCode = {PayPal}
                       AND Status IN (
                             {(byte)PaymentStatus.Initiated}, {(byte)PaymentStatus.Pending},
                             {(byte)PaymentStatus.Capturing});
                    """,
                    cancellationToken);

                if (moved != 1)
                {
                    return PayPalMoveResult.NotApplicable;
                }

                // In this transaction, so a payment that failed always has its banner and a rolled-back one
                // never does.
                if (alert is not null)
                {
                    alerts.Enlist(alert);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }

                return PayPalMoveResult.Applied;
            },
            result => result == PayPalMoveResult.Applied,
            cancellationToken);

    public async Task<IReadOnlyList<CapturingPayment>> FindCapturingAsync(
        DateTime startedBeforeUtc,
        int take,
        CancellationToken cancellationToken) =>
        await dbContext.Payments
            .AsNoTracking()

            // The literal enum member, so the predicate implies IX_Payments_Capturing's filter (DB28).
            .Where(payment =>
                payment.Status == PaymentStatus.Capturing
                && payment.ProviderCode == PayPal
                && payment.CaptureStartedAtUtc < startedBeforeUtc)
            .OrderBy(payment => payment.CaptureStartedAtUtc)
            .ThenBy(payment => payment.Id)
            .Take(take)
            .Select(payment => new CapturingPayment(
                payment.Id,
                payment.OrderId,
                payment.Order!.PublicId,
                payment.Order.OrderNumber,
                payment.ProviderReference,
                payment.CaptureStartedAtUtc!.Value,
                payment.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

    public async Task<bool> RecordRecoveryAttemptAsync(
        int paymentId,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.Payments
               SET UpdatedAtUtc = {nowUtc}
             WHERE Id = {paymentId}
               AND ProviderCode = {PayPal}
               AND Status = {(byte)PaymentStatus.Capturing};
            """,
            cancellationToken) == 1;

    public async Task<bool> ReclaimOrderLockAsync(
        int orderId,
        DateTime nowUtc,
        TimeSpan lockFor,
        CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.Orders
               SET CaptureLockedUntilUtc = {nowUtc + lockFor},
                   UpdatedAtUtc = {nowUtc}
             WHERE Id = {orderId}
               AND PaymentProviderCode = {PayPal}
               AND Status = {(byte)OrderStatus.PendingPayment};
            """,
            cancellationToken) == 1;

    private async Task<PayPalPaymentRef?> ProjectAsync(
        System.Linq.Expressions.Expression<Func<Payment, bool>> predicate,
        CancellationToken cancellationToken) =>
        await dbContext.Payments
            .AsNoTracking()
            .Where(payment => payment.ProviderCode == PayPal)
            .Where(predicate)
            .OrderByDescending(payment => payment.Id)
            .Select(payment => new PayPalPaymentRef(
                payment.Id,
                payment.OrderId,
                payment.Status,
                payment.ProviderReference,
                payment.ProviderTransactionId))
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<PayPalOrderView> WithPaymentsAsync(OrderRow order, CancellationToken cancellationToken)
    {
        var payments = await dbContext.Payments
            .AsNoTracking()
            .Where(payment => payment.OrderId == order.Id && payment.ProviderCode == PayPal)
            .OrderByDescending(payment => payment.Id)
            .Select(payment => new PayPalPaymentView(
                payment.Id,
                payment.Status,
                payment.ProviderReference,
                payment.ProviderAmount,
                payment.ProviderCurrency,
                payment.ExchangeRate,
                payment.FxBufferPercent,
                payment.FailureCode,
                payment.CaptureStartedAtUtc))
            .ToListAsync(cancellationToken);

        return new PayPalOrderView(
            order.Id,
            order.PublicId,
            order.OrderNumber,
            order.Status,
            order.PlacedAtUtc,
            order.PaymentDueAtUtc,
            order.CaptureLockedUntilUtc,
            order.GrandTotal,
            order.PaymentProviderCode,
            payments.FirstOrDefault(payment => payment.Status is PaymentStatus.Initiated
                or PaymentStatus.Pending
                or PaymentStatus.Capturing),
            payments.FirstOrDefault(),
            payments.Any(payment => payment.Status == PaymentStatus.CapturedRequiresRefund));
    }

    private async Task<OpenAttemptResult> OpenAttemptInTransactionAsync(
        int orderId,
        PaymentInitiation? initiation,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        // The order row first, and held for the rest of the transaction: a claim for the same order queues
        // behind this statement, and this one behind a claim. The conditions are the retry rule — a
        // PendingPayment PayPal order that is within its deadline and holds no live capture lock.
        var locked = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.Orders
               SET UpdatedAtUtc = {nowUtc}
             WHERE Id = {orderId}
               AND PaymentProviderCode = {PayPal}
               AND Status = {(byte)OrderStatus.PendingPayment}
               AND PaymentDueAtUtc > {nowUtc}
               AND (CaptureLockedUntilUtc IS NULL OR CaptureLockedUntilUtc < {nowUtc});
            """,
            cancellationToken);

        if (locked != 1)
        {
            return new OpenAttemptResult.Blocked(await DiagnoseAsync(orderId, nowUtc, cancellationToken));
        }

        // Money that arrived and could not settle this order is with staff. Checked after the order row is held,
        // so a settlement that is routing a payment to that state (which takes the same row first) has either
        // finished, and is seen here, or has not begun and cannot slip a new attempt in beside it.
        if (await dbContext.Payments.AnyAsync(
                payment => payment.OrderId == orderId
                    && payment.ProviderCode == PayPal
                    && payment.Status == PaymentStatus.CapturedRequiresRefund,
                cancellationToken))
        {
            return new OpenAttemptResult.Blocked(PayPalBlock.CapturedAwaitingStaff);
        }

        var open = await dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.OrderId == orderId
                && payment.ProviderCode == PayPal
                && (payment.Status == PaymentStatus.Initiated
                    || payment.Status == PaymentStatus.Pending
                    || payment.Status == PaymentStatus.Capturing))
            .OrderByDescending(payment => payment.Id)
            .Select(payment => new PayPalPaymentView(
                payment.Id,
                payment.Status,
                payment.ProviderReference,
                payment.ProviderAmount,
                payment.ProviderCurrency,
                payment.ExchangeRate,
                payment.FxBufferPercent,
                payment.FailureCode,
                payment.CaptureStartedAtUtc))
            .ToListAsync(cancellationToken);

        // Capturing blocks whatever the lock says: a payment that is being collected is not abandoned, and
        // a second open row beside it would meet UX_Payments_OneOpen if that capture then resolved Pending.
        if (open.Any(payment => payment.Status == PaymentStatus.Capturing))
        {
            return new OpenAttemptResult.Blocked(PayPalBlock.BeingProcessed);
        }

        // Doc 11 P8 step 9: a PENDING capture blocks retry until PayPal resolves it or the hold expires.
        if (open.Any(payment => payment.Status == PaymentStatus.Pending))
        {
            return new OpenAttemptResult.Blocked(PayPalBlock.PendingWithPayPal);
        }

        if (open.FirstOrDefault(payment => payment.Status == PaymentStatus.Initiated) is { } initiated)
        {
            return new OpenAttemptResult.Reused(initiated);
        }

        if (initiation is null)
        {
            return new OpenAttemptResult.NeedsInitiation();
        }

        var grandTotal = await dbContext.Orders
            .AsNoTracking()
            .Where(order => order.Id == orderId)
            .Select(order => order.GrandTotal)
            .SingleAsync(cancellationToken);

        var created = new Payment
        {
            OrderId = orderId,
            ProviderCode = PayPal,
            Status = initiation.Status,
            Amount = grandTotal,
            Currency = SalesCurrency.Lkr,
            ProviderCurrency = initiation.ProviderCurrency,
            ProviderAmount = initiation.ProviderAmount,
            ExchangeRate = initiation.ExchangeRate,
            FxBufferPercent = initiation.FxBufferPercent,
            CreatedAtUtc = nowUtc,
        };

        dbContext.Payments.Add(created);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new OpenAttemptResult.Created(new PayPalPaymentView(
            created.Id,
            created.Status,
            null,
            created.ProviderAmount,
            created.ProviderCurrency,
            created.ExchangeRate,
            created.FxBufferPercent,
            null,
            null));
    }

    private async Task<ClaimResult> ClaimInTransactionAsync(
        int orderId,
        int paymentId,
        DateTime nowUtc,
        DateTime lockUntilUtc,
        CancellationToken cancellationToken)
    {
        // Doc 08 §4.2 step 1, statement for statement. The order first: "UPDATE sales.Orders SET
        // CaptureLockedUntilUtc = now + CaptureLockHours WHERE Id = @o AND Status = 0 AND
        // (CaptureLockedUntilUtc IS NULL OR CaptureLockedUntilUtc < now)".
        var order = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.Orders
               SET CaptureLockedUntilUtc = {lockUntilUtc},
                   UpdatedAtUtc = {nowUtc}
             WHERE Id = {orderId}
               AND PaymentProviderCode = {PayPal}
               AND Status = {(byte)OrderStatus.PendingPayment}
               AND (CaptureLockedUntilUtc IS NULL OR CaptureLockedUntilUtc < {nowUtc});
            """,
            cancellationToken);

        if (order != 1)
        {
            return new ClaimResult.Refused(await DiagnoseAsync(orderId, nowUtc, cancellationToken));
        }

        // "UPDATE sales.Payments SET Status = 2, CaptureStartedAtUtc = now WHERE Id = @p AND OrderId = @o
        // AND Status = 0". Both must hit one row.
        var payment = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.Payments
               SET Status = {(byte)PaymentStatus.Capturing},
                   CaptureStartedAtUtc = {nowUtc},
                   UpdatedAtUtc = {nowUtc}
             WHERE Id = {paymentId}
               AND OrderId = {orderId}
               AND ProviderCode = {PayPal}
               AND Status = {(byte)PaymentStatus.Initiated};
            """,
            cancellationToken);

        if (payment != 1)
        {
            // Rolled back by the caller's transaction, which takes the order's lock with it: "don't call
            // capture; the uncaptured PayPal order simply lapses and no money moves".
            return new ClaimResult.Refused(await DiagnoseAsync(orderId, nowUtc, cancellationToken));
        }

        return new ClaimResult.Claimed();
    }

    /// <summary>
    /// Which of the two answers doc 08 §4.2 step 1 (N17) gives a buyer whose claim did not go through:
    /// "being processed" when the order is PendingPayment under a capture lock that is still live, and "can no
    /// longer be paid" for everything else.
    /// </summary>
    /// <remarks>
    /// Read <b>inside the transaction, after the failed statement</b>: under ReadCommitted it sees the
    /// winner's committed state, which is the question — what is the order <i>now</i> — and not what it was
    /// when the buyer arrived. A winner that is mid-claim holds the order row, so this read waits for it and
    /// then sees its lock.
    /// </remarks>
    private async Task<PayPalBlock> DiagnoseAsync(int orderId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var order = await dbContext.Orders
            .AsNoTracking()
            .Where(candidate => candidate.Id == orderId)
            .Select(candidate => new DiagnosisRow(
                candidate.Status,
                candidate.PaymentProviderCode,
                candidate.CaptureLockedUntilUtc))
            .FirstOrDefaultAsync(cancellationToken);

        if (order is null || order.Provider != PayPal)
        {
            return PayPalBlock.NotPayable;
        }

        if (order.Status == OrderStatus.PendingPayment && order.LockedUntilUtc is { } until && until > nowUtc)
        {
            return PayPalBlock.BeingProcessed;
        }

        return PayPalBlock.NotPayable;
    }

    /// <summary>A class, so "no such row" is representable — see <c>OrderSnapshot</c> for what a struct costs.</summary>
    private sealed record DiagnosisRow(OrderStatus Status, string Provider, DateTime? LockedUntilUtc);

    private sealed record OrderRow(
        int Id,
        Guid PublicId,
        string OrderNumber,
        OrderStatus Status,
        DateTime PlacedAtUtc,
        DateTime PaymentDueAtUtc,
        DateTime? CaptureLockedUntilUtc,
        decimal GrandTotal,
        string PaymentProviderCode);
}
