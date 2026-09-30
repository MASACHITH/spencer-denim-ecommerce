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
        var summary = new AdminUserSummary(5, "admin@example.com", "Ada", "Lovelace", IsActive: true, MustChangePassword: false, SecurityStamp: "stamp");
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
    public async Task Reports_a_pending_forced_password_change()
    {
        var summary = new AdminUserSummary(5, "admin@example.com", "Ada", null, IsActive: true, MustChangePassword: true, SecurityStamp: "stamp");
        var handler = new WhoAmIQueryHandler(new StubUserLookup(summary), new StubPermissionsProvider(new EffectivePermissions(true, [])));

        var response = await handler.HandleAsync(new WhoAmIQuery(5), TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.True(response.MustChangePassword);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public async Task Reports_two_factor_enrolment_as_required_only_for_a_grants_all_holder_without_it(
        bool grantsAll, bool twoFactorEnabled, bool expectedRequired)
    {
        // W1-10 / doc 08 §3.1 N14: the admin app sends such a user to the enrolment page at startup.
        var summary = new AdminUserSummary(5, "admin@example.com", "Ada", null, true, false, "stamp", twoFactorEnabled);
        var handler = new WhoAmIQueryHandler(new StubUserLookup(summary), new StubPermissionsProvider(new EffectivePermissions(grantsAll, [])));

        var response = await handler.HandleAsync(new WhoAmIQuery(5), TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.Equal(twoFactorEnabled, response.TwoFactorEnabled);
        Assert.Equal(expectedRequired, response.TwoFactorEnrolmentRequired);
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
        var summary = new AdminUserSummary(5, "admin@example.com", "Ada", "Lovelace", IsActive: false, MustChangePassword: false, SecurityStamp: "stamp");
        var handler = new WhoAmIQueryHandler(new StubUserLookup(summary), new StubPermissionsProvider(new EffectivePermissions(false, [])));

        var response = await handler.HandleAsync(new WhoAmIQuery(5), TestContext.Current.CancellationToken);

        Assert.Null(response);
    }
}
