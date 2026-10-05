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
    /// <param name="query">The validated listing query.</param>
    /// <param name="includeGrantsAllHolders">
    /// <c>false</c> removes, before the count and the page, every row whose subject or acting user is a current
    /// Super Admin — so the audit log cannot be used to learn who holds that role (tester round 3, Major 2).
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<PagedResult<AuditLogEntryDto>> ListAsync(
        GetAuditLogsQuery query,
        bool includeGrantsAllHolders,
        CancellationToken cancellationToken);
}
