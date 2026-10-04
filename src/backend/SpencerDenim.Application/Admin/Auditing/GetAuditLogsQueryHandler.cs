using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Admin.Auditing.Contracts;
using SpencerDenim.Application.Catalog.Contracts;

namespace SpencerDenim.Application.Admin.Auditing;

/// <summary>
/// Reads one page of the audit log viewer (ADM-03, W1-09).
/// <para>
/// <b>A viewer who does not hold GrantsAll never sees a row about, or written by, a Super Admin</b> (tester
/// round 3, Major 2). Without this the audit log walked straight around the rule the user and role endpoints
/// enforce: a holder of <c>AuditLog.View</c> alone — which is the seeded Store Manager's exact shape, per doc 08
/// §9 — got a 403 from <c>GET /users/{id}</c> and then read "roles":["Super Admin"] for that same id out of a
/// <c>PermissionChange</c> row. The audit log is narrower for such a viewer than it used to be, and that is the
/// deliberate trade: a trail that names who may not be named is a trail that answers the question the 403
/// refused.
/// </para>
/// </summary>
public sealed class GetAuditLogsQueryHandler(IAuditLogReader reader, IEffectivePermissionsProvider permissionsProvider)
    : IQueryHandler<GetAuditLogsQuery, PagedResult<AuditLogEntryDto>>
{
    public async Task<PagedResult<AuditLogEntryDto>> HandleAsync(GetAuditLogsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var viewer = await permissionsProvider.GetEffectivePermissionsAsync(query.ActorUserId, cancellationToken);
        return await reader.ListAsync(query, includeGrantsAllHolders: viewer.GrantsAllPermissions, cancellationToken);
    }
}
