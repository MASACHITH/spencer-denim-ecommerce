using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using SpencerDenim.Application.Admin.CurrentUser;

namespace SpencerDenim.Infrastructure.Identity.Authorization;

/// <summary>
/// An endpoint that any <b>one</b> of several permissions opens.
/// <para>
/// <b>It exists for exactly one shape of endpoint and should not spread.</b> Doc 03 §3a's rule is that an
/// admin endpoint declares <i>the</i> permission it needs, and a route that accepts several is usually a
/// route that should have been two. The case this was built for is the settings <i>read</i>
/// (<c>GET /api/admin/settings</c>): its payload is a list of groups, each row of which carries its own
/// <c>RequiredPermission</c> and is filtered against the caller's permissions individually (doc 08 §3.8
/// DB2). So the route guard is not deciding what the caller may see — the read model is — and a single
/// permission on the route would mean a staff member holding only <c>Settings.Payments</c> could not reach
/// the screen at all, not even to read the one group that is theirs.
/// </para>
/// <para>
/// <b>ADM-22 wrote this limitation down and left it to the task that would meet it.</b>
/// <c>AdminSettingsEndpoints</c>' own remarks say so: "this guard is what has to widen when the payments
/// group lands — the per-row filter in the read model already does the rest." W3-03 is the task that lands
/// it, because the <c>Bank.*</c> keys are the first in the catalogue that are not <c>Settings.Edit</c>.
/// </para>
/// <para>
/// <b>Requirements are ANDed by ASP.NET Core</b>, which is why this is one requirement holding a set
/// rather than several <see cref="PermissionRequirement"/>s: adding two of those to a policy demands
/// <i>both</i>.
/// </para>
/// </summary>
public sealed class AnyPermissionRequirement : IAuthorizationRequirement
{
    public AnyPermissionRequirement(params string[] permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        if (permissions.Length == 0)
        {
            // A requirement holding no permissions would succeed for nobody and read as a locked endpoint
            // with no explanation. Loudly at startup, where it is a configuration mistake.
            throw new ArgumentException(
                "An any-permission requirement needs at least one permission.",
                nameof(permissions));
        }

        Permissions = permissions;
    }

    public IReadOnlyList<string> Permissions { get; }
}

/// <summary>
/// Succeeds when the caller holds any one of <see cref="AnyPermissionRequirement.Permissions"/>.
/// </summary>
/// <remarks>
/// Reads the caller's <i>effective</i> permissions from their roles, never claims on the principal — the
/// same rule and the same reason as <see cref="PermissionAuthorizationHandler"/> (doc 08 §3.1 R4): a cookie
/// carries what was true when it was issued.
/// </remarks>
public sealed class AnyPermissionAuthorizationHandler(IEffectivePermissionsProvider permissionsProvider)
    : AuthorizationHandler<AnyPermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AnyPermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        var userIdValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
        {
            return;
        }

        var permissions = await permissionsProvider.GetEffectivePermissionsAsync(userId, CancellationToken.None);

        if (requirement.Permissions.Any(permissions.Has))
        {
            context.Succeed(requirement);
        }
    }
}
