using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Identity.Authorization;

namespace SpencerDenim.UnitTests.Infrastructure.Identity;

/// <summary>
/// W1-10: a GrantsAll holder without 2FA is refused everywhere except the exempt enrolment endpoints; everybody
/// else, and a GrantsAll holder with 2FA on, passes. Both facts are read from the database-backed ports.
/// </summary>
public sealed class TwoFactorEnrolmentCompletedHandlerTests
{
    private sealed class StubUserLookup(AdminUserSummary? summary) : IAdminUserLookup
    {
        public int Calls { get; private set; }

        public Task<AdminUserSummary?> FindByIdAsync(int userId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(summary);
        }
    }

    private sealed class StubPermissionsProvider(bool grantsAll) : IEffectivePermissionsProvider
    {
        public Task<EffectivePermissions> GetEffectivePermissionsAsync(int userId, CancellationToken cancellationToken) =>
            Task.FromResult(new EffectivePermissions(grantsAll, []));
    }

    private static AdminUserSummary Summary(bool twoFactorEnabled) =>
        new(42, "admin@example.com", "Ada", null, IsActive: true, MustChangePassword: false, SecurityStamp: "stamp", TwoFactorEnabled: twoFactorEnabled);

    private static ClaimsPrincipal StaffPrincipal() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "42"),
                new Claim(AdminAuthenticationDefaults.UserTypeClaimType, ((byte)UserType.Staff).ToString(CultureInfo.InvariantCulture)),
                new Claim(AdminAuthenticationDefaults.SecurityStampClaimType, "stamp"),
            ],
            AdminAuthenticationDefaults.Scheme));

    private static AuthorizationHandlerContext ContextFor(ClaimsPrincipal user, object? resource) =>
        new([TwoFactorEnrolmentCompletedRequirement.Instance], user, resource);

    private static DefaultHttpContext HttpContextFor(params object[] metadata)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test"));
        return httpContext;
    }

    [Fact]
    public async Task Refuses_a_grants_all_holder_without_two_factor_with_the_enrolment_reason()
    {
        var handler = new TwoFactorEnrolmentCompletedHandler(new StubUserLookup(Summary(twoFactorEnabled: false)), new StubPermissionsProvider(grantsAll: true));
        var context = ContextFor(StaffPrincipal(), HttpContextFor());

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.Contains(context.FailureReasons, r => r is TwoFactorEnrolmentRequiredFailureReason);
    }

    [Fact]
    public async Task Allows_a_grants_all_holder_with_two_factor_on()
    {
        var handler = new TwoFactorEnrolmentCompletedHandler(new StubUserLookup(Summary(twoFactorEnabled: true)), new StubPermissionsProvider(grantsAll: true));
        var context = ContextFor(StaffPrincipal(), HttpContextFor());

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Allows_other_staff_without_two_factor_because_it_is_optional_for_them()
    {
        var handler = new TwoFactorEnrolmentCompletedHandler(new StubUserLookup(Summary(twoFactorEnabled: false)), new StubPermissionsProvider(grantsAll: false));
        var context = ContextFor(StaffPrincipal(), HttpContextFor());

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Allows_an_enrolment_endpoint_for_a_grants_all_holder_without_two_factor()
    {
        var lookup = new StubUserLookup(Summary(twoFactorEnabled: false));
        var handler = new TwoFactorEnrolmentCompletedHandler(lookup, new StubPermissionsProvider(grantsAll: true));
        var context = ContextFor(StaffPrincipal(), HttpContextFor(AllowWhileTwoFactorEnrolmentRequiredMetadata.Instance));

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task The_password_change_exemption_does_not_exempt_from_enrolment()
    {
        // Only who-am-I, logout and change-password carry both; an endpoint exempt from the password gate alone
        // is still closed to a GrantsAll holder without 2FA.
        var handler = new TwoFactorEnrolmentCompletedHandler(new StubUserLookup(Summary(twoFactorEnabled: false)), new StubPermissionsProvider(grantsAll: true));
        var context = ContextFor(StaffPrincipal(), HttpContextFor(AllowWhilePasswordChangeRequiredMetadata.Instance));

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Does_not_succeed_when_the_user_no_longer_exists()
    {
        var handler = new TwoFactorEnrolmentCompletedHandler(new StubUserLookup(null), new StubPermissionsProvider(grantsAll: false));
        var context = ContextFor(StaffPrincipal(), HttpContextFor());

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Leaves_non_staff_principals_to_the_policy_other_requirements()
    {
        var lookup = new StubUserLookup(Summary(twoFactorEnabled: false));
        var handler = new TwoFactorEnrolmentCompletedHandler(lookup, new StubPermissionsProvider(grantsAll: true));
        var context = ContextFor(new ClaimsPrincipal(new ClaimsIdentity()), HttpContextFor());

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
        Assert.Equal(0, lookup.Calls);
    }

    [Fact]
    public async Task The_default_policy_and_every_permission_policy_carry_both_gates_password_first()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = "Server=example;Database=SpencerDenim;" })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddRouting();
        services.AddInfrastructure();
        await using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        AssertGates(options.DefaultPolicy);
        foreach (var permission in PermissionCatalog.All)
        {
            var policy = options.GetPolicy(PermissionPolicy.NameFor(permission));
            Assert.NotNull(policy);
            AssertGates(policy);
        }

        static void AssertGates(AuthorizationPolicy policy)
        {
            var requirements = policy.Requirements.ToList();
            var password = requirements.IndexOf(PasswordChangeCompletedRequirement.Instance);
            var twoFactor = requirements.IndexOf(TwoFactorEnrolmentCompletedRequirement.Instance);
            Assert.True(password >= 0 && twoFactor > password);
        }
    }
}
