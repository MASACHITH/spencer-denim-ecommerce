using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Payments;

/// <summary>What one pass of <c>RetryPaymentEvents</c> did.</summary>
/// <param name="Claimed">Events picked up.</param>
/// <param name="Processed">Applied.</param>
/// <param name="Ignored">Found to need nothing.</param>
/// <param name="Failed">Could not be applied this time; each has its next retry time (or none, once the attempts run out).</param>
public sealed record PaymentEventRetryReport(int Claimed, int Processed, int Ignored, int Failed);

/// <summary>
/// One pass of the <c>RetryPaymentEvents</c> job (doc 08 §3.5, §3.8): for each provider that has a processor,
/// claim the stored webhook events whose processing did not finish and apply them again.
/// <para>
/// <b>Provider-neutral.</b> It asks every registered <see cref="IPaymentEventProcessor"/> for its own events
/// and knows nothing else about them, so a second gateway's notifications are swept by the same job the day
/// it registers a processor. The processor is the same object the webhook endpoint calls on a fresh event, so
/// a retry is the original code path run again — and every apply is a conditional statement, so running it
/// twice, or racing a late first attempt, cannot settle an order twice.
/// </para>
/// <para>
/// <b>A failure on one event does not stop the pass</b> and is not allowed to leave it unmarked: it is
/// counted and given its next retry time by <see cref="PaymentEventRetryPolicy"/>, and once the attempts are
/// spent it keeps no retry time, so <c>IX_PaymentEvents_Retry</c> stops selecting it and a person has to look.
/// </para>
/// </summary>
internal sealed partial class PaymentEventRetryRunner(
    IEnumerable<IPaymentEventProcessor> processors,
    IDuePaymentEvents due,
    IPaymentEventStore events,
    IClock clock,
    ILogger<PaymentEventRetryRunner> logger)
{
    /// <summary>How many events one provider's claim takes per pass.</summary>
    public const int BatchSize = 20;

    /// <summary>
    /// How long a claim holds an event. Longer than any apply takes, so an overlapping pass leaves it alone;
    /// short enough that a worker that died does not delay the event for long.
    /// </summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    public async Task<PaymentEventRetryReport> RunOnceAsync(CancellationToken cancellationToken)
    {
        int claimed = 0, processedCount = 0, ignored = 0, failed = 0;

        foreach (var processor in processors)
        {
            var batch = await due.ClaimDueAsync(
                processor.ProviderCode,
                clock.UtcNow.UtcDateTime,
                Lease,
                BatchSize,
                cancellationToken);

            foreach (var paymentEvent in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                claimed++;

                var now = clock.UtcNow.UtcDateTime;

                try
                {
                    var outcome = await processor.ProcessAsync(paymentEvent, cancellationToken);

                    if (outcome == PaymentEventOutcome.Processed)
                    {
                        await events.MarkProcessedAsync(paymentEvent.EventId, now, cancellationToken);
                        processedCount++;
                    }
                    else
                    {
                        await events.MarkIgnoredAsync(paymentEvent.EventId, now, cancellationToken);
                        ignored++;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failed++;

                    var attempts = paymentEvent.AttemptCount + 1;
                    var next = PaymentEventRetryPolicy.NextAttemptAtUtc(attempts, now);

                    LogEventFailed(logger, paymentEvent.ProviderCode, paymentEvent.EventType, paymentEvent.EventId, attempts, next is null, exception);

                    await events.MarkFailedAsync(
                        paymentEvent.EventId,
                        $"{exception.GetType().Name}: {Clip(SqlErrorRedaction.SafeMessage(exception))}",
                        next,
                        CancellationToken.None);
                }
            }
        }

        return new PaymentEventRetryReport(claimed, processedCount, ignored, failed);
    }

    private static string Clip(string text) => text.Length <= 300 ? text : text[..300];

    [LoggerMessage(
        EventId = 3560,
        Level = LogLevel.Error,
        Message = "Retrying the {Provider} event {EventType} (stored event {EventId}) failed on attempt "
            + "{Attempts}. Gave up: {GaveUp}.")]
    private static partial void LogEventFailed(
        ILogger logger,
        string provider,
        string eventType,
        long eventId,
        int attempts,
        bool gaveUp,
        Exception exception);
}
