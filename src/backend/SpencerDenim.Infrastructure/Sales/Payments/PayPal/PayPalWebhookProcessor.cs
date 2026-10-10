using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Payments;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.PayPal;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Sales.Payments.PayPal;

/// <summary>
/// The webhook endpoint's logic (doc 08 §3.5 DB13, §4.2; doc 11 P8 step 7) <b>and</b> the code the retry job
/// applies a stored event with — one class, so a fresh delivery and a retry of it are the same code path.
/// <para>
/// <b>The order is the contract</b> (<see cref="IPaymentEventStore"/>):
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Verify the signature first, and store nothing before it passes.</b> An unverified request must never
/// consume an idempotency key, or anybody who guesses a real event id can get the genuine delivery ignored
/// as a duplicate. An error while verifying is <see cref="PayPalWebhookResult.VerificationUnavailable"/> (503,
/// nothing stored, PayPal resends); a signature that does not verify is
/// <see cref="PayPalWebhookResult.InvalidSignature"/> (400, nothing stored, a log line only).
/// </description></item>
/// <item><description>
/// <b>Record the verified event once.</b> A repeat answers <see cref="PayPalWebhookResult.Duplicate"/> and
/// stops: the retry job, not this request, owns an event whose first processing did not finish.
/// </description></item>
/// <item><description>
/// <b>Apply it</b> through <see cref="PayPalCaptureApplier"/> — the same decision table and the same stores
/// the capture response and the recovery job use — and say what became of it. A processing failure leaves
/// the event Failed with a retry time for the sweep and <b>still answers 200</b>: PayPal's redelivery would
/// be a duplicate of an event we already hold, so telling it to try again helps nobody.
/// </description></item>
/// </list>
/// <para>
/// <b>Out-of-order and replayed events are harmless by construction</b>: a COMPLETED that beats the capture
/// response settles the order and the response then finds it already settled; a DENIED after a COMPLETED is
/// ignored by <see cref="PayPalApplyDecision"/>; the same event twice is one stored row.
/// </para>
/// </summary>
internal sealed partial class PayPalWebhookProcessor(
    IPayPalGateway gateway,
    IPaymentEventStore events,
    IPayPalPaymentStore payments,
    PayPalCaptureApplier applier,
    IClock clock,
    ILogger<PayPalWebhookProcessor> logger) : IPayPalWebhookHandler, IPaymentEventProcessor
{
    /// <summary><c>sales.PaymentEvents.EventType</c> and <c>ProviderEventId</c> are <c>varchar(100)</c>.</summary>
    private const int ColumnLimit = 100;

    public string ProviderCode => PaymentProviderCodes.PayPal;

    public async Task<PayPalWebhookResult> HandleAsync(
        PayPalWebhookHeaders headers,
        string rawBody,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rawBody);

        // A body that cannot be an event is refused without troubling PayPal or the database.
        if (!PayPalWebhookEventParser.TryParse(rawBody, out var webhookEvent) || webhookEvent is null)
        {
            LogMalformed(logger);
            return PayPalWebhookResult.Malformed;
        }

        // 1. Verify first. Nothing is stored before this passes.
        switch (await gateway.VerifyWebhookAsync(headers, rawBody, cancellationToken))
        {
            case PayPalWebhookVerification.Verified:
                break;

            case PayPalWebhookVerification.Invalid:
                // Doc 08 DB13 step 3: "return 400 and log to Serilog only". The transmission id is PayPal's
                // header and is safe to log; the body is not logged.
                LogInvalidSignature(logger, headers.TransmissionId);
                return PayPalWebhookResult.InvalidSignature;

            default:
                LogVerificationFailed(logger);
                return PayPalWebhookResult.VerificationUnavailable;
        }

        var key = KeyFor(webhookEvent);

        if (key is null
            || webhookEvent.EventType.Length > ColumnLimit
            || webhookEvent.EventId is { Length: > ColumnLimit })
        {
            LogMalformed(logger);
            return PayPalWebhookResult.Malformed;
        }

        // 2. Record it, once.
        var now = clock.UtcNow.UtcDateTime;

        var receipt = await events.RecordAsync(
            new PaymentEventRecord(
                PaymentProviderCodes.PayPal,
                PaymentEventSource.VerifiedWebhook,
                webhookEvent.EventType,
                webhookEvent.EventId,
                key,
                rawBody,
                now),
            cancellationToken);

        if (!receipt.IsNew)
        {
            LogDuplicate(logger, webhookEvent.EventType, receipt.EventId);
            return PayPalWebhookResult.Duplicate;
        }

        // 3. Apply it and say what became of it.
        await ApplyAndMarkAsync(receipt.EventId, webhookEvent, attemptsCounted: 0, now, cancellationToken);

        return PayPalWebhookResult.Accepted;
    }

    /// <summary>
    /// The retry job's entry: applies a stored event. Throws when it could not finish, so the job counts the
    /// attempt and schedules the next.
    /// </summary>
    public async Task<PaymentEventOutcome> ProcessAsync(DuePaymentEvent paymentEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paymentEvent);

        if (!PayPalWebhookEventParser.TryParse(paymentEvent.PayloadJson, out var webhookEvent) || webhookEvent is null)
        {
            // Too large to have been stored, or not an event: it cannot be reprocessed from the log.
            throw new InvalidOperationException("The stored PayPal event cannot be read, so it cannot be applied.");
        }

        return await ApplyAsync(paymentEvent.EventId, webhookEvent, cancellationToken);
    }

    /// <summary>
    /// Applies the event and marks the row. Never throws for a processing failure: that is what the row's
    /// Failed status and retry time are for.
    /// </summary>
    private async Task ApplyAndMarkAsync(
        long eventId,
        PayPalWebhookEvent webhookEvent,
        int attemptsCounted,
        DateTime now,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await ApplyAsync(eventId, webhookEvent, cancellationToken);

            if (outcome == PaymentEventOutcome.Processed)
            {
                await events.MarkProcessedAsync(eventId, now, cancellationToken);
            }
            else
            {
                await events.MarkIgnoredAsync(eventId, now, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogProcessingFailed(logger, webhookEvent.EventType, eventId, exception);

            await events.MarkFailedAsync(
                eventId,
                $"{exception.GetType().Name}: {Clip(exception.Message)}",
                PaymentEventRetryPolicy.NextAttemptAtUtc(attemptsCounted + 1, now),
                CancellationToken.None);
        }
    }

    private async Task<PaymentEventOutcome> ApplyAsync(
        long eventId,
        PayPalWebhookEvent webhookEvent,
        CancellationToken cancellationToken)
    {
        // An event type this build does not act on — an order event, something new: recorded, nothing more.
        if (webhookEvent.CaptureStatus is not { } status)
        {
            return PaymentEventOutcome.Ignored;
        }

        // A refund or a reversal is kept for reconciliation and changes nothing here (doc 08 §4.3's
        // dashboard-refund handling belongs to the refund task). It is said in the log, because nobody else
        // will be told.
        if (status is PayPalCaptureStatus.Refunded or PayPalCaptureStatus.PartiallyRefunded or PayPalCaptureStatus.Reversed)
        {
            LogRefundOrReversal(logger, webhookEvent.EventType, eventId);
            return PaymentEventOutcome.Ignored;
        }

        var payment = await payments.FindPaymentForEventAsync(
            webhookEvent.ResourceId,
            webhookEvent.RelatedOrderId,
            PayPalRequestIds.PaymentIdFromCustomId(webhookEvent.CustomId),
            cancellationToken);

        if (payment is null)
        {
            // Not an error to give up on at once: the order id may not be stored yet when a very fast
            // notification arrives. The retry job tries again; after its attempts a person looks.
            throw new InvalidOperationException("No PayPal payment matches this event.");
        }

        await events.LinkAsync(eventId, payment.PaymentId, payment.OrderId, cancellationToken);

        var result = await applier.ApplyAsync(
            new PayPalCaptureFacts(
                payment.PaymentId,
                webhookEvent.ResourceId,
                status,
                webhookEvent.Amount,
                webhookEvent.Currency,
                PayerId: null,
                webhookEvent.Reason),
            cancellationToken);

        return result switch
        {
            PayPalApplyResult.Settled
                or PayPalApplyResult.CapturedRequiresRefund
                or PayPalApplyResult.Pending
                or PayPalApplyResult.Declined => PaymentEventOutcome.Processed,

            PayPalApplyResult.Duplicate or PayPalApplyResult.Recorded => PaymentEventOutcome.Ignored,

            _ => throw new InvalidOperationException("The PayPal payment this event names was not found."),
        };
    }

    /// <summary>
    /// <c>PayPal:{EventId}</c>, or <c>PayPal:{Type}:{ResourceId}</c> when the body has no event id (doc 08
    /// §3.5). Null when it has neither, and so cannot be told apart from any other event.
    /// </summary>
    private static string? KeyFor(PayPalWebhookEvent webhookEvent)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(webhookEvent.EventId))
            {
                return PaymentEventKey.ForEvent(PaymentProviderCodes.PayPal, webhookEvent.EventId);
            }

            return string.IsNullOrWhiteSpace(webhookEvent.ResourceId)
                ? null
                : PaymentEventKey.ForResource(PaymentProviderCodes.PayPal, webhookEvent.EventType, webhookEvent.ResourceId);
        }
        catch (ArgumentException)
        {
            // Longer than the column: not a key we can store.
            return null;
        }
    }

    private static string Clip(string text) => text.Length <= 300 ? text : text[..300];

    [LoggerMessage(
        EventId = 3550,
        Level = LogLevel.Warning,
        Message = "A PayPal webhook was rejected: the signature did not verify (transmission {TransmissionId}). "
            + "Nothing was stored.")]
    private static partial void LogInvalidSignature(ILogger logger, string transmissionId);

    [LoggerMessage(
        EventId = 3551,
        Level = LogLevel.Warning,
        Message = "A PayPal webhook could not be verified because PayPal could not be asked (it timed out or "
            + "errored, or the shop's PayPal credentials and webhook ID are not set). 503 was returned and "
            + "nothing was stored, so PayPal will send it again.")]
    private static partial void LogVerificationFailed(ILogger logger);

    [LoggerMessage(
        EventId = 3552,
        Level = LogLevel.Warning,
        Message = "A PayPal webhook was refused: the body is not a PayPal event. Nothing was stored.")]
    private static partial void LogMalformed(ILogger logger);

    [LoggerMessage(
        EventId = 3553,
        Level = LogLevel.Information,
        Message = "PayPal event {EventType} was already stored as event {EventId}; nothing was done a second time.")]
    private static partial void LogDuplicate(ILogger logger, string eventType, long eventId);

    [LoggerMessage(
        EventId = 3554,
        Level = LogLevel.Error,
        Message = "Applying the PayPal event {EventType} (stored event {EventId}) failed. It is kept as Failed "
            + "and the retry job will try again.")]
    private static partial void LogProcessingFailed(ILogger logger, string eventType, long eventId, Exception exception);

    [LoggerMessage(
        EventId = 3555,
        Level = LogLevel.Warning,
        Message = "PayPal reported {EventType} (stored event {EventId}), which is recorded and not applied: "
            + "refunds and reversals made in the PayPal dashboard are not yet handled (doc 08 §4.3). Check "
            + "PayPal and adjust the order and stock by hand if the goods came back.")]
    private static partial void LogRefundOrReversal(ILogger logger, string eventType, long eventId);
}
