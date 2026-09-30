using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Infrastructure.Identity.Authorization;

/// <summary>
/// Enforces <see cref="TwoFactorEnrolmentCompletedRequirement"/>. Both inputs come from the database, never from a
/// cookie claim: <c>TwoFactorEnabled</c> through the same per-request user read as
/// <see cref="PasswordChangeCompletedHandler"/> (the second read of the same row is served by the request's
/// DbContext), and GrantsAll through <see cref="IEffectivePermissionsProvider"/>, cached per security stamp.
/// The stale-session check lives in <see cref="PasswordChangeCompletedHandler"/>, which every policy carries too.
/// </summary>
public sealed class TwoFactorEnrolmentCompletedHandler(IAdminUserLookup userLookup, IEffectivePermissionsProvider permissionsProvider)
    : AuthorizationHandler<TwoFactorEnrolmentCompletedRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TwoFactorEnrolmentCompletedRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        // Same scoping as the password gate: only staff principals are this gate's business.
        if (context.User.FindFirstValue(AdminAuthenticationDefaults.UserTypeClaimType)
            != ((byte)UserType.Staff).ToString(CultureInfo.InvariantCulture))
        {
            context.Succeed(requirement);
            return;
        }

        if (IsExempt(context.Resource))
        {
            context.Succeed(requirement);
            return;
        }

        var userIdValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
        {
            return;
        }

        // AuthorizationHandler<T> exposes no cancellation token; these are single primary-key reads.
        var user = await userLookup.FindByIdAsync(userId, CancellationToken.None);
        if (user is null)
        {
            return;
        }

        if (user.TwoFactorEnabled)
        {
            context.Succeed(requirement);
            return;
        }

        var permissions = await permissionsProvider.GetEffectivePermissionsAsync(userId, CancellationToken.None);
        if (TwoFactorPolicy.IsRequired(permissions.GrantsAllPermissions))
        {
            context.Fail(new TwoFactorEnrolmentRequiredFailureReason(this));
            return;
        }

        context.Succeed(requirement);
    }

    /// <summary>See <see cref="PasswordChangeCompletedHandler"/>: the resource is the HttpContext with endpoint routing.</summary>
    private static bool IsExempt(object? resource)
    {
        var endpoint = resource switch
        {
            HttpContext httpContext => httpContext.GetEndpoint(),
            Endpoint e => e,
            _ => null,
        };

        return endpoint?.Metadata.GetMetadata<AllowWhileTwoFactorEnrolmentRequiredMetadata>() is not null;
    }
}
