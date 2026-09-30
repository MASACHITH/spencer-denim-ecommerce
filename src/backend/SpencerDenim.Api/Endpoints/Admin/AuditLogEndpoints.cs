using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Admin.Auditing;
using SpencerDenim.Application.Admin.Auditing.Contracts;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The read-only audit log viewer (ADM-03, W1-09, issue #8). Staff read <c>ops.AuditLogs</c> here; nothing
/// on this route ever writes a row — that only ever happens through the audit interceptor and the
/// use cases it wraps (doc 08 §3.8).
/// </summary>
internal static class AuditLogEndpoints
{
    public static IEndpointRouteBuilder MapAuditLogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/admin/audit-logs").WithTags("Admin Audit Log");

        api.MapGet("/", GetAuditLogsAsync)
            .RequirePermission(Permissions.AuditLog.View)
            .WithName("GetAuditLogs")
            .WithSummary("Browse the audit log, filtered and paged.")
            .WithDescription(
                "ADM-03. Filters by entity type, entity id, acting user, action and a date range. Always " +
                "sorted OccurredAtUtc DESC, matching IX_AuditLogs_Occurred / IX_AuditLogs_Entity / " +
                "IX_AuditLogs_User (doc 08 §3.8). Before/after JSON was already redacted when the row was " +
                "written (DB24), so this endpoint returns exactly what is stored.")
            .Produces<PagedResult<AuditLogEntryDto>>()
            .ProducesValidationProblem()
            .Produces(401)
            .Produces(403);

        return endpoints;
    }

    private static async Task<Results<Ok<PagedResult<AuditLogEntryDto>>, ValidationProblem>> GetAuditLogsAsync(
        [AsParameters] AuditLogListRequest request,
        IQueryHandler<GetAuditLogsQuery, PagedResult<AuditLogEntryDto>> handler,
        CancellationToken cancellationToken)
    {
        var query = GetAuditLogsQuery.Create(request.ToCriteria());
        if (!query.IsSuccess)
        {
            return ToValidationProblem(query.Errors);
        }

        var page = await handler.HandleAsync(query.Value, cancellationToken);
        return TypedResults.Ok(page);
    }

    /// <summary>One RFC 9457 response carrying every rejected parameter, keyed by its query-string name.</summary>
    private static ValidationProblem ToValidationProblem(IReadOnlyList<ValidationError> errors)
    {
        var grouped = errors
            .GroupBy(error => error.Field, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(error => error.Message).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        return TypedResults.ValidationProblem(grouped, title: "One or more query parameters are invalid.");
    }
}
