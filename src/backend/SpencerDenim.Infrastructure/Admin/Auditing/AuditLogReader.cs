using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Admin.Auditing;
using SpencerDenim.Application.Admin.Auditing.Contracts;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Admin.Auditing;

/// <summary>
/// The audit log viewer's read model (ADM-03, W1-09), implemented the same way as
/// <c>ProductCatalogReader</c>: predicates are composed only for the filters that are present, so an
/// unfiltered request keeps using <c>IX_AuditLogs_Occurred</c> instead of a predicate that always
/// evaluates true, and every query is <c>AsNoTracking</c> with a projection, so no EF entity leaves this
/// class (doc 08 §10.3).
/// </summary>
internal sealed class AuditLogReader(AppDbContext dbContext) : IAuditLogReader
{
    public async Task<PagedResult<AuditLogEntryDto>> ListAsync(GetAuditLogsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rows = ApplyFilters(dbContext.AuditLogs.AsNoTracking(), query);

        var totalCount = await rows.CountAsync(cancellationToken);
        if (totalCount == 0)
        {
            return PagedResult.Empty<AuditLogEntryDto>(query.Page, query.PageSize);
        }

        var page = await rows
            // OccurredAtUtc DESC matches IX_AuditLogs_Occurred / IX_AuditLogs_Entity / IX_AuditLogs_User
            // (doc 08 §3.8); Id DESC breaks ties between rows written in the same millisecond so paging
            // is stable, exactly like the catalogue listing's Id tiebreaker (doc 08 §7.1).
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .ThenByDescending(entry => entry.Id)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(entry => new AuditLogEntryDto(
                entry.Id,
                entry.OccurredAtUtc,
                entry.UserId,
                entry.UserEmail,
                entry.Action,
                entry.EntityType,
                entry.EntityId,
                entry.BeforeJson,
                entry.AfterJson,
                entry.IpAddress,
                entry.UserAgent,
                entry.CorrelationId))
            .ToListAsync(cancellationToken);

        return new PagedResult<AuditLogEntryDto>(page, query.Page, query.PageSize, totalCount);
    }

    private static IQueryable<AuditLog> ApplyFilters(IQueryable<AuditLog> rows, GetAuditLogsQuery query)
    {
        if (query.EntityType is { } entityType)
        {
            rows = rows.Where(entry => entry.EntityType == entityType);
        }

        if (query.EntityId is { } entityId)
        {
            rows = rows.Where(entry => entry.EntityId == entityId);
        }

        if (query.UserId is { } userId)
        {
            rows = rows.Where(entry => entry.UserId == userId);
        }

        if (query.Action is { } action)
        {
            rows = rows.Where(entry => entry.Action == action);
        }

        if (query.FromUtc is { } fromUtc)
        {
            rows = rows.Where(entry => entry.OccurredAtUtc >= fromUtc);
        }

        if (query.ToUtc is { } toUtc)
        {
            rows = rows.Where(entry => entry.OccurredAtUtc <= toUtc);
        }

        return rows;
    }
}
