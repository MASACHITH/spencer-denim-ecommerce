namespace SpencerDenim.Application.Admin.Auditing.Contracts;

/// <summary>
/// One <c>ops.AuditLogs</c> row (ADM-03, doc 08 §3.8), projected straight from the entity: no field is
/// renamed or recomputed, so this screen shows exactly what was written, nothing more. This is a
/// read-only passthrough, not an enforcement point: doc 08 §3.8 (DB24) describes a generic
/// <c>[AuditIgnore]</c> / <c>[AuditRedact]</c> attribute mechanism (plus a model test asserting the
/// redaction list) that has not been built yet — today only two hand-written use cases write
/// <c>ops.AuditLogs</c> rows (<c>AdminLoginCommandHandler</c>, <c>ChangeAdminPasswordCommandHandler</c>),
/// and each is manually careful not to embed a secret. Before a generic audited-entity write path is
/// added, DB24's actual attribute mechanism needs to exist — this viewer will otherwise display whatever
/// that future writer produces with no enforcement in between (tester finding, W1-09, 2026-09-30).
/// </summary>
/// <param name="Id">The row's identity.</param>
/// <param name="OccurredAtUtc">When the change happened, UTC.</param>
/// <param name="UserId">The acting user, or <c>null</c> for a system action.</param>
/// <param name="UserEmail">A snapshot of the acting user's email at the time, or <c>null</c>.</param>
/// <param name="Action">One of <c>SpencerDenim.Domain.Auditing.AuditActions</c>.</param>
/// <param name="EntityType">The changed entity's type name.</param>
/// <param name="EntityId">The changed entity's key, as text.</param>
/// <param name="BeforeJson">Changed properties before the change, already redacted, or <c>null</c>.</param>
/// <param name="AfterJson">Changed properties after the change, already redacted, or <c>null</c>.</param>
/// <param name="IpAddress">The caller's IP address at the time, or <c>null</c>.</param>
/// <param name="UserAgent">The caller's user agent at the time, or <c>null</c>.</param>
/// <param name="CorrelationId">Correlates this row with the request that caused it, or <c>null</c>.</param>
public sealed record AuditLogEntryDto(
    long Id,
    DateTime OccurredAtUtc,
    int? UserId,
    string? UserEmail,
    string Action,
    string EntityType,
    string? EntityId,
    string? BeforeJson,
    string? AfterJson,
    string? IpAddress,
    string? UserAgent,
    string? CorrelationId);
