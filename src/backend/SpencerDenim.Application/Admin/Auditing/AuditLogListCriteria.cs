namespace SpencerDenim.Application.Admin.Auditing;

/// <summary>
/// The raw, unvalidated query string of <c>GET /api/admin/audit-logs</c>, exactly as it arrives.
/// <see cref="GetAuditLogsQuery.Create"/> turns it into a validated query or a list of 400 errors.
/// <para>
/// Every value is a <see cref="string"/>, including the numbers and dates: model binding a typed
/// <c>int?</c> or <c>DateTime?</c> turns a bad value into a 500 before any of our code runs, so the
/// parsing happens in <see cref="GetAuditLogsQuery.Create"/> alongside the range checks, exactly the
/// way <c>ProductListQuery</c> does it for the catalogue listing (doc 06 §"match an existing pattern").
/// </para>
/// </summary>
/// <param name="EntityType">Exact match against <c>ops.AuditLogs.EntityType</c>, e.g. <c>Product</c>.</param>
/// <param name="EntityId">Exact match against <c>ops.AuditLogs.EntityId</c>.</param>
/// <param name="UserId">The acting user's <c>Users.Id</c>.</param>
/// <param name="Action">Exact match against <c>ops.AuditLogs.Action</c>, e.g. <c>Update</c>.</param>
/// <param name="FromUtc">Earliest <c>OccurredAtUtc</c> to include (inclusive), ISO 8601.</param>
/// <param name="ToUtc">Latest <c>OccurredAtUtc</c> to include (inclusive), ISO 8601.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Rows per page.</param>
public sealed record AuditLogListCriteria(
    string? EntityType = null,
    string? EntityId = null,
    string? UserId = null,
    string? Action = null,
    string? FromUtc = null,
    string? ToUtc = null,
    string? Page = null,
    string? PageSize = null);
