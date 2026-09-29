using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.Account;
using SpencerDenim.Application.Admin.Authentication;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// Admin login, logout, "who am I" and change-password (doc 03 §3a; ADM-01; issue #17). These are the only
/// authorized admin endpoints a user with a pending forced password change can reach
/// (<c>AllowWhilePasswordChangeRequired</c>); the antiforgery-token endpoint is anonymous anyway.
/// </summary>
internal static class AdminAuthEndpoints
{
    public static IEndpointRouteBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/admin/auth").WithTags("Admin Authentication");

        // No session cookie exists yet at this point, so there is nothing for an antiforgery token to protect;
        // this is the conventional exemption for a login endpoint (doc 03 §4 rationale in the final report).
        api.MapPost("/login", LoginAsync).WithName("AdminLogin").Produces<AdminLoginResponse>().Produces(401).Produces(423);

        api.MapPost("/logout", LogoutAsync)
            .RequireAuthorization()
            .AllowWhilePasswordChangeRequired()
            .WithName("AdminLogout")
            .Produces(204)
            .Produces(401)
            .Produces(400);

        api.MapGet("/whoami", WhoAmIAsync)
            .RequireAuthorization()
            .AllowWhilePasswordChangeRequired()
            .WithName("AdminWhoAmI")
            .Produces<WhoAmIResponse>()
            .Produces(401);

        api.MapPost("/change-password", ChangePasswordAsync)
            .RequireAuthorization()
            .AllowWhilePasswordChangeRequired()
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

        var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = httpContext.Request.Headers.UserAgent.ToString();
        var command = new AdminLoginCommand(request.Email, request.Password, ipAddress, userAgent);

        var result = await handler.HandleAsync(command, cancellationToken);

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
                    success.MustChangePassword));

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

    /// <summary>The fields the admin cookie principal is built from, at sign-in and after a password change.</summary>
    private sealed record SessionIdentity(int UserId, string Email, string FirstName, string? LastName, string SecurityStamp);

    private static Task SignInAsync(HttpContext httpContext, SessionIdentity session)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, session.UserId.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Email, session.Email),
            new(ClaimTypes.GivenName, session.FirstName),
            new(AdminAuthenticationDefaults.UserTypeClaimType, ((byte)UserType.Staff).ToString(CultureInfo.InvariantCulture)),
            new(AdminAuthenticationDefaults.SecurityStampClaimType, session.SecurityStamp),
        };

        if (!string.IsNullOrWhiteSpace(session.LastName))
        {
            claims.Add(new Claim(ClaimTypes.Surname, session.LastName));
        }

        var identity = new ClaimsIdentity(claims, AdminAuthenticationDefaults.Scheme);
        var principal = new ClaimsPrincipal(identity);

        return httpContext.SignInAsync(AdminAuthenticationDefaults.Scheme, principal);
    }

    private static async Task<IResult> LogoutAsync(HttpContext httpContext, IAntiforgery antiforgery)
    {
        if (!await IsAntiforgeryTokenValidAsync(httpContext, antiforgery))
        {
            return InvalidAntiforgeryToken();
        }

        await httpContext.SignOutAsync(AdminAuthenticationDefaults.Scheme);
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

        var userIdValue = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
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
                return ChangePasswordProblem(StatusCodes.Status400BadRequest, "invalid_current_password", "The current password is incorrect.");

            case ChangeAdminPasswordResult.NewPasswordRejected rejected:
                return TypedResults.ValidationProblem(
                    new Dictionary<string, string[]> { ["newPassword"] = [.. rejected.Errors] },
                    title: "The new password does not meet the password rules.",
                    extensions: new Dictionary<string, object?> { ["error"] = "new_password_rejected" });

            case ChangeAdminPasswordResult.LockedOut lockedOut:
                return ChangePasswordProblem(
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

    private static ProblemHttpResult ChangePasswordProblem(int statusCode, string error, string detail, DateTimeOffset? lockoutEndUtc = null)
    {
        var extensions = new Dictionary<string, object?> { ["error"] = error };
        if (lockoutEndUtc is not null)
        {
            extensions["lockoutEndUtc"] = lockoutEndUtc;
        }

        return TypedResults.Problem(statusCode: statusCode, title: detail, detail: detail, extensions: extensions);
    }

    private static async Task<bool> IsAntiforgeryTokenValidAsync(HttpContext httpContext, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static BadRequest<AdminLoginErrorResponse> InvalidAntiforgeryToken() =>
        TypedResults.BadRequest(new AdminLoginErrorResponse("invalid_antiforgery_token", "Missing or invalid antiforgery token.", null));

    private static async Task<IResult> WhoAmIAsync(
        ClaimsPrincipal user,
        IQueryHandler<WhoAmIQuery, WhoAmIResponse?> handler,
        CancellationToken cancellationToken)
    {
        var userIdValue = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
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

public sealed record AdminLoginRequest(string Email, string Password);

public sealed record AdminLoginResponse(
    int UserId,
    string Email,
    string FirstName,
    string? LastName,
    bool GrantsAllPermissions,
    IReadOnlyCollection<string> Permissions,
    bool MustChangePassword);

/// <summary>
/// Body of <c>POST /api/admin/auth/change-password</c>. Nullable so a missing field is answered like an empty
/// one (wrong current password, or a new password that fails the rules) rather than with a binding error.
/// </summary>
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record AdminLoginErrorResponse(string Error, string Message, DateTimeOffset? LockoutEndUtc);

public sealed record AntiforgeryTokenResponse(string Token);
