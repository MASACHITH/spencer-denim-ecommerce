using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.Auditing.Contracts;
using SpencerDenim.Application.Catalog.Contracts;

namespace SpencerDenim.Application.Admin.Auditing;

/// <summary>Reads one page of the audit log viewer (ADM-03, W1-09).</summary>
public sealed class GetAuditLogsQueryHandler(IAuditLogReader reader)
    : IQueryHandler<GetAuditLogsQuery, PagedResult<AuditLogEntryDto>>
{
    public Task<PagedResult<AuditLogEntryDto>> HandleAsync(GetAuditLogsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return reader.ListAsync(query, cancellationToken);
    }
}
