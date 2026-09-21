using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.Authentication;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>Admin login, logout and "who am I" (doc 03 §3a; ADM-01).</summary>
internal static class AdminAuthEndpoints
{
    public static IEndpointRouteBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/admin/auth").WithTags("Admin Authentication");

        // No session cookie exists yet at this point, so there is nothing for an antiforgery token to protect;
        // this is the conventional exemption for a login endpoint (doc 03 §4 rationale in the final report).
        api.MapPost("/login", LoginAsync).WithName("AdminLogin").Produces<AdminLoginResponse>().Produces(401).Produces(423);

        api.MapPost("/logout", LogoutAsync).RequireAuthorization().WithName("AdminLogout").Produces(204).Produces(401).Produces(400);

        api.MapGet("/whoami", WhoAmIAsync).RequireAuthorization().WithName("AdminWhoAmI").Produces<WhoAmIResponse>().Produces(401);

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
                await SignInAsync(httpContext, success);
                return TypedResults.Ok(new AdminLoginResponse(
                    success.UserId,
                    success.Email,
                    success.FirstName,
                    success.LastName,
                    success.GrantsAllPermissions,
                    success.Permissions));

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

    private static Task SignInAsync(HttpContext httpContext, AdminLoginResult.Success success)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, success.UserId.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Email, success.Email),
            new(ClaimTypes.GivenName, success.FirstName),
            new(AdminAuthenticationDefaults.UserTypeClaimType, ((byte)UserType.Staff).ToString(CultureInfo.InvariantCulture)),
        };

        if (!string.IsNullOrWhiteSpace(success.LastName))
        {
            claims.Add(new Claim(ClaimTypes.Surname, success.LastName));
        }

        var identity = new ClaimsIdentity(claims, AdminAuthenticationDefaults.Scheme);
        var principal = new ClaimsPrincipal(identity);

        return httpContext.SignInAsync(AdminAuthenticationDefaults.Scheme, principal);
    }

    private static async Task<IResult> LogoutAsync(HttpContext httpContext, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return TypedResults.BadRequest(new AdminLoginErrorResponse("invalid_antiforgery_token", "Missing or invalid antiforgery token.", null));
        }

        await httpContext.SignOutAsync(AdminAuthenticationDefaults.Scheme);
        return TypedResults.NoContent();
    }

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
    IReadOnlyCollection<string> Permissions);

public sealed record AdminLoginErrorResponse(string Error, string Message, DateTimeOffset? LockoutEndUtc);

public sealed record AntiforgeryTokenResponse(string Token);
