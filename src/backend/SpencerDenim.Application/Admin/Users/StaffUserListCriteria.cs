namespace SpencerDenim.Application.Admin.Users;

/// <summary>
/// The raw, unvalidated query string of <c>GET /api/admin/users</c>, exactly as it arrives.
/// <see cref="GetStaffUsersQuery.Create"/> turns it into a validated query or a list of 400 errors — every
/// value is a <see cref="string"/> for the same reason <c>AuditLogListCriteria</c> is (binding a typed
/// <c>int?</c>/<c>bool?</c> turns a bad value into a 500 before our own validation runs).
/// </summary>
/// <param name="Search">Free-text match against email, first name or last name.</param>
/// <param name="RoleId">Only users holding this role.</param>
/// <param name="IsActive">Only active (<c>true</c>) or only deactivated (<c>false</c>) users.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Rows per page.</param>
public sealed record StaffUserListCriteria(
    string? Search = null,
    string? RoleId = null,
    string? IsActive = null,
    string? Page = null,
    string? PageSize = null);
