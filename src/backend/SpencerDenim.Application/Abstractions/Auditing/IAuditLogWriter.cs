using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.Application.Abstractions.Auditing;

/// <summary>Port for writing <c>ops.AuditLogs</c> rows (doc 08 §3.8). Implemented in Infrastructure.</summary>
public interface IAuditLogWriter
{
    Task WriteAsync(AuditLog entry, CancellationToken cancellationToken);
}
