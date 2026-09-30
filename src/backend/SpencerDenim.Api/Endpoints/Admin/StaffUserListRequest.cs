using Microsoft.AspNetCore.Mvc;
using SpencerDenim.Application.Admin.Users;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The query string of <c>GET /api/admin/users</c>, bound straight from the request. Carries no rules —
/// <see cref="GetStaffUsersQuery.Create"/> validates and normalises it, the same convention
/// <c>AuditLogListRequest</c> uses for the audit log viewer.
/// </summary>
internal sealed record StaffUserListRequest(
    [property: FromQuery(Name = "search")] string? Search,
    [property: FromQuery(Name = "roleId")] string? RoleId,
    [property: FromQuery(Name = "isActive")] string? IsActive,
    [property: FromQuery(Name = "page")] string? Page,
    [property: FromQuery(Name = "pageSize")] string? PageSize)
{
    public StaffUserListCriteria ToCriteria() => new(Search, RoleId, IsActive, Page, PageSize);
}
