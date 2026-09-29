using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using SpencerDenim.Infrastructure.Identity.Authorization;

namespace SpencerDenim.Api.Authorization;

/// <summary>
/// Turns a refusal by the forced-password-change gate into a 403 problem-details response carrying the
/// <c>password_change_required</c> code (issue #17), so the admin app can send the user to the change-password
/// page instead of showing a generic "not allowed". Every other outcome is left to the framework's default
/// handler, which already answers 401/403 through the cookie scheme's events.
/// </summary>
internal sealed class AdminAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        var passwordChangeRequired = authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure?.FailureReasons.OfType<PasswordChangeRequiredFailureReason>().Any() == true;

        if (!passwordChangeRequired)
        {
            return _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
        }

        return TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Password change required.",
                detail: "Change your password before using the admin panel.",
                extensions: new Dictionary<string, object?> { ["error"] = PasswordChangeRequiredFailureReason.Code })
            .ExecuteAsync(context);
    }
}

/// <summary>Registers <see cref="AdminAuthorizationResultHandler"/>.</summary>
internal static class AdminAuthorizationResultHandlerRegistration
{
    public static IServiceCollection AddAdminAuthorizationResponses(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAuthorizationMiddlewareResultHandler, AdminAuthorizationResultHandler>();

        return services;
    }
}
