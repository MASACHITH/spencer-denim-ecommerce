using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.Users;
using SpencerDenim.Application.Admin.Users.Contracts;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// Read-only role list/detail (ADM-02, W1-08, issue #8). Role management (create/edit/delete a role, change
/// its permissions) is GrantsAll-only per doc 08 §3.1 DB4 rule 1 and was not asked for by this task — see
/// <c>AppRole</c>'s remarks. Both routes need only <see cref="Permissions.Users.View"/>: seeing what a role
/// grants is what lets a <see cref="Permissions.Users.Manage"/> holder decide which roles they can assign
/// under the subset rule in the first place.
/// </summary>
internal static class RoleEndpoints
{
    public static IEndpointRouteBuilder MapRoleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/admin/roles").WithTags("Admin Roles");

        api.MapGet("/", ListAsync)
            .RequirePermission(Permissions.Users.View)
            .WithName("GetRoles")
            .Produces<IReadOnlyList<RoleSummaryDto>>()
            .Produces(401)
            .Produces(403);

        api.MapGet("/{roleId:int}", GetByIdAsync)
            .RequirePermission(Permissions.Users.View)
            .WithName("GetRoleById")
            .Produces<RoleDetailDto>()
            .Produces(401)
            .Produces(403)
            .Produces(404);

        return endpoints;
    }

    private static async Task<Ok<IReadOnlyList<RoleSummaryDto>>> ListAsync(
        IQueryHandler<GetRolesQuery, IReadOnlyList<RoleSummaryDto>> handler,
        CancellationToken cancellationToken)
    {
        var roles = await handler.HandleAsync(new GetRolesQuery(), cancellationToken);
        return TypedResults.Ok(roles);
    }

    private static async Task<Results<Ok<RoleDetailDto>, NotFound>> GetByIdAsync(
        int roleId,
        IQueryHandler<GetRoleByIdQuery, RoleDetailDto?> handler,
        CancellationToken cancellationToken)
    {
        var detail = await handler.HandleAsync(new GetRoleByIdQuery(roleId), cancellationToken);
        return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
    }
}
