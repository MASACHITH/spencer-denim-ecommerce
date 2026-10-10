using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Ops.Alerts.Abstractions;
using SpencerDenim.Application.Sales.Payments.Abstractions;

namespace SpencerDenim.Application.Sales.Payments.PayHere;

/// <summary>A stored PayHere event that is due to be applied again.</summary>
/// <param name="EventId"><c>sales.PaymentEvents.Id</c>.</param>
/// <param name="PayloadJson">What <see cref="PayHereNotification.ToStoredJson"/> wrote.</param>
/// <param name="AttemptCount">How many attempts have already been counted against the event.</param>
public sealed record PayHereDueEvent(long EventId, string PayloadJson, int AttemptCount);

/// <summary>
/// Finds and claims the PayHere events a first delivery did not finish (doc 11 P8b step 5).
/// </summary>
public interface IPayHereEventRetryStore
{
    /// <summary>
    /// Up to <paramref name="take"/> PayHere events that are Received or Failed and whose retry time has
    /// come, oldest first.
    /// </summary>
    Task<IReadOnlyList<PayHereDueEvent>> FindDueAsync(DateTime nowUtc, int take, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the event for this caller by moving its retry time to <paramref name="nowUtc"/> plus
    /// <paramref name="lease"/>. <see langword="true"/> for exactly one of any number of callers racing for
    /// it, which is what lets several instances run the sweep without applying one event twice at once; a
    /// caller that dies holding the event simply lets the lease run out.
    /// </summary>
    Task<bool> ClaimAsync(long eventId, DateTime nowUtc, TimeSpan lease, CancellationToken cancellationToken);
}

/// <summary>What one pass of the sweep did.</summary>
/// <param name="Found">Events that were due.</param>
/// <param name="Claimed">Of those, how many this pass took.</param>
/// <param name="Applied">Of those, how many were applied or ignored.</param>
/// <param name="Failed">Of those, how many failed again (or were abandoned).</param>
public sealed record PayHereSweepResult(int Found, int Claimed, int Applied, int Failed);

/// <summary>
/// <c>RetryPaymentEvents</c> for PayHere (doc 11 P8b step 5): applies the verified notifications whose first
/// delivery died between being stored and being applied, or threw while applying.
/// <para>
/// <b>PayHere is told 200 as soon as the event is stored</b> and so never resends; if applying it then
/// failed — the database went away for a minute — this is the only thing that finishes the job. It works
/// because applying is idempotent, and because the event holds everything needed (the allow-listed fields):
/// the signature was verified once, before the event was stored, and is not checked again.
/// </para>
/// <para>
/// <b>The lease is five minutes</b>, longer than an apply takes and shorter than the first rung of the
/// backoff would matter: a pass that dies holding an event delays it by the lease and no more.
/// </para>
/// </summary>
public sealed class PayHereRetrySweep(
    IPayHereEventRetryStore store,
    PayHereNotificationProcessor processor,
    IPaymentEventStore events,
    IAdminAlertWriter alerts,
    IClock clock)
{
    /// <summary>How many events one pass takes.</summary>
    public const int BatchSize = 20;

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    /// <summary>One pass. Does not throw for an event that fails to apply; that is recorded on the event.</summary>
    public async Task<PayHereSweepResult> RunAsync(CancellationToken cancellationToken)
    {
        var due = await store.FindDueAsync(clock.UtcNow.UtcDateTime, BatchSize, cancellationToken);

        var claimed = 0;
        var applied = 0;
        var failed = 0;

        foreach (var item in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await store.ClaimAsync(item.EventId, clock.UtcNow.UtcDateTime, Lease, cancellationToken))
            {
                continue;
            }

            claimed++;

            var notification = PayHereNotification.FromStoredJson(item.PayloadJson);

            if (notification is null)
            {
                // A verified event whose stored text cannot be read back. Nothing will make it readable, so
                // it is not retried; it is left for a person, and an alert says so.
                var now = clock.UtcNow.UtcDateTime;

                await events.MarkFailedAsync(
                    item.EventId,
                    "The stored payload is not a PayHere notification.",
                    retryAtUtc: null,
                    cancellationToken);

                await alerts.RaiseAsync(
                    PayHereAlerts.NotificationNotApplied(item.EventId, item.AttemptCount + 1, now),
                    cancellationToken);

                failed++;
                continue;
            }

            var outcome = await processor.ProcessAsync(
                item.EventId,
                notification,
                item.AttemptCount,
                cancellationToken);

            if (outcome.Kind is PayHereOutcomeKind.Failed or PayHereOutcomeKind.GaveUp)
            {
                failed++;
            }
            else
            {
                applied++;
            }
        }

        return new PayHereSweepResult(due.Count, claimed, applied, failed);
    }
}
