namespace SpencerDenim.Application.Sales.Payments.Abstractions;

/// <summary>
/// The <c>RetryPaymentEvents</c> job's read (doc 08 §3.8 <c>ops.JobLeases</c> job names; §3.5
/// <c>IX_PaymentEvents_Retry</c>): the stored provider events whose processing did not finish and whose time
/// has come. <b>Provider-neutral</b> — the provider is a parameter — so a second gateway's notifications are
/// swept by the same job.
/// <para>
/// <b>Claiming is the guard, because there is no <c>ops.JobLeases</c> table in this build.</b> A claim moves
/// each row's <c>NextAttemptAtUtc</c> forward by a lease in the same statement that selects it, so a second
/// sweep that overlaps the first — or a second instance — finds nothing due and does nothing, and a worker
/// that dies mid-event leaves a row that becomes due again when its lease runs out. Doing the event twice is
/// harmless anyway (every apply is a conditional statement), so the claim is about not wasting the work
/// rather than about correctness.
/// </para>
/// <para>
/// Only <b>verified-webhook</b> events are ever returned. The log also holds our own API responses and the
/// buyer's return redirect, which are records of what happened and not instructions to do anything.
/// </para>
/// </summary>
public interface IDuePaymentEvents
{
    /// <summary>
    /// Claims up to <paramref name="take"/> webhook events of <paramref name="providerCode"/> that are
    /// Received or Failed with a retry time at or before <paramref name="nowUtc"/>, oldest first, pushing
    /// each one's retry time to <c>nowUtc + lease</c>.
    /// </summary>
    Task<IReadOnlyList<DuePaymentEvent>> ClaimDueAsync(
        string providerCode,
        DateTime nowUtc,
        TimeSpan lease,
        int take,
        CancellationToken cancellationToken);
}

/// <summary>A stored provider event that is due for another go.</summary>
/// <param name="EventId"><c>sales.PaymentEvents.Id</c>.</param>
/// <param name="ProviderCode">Whose event it is.</param>
/// <param name="EventType">The provider's event type.</param>
/// <param name="PayloadJson">
/// The stored notification. If it was too large to store, this is a marker object and the event cannot be
/// reprocessed from the log (see <c>PaymentEventPayload</c>).
/// </param>
/// <param name="AttemptCount">How many attempts have been counted so far.</param>
/// <param name="ReceivedAtUtc">When it was stored.</param>
public sealed record DuePaymentEvent(
    long EventId,
    string ProviderCode,
    string EventType,
    string PayloadJson,
    short AttemptCount,
    DateTime ReceivedAtUtc);

/// <summary>What became of a stored event that was processed without throwing.</summary>
public enum PaymentEventOutcome
{
    /// <summary>It changed something, or was recorded as it should be.</summary>
    Processed = 0,

    /// <summary>Recognised and deliberately not acted on (a duplicate, an event out of order, a type this build does not handle).</summary>
    Ignored = 1,
}

/// <summary>
/// One provider's way of applying a stored notification — the code the webhook endpoint calls on a fresh
/// event and the retry job calls on a stored one, so the two are the same code path.
/// <para>
/// A processor <b>throws</b> when it could not finish (the database was unreachable, the payment it names
/// is not recorded yet); the caller counts the attempt and schedules the next with
/// <see cref="PaymentEventRetryPolicy"/>.
/// </para>
/// </summary>
public interface IPaymentEventProcessor
{
    /// <summary>The <c>sales.PaymentProviders.Code</c> whose events this applies.</summary>
    string ProviderCode { get; }

    /// <summary>Applies the stored event.</summary>
    Task<PaymentEventOutcome> ProcessAsync(DuePaymentEvent paymentEvent, CancellationToken cancellationToken);
}

/// <summary>
/// When a failed event is tried again, and when to stop: 1, 5, 15, 60 and 240 minutes, then every 12 hours,
/// for ten attempts — roughly three days, which is how long PayPal itself keeps redelivering a webhook it
/// could not deliver. After that the row stays Failed with no retry time, which
/// <c>IX_PaymentEvents_Retry</c> never selects, so a person has to look.
/// </summary>
public static class PaymentEventRetryPolicy
{
    /// <summary>The attempts after which the job stops.</summary>
    public const int MaxAttempts = 10;

    private static readonly TimeSpan[] Gaps =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(4),
    ];

    private static readonly TimeSpan SteadyGap = TimeSpan.FromHours(12);

    /// <summary>
    /// The next attempt after <paramref name="attemptsCounted"/> have been counted (including the one that
    /// just failed), or null to stop.
    /// </summary>
    public static DateTime? NextAttemptAtUtc(int attemptsCounted, DateTime nowUtc)
    {
        if (attemptsCounted >= MaxAttempts)
        {
            return null;
        }

        var index = Math.Max(attemptsCounted - 1, 0);

        return nowUtc + (index < Gaps.Length ? Gaps[index] : SteadyGap);
    }
}
