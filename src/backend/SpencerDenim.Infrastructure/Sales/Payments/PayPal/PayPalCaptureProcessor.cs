using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Payments;
using SpencerDenim.Application.Sales.Payments.PayPal;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Sales.Payments.PayPal;

/// <summary>What the buyer is told after their return from PayPal.</summary>
internal enum PayPalCaptureOutcome
{
    /// <summary>The order is paid.</summary>
    Paid = 0,

    /// <summary>PayPal took the money and the order could not take it. Staff have been told.</summary>
    PaymentException = 1,

    /// <summary>PayPal is holding the payment.</summary>
    Pending = 2,

    /// <summary>PayPal refused it.</summary>
    Declined = 3,

    /// <summary>Another attempt is in flight, or the result is not known yet.</summary>
    Processing = 4,

    /// <summary>The buyer came back without approving.</summary>
    NotApproved = 5,

    /// <summary>The order can no longer be paid.</summary>
    NotPayable = 6,
}

/// <summary>
/// Doc 08 §4.2 steps 1, 2 and 4 for a buyer who has just come back from PayPal: <b>claim, capture, apply</b> —
/// and, for the recovery job, the same capture call and the same apply for a payment found stuck.
/// <para>
/// <b>The order of the three is the design.</b> The claim is one short transaction that proves the order is
/// still payable and takes the capture lock; the capture call is made <i>outside</i> any transaction, because
/// a database transaction held open across a call to another company's server is a lock held at the mercy of
/// their latency; the result is then applied by <see cref="PayPalCaptureApplier"/>, which is the same code a
/// verified webhook and the recovery job use. Nothing here is proof of payment by itself: only PayPal's
/// capture response (or its webhook) is.
/// </para>
/// <para>
/// <b>The capture is asked for with a request id that is a function of the payment</b>
/// (<see cref="PayPalRequestIds.Capture"/>), so a second call for the same payment — a double click, a second
/// tab, the recovery job — cannot capture twice.
/// </para>
/// </summary>
internal sealed partial class PayPalCaptureProcessor(
    IPayPalPaymentStore payments,
    IPayPalGateway gateway,
    PayPalCaptureApplier applier,
    IPayPalSettingsReader settings,
    PayPalEventLog eventLog,
    IClock clock,
    ILogger<PayPalCaptureProcessor> logger)
{
    /// <summary>
    /// Claims the capture, asks PayPal for the money and applies the answer.
    /// </summary>
    /// <param name="order">The order, with its open Initiated payment.</param>
    /// <param name="payment">The Initiated payment to capture; it must have a PayPal order id.</param>
    public async Task<PayPalCaptureOutcome> CaptureAsync(
        PayPalOrderView order,
        PayPalPaymentView payment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(payment);

        if (payment.ProviderReference is not { } paypalOrderId)
        {
            // Nothing to capture: the buyer cannot have approved an order that was never created.
            return PayPalCaptureOutcome.NotPayable;
        }

        var configured = await settings.ReadAsync(cancellationToken);
        var now = clock.UtcNow.UtcDateTime;

        var claim = await payments.ClaimCaptureAsync(
            order.OrderId,
            payment.Id,
            now,
            TimeSpan.FromHours(configured.CaptureLockHours),
            cancellationToken);

        if (claim is ClaimResult.Refused refused)
        {
            // N17: two different answers. Nothing was captured, so the PayPal order simply lapses.
            LogClaimRefused(logger, payment.Id, refused.Reason);

            return refused.Reason == PayPalBlock.BeingProcessed
                ? PayPalCaptureOutcome.Processing
                : PayPalCaptureOutcome.NotPayable;
        }

        // Outside any transaction (doc 08 §4.2 step 2).
        var call = await gateway.CaptureOrderAsync(
            paypalOrderId,
            PayPalRequestIds.Capture(payment.Id),
            cancellationToken);

        return await ResolveAsync(order.OrderId, payment.Id, paypalOrderId, call, forRecovery: false, cancellationToken);
    }

    /// <summary>
    /// Doc 08 §4.2 step 5 for one stuck payment: asks PayPal what the order is and does what the table says.
    /// </summary>
    public async Task<PayPalRecoveryResult> RecoverAsync(CapturingPayment stuck, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stuck);

        var configured = await settings.ReadAsync(cancellationToken);
        var now = clock.UtcNow.UtcDateTime;

        if (stuck.ProviderReference is not { } paypalOrderId)
        {
            // A claimed capture always has a PayPal order. If this one does not, nothing can ever be asked
            // about it and nothing can have been captured.
            await FailAsync(stuck.OrderId, stuck.PaymentId, "NOT_FOUND", "The payment has no PayPal order.", null, cancellationToken);
            return PayPalRecoveryResult.Failed;
        }

        var read = await gateway.GetOrderAsync(paypalOrderId, cancellationToken);

        switch (read)
        {
            case PayPalGetOrderResult.Unavailable unavailable:
                LogRecoveryUnreachable(logger, stuck.PaymentId, unavailable.Reason);
                return await HandleUnreachableAsync(stuck, configured, now, cancellationToken);

            case PayPalGetOrderResult.NotFound:
                await FailAsync(stuck.OrderId, stuck.PaymentId, "NOT_FOUND", "PayPal has no such order.", null, cancellationToken);
                return PayPalRecoveryResult.Failed;

            case PayPalGetOrderResult.Found found:
                return await RecoverFoundAsync(stuck, configured, found.Order, now, cancellationToken);
        }

        return PayPalRecoveryResult.StillUnreachable;
    }

    private async Task<PayPalRecoveryResult> RecoverFoundAsync(
        CapturingPayment stuck,
        PayPalSettings configured,
        PayPalOrderSnapshot order,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // A capture exists: whatever its status, that is the news. COMPLETED settles; PENDING moves the
        // payment to Pending; DECLINED fails it.
        if (order.Captures.Count > 0)
        {
            var capture = order.Captures[^1];

            var result = await applier.ApplyAsync(
                new PayPalCaptureFacts(
                    stuck.PaymentId,
                    capture.CaptureId,
                    capture.Status,
                    capture.Amount,
                    capture.Currency,
                    order.PayerId,
                    capture.StatusReason),
                cancellationToken);

            LogRecoveryApplied(logger, stuck.PaymentId, result.GetType().Name);
            return PayPalRecoveryResult.Applied;
        }

        switch (order.Status)
        {
            case PayPalOrderStatus.Voided:
                await FailAsync(stuck.OrderId, stuck.PaymentId, "VOIDED", "PayPal voided the order.", null, cancellationToken);
                return PayPalRecoveryResult.Failed;

            case PayPalOrderStatus.Approved:
            {
                // "APPROVED (not captured), order payable": renew the lock, then capture again with the SAME
                // request id. If the order is no longer payable nothing is captured.
                var reclaimed = await payments.ReclaimOrderLockAsync(
                    stuck.OrderId,
                    now,
                    TimeSpan.FromHours(configured.CaptureLockHours),
                    cancellationToken);

                if (!reclaimed)
                {
                    await FailAsync(
                        stuck.OrderId,
                        stuck.PaymentId,
                        "ORDER_NOT_PAYABLE",
                        "The order could no longer be paid when PayPal was asked to capture it.",
                        null,
                        cancellationToken);

                    return PayPalRecoveryResult.Failed;
                }

                var call = await gateway.CaptureOrderAsync(
                    order.OrderId,
                    PayPalRequestIds.Capture(stuck.PaymentId),
                    cancellationToken);

                if (call is PayPalCaptureCallResult.Unavailable unavailable)
                {
                    LogRecoveryUnreachable(logger, stuck.PaymentId, unavailable.Reason);
                    return await HandleUnreachableAsync(stuck, configured, now, cancellationToken);
                }

                await ResolveAsync(stuck.OrderId, stuck.PaymentId, order.OrderId, call, forRecovery: true, cancellationToken);
                return PayPalRecoveryResult.Applied;
            }

            default:
            {
                // CREATED, SAVED, PAYER_ACTION_REQUIRED, or a status this build does not know: the buyer
                // never approved, so nothing can be captured. Give the claim back if the order can still be
                // paid; otherwise the payment is over.
                if (await payments.ReleaseClaimAsync(stuck.OrderId, stuck.PaymentId, now, cancellationToken))
                {
                    return PayPalRecoveryResult.Released;
                }

                await FailAsync(
                    stuck.OrderId,
                    stuck.PaymentId,
                    "ORDER_NOT_PAYABLE",
                    "The buyer did not approve the payment and the order can no longer be paid.",
                    null,
                    cancellationToken);

                return PayPalRecoveryResult.Failed;
            }
        }
    }

    /// <summary>
    /// PayPal could not be reached for a stuck payment. Within the recovery window it is stamped for a
    /// later look; once <c>CaptureRecoveryMaxHours</c> have passed since the claim, the payment fails with
    /// <c>RECOVERY_TIMEOUT</c>, the lock is cleared and staff are told (doc 08 §4.2 step 5, R10).
    /// </summary>
    private async Task<PayPalRecoveryResult> HandleUnreachableAsync(
        CapturingPayment stuck,
        PayPalSettings configured,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (!PayPalRecoverySchedule.WindowHasClosed(stuck.CaptureStartedAtUtc, now, configured.CaptureRecoveryMaxHours))
        {
            await payments.RecordRecoveryAttemptAsync(stuck.PaymentId, now, cancellationToken);
            return PayPalRecoveryResult.StillUnreachable;
        }

        var moved = await payments.RecordFailedAsync(
            stuck.OrderId,
            stuck.PaymentId,
            "RECOVERY_TIMEOUT",
            $"PayPal could not be reached for {configured.CaptureRecoveryMaxHours} hours, so the payment could not be confirmed.",
            now,
            CaptureRecoveryFailedAlert.For(
                new CaptureRecoveryFailedFacts(
                    stuck.PaymentId,
                    stuck.OrderPublicId,
                    stuck.OrderNumber,
                    configured.CaptureRecoveryMaxHours),
                now),
            cancellationToken);

        LogRecoveryGaveUp(logger, stuck.PaymentId, stuck.OrderNumber, configured.CaptureRecoveryMaxHours);

        return moved == PayPalMoveResult.Applied ? PayPalRecoveryResult.GaveUp : PayPalRecoveryResult.Applied;
    }

    /// <summary>
    /// Turns PayPal's answer to a capture request into the state change it calls for — the one place both
    /// the buyer's return and the recovery job end up.
    /// </summary>
    private async Task<PayPalCaptureOutcome> ResolveAsync(
        int orderId,
        int paymentId,
        string paypalOrderId,
        PayPalCaptureCallResult call,
        bool forRecovery,
        CancellationToken cancellationToken)
    {
        await eventLog.CaptureAnsweredAsync(paymentId, orderId, paypalOrderId, call);

        switch (call)
        {
            case PayPalCaptureCallResult.Captured captured:
                return await ApplySnapshotAsync(paymentId, captured.Order, cancellationToken);

            case PayPalCaptureCallResult.AlreadyCaptured:
            {
                // ORDER_ALREADY_CAPTURED: a repeat of a capture that went through. Read the capture it made.
                var read = await gateway.GetOrderAsync(paypalOrderId, cancellationToken);

                return read is PayPalGetOrderResult.Found found && found.Order.Captures.Count > 0
                    ? await ApplySnapshotAsync(paymentId, found.Order, cancellationToken)
                    : PayPalCaptureOutcome.Processing;
            }

            case PayPalCaptureCallResult.Declined declined:
            {
                var result = await applier.ApplyAsync(
                    new PayPalCaptureFacts(
                        paymentId,
                        CaptureId: null,
                        PayPalCaptureStatus.Declined,
                        Amount: null,
                        Currency: null,
                        PayerId: null,
                        declined.Reason),
                    cancellationToken);

                return Outcome(result);
            }

            case PayPalCaptureCallResult.NotApproved:
            {
                // The buyer came back without approving. Nothing was captured; give the claim back so they
                // can go and approve. If the order has since stopped being payable, the payment is over.
                var released = await payments.ReleaseClaimAsync(
                    orderId,
                    paymentId,
                    clock.UtcNow.UtcDateTime,
                    cancellationToken);

                if (!released)
                {
                    await FailAsync(
                        orderId,
                        paymentId,
                        "ORDER_NOT_PAYABLE",
                        "The buyer did not approve the payment and the order can no longer be paid.",
                        null,
                        cancellationToken);

                    return PayPalCaptureOutcome.NotPayable;
                }

                return PayPalCaptureOutcome.NotApproved;
            }

            case PayPalCaptureCallResult.OrderNotFound:
                await FailAsync(orderId, paymentId, "NOT_FOUND", "PayPal has no such order.", null, cancellationToken);
                return PayPalCaptureOutcome.NotPayable;

            case PayPalCaptureCallResult.Rejected rejected:
                // A refusal this build has no specific answer for is NOT proof that nothing was captured.
                // The payment stays Capturing and the lock stays on; recovery asks PayPal what the order is.
                LogCaptureRejected(logger, paymentId, rejected.Reason, forRecovery);
                return PayPalCaptureOutcome.Processing;

            case PayPalCaptureCallResult.Unavailable unavailable:
                // Timeout or network error: "Payment stays Capturing and the lock is kept -> recovery".
                LogCaptureUnavailable(logger, paymentId, unavailable.Reason);
                return PayPalCaptureOutcome.Processing;

            default:
                return PayPalCaptureOutcome.Processing;
        }
    }

    /// <summary>
    /// Applies the last capture on an order PayPal reported. An order with no capture on it cannot be
    /// applied: the payment stays Capturing.
    /// </summary>
    private async Task<PayPalCaptureOutcome> ApplySnapshotAsync(
        int paymentId,
        PayPalOrderSnapshot order,
        CancellationToken cancellationToken)
    {
        if (order.Captures.Count == 0)
        {
            return PayPalCaptureOutcome.Processing;
        }

        var capture = order.Captures[^1];

        var result = await applier.ApplyAsync(
            new PayPalCaptureFacts(
                paymentId,
                capture.CaptureId,
                capture.Status,
                capture.Amount,
                capture.Currency,
                order.PayerId,
                capture.StatusReason),
            cancellationToken);

        return Outcome(result);
    }

    private async Task FailAsync(
        int orderId,
        int paymentId,
        string code,
        string message,
        AdminAlert? alert,
        CancellationToken cancellationToken) =>
        await payments.RecordFailedAsync(
            orderId,
            paymentId,
            code,
            message,
            clock.UtcNow.UtcDateTime,
            alert,
            cancellationToken);

    /// <summary>What the buyer is told for each way a result can have been applied.</summary>
    internal static PayPalCaptureOutcome Outcome(PayPalApplyResult result) =>
        result switch
        {
            PayPalApplyResult.Settled => PayPalCaptureOutcome.Paid,
            PayPalApplyResult.CapturedRequiresRefund => PayPalCaptureOutcome.PaymentException,
            PayPalApplyResult.Pending => PayPalCaptureOutcome.Pending,
            PayPalApplyResult.Declined => PayPalCaptureOutcome.Declined,

            // The webhook got there first (or the other way round): say what the payment became.
            PayPalApplyResult.Duplicate duplicate => duplicate.Current switch
            {
                PaymentStatus.Succeeded or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded =>
                    PayPalCaptureOutcome.Paid,
                PaymentStatus.CapturedRequiresRefund => PayPalCaptureOutcome.PaymentException,
                PaymentStatus.Pending => PayPalCaptureOutcome.Pending,
                PaymentStatus.Failed => PayPalCaptureOutcome.Declined,
                PaymentStatus.Capturing => PayPalCaptureOutcome.Processing,
                _ => PayPalCaptureOutcome.NotPayable,
            },

            _ => PayPalCaptureOutcome.NotPayable,
        };

    [LoggerMessage(
        EventId = 3530,
        Level = LogLevel.Information,
        Message = "PayPal payment {PaymentId} was not claimed ({Reason}); nothing was captured.")]
    private static partial void LogClaimRefused(ILogger logger, int paymentId, PayPalBlock reason);

    [LoggerMessage(
        EventId = 3531,
        Level = LogLevel.Warning,
        Message = "PayPal payment {PaymentId} is still Capturing: PayPal could not be reached ({Reason}). "
            + "The recovery job will ask again.")]
    private static partial void LogCaptureUnavailable(ILogger logger, int paymentId, string reason);

    [LoggerMessage(
        EventId = 3532,
        Level = LogLevel.Warning,
        Message = "PayPal refused the capture for payment {PaymentId} ({Reason}, recovery: {ForRecovery}); the "
            + "payment stays Capturing so the recovery job can find out what PayPal holds.")]
    private static partial void LogCaptureRejected(ILogger logger, int paymentId, string reason, bool forRecovery);

    [LoggerMessage(
        EventId = 3533,
        Level = LogLevel.Warning,
        Message = "Recovery of PayPal payment {PaymentId} could not reach PayPal ({Reason}); it will be tried "
            + "again on the backoff schedule.")]
    private static partial void LogRecoveryUnreachable(ILogger logger, int paymentId, string reason);

    [LoggerMessage(
        EventId = 3534,
        Level = LogLevel.Information,
        Message = "Recovery applied PayPal's answer for payment {PaymentId}: {Result}.")]
    private static partial void LogRecoveryApplied(ILogger logger, int paymentId, string result);

    [LoggerMessage(
        EventId = 3535,
        Level = LogLevel.Error,
        Message = "PayPal payment {PaymentId} (order {OrderNumber}) could not be confirmed for {Hours} hours and "
            + "was failed with RECOVERY_TIMEOUT. Staff must check PayPal by hand; a later verified COMPLETED "
            + "webhook still applies.")]
    private static partial void LogRecoveryGaveUp(ILogger logger, int paymentId, string orderNumber, int hours);
}

/// <summary>What one recovery attempt on a stuck payment did.</summary>
internal enum PayPalRecoveryResult
{
    /// <summary>PayPal's answer was applied (paid, pending, declined, an exception, a repeat capture).</summary>
    Applied = 0,

    /// <summary>The payment was failed (voided, not found, not payable).</summary>
    Failed = 1,

    /// <summary>The buyer never approved; the claim was handed back and the payment is Initiated again.</summary>
    Released = 2,

    /// <summary>PayPal could not be reached; the payment stays Capturing and is stamped for a later look.</summary>
    StillUnreachable = 3,

    /// <summary>The recovery window closed: <c>RECOVERY_TIMEOUT</c> and a <c>CaptureRecoveryFailed</c> alert.</summary>
    GaveUp = 4,
}
