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
/// Issue #17: while a staff account has <c>MustChangePassword</c> set, every admin endpoint except the exempt
/// change-password flow is refused — a server-side check that reads the flag from the database, not the cookie.
/// </summary>
public sealed class PasswordChangeCompletedHandlerTests
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

    private const string CurrentStamp = "stamp-current";

    private static AdminUserSummary Summary(bool mustChangePassword, string securityStamp = CurrentStamp) =>
        new(42, "admin@example.com", "Ada", null, IsActive: true, MustChangePassword: mustChangePassword, SecurityStamp: securityStamp);

    private static ClaimsPrincipal StaffPrincipal(int userId = 42, string? sessionStamp = CurrentStamp)
    {
        List<Claim> claims =
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture)),
            new Claim(AdminAuthenticationDefaults.UserTypeClaimType, ((byte)UserType.Staff).ToString(CultureInfo.InvariantCulture)),
        ];
        if (sessionStamp is not null)
        {
            claims.Add(new Claim(AdminAuthenticationDefaults.SecurityStampClaimType, sessionStamp));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, AdminAuthenticationDefaults.Scheme));
    }

    private static AuthorizationHandlerContext ContextFor(ClaimsPrincipal user, object? resource = null) =>
        new([PasswordChangeCompletedRequirement.Instance], user, resource);

    private static DefaultHttpContext HttpContextFor(bool exempt)
    {
        var metadata = exempt
            ? new EndpointMetadataCollection(AllowWhilePasswordChangeRequiredMetadata.Instance)
            : new EndpointMetadataCollection();
        var httpContext = new DefaultHttpContext();
        httpContext.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "test"));
        return httpContext;
    }

    [Fact]
    public async Task Refuses_a_staff_user_whose_password_change_is_pending_with_the_password_change_reason()
    {
        var handler = new PasswordChangeCompletedHandler(new StubUserLookup(Summary(mustChangePassword: true)));
        var context = ContextFor(StaffPrincipal(), HttpContextFor(exempt: false));

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.True(context.HasFailed);
        Assert.Contains(context.FailureReasons, r => r is PasswordChangeRequiredFailureReason);
    }

    [Fact]
    public async Task Allows_a_staff_user_with_no_pending_change()
    {
        var handler = new PasswordChangeCompletedHandler(new StubUserLookup(Summary(mustChangePassword: false)));
        var context = ContextFor(StaffPrincipal(), HttpContextFor(exempt: false));

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Allows_an_exempt_endpoint_while_the_change_is_pending()
    {
        var handler = new PasswordChangeCompletedHandler(new StubUserLookup(Summary(mustChangePassword: true)));
        var context = ContextFor(StaffPrincipal(), HttpContextFor(exempt: true));

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Refuses_a_session_issued_before_the_stamp_changed_even_with_no_change_pending()
    {
        // Tester finding on #17: right after a forced change the flag is false, so without this a session opened
        // before the change had full access until its next cookie revalidation.
        var handler = new PasswordChangeCompletedHandler(new StubUserLookup(Summary(mustChangePassword: false, securityStamp: "stamp-after-change")));
        var context = ContextFor(StaffPrincipal(sessionStamp: "stamp-before-change"), HttpContextFor(exempt: false));

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.Contains(context.FailureReasons, r => r is SessionSupersededFailureReason);
    }

    [Fact]
    public async Task Refuses_a_stale_session_on_exempt_endpoints_too()
    {
        var handler = new PasswordChangeCompletedHandler(new StubUserLookup(Summary(mustChangePassword: false, securityStamp: "stamp-after-change")));
        var context = ContextFor(StaffPrincipal(sessionStamp: "stamp-before-change"), HttpContextFor(exempt: true));

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.Contains(context.FailureReasons, r => r is SessionSupersededFailureReason);
    }

    [Fact]
    public async Task Refuses_a_staff_session_that_carries_no_stamp_at_all()
    {
        var handler = new PasswordChangeCompletedHandler(new StubUserLookup(Summary(mustChangePassword: false)));
        var context = ContextFor(StaffPrincipal(sessionStamp: null), HttpContextFor(exempt: false));

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.Contains(context.FailureReasons, r => r is SessionSupersededFailureReason);
    }

    [Fact]
    public async Task Reads_the_exemption_from_an_endpoint_passed_as_the_resource_too()
    {
        var handler = new PasswordChangeCompletedHandler(new StubUserLookup(Summary(mustChangePassword: true)));
        var endpoint = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(AllowWhilePasswordChangeRequiredMetadata.Instance), "test");
        var context = ContextFor(StaffPrincipal(), endpoint);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Ignores_a_forged_claim_and_reads_the_flag_from_the_lookup()
    {
        // There is no claim that could clear the gate: even a principal carrying a made-up "password changed"
        // claim is refused while the database still says the change is pending.
        var handler = new PasswordChangeCompletedHandler(new StubUserLookup(Summary(mustChangePassword: true)));
        var principal = StaffPrincipal();
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("sd_must_change_password", "false"));
        var context = ContextFor(principal, HttpContextFor(exempt: false));

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Does_not_succeed_when_the_user_no_longer_exists()
    {
        var handler = new PasswordChangeCompletedHandler(new StubUserLookup(null));
        var context = ContextFor(StaffPrincipal(), HttpContextFor(exempt: false));

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Leaves_non_staff_principals_to_the_policy_other_requirements()
    {
        var lookup = new StubUserLookup(Summary(mustChangePassword: true));
        var handler = new PasswordChangeCompletedHandler(lookup);
        var context = ContextFor(new ClaimsPrincipal(new ClaimsIdentity()), HttpContextFor(exempt: false));

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
        Assert.Equal(0, lookup.Calls);
    }

    [Fact]
    public async Task The_default_policy_and_every_permission_policy_carry_the_gate()
    {
        // What makes the gate cover "every admin endpoint": a bare RequireAuthorization() uses the default
        // policy, and RequirePermission(...) uses one of the per-permission policies. Both must include it, so
        // a new admin endpoint is gated without having to opt in.
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

        Assert.Contains(PasswordChangeCompletedRequirement.Instance, options.DefaultPolicy.Requirements);
        foreach (var permission in PermissionCatalog.All)
        {
            var policy = options.GetPolicy(PermissionPolicy.NameFor(permission));
            Assert.NotNull(policy);
            Assert.Contains(PasswordChangeCompletedRequirement.Instance, policy.Requirements);
            Assert.Contains(policy.Requirements, r => r is PermissionRequirement p && p.Permission == permission);
        }
    }
}
