using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.Account;
using SpencerDenim.Infrastructure.Identity;
using static SpencerDenim.Api.Endpoints.Admin.AdminSessionCookies;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The signed-in staff user's own two-factor authentication (W1-10; doc 08 §3.1 N14): status, enrolment with an
/// authenticator app, new recovery codes, and turning it off (never for GrantsAll holders). Status, setup and
/// enable stay reachable for a GrantsAll holder who has not enrolled (<c>AllowWhileTwoFactorEnrolmentRequired</c>),
/// but not before a forced password change is done. Every POST requires the antiforgery token.
/// </summary>
internal static class AdminTwoFactorEndpoints
{
    public static IEndpointRouteBuilder MapAdminTwoFactorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/admin/auth/two-factor").WithTags("Admin Two-Factor Authentication");

        api.MapGet(string.Empty, GetStatusAsync)
            .RequireAuthorization()
            .AllowWhileTwoFactorEnrolmentRequired()
            .WithName("AdminTwoFactorStatus")
            .Produces<AdminTwoFactorStatusResponse>()
            .Produces(401);

        api.MapPost("/setup", BeginSetupAsync)
            .RequireAuthorization()
            .AllowWhileTwoFactorEnrolmentRequired()
            .WithName("AdminTwoFactorSetup")
            .Produces<AdminTwoFactorSetupResponse>()
            .Produces(400)
            .Produces(401)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPost("/enable", EnableAsync)
            .RequireAuthorization()
            .AllowWhileTwoFactorEnrolmentRequired()
            .WithName("AdminTwoFactorEnable")
            .Produces<AdminRecoveryCodesResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(401)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // Only reachable once 2FA is on (or, for a non-GrantsAll user, never required), so no enrolment exemption.
        api.MapPost("/recovery-codes", RegenerateRecoveryCodesAsync)
            .RequireAuthorization()
            .WithName("AdminTwoFactorRecoveryCodes")
            .Produces<AdminRecoveryCodesResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(401)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status423Locked);

        api.MapPost("/disable", DisableAsync)
            .RequireAuthorization()
            .WithName("AdminTwoFactorDisable")
            .Produces(204)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(401)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status423Locked);

        return endpoints;
    }

    private static async Task<IResult> GetStatusAsync(
        ClaimsPrincipal user,
        IQueryHandler<GetAdminTwoFactorStatusQuery, AdminTwoFactorStatusResponse?> handler,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(user, out var userId))
        {
            return TypedResults.Unauthorized();
        }

        var response = await handler.HandleAsync(new GetAdminTwoFactorStatusQuery(userId), cancellationToken);
        return response is null ? TypedResults.Unauthorized() : TypedResults.Ok(response);
    }

    private static async Task<IResult> BeginSetupAsync(
        ClaimsPrincipal user,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<BeginAdminTwoFactorSetupCommand, AdminTwoFactorResult> handler,
        CancellationToken cancellationToken)
    {
        if (!await IsAntiforgeryTokenValidAsync(httpContext, antiforgery))
        {
            return InvalidAntiforgeryToken();
        }

        if (!TryGetUserId(user, out var userId))
        {
            return TypedResults.Unauthorized();
        }

        var result = await handler.HandleAsync(new BeginAdminTwoFactorSetupCommand(userId), cancellationToken);
        return await ToResponseAsync(httpContext, result);
    }

    private static async Task<IResult> EnableAsync(
        AdminTwoFactorCodeRequest request,
        ClaimsPrincipal user,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<EnableAdminTwoFactorCommand, AdminTwoFactorResult> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await IsAntiforgeryTokenValidAsync(httpContext, antiforgery))
        {
            return InvalidAntiforgeryToken();
        }

        if (!TryGetUserId(user, out var userId))
        {
            return TypedResults.Unauthorized();
        }

        var command = new EnableAdminTwoFactorCommand(userId, request.Code ?? string.Empty, IpAddress(httpContext), UserAgent(httpContext));
        var result = await handler.HandleAsync(command, cancellationToken);
        return await ToResponseAsync(httpContext, result);
    }

    private static async Task<IResult> RegenerateRecoveryCodesAsync(
        AdminCurrentPasswordRequest request,
        ClaimsPrincipal user,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<RegenerateAdminRecoveryCodesCommand, AdminTwoFactorResult> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await IsAntiforgeryTokenValidAsync(httpContext, antiforgery))
        {
            return InvalidAntiforgeryToken();
        }

        if (!TryGetUserId(user, out var userId))
        {
            return TypedResults.Unauthorized();
        }

        var command = new RegenerateAdminRecoveryCodesCommand(
            userId, request.CurrentPassword ?? string.Empty, IpAddress(httpContext), UserAgent(httpContext));
        var result = await handler.HandleAsync(command, cancellationToken);
        return await ToResponseAsync(httpContext, result);
    }

    private static async Task<IResult> DisableAsync(
        AdminCurrentPasswordRequest request,
        ClaimsPrincipal user,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<DisableAdminTwoFactorCommand, AdminTwoFactorResult> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await IsAntiforgeryTokenValidAsync(httpContext, antiforgery))
        {
            return InvalidAntiforgeryToken();
        }

        if (!TryGetUserId(user, out var userId))
        {
            return TypedResults.Unauthorized();
        }

        var command = new DisableAdminTwoFactorCommand(
            userId, request.CurrentPassword ?? string.Empty, IpAddress(httpContext), UserAgent(httpContext));
        var result = await handler.HandleAsync(command, cancellationToken);
        return await ToResponseAsync(httpContext, result);
    }

    /// <summary>
    /// One mapping for every 2FA result. Whenever a change rotated the security stamp, the caller's own cookie is
    /// re-issued with the new one, so this session carries on while every other session for the account is refused
    /// on its next request (the gate compares stamps).
    /// </summary>
    private static async Task<IResult> ToResponseAsync(HttpContext httpContext, AdminTwoFactorResult result)
    {
        switch (result)
        {
            case AdminTwoFactorResult.SetupStarted setup:
                await ReissueAsync(httpContext, setup.Session);
                DoNotStore(httpContext);
                return TypedResults.Ok(new AdminTwoFactorSetupResponse(setup.SharedKey, setup.AuthenticatorUri));

            case AdminTwoFactorResult.Enabled enabled:
                await ReissueAsync(httpContext, enabled.Session);
                DoNotStore(httpContext);
                return TypedResults.Ok(new AdminRecoveryCodesResponse(enabled.RecoveryCodes));

            case AdminTwoFactorResult.RecoveryCodesRegenerated regenerated:
                DoNotStore(httpContext);
                return TypedResults.Ok(new AdminRecoveryCodesResponse(regenerated.RecoveryCodes));

            case AdminTwoFactorResult.Disabled disabled:
                await ReissueAsync(httpContext, disabled.Session);
                return TypedResults.NoContent();

            case AdminTwoFactorResult.InvalidCode:
                return AdminAuthEndpoints.AccountProblem(
                    StatusCodes.Status400BadRequest, "invalid_code", "That code is not valid. Check the time on your phone and try again.");

            case AdminTwoFactorResult.InvalidCurrentPassword:
                return AdminAuthEndpoints.AccountProblem(StatusCodes.Status400BadRequest, "invalid_current_password", "The current password is incorrect.");

            case AdminTwoFactorResult.LockedOut lockedOut:
                return AdminAuthEndpoints.AccountProblem(
                    StatusCodes.Status423Locked,
                    "locked_out",
                    "This account is temporarily locked after repeated failed password attempts. Try again later.",
                    lockedOut.LockoutEndUtc);

            case AdminTwoFactorResult.AlreadyEnabled:
                return AdminAuthEndpoints.AccountProblem(
                    StatusCodes.Status409Conflict, "two_factor_already_enabled", "Two-factor authentication is already on for this account.");

            case AdminTwoFactorResult.NotEnabled:
                return AdminAuthEndpoints.AccountProblem(
                    StatusCodes.Status409Conflict, "two_factor_not_enabled", "Two-factor authentication is not on for this account.");

            case AdminTwoFactorResult.NotSetUp:
                return AdminAuthEndpoints.AccountProblem(
                    StatusCodes.Status409Conflict, "two_factor_not_set_up", "Start the set-up first, then enter the code your app shows.");

            case AdminTwoFactorResult.RequiredForRole:
                return AdminAuthEndpoints.AccountProblem(
                    StatusCodes.Status403Forbidden, "two_factor_required", "Two-factor authentication cannot be turned off for a Super Admin.");

            default:
                // The account behind this session is gone or deactivated: end the session outright.
                await httpContext.SignOutAsync(AdminAuthenticationDefaults.Scheme);
                return TypedResults.Unauthorized();
        }
    }

    private static Task ReissueAsync(HttpContext httpContext, ReissuedAdminSession session) =>
        SignInAsync(httpContext, new SessionIdentity(session.UserId, session.Email, session.FirstName, session.LastName, session.SecurityStamp));

    private static string? IpAddress(HttpContext httpContext) => httpContext.Connection.RemoteIpAddress?.ToString();

    private static string UserAgent(HttpContext httpContext) => httpContext.Request.Headers.UserAgent.ToString();
}

/// <summary>What the enrolment page shows: the key to type in by hand, and the URI its QR code encodes.</summary>
public sealed record AdminTwoFactorSetupResponse(string SharedKey, string AuthenticatorUri);

/// <summary>Recovery codes, shown once. Each signs in once in place of an authenticator code.</summary>
public sealed record AdminRecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

/// <summary>Body of <c>POST …/two-factor/enable</c>. Nullable so a missing code is answered like a wrong one.</summary>
public sealed record AdminTwoFactorCodeRequest(string? Code);

/// <summary>Body of <c>POST …/two-factor/recovery-codes</c> and <c>…/disable</c>: the current password, re-checked.</summary>
public sealed record AdminCurrentPasswordRequest(string? CurrentPassword);
