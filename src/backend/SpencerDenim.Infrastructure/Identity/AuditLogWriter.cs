using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>Inserts one <c>ops.AuditLogs</c> row (doc 08 §3.8). Rows are append-only — no update path exists.</summary>
public sealed class AuditLogWriter(AppDbContext dbContext) : IAuditLogWriter
{
    public async Task WriteAsync(AuditLog entry, CancellationToken cancellationToken)
    {
        Enlist(entry);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Adds the row to the same scoped <see cref="AppDbContext"/> every use case in this request writes
    /// through, so the caller's own <c>SaveChangesAsync</c> commits it in the same transaction as the
    /// change it describes (issue #59).
    /// </summary>
    public void Enlist(AuditLog entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        dbContext.AuditLogs.Add(entry);
    }
}
