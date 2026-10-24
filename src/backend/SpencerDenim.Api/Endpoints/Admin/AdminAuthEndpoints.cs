using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.Account;
using SpencerDenim.Application.Admin.Authentication;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Infrastructure.Identity;
using static SpencerDenim.Api.Endpoints.Admin.AdminSessionCookies;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// Admin login (both steps when the account has 2FA on, W1-10), logout, "who am I" and change-password (doc 03
/// §3a; ADM-01; issue #17). Who-am-I, change-password and logout are the only authorized admin endpoints a user
/// with a pending forced password change can reach (<c>AllowWhilePasswordChangeRequired</c>); they are also open
/// to a GrantsAll holder who has not enrolled in 2FA yet, together with the enrolment endpoints
/// (<see cref="AdminTwoFactorEndpoints"/>). The antiforgery-token endpoint is anonymous anyway.
/// </summary>
internal static class AdminAuthEndpoints
{
    public static IEndpointRouteBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/admin/auth").WithTags("Admin Authentication");

        // No session cookie exists yet at this point, so there is nothing for an antiforgery token to protect;
        // this is the conventional exemption for a login endpoint (doc 03 §4 rationale in the final report).
        api.MapPost("/login", LoginAsync)
            .WithName("AdminLogin")
            .Produces<AdminLoginResponse>()
            .Produces<AdminTwoFactorChallengeResponse>()
            .ProducesValidationProblem()
            .Produces(401)
            .Produces(423);

        // Anonymous at the admin scheme: the caller only holds the pending cookie from the password step. It does
        // carry that cookie, so unlike /login this step requires the antiforgery token like every other POST.
        api.MapPost("/login/two-factor", CompleteTwoFactorLoginAsync)
            .AllowAnonymous()
            .WithName("AdminLoginTwoFactor")
            .Produces<AdminLoginResponse>()
            .Produces(400)
            .Produces(401)
            .Produces(423);

        api.MapPost("/logout", LogoutAsync)
            .RequireAuthorization()
            .AllowWhilePasswordChangeRequired()
            .AllowWhileTwoFactorEnrolmentRequired()
            .WithName("AdminLogout")
            .Produces(204)
            .Produces(401)
            .Produces(400);

        api.MapGet("/whoami", WhoAmIAsync)
            .RequireAuthorization()
            .AllowWhilePasswordChangeRequired()
            .AllowWhileTwoFactorEnrolmentRequired()
            .WithName("AdminWhoAmI")
            .Produces<WhoAmIResponse>()
            .Produces(401);

        api.MapPost("/change-password", ChangePasswordAsync)
            .RequireAuthorization()
            .AllowWhilePasswordChangeRequired()
            .AllowWhileTwoFactorEnrolmentRequired()
            .WithName("AdminChangePassword")
            .Produces(204)
            .ProducesValidationProblem()
            .Produces(401)
            .ProducesProblem(StatusCodes.Status423Locked);

        // The Angular admin app fetches this once to have a token ready for the first mutating call (logout).
        api.MapGet("/antiforgery-token", GetAntiforgeryToken).WithName("AdminAntiforgeryToken").Produces<AntiforgeryTokenResponse>();

        return endpoints;
    }

    private static async Task<IResult> LoginAsync(
        AdminLoginRequest request,
        HttpContext httpContext,
        ICommandHandler<AdminLoginCommand, AdminLoginResult> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Issue #47. `System.Text.Json` does not honour nullable reference annotations, so a body that omits a
        // property ({}, or {"email":"a@b.c"}) binds that property as null, and the null used to travel into
        // AdminAuthenticator, whose ThrowIfNull guards answered 500. Those guards are right as internal
        // invariants; they are the wrong first check for a hostile request. An empty string is a valid, if wrong,
        // credential and still goes through the normal path, so it costs the same as any other wrong password.
        var missing = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.Email is null)
        {
            missing["email"] = ["The email field is required."];
        }

        if (request.Password is null)
        {
            missing["password"] = ["The password field is required."];
        }

        if (missing.Count > 0)
        {
            return TypedResults.ValidationProblem(
                missing,
                title: "The sign-in request is missing a field.",
                extensions: new Dictionary<string, object?> { ["code"] = "missing_field" });
        }

        var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = httpContext.Request.Headers.UserAgent.ToString();
        var command = new AdminLoginCommand(request.Email!, request.Password!, ipAddress, userAgent);

        var result = await handler.HandleAsync(command, cancellationToken);

        if (result is AdminLoginResult.TwoFactorRequired challenge)
        {
            // W1-10: no session yet. The pending cookie ties the second step to this user and this stamp.
            await BeginTwoFactorSignInAsync(
                httpContext, new PendingTwoFactorSignIn(challenge.UserId, challenge.SecurityStamp, challenge.PendingSignInId));
            return TypedResults.Ok(new AdminTwoFactorChallengeResponse(TwoFactorRequired: true));
        }

        return await CompleteLoginAsync(httpContext, result);
    }

    /// <summary>
    /// W1-10, the second sign-in step: an authenticator code or a recovery code for the user the pending cookie
    /// names. A wrong code leaves the pending sign-in open for another try (each counts towards the lockout); the
    /// lockout, success, or a stale pending sign-in all delete the pending cookie.
    /// </summary>
    private static async Task<IResult> CompleteTwoFactorLoginAsync(
        AdminTwoFactorLoginRequest request,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<CompleteAdminTwoFactorSignInCommand, AdminLoginResult> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await IsAntiforgeryTokenValidAsync(httpContext, antiforgery))
        {
            return InvalidAntiforgeryToken();
        }

        var pending = await ReadTwoFactorSignInAsync(httpContext);
        if (pending is null)
        {
            return TwoFactorSessionExpired();
        }

        var command = new CompleteAdminTwoFactorSignInCommand(
            pending.UserId,
            pending.SecurityStamp,
            pending.SignInId,
            request.Code ?? string.Empty,
            request.IsRecoveryCode,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString());

        var result = await handler.HandleAsync(command, cancellationToken);

        switch (result)
        {
            case AdminLoginResult.InvalidTwoFactorCode:
                return TypedResults.Json(
                    new AdminLoginErrorResponse("invalid_two_factor_code", "That code is not valid. Check it and try again.", null),
                    statusCode: StatusCodes.Status401Unauthorized);

            case AdminLoginResult.TwoFactorSessionExpired:
                await EndTwoFactorSignInAsync(httpContext);
                return TwoFactorSessionExpired();

            default:
                await EndTwoFactorSignInAsync(httpContext);
                return await CompleteLoginAsync(httpContext, result);
        }
    }

    /// <summary>The replies shared by both sign-in steps: a session on success, otherwise lockout or a generic 401.</summary>
    private static async Task<IResult> CompleteLoginAsync(HttpContext httpContext, AdminLoginResult result)
    {
        switch (result)
        {
            case AdminLoginResult.Success success:
                await SignInAsync(
                    httpContext,
                    new SessionIdentity(success.UserId, success.Email, success.FirstName, success.LastName, success.SecurityStamp));
                return TypedResults.Ok(new AdminLoginResponse(
                    success.UserId,
                    success.Email,
                    success.FirstName,
                    success.LastName,
                    success.GrantsAllPermissions,
                    success.Permissions,
                    success.MustChangePassword,
                    success.TwoFactorEnabled,
                    success.TwoFactorEnrolmentRequired));

            // Only the password step can reach this from here (the second step answers it before it gets this far):
            // another sign-in took the account's pending sign-in between the password check and its write (#145).
            case AdminLoginResult.TwoFactorSessionExpired:
                return TwoFactorSessionExpired();

            case AdminLoginResult.LockedOut lockedOut:
                return TypedResults.Json(
                    new AdminLoginErrorResponse("locked_out", "This account is temporarily locked after repeated failed sign-in attempts. Try again later.", lockedOut.LockoutEndUtc),
                    statusCode: StatusCodes.Status423Locked);

            default:
                return TypedResults.Json(
                    new AdminLoginErrorResponse("invalid_credentials", "Invalid email or password.", null),
                    statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    private static JsonHttpResult<AdminLoginErrorResponse> TwoFactorSessionExpired() =>
        TypedResults.Json(
            new AdminLoginErrorResponse("two_factor_session_expired", "Your sign-in has expired. Enter your email and password again.", null),
            statusCode: StatusCodes.Status401Unauthorized);

    private static async Task<IResult> LogoutAsync(HttpContext httpContext, IAntiforgery antiforgery)
    {
        if (!await IsAntiforgeryTokenValidAsync(httpContext, antiforgery))
        {
            return InvalidAntiforgeryToken();
        }

        await httpContext.SignOutAsync(AdminAuthenticationDefaults.Scheme);
        await EndTwoFactorSignInAsync(httpContext);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Issue #17: the signed-in staff user changes their own password — the forced change after the first Super
    /// Admin's bootstrap sign-in (doc 08 §9), or any voluntary change. On success the stamp has been rotated, so
    /// the caller is signed straight back in with the new stamp (this session carries on) while every other
    /// session for the account fails its next revalidation (doc 08 §3.1 DB10, at most one interval later).
    /// </summary>
    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        ClaimsPrincipal user,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<ChangeAdminPasswordCommand, ChangeAdminPasswordResult> handler,
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

        var command = new ChangeAdminPasswordCommand(
            userId,
            request.CurrentPassword ?? string.Empty,
            request.NewPassword ?? string.Empty,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString());

        var result = await handler.HandleAsync(command, cancellationToken);

        switch (result)
        {
            case ChangeAdminPasswordResult.Success success:
                await SignInAsync(
                    httpContext,
                    new SessionIdentity(success.UserId, success.Email, success.FirstName, success.LastName, success.SecurityStamp));
                return TypedResults.NoContent();

            case ChangeAdminPasswordResult.InvalidCurrentPassword:
                return AccountProblem(StatusCodes.Status400BadRequest, "invalid_current_password", "The current password is incorrect.");

            case ChangeAdminPasswordResult.NewPasswordRejected rejected:
                return TypedResults.ValidationProblem(
                    new Dictionary<string, string[]> { ["newPassword"] = [.. rejected.Errors] },
                    title: "The new password does not meet the password rules.",
                    extensions: new Dictionary<string, object?> { ["error"] = "new_password_rejected" });

            case ChangeAdminPasswordResult.LockedOut lockedOut:
                return AccountProblem(
                    StatusCodes.Status423Locked,
                    "locked_out",
                    "This account is temporarily locked after repeated failed password attempts. Try again later.",
                    lockedOut.LockoutEndUtc);

            default:
                // The account behind this session is gone or deactivated: end the session outright.
                await httpContext.SignOutAsync(AdminAuthenticationDefaults.Scheme);
                return TypedResults.Unauthorized();
        }
    }

    /// <summary>Problem-details reply of the signed-in account endpoints, with the machine-readable <c>error</c> code.</summary>
    internal static ProblemHttpResult AccountProblem(int statusCode, string error, string detail, DateTimeOffset? lockoutEndUtc = null)
    {
        var extensions = new Dictionary<string, object?> { ["error"] = error };
        if (lockoutEndUtc is not null)
        {
            extensions["lockoutEndUtc"] = lockoutEndUtc;
        }

        return TypedResults.Problem(statusCode: statusCode, title: detail, detail: detail, extensions: extensions);
    }

    private static async Task<IResult> WhoAmIAsync(
        ClaimsPrincipal user,
        IQueryHandler<WhoAmIQuery, WhoAmIResponse?> handler,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(user, out var userId))
        {
            return TypedResults.Unauthorized();
        }

        var response = await handler.HandleAsync(new WhoAmIQuery(userId), cancellationToken);
        return response is null ? TypedResults.Unauthorized() : TypedResults.Ok(response);
    }

    private static Ok<AntiforgeryTokenResponse> GetAntiforgeryToken(HttpContext httpContext, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(httpContext);
        return TypedResults.Ok(new AntiforgeryTokenResponse(tokens.RequestToken ?? string.Empty));
    }
}

public sealed record AdminLoginRequest(string? Email, string? Password);

/// <summary>
/// A completed sign-in. <see cref="TwoFactorEnrolmentRequired"/> (W1-10): a GrantsAll holder without 2FA, whom the
/// admin app sends to the enrolment page; the API refuses everything else until they enrol.
/// </summary>
public sealed record AdminLoginResponse(
    int UserId,
    string Email,
    string FirstName,
    string? LastName,
    bool GrantsAllPermissions,
    IReadOnlyCollection<string> Permissions,
    bool MustChangePassword,
    bool TwoFactorEnabled,
    bool TwoFactorEnrolmentRequired)
{
    /// <summary>Always false here; lets the client tell this reply apart from <see cref="AdminTwoFactorChallengeResponse"/>.</summary>
    public bool TwoFactorRequired => false;
}

/// <summary>W1-10: the password was right, and a code is needed to finish signing in. No session exists yet.</summary>
public sealed record AdminTwoFactorChallengeResponse(bool TwoFactorRequired);

/// <summary>
/// Body of <c>POST /api/admin/auth/login/two-factor</c>. <see cref="Code"/> is nullable so a missing code is answered
/// like a wrong one rather than with a binding error.
/// </summary>
public sealed record AdminTwoFactorLoginRequest(string? Code, bool IsRecoveryCode);

/// <summary>
/// Body of <c>POST /api/admin/auth/change-password</c>. Nullable so a missing field is answered like an empty
/// one (wrong current password, or a new password that fails the rules) rather than with a binding error.
/// </summary>
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record AdminLoginErrorResponse(string Error, string Message, DateTimeOffset? LockoutEndUtc);

public sealed record AntiforgeryTokenResponse(string Token);
