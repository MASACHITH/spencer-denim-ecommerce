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

    /// <summary>
    /// Lets any holder of a <c>Settings.*</c> permission reach this endpoint, for the settings <b>read</b>
    /// alone (ADM-22 + W3-03).
    /// </summary>
    /// <remarks>
    /// <b>Not a general-purpose "any of these".</b> <c>GET /api/admin/settings</c> returns a list of groups
    /// filtered per row against the caller's own permissions (doc 08 §3.8 DB2), so the route guard is not
    /// what decides what they see — and a single permission on it would shut a caller holding only
    /// <c>Settings.Payments</c> out of the screen entirely. Every settings <i>write</i> stays on
    /// <see cref="RequirePermission"/> with the one permission its group needs.
    /// </remarks>
    public static RouteHandlerBuilder RequireAnySettingsPermission(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.RequireAuthorization(PermissionPolicy.ReadAnySettingsGroup);
    }

    /// <summary>
    /// Lets a signed-in staff user reach this endpoint while their account still has a forced password change
    /// pending (issue #17, doc 11 P14 step 3). Every other authorized admin endpoint answers 403
    /// <c>password_change_required</c> until the password is changed. Only for the endpoints needed to get
    /// through that change: who-am-I, change-password and logout.
    /// </summary>
    public static TBuilder AllowWhilePasswordChangeRequired<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithMetadata(AllowWhilePasswordChangeRequiredMetadata.Instance);
    }

    /// <summary>
    /// Lets a signed-in GrantsAll holder reach this endpoint before they have enrolled in 2FA (W1-10, doc 08 §3.1
    /// N14). Every other authorized admin endpoint answers 403 <c>two_factor_enrolment_required</c> until they do.
    /// Only for who-am-I, logout, change-password and the enrolment endpoints themselves. It does not lift the
    /// forced password change, which has to be done first.
    /// </summary>
    public static TBuilder AllowWhileTwoFactorEnrolmentRequired<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithMetadata(AllowWhileTwoFactorEnrolmentRequiredMetadata.Instance);
    }
}
