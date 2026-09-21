using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Infrastructure.Identity.Authorization;

namespace SpencerDenim.UnitTests.Infrastructure.Identity;

/// <summary>
/// The security boundary doc 03 §3a describes: <c>RequirePermission("X")</c> resolves to a policy backed by
/// this handler, which must never trust anything on the principal itself.
/// </summary>
public sealed class PermissionAuthorizationHandlerTests
{
    private sealed class StubPermissionsProvider(EffectivePermissions permissions) : IEffectivePermissionsProvider
    {
        public int? RequestedUserId { get; private set; }

        public Task<EffectivePermissions> GetEffectivePermissionsAsync(int userId, CancellationToken cancellationToken)
        {
            RequestedUserId = userId;
            return Task.FromResult(permissions);
        }
    }

    private static ClaimsPrincipal PrincipalFor(int userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString(System.Globalization.CultureInfo.InvariantCulture))], "Test"));

    private static AuthorizationHandlerContext ContextFor(ClaimsPrincipal user, string permission) =>
        new([new PermissionRequirement(permission)], user, resource: null);

    [Fact]
    public async Task Succeeds_when_the_user_holds_the_required_permission()
    {
        var provider = new StubPermissionsProvider(new EffectivePermissions(false, ["Orders.Approve"]));
        var handler = new PermissionAuthorizationHandler(provider);
        var context = ContextFor(PrincipalFor(42), "Orders.Approve");

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
        Assert.Equal(42, provider.RequestedUserId);
    }

    [Fact]
    public async Task Fails_when_the_user_does_not_hold_the_required_permission()
    {
        var provider = new StubPermissionsProvider(new EffectivePermissions(false, ["Orders.View"]));
        var handler = new PermissionAuthorizationHandler(provider);
        var context = ContextFor(PrincipalFor(42), "Orders.Approve");

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Succeeds_for_a_grants_all_user_regardless_of_the_requested_permission()
    {
        var provider = new StubPermissionsProvider(new EffectivePermissions(true, []));
        var handler = new PermissionAuthorizationHandler(provider);
        var context = ContextFor(PrincipalFor(1), "Settings.Security");

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Fails_without_consulting_the_provider_when_the_principal_has_no_user_id_claim()
    {
        var provider = new StubPermissionsProvider(new EffectivePermissions(true, []));
        var handler = new PermissionAuthorizationHandler(provider);
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var context = ContextFor(anonymous, "Orders.View");

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.Null(provider.RequestedUserId);
    }

    [Fact]
    public async Task Ignores_a_forged_permission_claim_on_the_principal_itself()
    {
        // Doc 08 §3.1 R4: "never trusts perm claims on the principal or in the cookie." Even if a claim named
        // "perm" with the requested value is smuggled onto the principal, only the DB-backed provider decides.
        var provider = new StubPermissionsProvider(new EffectivePermissions(false, []));
        var handler = new PermissionAuthorizationHandler(provider);
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "42"),
                new Claim("perm", "Orders.Approve"),
            ],
            "Test");
        var context = ContextFor(new ClaimsPrincipal(identity), "Orders.Approve");

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }
}
