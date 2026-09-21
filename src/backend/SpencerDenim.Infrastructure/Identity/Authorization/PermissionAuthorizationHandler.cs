using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using SpencerDenim.Application.Admin.CurrentUser;

namespace SpencerDenim.Infrastructure.Identity.Authorization;

/// <summary>
/// The custom <c>IAuthorizationHandler</c> doc 03 §3a calls "the security boundary": it never trusts permission
/// claims on the principal, and instead asks <see cref="IEffectivePermissionsProvider"/> (cached per
/// <c>SecurityStamp</c>, doc 08 §3.1 R4) for the caller's current permissions on every check.
/// </summary>
public sealed class PermissionAuthorizationHandler(IEffectivePermissionsProvider permissionsProvider)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        var userIdValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
        {
            return;
        }

        // AuthorizationHandler<T> exposes no cancellation token; a permission check is a fast, cache-backed read.
        var permissions = await permissionsProvider.GetEffectivePermissionsAsync(userId, CancellationToken.None);

        if (permissions.Has(requirement.Permission))
        {
            context.Succeed(requirement);
        }
    }
}
