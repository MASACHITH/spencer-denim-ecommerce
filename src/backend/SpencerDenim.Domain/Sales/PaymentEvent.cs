namespace SpencerDenim.Domain.Sales;

/// <summary>
/// <c>sales.PaymentEvents</c> (doc 08 §3.5, PAY-02/03): the provider event log, append-only, keyed by a
/// <c>bigint IDENTITY</c> (doc 08 §1.3's high-volume list).
/// <para>
/// <b><see cref="IdempotencyKey"/> is the point of the table.</b> Doc 08 DB13: a webhook's signature is
/// verified <i>before</i> anything is inserted, so an unverified request can never consume a key; then the
/// INSERT either succeeds — and the event is processed exactly once — or hits the unique index, which is
/// how a redelivered webhook is recognised as already handled and answered 200. Everything else here
/// (<see cref="ProcessingStatus"/>, <see cref="AttemptCount"/>, <see cref="NextAttemptAtUtc"/>) exists so
/// a processing failure can be retried without the provider having to resend it.
/// </para>
/// <para>
/// <b>Never audited</b> (doc 08 §3.8 names it among the tables that are not): it is already a log, and
/// auditing a log would double every payload.
/// </para>
/// </summary>
public sealed class PaymentEvent
{
    public long Id { get; set; }

    /// <summary>FK <c>sales.PaymentProviders</c>.</summary>
    public string ProviderCode { get; set; } = string.Empty;

    public PaymentProvider? PaymentProvider { get; set; }

    /// <summary>CK 1–3. A security boundary — see <see cref="PaymentEventSource"/>.</summary>
    public PaymentEventSource EventSource { get; set; }

    /// <summary>The provider's own event id, when it has one.</summary>
    public string? ProviderEventId { get; set; }

    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// <b>UX.</b> <c>{Provider}:{EventId}</c>, or <c>{Provider}:{Type}:{ResourceId}</c> when the provider
    /// gives no event id. Longer than the parts it is built from (160) on purpose.
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// Both nullable: an event can arrive that we cannot yet attribute to a payment or an order, and doc 08
    /// DB13 still wants it stored rather than dropped.
    /// </summary>
    public int? PaymentId { get; set; }

    public Payment? Payment { get; set; }

    public int? OrderId { get; set; }

    public Order? Order { get; set; }

    /// <summary>
    /// The verbatim provider payload. <c>nvarchar(max)</c> with an <c>ISJSON</c> check (doc 08 §1.4, DB15)
    /// and <b>NOT NULL</b>: an event with no payload could never be reprocessed or disputed.
    /// </summary>
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>CK 0–3.</summary>
    public PaymentEventProcessingStatus ProcessingStatus { get; set; }

    public short AttemptCount { get; set; }

    /// <summary>When the retry job should look again. <c>IX_PaymentEvents_Retry</c> leads on it.</summary>
    public DateTime? NextAttemptAtUtc { get; set; }

    public string? LastError { get; set; }

    public DateTime ReceivedAtUtc { get; set; }

    public DateTime? ProcessedAtUtc { get; set; }
}
