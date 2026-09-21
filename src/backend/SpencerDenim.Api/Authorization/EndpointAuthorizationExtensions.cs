using SpencerDenim.Infrastructure.Identity.Authorization;

namespace SpencerDenim.Api.Authorization;

/// <summary>
/// Doc 03 §3a: "every admin endpoint declares <c>RequirePermission("Orders.Approve")</c>." A policy per
/// permission is pre-registered at startup (see <c>IdentityRegistration.AddAdminIdentity</c>), so this is a thin
/// convenience wrapper over <see cref="AuthorizationEndpointConventionBuilderExtensions.RequireAuthorization"/>.
/// No endpoint in this slice calls it yet (Users/Products/Orders admin endpoints don't exist), but it is
/// unit-tested end to end through <c>PermissionAuthorizationHandler</c> and ready for the next admin slice.
/// </summary>
public static class EndpointAuthorizationExtensions
{
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permission)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.RequireAuthorization(PermissionPolicy.NameFor(permission));
    }
}
