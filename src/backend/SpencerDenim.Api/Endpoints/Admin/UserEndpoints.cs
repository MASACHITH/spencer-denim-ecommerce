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

        // #254: staff account recovery. Users.Manage at the route, as every staff write; the handler then requires a
        // Super Admin, before it reads the target.
        api.MapPost("/{userId:int}/reset-password", ResetPasswordAsync)
            .RequirePermission(Permissions.Users.Manage)
            .WithName("ResetStaffUserPassword")
            .WithSummary("Set a temporary password the staff member must replace at their next sign-in (#254).")
            .WithDescription(
                "Super Admin only. The temporary password is typed by the Super Admin and handed over in person or by "
                + "phone; it is never emailed. The member's sessions end, their lockout is cleared, and they must choose "
                + "a new password when they sign in. Refused on yourself, on a protected account, and on a guarded "
                + "account unless you are a protected account. Audited (AdminPasswordReset), never with the password.")
            .Produces<StaffAccountRecoveryResponse>()
            .ProducesValidationProblem()
            .Produces(401)
            .Produces(403)
            .Produces(404)
            .Produces(409);

        api.MapPost("/{userId:int}/unlock", UnlockAsync)
            .RequirePermission(Permissions.Users.Manage)
            .WithName("UnlockStaffUser")
            .WithSummary("Clear a staff member's lockout and failed-attempt count (#254).")
            .WithDescription("Super Admin only; same refusals as reset-password. changed=false when the account was not locked.")
            .Produces<StaffAccountRecoveryResponse>()
            .Produces(401)
            .Produces(403)
            .Produces(404)
            .Produces(409);

        api.MapPost("/{userId:int}/reset-two-factor", ResetTwoFactorAsync)
            .RequirePermission(Permissions.Users.Manage)
            .WithName("ResetStaffUserTwoFactor")
            .WithSummary("Remove a staff member's authenticator and recovery codes, for a lost phone (#254).")
            .WithDescription(
                "Super Admin only; same refusals as reset-password. The member's sessions end; a Super Admin must set "
                + "two-factor up again at their next sign-in. changed=false when there was no authenticator. Audited "
                + "(TwoFactorReset), never with the key or the codes.")
            .Produces<StaffAccountRecoveryResponse>()
            .Produces(401)
            .Produces(403)
            .Produces(404)
            .Produces(409);

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
            UpdateStaffUserProfileResult.ProtectedEmail => ProtectedAccountProblem("The email address of a protected account cannot be changed."),
            UpdateStaffUserProfileResult.GuardedEmail => GuardedAccountProblem("Only the technical owner's account can change this account's email address."),
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
            SetStaffUserActiveResult.ProtectedAccount => ProtectedAccountProblem("This account is protected and can never be deactivated."),
            SetStaffUserActiveResult.GuardedAccount => GuardedAccountProblem("Only the technical owner's account can activate or deactivate this account."),
            _ => throw new InvalidOperationException($"Unhandled {nameof(SetStaffUserActiveResult)}: {result.GetType().Name}"),
        };
    }

    private static Task<IResult> ResetPasswordAsync(
        int userId,
        ResetStaffPasswordRequest? request,
        ClaimsPrincipal principal,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<RecoverStaffAccountCommand, RecoverStaffAccountResult> handler,
        CancellationToken cancellationToken) =>
        RecoverAsync(userId, StaffAccountRecovery.ResetPassword, request?.TemporaryPassword, principal, httpContext, antiforgery, handler, cancellationToken);

    private static Task<IResult> UnlockAsync(
        int userId,
        ClaimsPrincipal principal,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<RecoverStaffAccountCommand, RecoverStaffAccountResult> handler,
        CancellationToken cancellationToken) =>
        RecoverAsync(userId, StaffAccountRecovery.Unlock, null, principal, httpContext, antiforgery, handler, cancellationToken);

    private static Task<IResult> ResetTwoFactorAsync(
        int userId,
        ClaimsPrincipal principal,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<RecoverStaffAccountCommand, RecoverStaffAccountResult> handler,
        CancellationToken cancellationToken) =>
        RecoverAsync(userId, StaffAccountRecovery.ResetTwoFactor, null, principal, httpContext, antiforgery, handler, cancellationToken);

    private static async Task<IResult> RecoverAsync(
        int userId,
        StaffAccountRecovery operation,
        string? temporaryPassword,
        ClaimsPrincipal principal,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<RecoverStaffAccountCommand, RecoverStaffAccountResult> handler,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";

        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        if (!TryGetActorId(principal, out var actorId))
        {
            return TypedResults.Unauthorized();
        }

        var result = await handler.HandleAsync(
            new RecoverStaffAccountCommand(
                actorId,
                userId,
                operation,
                temporaryPassword,
                httpContext.Connection.RemoteIpAddress?.ToString(),
                httpContext.Request.Headers.UserAgent.ToString()),
            cancellationToken);

        return result switch
        {
            RecoverStaffAccountResult.Success success => TypedResults.Ok(
                new StaffAccountRecoveryResponse(success.UserId, success.Operation.ToString(), success.Changed)),
            RecoverStaffAccountResult.TargetNotFound => TypedResults.NotFound(),
            RecoverStaffAccountResult.SelfNotAllowed => ToProblem(
                RbacDenialReason.SelfEditNotAllowed,
                "You cannot do this to your own account. Use Security in your own menu instead."),
            RecoverStaffAccountResult.Forbidden forbidden => ToProblem(RbacDenialReason.NotAuthorized, forbidden.Message),
            RecoverStaffAccountResult.ProtectedAccount => ProtectedAccountProblem(
                "This account is protected. Its owner recovers it with the break-glass tool, not from the admin."),
            RecoverStaffAccountResult.GuardedAccount => GuardedAccountProblem(
                "Only the technical owner's account can reset this account's password or two-factor authentication, or unlock it."),
            RecoverStaffAccountResult.PasswordRejected rejected => TypedResults.ValidationProblem(
                new Dictionary<string, string[]>(StringComparer.Ordinal) { ["temporaryPassword"] = [.. rejected.Errors] },
                title: "That temporary password cannot be used."),
            _ => throw new InvalidOperationException($"Unhandled {nameof(RecoverStaffAccountResult)}: {result.GetType().Name}"),
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

    private static ProblemHttpResult ProtectedAccountProblem(string detail) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Protected account.",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["error"] = "protected_account" });

    private static ProblemHttpResult GuardedAccountProblem(string detail) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Guarded account.",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["error"] = "guarded_account" });

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

/// <summary>The body of <c>POST /api/admin/users/{id}/reset-password</c> (#254).</summary>
public sealed record ResetStaffPasswordRequest(string? TemporaryPassword);

/// <summary>
/// What a recovery did (#254). <paramref name="Operation"/> is <c>ResetPassword</c>, <c>Unlock</c> or
/// <c>ResetTwoFactor</c>; <paramref name="Changed"/> is false when there was nothing to do.
/// </summary>
public sealed record StaffAccountRecoveryResponse(int Id, string Operation, bool Changed);
