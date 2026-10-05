using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Admin.Users;
using SpencerDenim.Application.Admin.Users.Contracts;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// Staff user list/create/edit/deactivate/reactivate and role assignment (ADM-02, W1-08, issue #8). Every
/// mutating route validates the antiforgery token by hand (doc 03 §3a) through the same
/// <see cref="AdminWriteRequest.ValidateAntiforgeryAsync"/> the catalogue write routes use, so every admin write
/// refuses a missing or forged token with one error contract,
/// exactly like <c>AdminAuthEndpoints</c>'s change-password route — there is no global antiforgery middleware.
/// Read routes need <see cref="Permissions.Users.View"/>; every write route needs
/// <see cref="Permissions.Users.Manage"/>, which is necessary but never sufficient on its own — the command
/// handlers behind these routes additionally enforce the doc 08 §3.1 DB4 subset rule against the actor's own
/// permissions, so a 403 from here can come from either layer (see <see cref="ToProblem"/>).
/// </summary>
internal static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/admin/users").WithTags("Admin Users");

        api.MapGet("/", ListAsync)
            .RequirePermission(Permissions.Users.View)
            .WithName("GetStaffUsers")
            .Produces<PagedResult<StaffUserSummaryDto>>()
            .ProducesValidationProblem()
            .Produces(401)
            .Produces(403);

        api.MapGet("/{userId:int}", GetByIdAsync)
            .RequirePermission(Permissions.Users.View)
            .WithName("GetStaffUserById")
            .Produces<StaffUserDetailDto>()
            .Produces(401)
            .Produces(403)
            .Produces(404);

        api.MapPost("/", CreateAsync)
            .RequirePermission(Permissions.Users.Manage)
            .WithName("CreateStaffUser")
            .Produces<StaffUserResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .Produces(401)
            .Produces(403)
            .Produces(409);

        api.MapPut("/{userId:int}", UpdateProfileAsync)
            .RequirePermission(Permissions.Users.Manage)
            .WithName("UpdateStaffUserProfile")
            .Produces<StaffUserResponse>()
            .ProducesValidationProblem()
            .Produces(401)
            .Produces(403)
            .Produces(404)
            .Produces(409);

        api.MapPut("/{userId:int}/roles", AssignRolesAsync)
            .RequirePermission(Permissions.Users.Manage)
            .WithName("AssignStaffUserRoles")
            .Produces<StaffUserRolesResponse>()
            .Produces(401)
            .Produces(403)
            .Produces(404)
            .Produces(409);

        api.MapPost("/{userId:int}/deactivate", DeactivateAsync)
            .RequirePermission(Permissions.Users.Manage)
            .WithName("DeactivateStaffUser")
            .Produces<StaffUserActiveResponse>()
            .Produces(401)
            .Produces(403)
            .Produces(404)
            .Produces(409);

        api.MapPost("/{userId:int}/reactivate", ReactivateAsync)
            .RequirePermission(Permissions.Users.Manage)
            .WithName("ReactivateStaffUser")
            .Produces<StaffUserActiveResponse>()
            .Produces(401)
            .Produces(403)
            .Produces(404);

        return endpoints;
    }

    private static async Task<Results<Ok<PagedResult<StaffUserSummaryDto>>, ValidationProblem, UnauthorizedHttpResult>> ListAsync(
        [AsParameters] StaffUserListRequest request,
        ClaimsPrincipal principal,
        IQueryHandler<GetStaffUsersQuery, PagedResult<StaffUserSummaryDto>> handler,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorId(principal, out var actorId))
        {
            return TypedResults.Unauthorized();
        }

        var query = GetStaffUsersQuery.Create(request.ToCriteria(), actorId);
        if (!query.IsSuccess)
        {
            return ToValidationProblem(query.Errors);
        }

        var page = await handler.HandleAsync(query.Value, cancellationToken);
        return TypedResults.Ok(page);
    }

    private static async Task<Results<Ok<StaffUserDetailDto>, NotFound, UnauthorizedHttpResult>> GetByIdAsync(
        int userId,
        ClaimsPrincipal principal,
        IQueryHandler<GetStaffUserByIdQuery, StaffUserDetailDto?> handler,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorId(principal, out var actorId))
        {
            return TypedResults.Unauthorized();
        }

        var detail = await handler.HandleAsync(new GetStaffUserByIdQuery(userId, actorId), cancellationToken);
        return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
    }

    private static async Task<IResult> CreateAsync(
        CreateStaffUserRequest request,
        ClaimsPrincipal principal,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<CreateStaffUserCommand, CreateStaffUserResult> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        if (!TryGetActorId(principal, out var actorId))
        {
            return TypedResults.Unauthorized();
        }

        var command = new CreateStaffUserCommand(
            actorId,
            request.Email ?? string.Empty,
            request.FirstName ?? string.Empty,
            request.LastName,
            request.InitialPassword ?? string.Empty,
            request.RoleIds ?? [],
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString());

        var result = await handler.HandleAsync(command, cancellationToken);

        return result switch
        {
            CreateStaffUserResult.Success success => TypedResults.Created(
                $"/api/admin/users/{success.UserId}",
                new StaffUserResponse(success.UserId, success.Email, success.FirstName, success.LastName, success.GrantsAllPermissions, success.Roles)),
            CreateStaffUserResult.ValidationFailed validationFailed => ToValidationProblem(validationFailed.Errors),
            CreateStaffUserResult.RoleNotFound roleNotFound => RoleNotFoundProblem(roleNotFound.UnknownRoleIds),
            CreateStaffUserResult.Forbidden forbidden => ToProblem(forbidden.Reason, forbidden.Message),
            CreateStaffUserResult.EmailAlreadyInUse => EmailAlreadyInUseProblem(),
            CreateStaffUserResult.PasswordRejected passwordRejected => TypedResults.ValidationProblem(
                new Dictionary<string, string[]> { ["initialPassword"] = [.. passwordRejected.Errors] },
                title: "The initial password does not meet the password rules."),
            _ => throw new InvalidOperationException($"Unhandled {nameof(CreateStaffUserResult)}: {result.GetType().Name}"),
        };
    }

    private static async Task<IResult> UpdateProfileAsync(
        int userId,
        UpdateStaffUserProfileRequest request,
        ClaimsPrincipal principal,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdateStaffUserProfileCommand, UpdateStaffUserProfileResult> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        if (!TryGetActorId(principal, out var actorId))
        {
            return TypedResults.Unauthorized();
        }

        var command = new UpdateStaffUserProfileCommand(
            actorId,
            userId,
            request.FirstName ?? string.Empty,
            request.LastName,
            request.Email,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString());

        var result = await handler.HandleAsync(command, cancellationToken);

        return result switch
        {
            UpdateStaffUserProfileResult.Success success => TypedResults.Ok(
                new { id = success.UserId, email = success.Email, firstName = success.FirstName, lastName = success.LastName }),
            UpdateStaffUserProfileResult.ValidationFailed validationFailed => ToValidationProblem(validationFailed.Errors),
            UpdateStaffUserProfileResult.TargetNotFound => TypedResults.NotFound(),
            UpdateStaffUserProfileResult.Forbidden forbidden => ToProblem(forbidden.Reason, forbidden.Message),
            UpdateStaffUserProfileResult.EmailAlreadyInUse => EmailAlreadyInUseProblem(),
            _ => throw new InvalidOperationException($"Unhandled {nameof(UpdateStaffUserProfileResult)}: {result.GetType().Name}"),
        };
    }

    private static async Task<IResult> AssignRolesAsync(
        int userId,
        AssignUserRolesRequest request,
        ClaimsPrincipal principal,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<AssignUserRolesCommand, AssignUserRolesResult> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        if (!TryGetActorId(principal, out var actorId))
        {
            return TypedResults.Unauthorized();
        }

        var command = new AssignUserRolesCommand(
            actorId,
            userId,
            request.RoleIds ?? [],
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString());

        var result = await handler.HandleAsync(command, cancellationToken);

        return result switch
        {
            AssignUserRolesResult.Success success => TypedResults.Ok(
                new StaffUserRolesResponse(success.UserId, success.GrantsAllPermissions, success.Roles)),
            AssignUserRolesResult.TargetNotFound => TypedResults.NotFound(),
            AssignUserRolesResult.RoleNotFound roleNotFound => RoleNotFoundProblem(roleNotFound.UnknownRoleIds),
            AssignUserRolesResult.SelfEditNotAllowed => ToProblem(RbacDenialReason.SelfEditNotAllowed, "You cannot change your own role assignments."),
            AssignUserRolesResult.Forbidden forbidden => ToProblem(forbidden.Reason, forbidden.Message),
            AssignUserRolesResult.LastSuperAdminGuardTriggered => LastSuperAdminGuardProblem(),
            _ => throw new InvalidOperationException($"Unhandled {nameof(AssignUserRolesResult)}: {result.GetType().Name}"),
        };
    }

    private static async Task<IResult> DeactivateAsync(
        int userId,
        ClaimsPrincipal principal,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<SetStaffUserActiveCommand, SetStaffUserActiveResult> handler,
        CancellationToken cancellationToken) =>
        await SetActiveAsync(userId, active: false, principal, httpContext, antiforgery, handler, cancellationToken);

    private static async Task<IResult> ReactivateAsync(
        int userId,
        ClaimsPrincipal principal,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<SetStaffUserActiveCommand, SetStaffUserActiveResult> handler,
        CancellationToken cancellationToken) =>
        await SetActiveAsync(userId, active: true, principal, httpContext, antiforgery, handler, cancellationToken);

    private static async Task<IResult> SetActiveAsync(
        int userId,
        bool active,
        ClaimsPrincipal principal,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<SetStaffUserActiveCommand, SetStaffUserActiveResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        if (!TryGetActorId(principal, out var actorId))
        {
            return TypedResults.Unauthorized();
        }

        var command = new SetStaffUserActiveCommand(
            actorId,
            userId,
            active,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString());

        var result = await handler.HandleAsync(command, cancellationToken);

        return result switch
        {
            SetStaffUserActiveResult.Success success => TypedResults.Ok(new StaffUserActiveResponse(success.UserId, success.IsActive)),
            SetStaffUserActiveResult.TargetNotFound => TypedResults.NotFound(),
            SetStaffUserActiveResult.SelfEditNotAllowed => ToProblem(RbacDenialReason.SelfEditNotAllowed, "You cannot deactivate your own account."),
            SetStaffUserActiveResult.Forbidden forbidden => ToProblem(forbidden.Reason, forbidden.Message),
            SetStaffUserActiveResult.LastSuperAdminGuardTriggered => LastSuperAdminGuardProblem(),
            _ => throw new InvalidOperationException($"Unhandled {nameof(SetStaffUserActiveResult)}: {result.GetType().Name}"),
        };
    }

    private static bool TryGetActorId(ClaimsPrincipal principal, out int actorId) =>
        AdminSessionCookies.TryGetUserId(principal, out actorId);

    private static ProblemHttpResult ToProblem(RbacDenialReason reason, string message) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Not authorized.",
            detail: message,
            extensions: new Dictionary<string, object?> { ["error"] = reason.ToString() });

    private static ProblemHttpResult RoleNotFoundProblem(IReadOnlyList<int> unknownRoleIds) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Unknown role.",
            detail: "One or more of the selected roles does not exist.",
            extensions: new Dictionary<string, object?> { ["error"] = "role_not_found", ["unknownRoleIds"] = unknownRoleIds });

    private static ProblemHttpResult EmailAlreadyInUseProblem() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Email already in use.",
            detail: "Another account already uses this email address.",
            extensions: new Dictionary<string, object?> { ["error"] = "email_already_in_use" });

    private static ProblemHttpResult LastSuperAdminGuardProblem() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "At least one Super Admin is required.",
            detail: "This would leave no active Super Admin. Assign the Super Admin role to another active user first.",
            extensions: new Dictionary<string, object?> { ["error"] = "last_super_admin_guard" });

    private static ValidationProblem ToValidationProblem(IReadOnlyList<ValidationError> errors)
    {
        var grouped = errors
            .GroupBy(error => error.Field, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(error => error.Message).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        return TypedResults.ValidationProblem(grouped, title: "One or more fields are invalid.");
    }
}

public sealed record CreateStaffUserRequest(string? Email, string? FirstName, string? LastName, string? InitialPassword, IReadOnlyList<int>? RoleIds);

public sealed record UpdateStaffUserProfileRequest(string? FirstName, string? LastName, string? Email);

public sealed record AssignUserRolesRequest(IReadOnlyList<int>? RoleIds);

public sealed record StaffUserResponse(int Id, string Email, string FirstName, string? LastName, bool GrantsAllPermissions, IReadOnlyList<RoleRefDto> Roles);

public sealed record StaffUserRolesResponse(int Id, bool GrantsAllPermissions, IReadOnlyList<RoleRefDto> Roles);

public sealed record StaffUserActiveResponse(int Id, bool IsActive);
