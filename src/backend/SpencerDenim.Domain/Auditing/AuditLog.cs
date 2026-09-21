namespace SpencerDenim.Domain.Auditing;

/// <summary>
/// <c>ops.AuditLogs</c> (doc 08 §3.8, ADM-03): who changed what, when. Rows are never deleted or updated
/// (doc 08 §1.6) — only inserts are ever issued against this table.
/// </summary>
public sealed class AuditLog
{
    public long Id { get; init; }

    public DateTime OccurredAtUtc { get; init; }

    /// <summary>No FK (doc 08 §1.5): users are never hard-deleted, but this column intentionally has none anyway.</summary>
    public int? UserId { get; init; }

    /// <summary>Snapshot of the acting (or attempted) user's email, taken at the time of the event.</summary>
    public string? UserEmail { get; init; }

    /// <summary>One of the values in <see cref="AuditActions"/>.</summary>
    public required string Action { get; init; }

    public required string EntityType { get; init; }

    public string? EntityId { get; init; }

    /// <summary>Changed properties only, valid JSON when set (doc 08 §3.8 <c>CK ISJSON</c>).</summary>
    public string? BeforeJson { get; init; }

    /// <summary>Changed properties only, valid JSON when set (doc 08 §3.8 <c>CK ISJSON</c>).</summary>
    public string? AfterJson { get; init; }

    public string? IpAddress { get; init; }

    public string? UserAgent { get; init; }

    public string? CorrelationId { get; init; }
}
