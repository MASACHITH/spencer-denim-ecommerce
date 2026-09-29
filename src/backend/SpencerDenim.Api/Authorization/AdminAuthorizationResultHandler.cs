using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Identity.Authorization;

namespace SpencerDenim.Api.Authorization;

/// <summary>
/// Turns refusals by the admin account gate into responses the admin app can act on (issue #17):
/// <list type="bullet">
/// <item><description>a session issued before the account's security stamp changed (for example, before a
/// password change made elsewhere) is signed out and answered 401 <c>session_ended</c>, the same outcome the
/// periodic cookie revalidation reaches, only immediately;</description></item>
/// <item><description>a pending forced password change is answered 403 <c>password_change_required</c>, so the
/// app can send the user to the change-password page instead of showing a generic "not allowed".</description></item>
/// </list>
/// Every other outcome is left to the framework's default handler, which already answers 401/403 through the
/// cookie scheme's events.
/// </summary>
internal sealed class AdminAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        var reasons = authorizeResult.Forbidden
            ? authorizeResult.AuthorizationFailure?.FailureReasons ?? []
            : [];

        if (reasons.OfType<SessionSupersededFailureReason>().Any())
        {
            await context.SignOutAsync(AdminAuthenticationDefaults.Scheme);
            await TypedResults.Problem(
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "Session ended.",
                    detail: "This session has ended. Sign in again.",
                    extensions: new Dictionary<string, object?> { ["error"] = SessionSupersededFailureReason.Code })
                .ExecuteAsync(context);
            return;
        }

        if (reasons.OfType<PasswordChangeRequiredFailureReason>().Any())
        {
            await TypedResults.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Password change required.",
                    detail: "Change your password before using the admin panel.",
                    extensions: new Dictionary<string, object?> { ["error"] = PasswordChangeRequiredFailureReason.Code })
                .ExecuteAsync(context);
            return;
        }

        await _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
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
