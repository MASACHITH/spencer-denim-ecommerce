using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Sales.Payments;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Payments;

/// <summary>
/// <see cref="IPaymentEventStore"/> over <c>sales.PaymentEvents</c> (doc 08 §3.5, DB13).
/// <para>
/// <b>Recording is one guarded <c>INSERT … WHERE NOT EXISTS</c>, and the row count is the answer.</b> One
/// affected row means this caller stored the event; none means the key was already there. The check is
/// made under <c>UPDLOCK, HOLDLOCK</c>, which takes a key-range lock on <c>UX_PaymentEvents_IdempotencyKey</c>
/// for the statement: two redeliveries arriving on two threads queue behind each other on that range
/// instead of both passing the check, so the second finds the first's row. This is the same
/// "conditional statement, count the rows" mechanism every other money path here uses (doc 08 §5.2), and
/// it keeps the unique index what doc 08 §4.2 says it should be — <b>a backstop that is not caught as
/// control flow</b>: a 2627 from this statement would mean the lock hint was lost, and it is deliberately
/// left to surface (the webhook answers 5xx, the provider redelivers, and the redelivery is recognised)
/// rather than swallowed, so that <c>PaymentEventStoreTests</c>'s concurrency test fails if it ever happens.
/// </para>
/// <para>
/// <b>Each method is a single statement and so needs no transaction of its own</b> — which is the point:
/// recording must be durable <i>before</i> the event is applied, and applying is a separate transaction
/// (see <see cref="IPaymentEventStore"/>). Run inside a caller's transaction they simply join it.
/// </para>
/// </summary>
internal sealed class PaymentEventStore(AppDbContext dbContext) : IPaymentEventStore
{
    /// <summary><c>varchar(100)</c>.</summary>
    private const int ProviderEventIdMaxLength = 100;

    /// <summary><c>varchar(100)</c>.</summary>
    private const int EventTypeMaxLength = 100;

    /// <summary><c>nvarchar(2000)</c>.</summary>
    private const int LastErrorMaxLength = 2000;

    public async Task<PaymentEventReceipt> RecordAsync(
        PaymentEventRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        RequireKeyText(record.ProviderCode, PaymentProviderCodes.MaxCodeLength, nameof(record.ProviderCode));
        RequireKeyText(record.EventType, EventTypeMaxLength, nameof(record.EventType));
        RequireKeyText(record.IdempotencyKey, PaymentEventKey.MaxLength, nameof(record.IdempotencyKey));

        if (record.ProviderEventId is not null)
        {
            RequireKeyText(record.ProviderEventId, ProviderEventIdMaxLength, nameof(record.ProviderEventId));
        }

        if (!Enum.IsDefined(record.Source))
        {
            throw new ArgumentException("EventSource must be 1, 2 or 3.", nameof(record));
        }

        // Validated, redacted and capped before the database is asked: a payload that is not JSON is a
        // caller's bug and is reported as one, not as a constraint violation.
        var payload = PaymentEventPayload.Prepare(record.PayloadJson);

        // A Received row carries a retry time (doc 08 §3.5): IX_PaymentEvents_Retry selects on it, and an
        // event stored and then never applied — the process died between the two — would otherwise have
        // none and never be found. MarkProcessed clears it.
        var firstRetryAt = record.ReceivedAtUtc + PaymentEventRetry.GraceBeforeFirstRetry;

        var inserted = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO sales.PaymentEvents
                (ProviderCode, EventSource, ProviderEventId, EventType, IdempotencyKey, PaymentId, OrderId,
                 PayloadJson, ProcessingStatus, AttemptCount, NextAttemptAtUtc, ReceivedAtUtc)
            SELECT CONVERT(varchar(30), {record.ProviderCode}), {(byte)record.Source},
                   CONVERT(varchar(100), {record.ProviderEventId}), CONVERT(varchar(100), {record.EventType}),
                   CONVERT(varchar(160), {record.IdempotencyKey}), {record.PaymentId}, {record.OrderId},
                   {payload.Json}, {(byte)PaymentEventProcessingStatus.Received}, 0,
                   {firstRetryAt}, {record.ReceivedAtUtc}
             WHERE NOT EXISTS (
                     SELECT 1 FROM sales.PaymentEvents WITH (UPDLOCK, HOLDLOCK)
                      WHERE IdempotencyKey = CONVERT(varchar(160), {record.IdempotencyKey}));
            """,
            cancellationToken);

        // The row's id and status, whether this call wrote it or an earlier one did. A second statement
        // rather than OUTPUT: ExecuteSql returns only a count, and nothing deletes events, so the row
        // found here is the one the INSERT either made or lost to.
        var stored = await dbContext.PaymentEvents
            .AsNoTracking()
            .Where(candidate => candidate.IdempotencyKey == record.IdempotencyKey)
            .Select(candidate => new StoredEvent(candidate.Id, candidate.ProcessingStatus))
            .SingleAsync(cancellationToken);

        return new PaymentEventReceipt(
            stored.Id,
            IsNew: inserted == 1,
            stored.Status,
            payload.Truncated,
            payload.Redacted);
    }

    public async Task<bool> MarkProcessedAsync(long eventId, DateTime nowUtc, CancellationToken cancellationToken) =>
        await MarkOutcomeAsync(eventId, PaymentEventProcessingStatus.Processed, nowUtc, cancellationToken);

    public async Task<bool> MarkIgnoredAsync(long eventId, DateTime nowUtc, CancellationToken cancellationToken) =>
        await MarkOutcomeAsync(eventId, PaymentEventProcessingStatus.Ignored, nowUtc, cancellationToken);

    public async Task<bool> MarkFailedAsync(
        long eventId,
        string failure,
        DateTime? retryAtUtc,
        CancellationToken cancellationToken)
    {
        var text = string.IsNullOrWhiteSpace(failure) ? "Unspecified failure." : failure.Trim();

        if (text.Length > LastErrorMaxLength)
        {
            text = text[..LastErrorMaxLength];
        }

        // From Received or Failed only: an event that has been Processed or Ignored is finished, and a late
        // or duplicate worker must not turn it back into a failure.
        var moved = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.PaymentEvents
               SET ProcessingStatus = {(byte)PaymentEventProcessingStatus.Failed},
                   AttemptCount = AttemptCount + 1,
                   LastError = {text},
                   NextAttemptAtUtc = {retryAtUtc}
             WHERE Id = {eventId}
               AND ProcessingStatus IN (
                     {(byte)PaymentEventProcessingStatus.Received}, {(byte)PaymentEventProcessingStatus.Failed});
            """,
            cancellationToken);

        return moved == 1;
    }

    public async Task<bool> LinkAsync(
        long eventId,
        int? paymentId,
        int? orderId,
        CancellationToken cancellationToken)
    {
        // COALESCE: a null argument leaves its column alone, so linking the payment later does not erase
        // an order that was already known.
        var moved = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.PaymentEvents
               SET PaymentId = COALESCE({paymentId}, PaymentId),
                   OrderId = COALESCE({orderId}, OrderId)
             WHERE Id = {eventId};
            """,
            cancellationToken);

        return moved == 1;
    }

    /// <summary>
    /// Moves a Received or Failed event to a finished status, counting the attempt and clearing the retry
    /// time and the last error — a finished event must not be picked up by the sweep, and an old error on
    /// a processed row would read as a current problem.
    /// </summary>
    private async Task<bool> MarkOutcomeAsync(
        long eventId,
        PaymentEventProcessingStatus outcome,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var moved = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.PaymentEvents
               SET ProcessingStatus = {(byte)outcome},
                   AttemptCount = AttemptCount + 1,
                   ProcessedAtUtc = {nowUtc},
                   NextAttemptAtUtc = NULL,
                   LastError = NULL
             WHERE Id = {eventId}
               AND ProcessingStatus IN (
                     {(byte)PaymentEventProcessingStatus.Received}, {(byte)PaymentEventProcessingStatus.Failed});
            """,
            cancellationToken);

        return moved == 1;
    }

    /// <summary>
    /// A key-like text: non-empty, at most <paramref name="maxLength"/> characters, printable ASCII with no
    /// space. <see cref="PaymentEventKey.IsStorable"/> says why the last two matter — the column is
    /// <c>varchar</c>, and a character outside it is folded to <c>?</c> and makes different keys collide.
    /// </summary>
    private static void RequireKeyText(string value, int maxLength, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);

        if (value.Length > maxLength)
        {
            throw new ArgumentException($"{name} is at most {maxLength} characters.", name);
        }

        if (!PaymentEventKey.IsStorable(value))
        {
            throw new ArgumentException(
                $"{name} must be printable ASCII with no spaces: the column is varchar, and any other "
                + "character is folded to '?', which makes different keys collide.",
                name);
        }
    }

    /// <summary>A class, so a missing row is representable; here it cannot be missing, but the habit is cheap.</summary>
    private sealed record StoredEvent(long Id, PaymentEventProcessingStatus Status);
}
