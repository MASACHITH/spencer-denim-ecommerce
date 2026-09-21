using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>Inserts one <c>ops.AuditLogs</c> row (doc 08 §3.8). Rows are append-only — no update path exists.</summary>
public sealed class AuditLogWriter(AppDbContext dbContext) : IAuditLogWriter
{
    public async Task WriteAsync(AuditLog entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        dbContext.AuditLogs.Add(entry);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
