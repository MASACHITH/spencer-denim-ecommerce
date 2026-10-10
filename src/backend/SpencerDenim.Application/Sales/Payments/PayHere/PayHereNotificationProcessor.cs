using System.Globalization;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Ops.Alerts.Abstractions;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Payments.PayHere;

/// <summary>What became of one stored PayHere notification.</summary>
public enum PayHereOutcomeKind
{
    /// <summary>It changed something, or put money on record: the event is Processed.</summary>
    Applied,

    /// <summary>It was recognised and needs no action — a duplicate, or out of order: the event is Ignored.</summary>
    Ignored,

    /// <summary>It names no PayHere payment of this shop: the event is Ignored, and the caller should say so.</summary>
    Unmatched,

    /// <summary>Applying it threw. The event is Failed, with a time for the sweep to try again.</summary>
    Failed,

    /// <summary>Applying it threw and no attempt is left. The event stays Failed with no retry time, and an alert says so.</summary>
    GaveUp,
}

/// <summary>The outcome, a sentence for a log, and the exception if there was one.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Detail">A sentence naming no card, name or address, safe for a log line.</param>
/// <param name="Error">The exception, for <see cref="PayHereOutcomeKind.Failed"/> and <see cref="PayHereOutcomeKind.GaveUp"/>.</param>
public sealed record PayHereProcessingOutcome(PayHereOutcomeKind Kind, string Detail, Exception? Error = null);

/// <summary>
/// Applies one <b>verified, stored</b> PayHere notification (doc 11 P8b step 4), and then says what became of
/// it: Processed, Ignored or Failed. The first delivery calls it from the notify endpoint; the retry sweep
/// calls it for an event whose first attempt died. <b>It is idempotent</b>, which is what lets both do so.
/// <para>
/// <b>It trusts its input and nothing else.</b> The signature and the merchant ID were checked before the
/// event was stored (see <see cref="PayHereNotifyCommandHandler"/>), so what arrives here is a fact about
/// PayHere's side. Everything that decides what happens on <i>our</i> side is read afresh: the payment, from
/// the database, by the order ID the signature covers; the amount it must equal, from the payment row and not
/// from anything in the notification; and the settings, at the moment of use.
/// </para>
/// <para>
/// <b>What each status does</b> (doc 11 P8b step 4):
/// </para>
/// <list type="table">
/// <item><term>2, success</term><description><see cref="IOrderSettlementStore.SettleAsync"/> — the shared
/// transaction that checks the amount and currency against the order, pays it if it still can be paid, and
/// otherwise keeps the money on record as a payment exception with an alert. A success on a payment that is
/// already settled is a duplicate; a success with a <i>different</i> PayHere payment ID is a second charge,
/// and gets an alert.</description></item>
/// <item><term>0, pending</term><description>Payment Pending, and the order's hold is extended.</description></item>
/// <item><term>−1 / −2</term><description>Payment Failed; a Pending one cuts the hold back; a fresh attempt is
/// opened if the order can still be paid.</description></item>
/// <item><term>−3, charged back</term><description>An alert, and nothing else changes.</description></item>
/// </list>
/// </summary>
public sealed class PayHereNotificationProcessor(
    IPayHerePaymentStore payments,
    IOrderSettlementStore settlements,
    IPaymentEventStore events,
    IPayHereSettingsReader settings,
    IAdminAlertWriter alerts,
    IClock clock)
{
    /// <summary>
    /// How long to wait before the next attempt, indexed by how many attempts have already failed: one
    /// minute, five, fifteen, an hour, four hours. After the last, nothing tries again.
    /// </summary>
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(4),
    ];

    /// <summary>The number of failed attempts after which the event is left for a person.</summary>
    public static int MaxAttempts => Backoff.Length + 1;

    /// <summary>The delay before attempt number <paramref name="failedAttempts"/> + 1, or null once the ladder is spent.</summary>
    public static TimeSpan? RetryDelay(int failedAttempts) =>
        failedAttempts >= 1 && failedAttempts <= Backoff.Length ? Backoff[failedAttempts - 1] : null;

    /// <summary>
    /// Applies the notification and records the outcome on the event. Does not throw for a failure to apply
    /// (that becomes <see cref="PayHereOutcomeKind.Failed"/>); does throw if <paramref name="cancellationToken"/>
    /// is cancelled.
    /// </summary>
    /// <param name="eventId">The stored event.</param>
    /// <param name="notification">Its allow-listed fields.</param>
    /// <param name="failedAttempts">How many attempts have already failed: 0 for a first delivery.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public async Task<PayHereProcessingOutcome> ProcessAsync(
        long eventId,
        PayHereNotification notification,
        int failedAttempts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        PayHereProcessingOutcome outcome;

        try
        {
            outcome = await ApplyAsync(eventId, notification, cancellationToken);

            var now = clock.UtcNow.UtcDateTime;

            if (outcome.Kind == PayHereOutcomeKind.Applied)
            {
                await events.MarkProcessedAsync(eventId, now, cancellationToken);
            }
            else
            {
                await events.MarkIgnoredAsync(eventId, now, cancellationToken);
            }

            return outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return await RecordFailureAsync(eventId, failedAttempts + 1, exception, cancellationToken);
        }
    }

    private async Task<PayHereProcessingOutcome> RecordFailureAsync(
        long eventId,
        int failures,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow.UtcDateTime;
        var delay = RetryDelay(failures);
        var text = $"{exception.GetType().Name}: {exception.Message}";

        try
        {
            await events.MarkFailedAsync(eventId, text, delay is { } wait ? now + wait : null, cancellationToken);

            if (delay is null)
            {
                await alerts.RaiseAsync(PayHereAlerts.NotificationNotApplied(eventId, failures, now), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception bookkeeping)
        {
            // The event stays Received with the retry time it was stored with, so the sweep still finds it.
            return new PayHereProcessingOutcome(
                PayHereOutcomeKind.Failed,
                "Applying the notification failed, and so did recording that it failed; the sweep will find the event.",
                new AggregateException(exception, bookkeeping));
        }

        return new PayHereProcessingOutcome(
            delay is null ? PayHereOutcomeKind.GaveUp : PayHereOutcomeKind.Failed,
            delay is null
                ? $"Applying the notification failed {failures} times; no further attempt will be made."
                : $"Applying the notification failed (attempt {failures}); it will be retried.",
            exception);
    }

    private async Task<PayHereProcessingOutcome> ApplyAsync(
        long eventId,
        PayHereNotification notification,
        CancellationToken cancellationToken)
    {
        if (!PayHereOrderReference.TryParse(notification.OrderId, out var orderNumber, out var paymentId))
        {
            return new PayHereProcessingOutcome(
                PayHereOutcomeKind.Unmatched,
                "The order ID is not one this shop issued.");
        }

        var payment = await payments.FindAsync(orderNumber, paymentId, cancellationToken);

        if (payment is null)
        {
            return new PayHereProcessingOutcome(
                PayHereOutcomeKind.Unmatched,
                "No PayHere payment of this shop matches the order ID.");
        }

        await events.LinkAsync(eventId, payment.PaymentId, payment.OrderId, cancellationToken);

        var now = clock.UtcNow.UtcDateTime;

        return notification.Status switch
        {
            PayHereStatus.Success => await ApplySuccessAsync(notification, payment, now, cancellationToken),
            PayHereStatus.Pending => await ApplyPendingAsync(notification, payment, now, cancellationToken),
            PayHereStatus.Cancelled => await ApplyFailedAsync(
                notification, payment, PayHereFailureCodes.Cancelled, "PayHere reported the payment as cancelled.", now, cancellationToken),
            PayHereStatus.Failed => await ApplyFailedAsync(
                notification, payment, PayHereFailureCodes.Failed, "PayHere reported the payment as failed.", now, cancellationToken),
            PayHereStatus.ChargedBack => await ApplyChargebackAsync(notification, payment, now, cancellationToken),
            _ => new PayHereProcessingOutcome(
                PayHereOutcomeKind.Ignored,
                "The status code is not one this shop acts on."),
        };
    }

    private async Task<PayHereProcessingOutcome> ApplySuccessAsync(
        PayHereNotification notification,
        PayHerePayment payment,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // PayHere's own id for the payment, written before the settlement so a refund made in the PayHere
        // portal can be found from our side whatever the settlement decides. Only on a payment that has not
        // settled: a second charge must not overwrite the first one's id.
        await payments.RecordReferenceAsync(payment.PaymentId, notification.PaymentId, now, cancellationToken);

        var amount = notification.AmountValue;

        PaymentSettlementResult result;

        if (amount is { } confirmed)
        {
            result = await settlements.SettleAsync(
                new PaymentSettlement(
                    payment.PaymentId,
                    PaymentProviderCodes.PayHere,
                    confirmed,
                    notification.PayhereCurrency,
                    now,
                    ProviderTransactionId: notification.PaymentId),
                cancellationToken);
        }
        else
        {
            // Signed and still not a number. Cannot happen from PayHere; if it ever does, the money is on
            // record as an exception rather than guessed at.
            result = await settlements.RouteToExceptionAsync(
                new PaymentExceptionRouting(
                    payment.PaymentId,
                    PaymentProviderCodes.PayHere,
                    PaymentExceptionReasons.AmountMismatch,
                    now,
                    ConfirmedAmount: null,
                    notification.PayhereCurrency,
                    ProviderTransactionId: notification.PaymentId),
                cancellationToken);
        }

        switch (result)
        {
            case PaymentSettlementResult.Settled:
                return new PayHereProcessingOutcome(PayHereOutcomeKind.Applied, "The order was paid.");

            case PaymentSettlementResult.CapturedRequiresRefund routed:
                return new PayHereProcessingOutcome(
                    PayHereOutcomeKind.Applied,
                    $"The money was put on record as a payment exception ({routed.Reason}).");

            case PaymentSettlementResult.AlreadyHandled handled:
                return await ApplyDuplicateSuccessAsync(
                    notification, payment, handled.PaymentStatus, amount, now, cancellationToken);

            default:
                return new PayHereProcessingOutcome(
                    PayHereOutcomeKind.Unmatched,
                    "The settlement store does not know the payment.");
        }
    }

    /// <summary>
    /// A success for a payment that has already settled, or been routed to the exception path.
    /// <para>
    /// <b>The same PayHere payment ID is a duplicate</b> and is ignored. <b>A different one is a second
    /// charge</b> — the customer paid twice at PayHere, in two tabs — and ignoring it would leave real money
    /// with no record on our side, so it is put on the dashboard as the exception it is: the payment is
    /// already the settling one and stays that way, and a person refunds the extra.
    /// </para>
    /// </summary>
    private async Task<PayHereProcessingOutcome> ApplyDuplicateSuccessAsync(
        PayHereNotification notification,
        PayHerePayment payment,
        PaymentStatus settledStatus,
        decimal? amount,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // Read again: the pre-read above was before the settlement call, and the transaction id is written by it.
        var current = await payments.FindAsync(payment.OrderNumber, payment.PaymentId, cancellationToken);
        var known = current?.ProviderTransactionId ?? payment.ProviderTransactionId;

        if (known is null || string.Equals(known, notification.PaymentId, StringComparison.Ordinal))
        {
            return new PayHereProcessingOutcome(
                PayHereOutcomeKind.Ignored,
                "The payment had already been handled; this is a duplicate.");
        }

        await alerts.RaiseAsync(
            PaymentExceptionAlert.For(
                new PaymentExceptionFacts(
                    payment.PaymentId,
                    PaymentProviderCodes.PayHere,
                    payment.OrderPublicId,
                    payment.OrderNumber,

                    // "The order was already paid" is true when the payment that was handled is the settling
                    // one (or has been refunded since). When it was parked as an exception instead, the order
                    // was *not* paid, and saying so on a banner would send a person looking for a payment
                    // that does not exist.
                    settledStatus is PaymentStatus.CapturedRequiresRefund
                        ? PaymentExceptionReasons.OrderNotPayable
                        : PaymentExceptionReasons.OrderAlreadyPaid,
                    payment.Amount,
                    PayHereHash.Currency,
                    amount,
                    notification.PayhereCurrency),
                now),
            cancellationToken);

        return new PayHereProcessingOutcome(
            PayHereOutcomeKind.Applied,
            "A second PayHere payment arrived for an order that was already paid; an alert was raised.");
    }

    private async Task<PayHereProcessingOutcome> ApplyPendingAsync(
        PayHereNotification notification,
        PayHerePayment payment,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var current = await settings.ReadAsync(cancellationToken);

        var transition = await payments.MarkPendingAsync(
            payment.PaymentId,
            payment.OrderId,
            notification.PaymentId,
            current.PendingMaxDays,
            now,
            cancellationToken);

        return transition == PayHereTransition.Applied
            ? new PayHereProcessingOutcome(PayHereOutcomeKind.Applied, "The payment is pending and the hold was extended.")
            : new PayHereProcessingOutcome(
                PayHereOutcomeKind.Ignored,
                "The payment is no longer awaiting PayHere; the pending notice changes nothing.");
    }

    private async Task<PayHereProcessingOutcome> ApplyFailedAsync(
        PayHereNotification notification,
        PayHerePayment payment,
        string failureCode,
        string fallbackMessage,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var current = await settings.ReadAsync(cancellationToken);

        var result = await payments.MarkFailedAsync(
            new PayHereFailure(
                payment.PaymentId,
                payment.OrderId,
                notification.PaymentId,
                failureCode,
                notification.FailureMessage(fallbackMessage),
                current.ExpiryMinutes,
                now),
            cancellationToken);

        if (result.Transition != PayHereTransition.Applied)
        {
            return new PayHereProcessingOutcome(
                PayHereOutcomeKind.Ignored,
                "The payment is no longer open; the failure notice changes nothing.");
        }

        var detail = string.Create(
            CultureInfo.InvariantCulture,
            $"The payment failed ({failureCode}).{(result.WasPending ? " The pending hold was cut back." : string.Empty)}"
            + $"{(result.ReplacementOpened ? " A new attempt was opened." : string.Empty)}");

        return new PayHereProcessingOutcome(PayHereOutcomeKind.Applied, detail);
    }

    private async Task<PayHereProcessingOutcome> ApplyChargebackAsync(
        PayHereNotification notification,
        PayHerePayment payment,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await alerts.RaiseAsync(
            PayHereAlerts.Chargeback(payment, notification.AmountValue, notification.PayhereCurrency, now),
            cancellationToken);

        return new PayHereProcessingOutcome(
            PayHereOutcomeKind.Applied,
            "A chargeback was reported; an alert was raised and nothing else changed.");
    }
}
