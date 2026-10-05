using System.Globalization;
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
    /// <summary>The <c>EntityType</c> the staff-user use cases stamp on their rows (see the Users command handlers).</summary>
    private const string UserEntityType = "User";

    public async Task<PagedResult<AuditLogEntryDto>> ListAsync(
        GetAuditLogsQuery query,
        bool includeGrantsAllHolders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rows = ApplyFilters(dbContext.AuditLogs.AsNoTracking(), query);

        if (!includeGrantsAllHolders)
        {
            rows = await ExcludeGrantsAllHoldersAsync(rows, cancellationToken);
        }

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

    /// <summary>
    /// Removes every row whose <b>subject</b> is a current Super Admin (<c>EntityType = 'User'</c> and an
    /// <c>EntityId</c> naming one) or whose <b>acting user</b> is one, so neither the row's target nor its author
    /// can identify a GrantsAll holder (tester round 3, Major 2).
    /// <para>
    /// The holder ids are read first and filtered as a literal list rather than composed as a subquery, because
    /// <c>EntityId</c> is <c>varchar(100)</c> and matching it needs the ids as strings; <c>UX_Roles_GrantsAll</c>
    /// (doc 08 §3.1) allows at most one GrantsAll role in the database, so that list is a handful of ids, not a
    /// set that could grow into an unreasonable <c>IN</c> clause.
    /// </para>
    /// <para>
    /// <b>What this deliberately does not hide:</b> someone who <i>used to</i> hold the role. Once the role is
    /// unassigned they are no longer a current holder, so the historical rows about them become visible again —
    /// the same boundary the user and role endpoints draw, which hide current holders only.
    /// </para>
    /// </summary>
    private async Task<IQueryable<AuditLog>> ExcludeGrantsAllHoldersAsync(IQueryable<AuditLog> rows, CancellationToken cancellationToken)
    {
        var holderIds = await dbContext.UserRoles.AsNoTracking()
            .Where(ur => dbContext.Roles.Any(r => r.Id == ur.RoleId && r.GrantsAllPermissions))
            .Select(ur => ur.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (holderIds.Count == 0)
        {
            return rows;
        }

        var holderEntityIds = holderIds.ConvertAll(id => id.ToString(CultureInfo.InvariantCulture));

        return rows.Where(entry =>
            !(entry.UserId != null && holderIds.Contains(entry.UserId.Value))
            && !(entry.EntityType == UserEntityType && entry.EntityId != null && holderEntityIds.Contains(entry.EntityId)));
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
