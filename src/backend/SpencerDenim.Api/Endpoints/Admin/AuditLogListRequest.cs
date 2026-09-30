using Microsoft.AspNetCore.Mvc;
using SpencerDenim.Application.Admin.Auditing;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The query string of <c>GET /api/admin/audit-logs</c>, bound straight from the request. It carries no
/// rules: <see cref="GetAuditLogsQuery.Create"/> validates and normalises it, exactly like
/// <c>ProductListRequest</c> does for the catalogue listing.
/// <para>
/// Numbers and dates bind as <see cref="string"/>. Binding them as <c>int?</c> or <c>DateTime?</c> makes
/// the framework throw on a bad value before the handler runs, which is a 500 instead of the 400 every
/// other bad value gets.
/// </para>
/// </summary>
internal sealed record AuditLogListRequest(
    [property: FromQuery(Name = "entityType")] string? EntityType,
    [property: FromQuery(Name = "entityId")] string? EntityId,
    [property: FromQuery(Name = "userId")] string? UserId,
    [property: FromQuery(Name = "action")] string? Action,
    [property: FromQuery(Name = "fromUtc")] string? FromUtc,
    [property: FromQuery(Name = "toUtc")] string? ToUtc,
    [property: FromQuery(Name = "page")] string? Page,
    [property: FromQuery(Name = "pageSize")] string? PageSize)
{
    public AuditLogListCriteria ToCriteria() => new(
        EntityType,
        EntityId,
        UserId,
        Action,
        FromUtc,
        ToUtc,
        Page,
        PageSize);
}
