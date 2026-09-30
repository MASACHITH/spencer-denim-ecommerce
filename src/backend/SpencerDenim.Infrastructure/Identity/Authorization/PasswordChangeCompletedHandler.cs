using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Infrastructure.Identity.Authorization;

/// <summary>
/// Enforces <see cref="PasswordChangeCompletedRequirement"/>. The flag is read from the database on every check
/// (one indexed primary-key read, the same cost as <see cref="PermissionAuthorizationHandler"/>'s stamp read),
/// never from a cookie claim: a claim would stay stale for up to the revalidation interval after the flag
/// changes, and this gate exists precisely because the bootstrap password must not stay usable.
/// </summary>
public sealed class PasswordChangeCompletedHandler(IAdminUserLookup userLookup)
    : AuthorizationHandler<PasswordChangeCompletedRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PasswordChangeCompletedRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        // Only staff principals carry this flag. Anything else (no principal yet, or a future customer-scheme
        // principal) is not this gate's business: the policy's other requirements decide.
        if (context.User.FindFirstValue(AdminAuthenticationDefaults.UserTypeClaimType)
            != ((byte)UserType.Staff).ToString(CultureInfo.InvariantCulture))
        {
            context.Succeed(requirement);
            return;
        }

        var userIdValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
        {
            return;
        }

        // AuthorizationHandler<T> exposes no cancellation token; this is a single primary-key read.
        var user = await userLookup.FindByIdAsync(userId, CancellationToken.None);
        if (user is null)
        {
            return;
        }

        // A password change rotates the stamp and clears the flag in one update. Without this check, every
        // session opened before the change (another browser, or a copy of the old cookie) would pass the gate
        // with full access until its next cookie revalidation, up to a minute later. Checked before the
        // exemptions, so such a session cannot use who-am-I or change-password either; the caller's own
        // session was re-issued with the new stamp and is unaffected.
        var sessionStamp = context.User.FindFirstValue(AdminAuthenticationDefaults.SecurityStampClaimType);
        if (string.IsNullOrEmpty(sessionStamp) || !string.Equals(sessionStamp, user.SecurityStamp, StringComparison.Ordinal))
        {
            context.Fail(new SessionSupersededFailureReason(this));
            return;
        }

        if (IsExempt(context.Resource))
        {
            context.Succeed(requirement);
            return;
        }

        if (user.MustChangePassword)
        {
            context.Fail(new PasswordChangeRequiredFailureReason(this));
            return;
        }

        context.Succeed(requirement);
    }

    /// <summary>
    /// With endpoint routing the authorization middleware passes the <see cref="HttpContext"/> as the resource;
    /// an <see cref="Endpoint"/> is accepted too so the handler also works when invoked with one directly.
    /// </summary>
    private static bool IsExempt(object? resource)
    {
        var endpoint = resource switch
        {
            HttpContext httpContext => httpContext.GetEndpoint(),
            Endpoint e => e,
            _ => null,
        };

        return endpoint?.Metadata.GetMetadata<AllowWhilePasswordChangeRequiredMetadata>() is not null;
    }
}
