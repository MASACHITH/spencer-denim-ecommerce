using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Payments.Abstractions;

/// <summary>
/// The provider event log, <c>sales.PaymentEvents</c> (doc 08 §3.5, DB13): record each event exactly once,
/// then say what became of it.
/// <para>
/// <b>The order of operations is the contract, and it is the same for every gateway.</b>
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Verify the provider's signature first</b>, and store nothing before it passes. An unverified request
/// must never consume an idempotency key, or an attacker who guesses a real event id can get the genuine
/// delivery ignored as a duplicate. This port cannot enforce that — it is handed a payload, not a
/// request — so it is stated here, where the next implementer reads it before writing the endpoint.
/// </description></item>
/// <item><description>
/// <see cref="RecordAsync"/> the verified event. A <see cref="PaymentEventReceipt.IsNew"/> of
/// <see langword="false"/> is a redelivery: <b>answer 200 and stop</b>. (If its
/// <see cref="PaymentEventReceipt.ProcessingStatus"/> is still Received or Failed, the retry sweep owns
/// it; the endpoint does not process it a second time on the provider's say-so.)
/// </description></item>
/// <item><description>
/// Apply it — <see cref="IOrderSettlementStore"/> for a confirmed payment — and then
/// <see cref="MarkProcessedAsync"/>, <see cref="MarkIgnoredAsync"/> or <see cref="MarkFailedAsync"/>. The
/// outcome is its own statement after the apply's transaction and not part of it, so a crash between the
/// two leaves the event Received with a retry time, which the sweep finds (<c>IX_PaymentEvents_Retry</c>)
/// and re-applies; the apply is idempotent, so doing it twice is safe.
/// </description></item>
/// </list>
/// <para>
/// <b>Never store a secret.</b> The payload is the provider's notification and nothing else — no request
/// headers, no signature header, no credential. <see cref="PaymentEventPayload"/> overwrites
/// secret-named properties as a net and caps the size, but a net is not a licence.
/// </para>
/// </summary>
public interface IPaymentEventStore
{
    /// <summary>
    /// Stores the event once. Safe to call concurrently for the same key: exactly one caller is told
    /// <see cref="PaymentEventReceipt.IsNew"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A field is empty or too long for its column, or the payload is not a JSON object or array
    /// (<see cref="PaymentEventPayload.Prepare"/>). These are programming errors in the caller and are
    /// reported before the database is asked, rather than as a constraint violation.
    /// </exception>
    Task<PaymentEventReceipt> RecordAsync(PaymentEventRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// The event was applied. Counts the attempt, clears any retry time and the last error.
    /// </summary>
    /// <returns>
    /// True if this call moved it; false if the event was not found or had already been
    /// Processed or Ignored (a second worker must not rewrite the first one's outcome).
    /// </returns>
    Task<bool> MarkProcessedAsync(long eventId, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// The event needed no action — a duplicate of something already settled (doc 08 §4.2 steps 3.4 and 6),
    /// or a type this build does not handle. Counts the attempt like <see cref="MarkProcessedAsync"/>.
    /// </summary>
    Task<bool> MarkIgnoredAsync(long eventId, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Applying the event threw. Counts the attempt and records why.
    /// </summary>
    /// <param name="eventId">The event.</param>
    /// <param name="failure">
    /// What went wrong, for a human. Clipped to the column. <b>Not a place for a secret or a customer's
    /// details</b>: an exception type and a short message.
    /// </param>
    /// <param name="retryAtUtc">
    /// When the sweep should look again, or <see langword="null"/> to stop retrying — the event is left
    /// Failed with no retry time, which <c>IX_PaymentEvents_Retry</c> never selects, so a human has to look.
    /// The backoff policy is the caller's: it depends on the provider and on how many attempts have been
    /// made, which the store counts but does not interpret.
    /// </param>
    Task<bool> MarkFailedAsync(
        long eventId,
        string failure,
        DateTime? retryAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Attaches the payment and/or order the event turned out to be about. For an event stored before it
    /// could be attributed (doc 08 DB13: such an event is stored, not dropped) and attributed on a retry.
    /// A null argument leaves that column as it is.
    /// </summary>
    Task<bool> LinkAsync(long eventId, int? paymentId, int? orderId, CancellationToken cancellationToken);
}

/// <summary>One verified provider event, ready to be stored.</summary>
/// <param name="ProviderCode"><c>sales.PaymentProviders.Code</c>.</param>
/// <param name="Source">
/// Where the event came from. <see cref="PaymentEventSource.VerifiedWebhook"/> only after the signature
/// check; a browser return is <see cref="PaymentEventSource.ReturnRedirect"/> and never proof of payment.
/// </param>
/// <param name="EventType">The provider's own type name, <c>varchar(100)</c>.</param>
/// <param name="ProviderEventId">The provider's event id when it has one, <c>varchar(100)</c>.</param>
/// <param name="IdempotencyKey">
/// See <see cref="PaymentEventKey"/>. Unique across the table: the same key is the same event.
/// </param>
/// <param name="PayloadJson">
/// The notification as a JSON object or array. See <see cref="PaymentEventPayload"/> for what is done to it.
/// </param>
/// <param name="ReceivedAtUtc">One instant for the row, taken by the caller from <c>IClock</c>.</param>
/// <param name="PaymentId">The payment it is about, if already known.</param>
/// <param name="OrderId">The order it is about, if already known.</param>
public sealed record PaymentEventRecord(
    string ProviderCode,
    PaymentEventSource Source,
    string EventType,
    string? ProviderEventId,
    string IdempotencyKey,
    string PayloadJson,
    DateTime ReceivedAtUtc,
    int? PaymentId = null,
    int? OrderId = null);

/// <summary>What <see cref="IPaymentEventStore.RecordAsync"/> found or did.</summary>
/// <param name="EventId">The row's id — the new one, or the one that already held this key.</param>
/// <param name="IsNew">True for the one call that inserted it; false for a redelivery.</param>
/// <param name="ProcessingStatus">
/// The row's status when this call returned: Received for a new row, whatever it has reached for a
/// redelivery.
/// </param>
/// <param name="PayloadTruncated">
/// True when the payload was over <see cref="PaymentEventPayload.MaxLength"/> and only a marker was
/// stored. The event cannot be reprocessed from the log; the caller still holds the real payload.
/// </param>
/// <param name="PayloadRedacted">True when a secret-named property's value was overwritten before storing.</param>
public sealed record PaymentEventReceipt(
    long EventId,
    bool IsNew,
    PaymentEventProcessingStatus ProcessingStatus,
    bool PayloadTruncated,
    bool PayloadRedacted);

/// <summary>Timing the event log applies by default.</summary>
public static class PaymentEventRetry
{
    /// <summary>
    /// How long after it is stored an unprocessed event waits before the sweep may pick it up. Long enough
    /// that the request that stored it has finished applying it in the ordinary case; short enough that a
    /// crash between storing and applying does not leave a payment unrecorded for long.
    /// </summary>
    public static readonly TimeSpan GraceBeforeFirstRetry = TimeSpan.FromMinutes(2);
}
