using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Identity.Seeding;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// ADM-01 admin login/logout/whoami and the admin seeding path, end to end against a real SQL Server database
/// (doc 06 §4 "Integration | xUnit + Testcontainers SQL Server + WebApplicationFactory"). All test methods share
/// one class so they run sequentially against the one migrated database (see <see cref="TestDatabaseMigrator"/>).
/// <para>
/// Only <see cref="Admin_seeding_creates_exactly_one_super_admin_who_can_then_sign_in"/> ever sets
/// <c>Seed:Admin:Enabled</c>: doing that from more than one test would race the "only ever seed once" gate
/// against the same shared database (whichever test runs first would win, and the API contract under test —
/// idempotency — only makes sense measured from a clean slate). Every other test creates its own plain staff
/// user directly instead.
/// </para>
/// </summary>
public sealed class AdminAuthEndpointTests(SqlServerDatabaseFixture sqlServer)
{
    private const string LoginUrl = "/api/admin/auth/login";
    private const string LogoutUrl = "/api/admin/auth/logout";
    private const string WhoAmIUrl = "/api/admin/auth/whoami";
    private const string AntiforgeryUrl = "/api/admin/auth/antiforgery-token";

    [Fact]
    public async Task Login_succeeds_for_an_active_staff_user_and_whoami_returns_their_profile()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("staff-login");
        const string password = "Correct-Horse-Battery-Staple-1!";
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserAsync(factory, email, password, isActive: true);
        using var client = CreateClient(factory);

        using var loginResponse = await client.PostAsJsonAsync(LoginUrl, new { email, password }, TestContext.Current.CancellationToken);
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        Assert.Equal(email, loginBody.GetProperty("email").GetString());
        Assert.False(loginBody.GetProperty("grantsAllPermissions").GetBoolean());
        Assert.False(loginBody.GetProperty("mustChangePassword").GetBoolean());

        using var whoAmIResponse = await client.GetAsync(new Uri(WhoAmIUrl, UriKind.Relative), TestContext.Current.CancellationToken);
        var whoAmIBody = await whoAmIResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, whoAmIResponse.StatusCode);
        Assert.Equal(email, whoAmIBody.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Login_fails_with_a_generic_error_for_the_wrong_password()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("wrong-password");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserAsync(factory, email, "Correct-Password-1!", isActive: true);
        using var client = CreateClient(factory);

        using var response = await client.PostAsJsonAsync(LoginUrl, new { email, password = "totally-wrong" }, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_credentials", body.GetProperty("error").GetString());
        await AssertAuditActionWrittenAsync(factory, email, "LoginFailed");
    }

    [Fact]
    public async Task Login_fails_for_an_unknown_email()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        using var client = CreateClient(factory);

        using var response = await client.PostAsJsonAsync(
            LoginUrl,
            new { email = UniqueEmail("nobody"), password = "whatever-1!" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_fails_for_a_deactivated_staff_account_even_with_the_correct_password()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("inactive");
        const string password = "Correct-Password-1!";
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserAsync(factory, email, password, isActive: false);
        using var client = CreateClient(factory);

        using var response = await client.PostAsJsonAsync(LoginUrl, new { email, password }, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_credentials", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Account_locks_out_after_the_configured_number_of_failed_attempts()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("lockout");
        const string password = "Correct-Password-1!";
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserAsync(factory, email, password, isActive: true);
        using var client = CreateClient(factory);

        // AdminSecurityOptions default: 5 failed attempts trigger lockout (doc 08 §3.8 Security.StaffLockoutMaxFailures).
        // ASP.NET Core Identity locks the account out on the attempt that REACHES the threshold, not the one
        // after, so the 5th wrong attempt itself is already the locked-out response.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var failed = await client.PostAsJsonAsync(LoginUrl, new { email, password = "wrong" }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        using var lockedResponse = await client.PostAsJsonAsync(LoginUrl, new { email, password = "wrong" }, TestContext.Current.CancellationToken);
        var lockedBody = await lockedResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal((HttpStatusCode)423, lockedResponse.StatusCode);
        Assert.Equal("locked_out", lockedBody.GetProperty("error").GetString());
        await AssertAuditActionWrittenAsync(factory, email, "Lockout");

        // Even the correct password is rejected while the time-boxed lockout is in effect.
        using var stillLockedResponse = await client.PostAsJsonAsync(LoginUrl, new { email, password }, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)423, stillLockedResponse.StatusCode);
    }

    /// <summary>
    /// Regression test for the defect in issue #16: before this fix, <c>IsActive</c> was only checked at login and
    /// on <c>/whoami</c>, so a deactivated staff account's cookie kept authenticating every other endpoint for the
    /// rest of its 60-minute sliding lifetime. <c>AdminSecurity:SecurityStampValidationIntervalSeconds</c> is
    /// shrunk to 1 second here so the test proves the real revalidation path without waiting out a real minute.
    /// Deliberately doesn't also assert that a request made *before* the interval elapses is still accepted: with
    /// a real HTTP client against a real SQL Server, the round-trips around that request are themselves not
    /// reliably faster than a 1-second window, which would make that assertion flaky rather than meaningful — the
    /// throttling itself is a plain time comparison, in no way dependent on doing this over HTTP.
    /// </summary>
    [Fact]
    public async Task A_deactivated_admin_is_rejected_on_the_next_request_once_the_revalidation_interval_has_passed()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("deactivated-mid-session");
        const string password = "Correct-Password-1!";
        var configuration = new Dictionary<string, string?> { ["AdminSecurity:SecurityStampValidationIntervalSeconds"] = "1" };
        await using var factory = new ApiFactory(sqlServer.ConnectionString, additionalConfiguration: configuration);
        var userId = await CreateStaffUserAsync(factory, email, password, isActive: true);
        using var client = CreateClient(factory);

        using var login = await client.PostAsJsonAsync(LoginUrl, new { email, password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        await DeactivateStaffUserAsync(factory, userId);
        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);

        // Same cookie, no new login: the next request re-checks the database because the interval has elapsed,
        // finds IsActive false, and ends the session — instead of trusting the cookie's claims for the rest of
        // its 60-minute sliding lifetime, which is the defect this test guards against.
        using var whoAmIAfterInterval = await client.GetAsync(new Uri(WhoAmIUrl, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, whoAmIAfterInterval.StatusCode);
    }

    /// <summary>
    /// Same mechanism as the deactivation test above, but for the other trigger doc 08 §3.1 DB10 names: a
    /// security-stamp bump (what a password reset does, once that feature exists) ends the session too, even
    /// though <c>IsActive</c> never changes.
    /// </summary>
    [Fact]
    public async Task A_security_stamp_bump_rejects_the_session_on_the_next_request_once_the_revalidation_interval_has_passed()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("stamp-bumped-mid-session");
        const string password = "Correct-Password-1!";
        var configuration = new Dictionary<string, string?> { ["AdminSecurity:SecurityStampValidationIntervalSeconds"] = "1" };
        await using var factory = new ApiFactory(sqlServer.ConnectionString, additionalConfiguration: configuration);
        var userId = await CreateStaffUserAsync(factory, email, password, isActive: true);
        using var client = CreateClient(factory);

        using var login = await client.PostAsJsonAsync(LoginUrl, new { email, password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        await BumpSecurityStampAsync(factory, userId);
        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);

        using var whoAmIAfterInterval = await client.GetAsync(new Uri(WhoAmIUrl, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, whoAmIAfterInterval.StatusCode);
    }

    [Fact]
    public async Task WhoAmI_requires_authentication()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        using var client = CreateClient(factory);

        using var response = await client.GetAsync(new Uri(WhoAmIUrl, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_requires_a_valid_antiforgery_token_and_then_ends_the_session()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("logout");
        const string password = "Correct-Password-1!";
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserAsync(factory, email, password, isActive: true);
        using var client = CreateClient(factory);

        using var login = await client.PostAsJsonAsync(LoginUrl, new { email, password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // Without a token: rejected, and the session is still intact.
        using var unauthorizedLogout = await client.PostAsync(new Uri(LogoutUrl, UriKind.Relative), content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, unauthorizedLogout.StatusCode);

        using var tokenResponse = await client.GetAsync(new Uri(AntiforgeryUrl, UriKind.Relative), TestContext.Current.CancellationToken);
        var tokenBody = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var token = tokenBody.GetProperty("token").GetString();

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, LogoutUrl);
        logoutRequest.Headers.Add("X-XSRF-TOKEN", token);
        using var logoutResponse = await client.SendAsync(logoutRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        using var whoAmIAfterLogout = await client.GetAsync(new Uri(WhoAmIUrl, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, whoAmIAfterLogout.StatusCode);
    }

    /// <summary>
    /// Regression test for a real defect: with the default (secure) cookie policy, this exact request — made
    /// with a client that is NOT artificially flagged as HTTPS the way <see cref="CreateClient"/> is — used to
    /// 500 on the antiforgery endpoint (<c>AntiforgeryOptions.Cookie.SecurePolicy = Always</c> throws outright
    /// when the request isn't HTTPS) and would have silently dropped the admin auth cookie too. This is exactly
    /// what happens locally with <c>dotnet run</c> (no Caddy/TLS in front at all), which is why
    /// <c>appsettings.Development.json</c> sets <c>AdminSecurity:RequireSecureCookies=false</c> — reproduced
    /// here explicitly via configuration instead, so the test doesn't depend on the ASPNETCORE_ENVIRONMENT name.
    /// Deliberately uses the factory's default <c>CreateClient()</c> (plain <c>http://localhost</c>), not the
    /// HTTPS-flagged <see cref="CreateClient"/> helper the other tests use — that helper is exactly what let
    /// this regression slip through the suite once already.
    /// </summary>
    [Fact]
    public async Task Antiforgery_token_and_logout_work_over_a_plain_http_request_when_secure_cookies_are_relaxed()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("plain-http");
        const string password = "Correct-Password-1!";
        var configuration = new Dictionary<string, string?> { ["AdminSecurity:RequireSecureCookies"] = "false" };
        await using var factory = new ApiFactory(sqlServer.ConnectionString, additionalConfiguration: configuration);
        await CreateStaffUserAsync(factory, email, password, isActive: true);

        // No WebApplicationFactoryClientOptions override: this is a genuinely plain-HTTP request, exactly the
        // condition that broke under CookieSecurePolicy.Always.
        using var client = factory.CreateClient();

        using var login = await client.PostAsJsonAsync(LoginUrl, new { email, password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // Proves the auth cookie actually round-tripped over plain HTTP (it would not have, Secure-flagged).
        using var whoAmI = await client.GetAsync(new Uri(WhoAmIUrl, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, whoAmI.StatusCode);

        // The endpoint that previously threw 500 here.
        using var tokenResponse = await client.GetAsync(new Uri(AntiforgeryUrl, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var tokenBody = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var token = tokenBody.GetProperty("token").GetString();

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, LogoutUrl);
        logoutRequest.Headers.Add("X-XSRF-TOKEN", token);
        using var logoutResponse = await client.SendAsync(logoutRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        using var whoAmIAfterLogout = await client.GetAsync(new Uri(WhoAmIUrl, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, whoAmIAfterLogout.StatusCode);
    }

    /// <summary>
    /// Regression test for the defect the tester found in W1-01's forwarded-headers round 3 (see MEMORY.md,
    /// 2026-09-18): with no real proxy in front of this API yet (W0-15 Docker/Caddy is still TODO),
    /// <c>ForwardedHeadersSetup</c> deliberately leaves <c>X-Forwarded-For</c> out of
    /// <c>ForwardedHeadersOptions.ForwardedHeaders</c> — trusting that header from *any* direct caller, even a
    /// loopback one (which is ASP.NET Core's own middleware default, not some special-cased exemption), let an
    /// unauthenticated caller plant an arbitrary IP address straight into <c>ops.AuditLogs.IpAddress</c> (ADM-03)
    /// through this very endpoint. This proves that protection still holds today: a forged header from a direct,
    /// non-proxied caller must never reach the audit row a login attempt writes.
    /// <para>
    /// Placed here rather than in a new unit test (W1-11, #8): the old blocker note (TASKS.md / MEMORY.md) said
    /// <c>Microsoft.AspNetCore.HttpOverrides</c> types didn't resolve in "the plain-SDK unit test project" and
    /// guessed it might be machine-specific. Reproduced on this machine too — it isn't: <c>SpencerDenim.UnitTests</c>
    /// is a plain <c>Microsoft.NET.Sdk</c> project with only a <c>ProjectReference</c> to <c>SpencerDenim.Api</c>
    /// (a <c>Microsoft.NET.Sdk.Web</c> project); a <c>ProjectReference</c> alone does not carry the ASP.NET Core
    /// shared framework's ref assemblies down to a consuming plain-SDK project — that needs either the Web SDK
    /// itself or an explicit <c>FrameworkReference</c> (or a package that brings one). This integration test
    /// project already has that, via its <c>Microsoft.AspNetCore.Mvc.Testing</c> package reference, so the type
    /// resolves here with no project changes needed. It is also simply the right layer for this particular
    /// regression: proving it requires driving the real HTTP pipeline (<see cref="WebApplicationFactory{TEntryPoint}"/>
    /// / TestServer) end to end — header in, audit row out — not just exercising the options object in isolation.
    /// </para>
    /// <para>
    /// <see cref="TestServer"/> has no real socket, so <c>HttpContext.Connection.RemoteIpAddress</c> is <c>null</c>
    /// by default — there is no "real peer" for the test to fall back on the way a live Kestrel connection would
    /// have one. <see cref="SimulatedRemoteIpStartupFilter"/> stands in for that real peer with a fixed, known
    /// address, stamped onto the connection before <c>UseForwardedHeaders</c> runs (it must not be the loopback
    /// address <see cref="ForwardedHeadersSetup"/> also happens to pin <c>KnownIPNetworks</c> to, or a false pass
    /// could hide the middleware actually trusting the header instead of ignoring it). The header then claims a
    /// different address again, so the only way the test passes is if the audit row ends up with the *simulated
    /// connection's* address and neither of the header's.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_forged_X_Forwarded_For_header_from_a_direct_caller_does_not_reach_the_audit_log()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("forged-xff");
        const string password = "Correct-Password-1!";
        var realPeer = IPAddress.Parse("198.51.100.7");
        const string forgedHeaderValue = "203.0.113.99";

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserAsync(factory, email, password, isActive: true);

        await using var factoryWithSimulatedPeer = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new SimulatedRemoteIpStartupFilter(realPeer))));
        using var client = factoryWithSimulatedPeer.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        using var request = new HttpRequestMessage(HttpMethod.Post, LoginUrl)
        {
            Content = JsonContent.Create(new { email, password }),
        };
        // A direct caller forging the header a real reverse proxy would otherwise be the one to set — exactly the
        // shape of the exploit the tester's round-3 repro used against this login endpoint.
        request.Headers.Add("X-Forwarded-For", forgedHeaderValue);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var recordedIp = await GetAuditIpAddressAsync(factory, email, "Login");
        Assert.Equal(realPeer.ToString(), recordedIp);
        Assert.NotEqual(forgedHeaderValue, recordedIp);
    }

    /// <summary>Stamps a fixed "real connection" peer address onto every request, standing in for the real socket peer a live Kestrel connection would have that <see cref="TestServer"/> does not simulate on its own.</summary>
    private sealed class SimulatedRemoteIpStartupFilter(IPAddress remoteIpAddress) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = remoteIpAddress;
                await nextMiddleware(context);
            });
            next(app);
        };
    }

    [Fact]
    public async Task Admin_seeding_creates_exactly_one_super_admin_who_can_then_sign_in()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("seeded-super-admin");
        const string password = "Correct-Horse-Battery-Staple-1!";
        var configuration = SeedConfiguration(email, password);
        var normalizedEmail = email.ToUpperInvariant();

        await using (var firstFactory = new ApiFactory(sqlServer.ConnectionString, additionalConfiguration: configuration))
        {
            // Touching Services starts the host, which runs AdminSeedHostedService synchronously as part of startup.
            _ = firstFactory.Services;

            await using var firstScope = firstFactory.Services.CreateAsyncScope();
            var firstUserManager = firstScope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var holdersAfterFirstBoot = await firstUserManager.GetUsersInRoleAsync(AdminSeedHostedService.SuperAdminRoleName);
            var seeded = Assert.Single(holdersAfterFirstBoot, u => string.Equals(u.NormalizedEmail, normalizedEmail, StringComparison.Ordinal));

            // Issue #17 / doc 08 §9: the bootstrap password from .env must be replaced on first sign-in.
            Assert.True(seeded.MustChangePassword);

            using var client = CreateClient(firstFactory);
            using var loginResponse = await client.PostAsJsonAsync(LoginUrl, new { email, password }, TestContext.Current.CancellationToken);
            var loginBody = await loginResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
            Assert.True(loginBody.GetProperty("grantsAllPermissions").GetBoolean());
            Assert.True(loginBody.GetProperty("mustChangePassword").GetBoolean());
        }

        // A second host start with the same configuration must not create a second Super Admin.
        await using var secondFactory = new ApiFactory(sqlServer.ConnectionString, additionalConfiguration: configuration);
        _ = secondFactory.Services;

        await using var secondScope = secondFactory.Services.CreateAsyncScope();
        var userManager = secondScope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var matchingUsers = await userManager.Users
            .Where(u => u.NormalizedEmail == normalizedEmail)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Single(matchingUsers);
    }

    private static string UniqueEmail(string label) => $"{label}-{Guid.NewGuid():N}@example.com";

    private static Dictionary<string, string?> SeedConfiguration(string email, string password) => new()
    {
        ["Seed:Admin:Enabled"] = "true",
        ["Seed:Admin:Email"] = email,
        ["Seed:Admin:Password"] = password,
    };

    /// <summary>
    /// The admin cookie is <c>Secure</c> (doc 03 §3a), so it is only ever sent back over an HTTPS request.
    /// <see cref="Microsoft.AspNetCore.TestHost.TestServer"/> has no real TLS, but it does respect the scheme of
    /// the request URI for <c>HttpContext.Request.IsHttps</c>, so pointing the test client at an https base
    /// address is enough for cookie authentication to round-trip exactly as it would in production.
    /// </summary>
    private static HttpClient CreateClient(ApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    private static async Task<int> CreateStaffUserAsync(ApiFactory factory, string email, string password, bool isActive)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            UserType = UserType.Staff,
            FirstName = "Test",
            LastName = "Staff",
            IsActive = isActive,
            CreatedAtUtc = DateTime.UtcNow,
        };

        var result = await userManager.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        return user.Id;
    }

    /// <summary>
    /// Stands in for the (not yet built, see W1-08) admin "deactivate user" action: sets <c>IsActive</c> directly
    /// through <see cref="UserManager{TUser}"/>, exactly as that future endpoint will.
    /// </summary>
    private static async Task DeactivateStaffUserAsync(ApiFactory factory, int userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var user = await userManager.FindByIdAsync(userId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.NotNull(user);
        user.IsActive = false;
        var result = await userManager.UpdateAsync(user);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
    }

    /// <summary>
    /// Stands in for whatever future action bumps the security stamp (a password reset, once that feature
    /// exists — <c>UserManager</c> does this automatically for its own password-changing methods).
    /// </summary>
    private static async Task BumpSecurityStampAsync(ApiFactory factory, int userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var user = await userManager.FindByIdAsync(userId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.NotNull(user);
        var result = await userManager.UpdateSecurityStampAsync(user);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
    }

    private static async Task AssertAuditActionWrittenAsync(ApiFactory factory, string email, string action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var found = await dbContext.AuditLogs.AnyAsync(a => a.UserEmail == email && a.Action == action, TestContext.Current.CancellationToken);
        Assert.True(found, $"Expected an AuditLogs row with Action '{action}' for '{email}'.");
    }

    /// <summary>The <c>IpAddress</c> column of the audit row a login attempt wrote, for asserting what actually landed there.</summary>
    private static async Task<string?> GetAuditIpAddressAsync(ApiFactory factory, string email, string action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.AuditLogs
            .Where(a => a.UserEmail == email && a.Action == action)
            .Select(a => a.IpAddress)
            .SingleAsync(TestContext.Current.CancellationToken);
    }
}
