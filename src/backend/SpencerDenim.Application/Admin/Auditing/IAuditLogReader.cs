using SpencerDenim.Application.Admin.Auditing.Contracts;
using SpencerDenim.Application.Catalog.Contracts;

namespace SpencerDenim.Application.Admin.Auditing;

/// <summary>
/// Read port for the audit log viewer (ADM-03, W1-09). Implemented in Infrastructure against
/// <c>ops.AuditLogs</c>, projecting straight to <see cref="AuditLogEntryDto"/> with <c>AsNoTracking</c> so
/// no EF entity ever leaves that layer (doc 08 §10.3) — the same shape <c>IProductCatalogReader</c> uses.
/// </summary>
public interface IAuditLogReader
{
    /// <summary>One page of audit log rows, filtered and sorted <c>OccurredAtUtc DESC</c>.</summary>
    Task<PagedResult<AuditLogEntryDto>> ListAsync(GetAuditLogsQuery query, CancellationToken cancellationToken);
}
