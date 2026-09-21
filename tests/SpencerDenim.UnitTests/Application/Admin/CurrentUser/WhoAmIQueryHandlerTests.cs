using SpencerDenim.Application.Admin.CurrentUser;

namespace SpencerDenim.UnitTests.Application.Admin.CurrentUser;

public sealed class WhoAmIQueryHandlerTests
{
    private sealed class StubUserLookup(AdminUserSummary? summary) : IAdminUserLookup
    {
        public Task<AdminUserSummary?> FindByIdAsync(int userId, CancellationToken cancellationToken) => Task.FromResult(summary);
    }

    private sealed class StubPermissionsProvider(EffectivePermissions permissions) : IEffectivePermissionsProvider
    {
        public Task<EffectivePermissions> GetEffectivePermissionsAsync(int userId, CancellationToken cancellationToken) =>
            Task.FromResult(permissions);
    }

    [Fact]
    public async Task Returns_profile_and_permissions_for_an_active_user()
    {
        var summary = new AdminUserSummary(5, "admin@example.com", "Ada", "Lovelace", IsActive: true);
        var permissions = new EffectivePermissions(false, ["Orders.View"]);
        var handler = new WhoAmIQueryHandler(new StubUserLookup(summary), new StubPermissionsProvider(permissions));

        var response = await handler.HandleAsync(new WhoAmIQuery(5), TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.Equal(5, response.UserId);
        Assert.Equal("admin@example.com", response.Email);
        Assert.False(response.GrantsAllPermissions);
        Assert.Contains("Orders.View", response.Permissions);
    }

    [Fact]
    public async Task Returns_null_when_the_user_no_longer_exists()
    {
        var handler = new WhoAmIQueryHandler(new StubUserLookup(null), new StubPermissionsProvider(new EffectivePermissions(false, [])));

        var response = await handler.HandleAsync(new WhoAmIQuery(99), TestContext.Current.CancellationToken);

        Assert.Null(response);
    }

    [Fact]
    public async Task Returns_null_for_a_deactivated_user()
    {
        var summary = new AdminUserSummary(5, "admin@example.com", "Ada", "Lovelace", IsActive: false);
        var handler = new WhoAmIQueryHandler(new StubUserLookup(summary), new StubPermissionsProvider(new EffectivePermissions(false, [])));

        var response = await handler.HandleAsync(new WhoAmIQuery(5), TestContext.Current.CancellationToken);

        Assert.Null(response);
    }
}
