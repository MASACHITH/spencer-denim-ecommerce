using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// Issue #17 end to end against a real SQL Server database: a staff account with <c>MustChangePassword</c> set
/// can sign in, but every admin endpoint other than who-am-I / change-password / logout answers 403
/// <c>password_change_required</c> until the password is changed through <c>POST /api/admin/auth/change-password</c>.
/// <para>
/// No admin feature endpoint exists yet (Users, Products, Orders arrive in later slices), so the gate is proven
/// against two probe endpoints mapped only in this test host (<see cref="ProbeEndpointsStartupFilter"/>): one
/// with a bare <c>RequireAuthorization()</c> (the default policy) and one with <c>RequirePermission(...)</c>
/// (a permission policy). Those are exactly the two ways every future admin endpoint is declared.
/// </para>
/// <para>
/// None of these tests enables <c>Seed:Admin</c> or assigns the Super Admin role: that would race
/// <see cref="AdminAuthEndpointTests.Admin_seeding_creates_exactly_one_super_admin_who_can_then_sign_in"/>'s
/// "seed only when no holder exists" gate on the shared database.
/// </para>
/// </summary>
public sealed class AdminPasswordChangeEndpointTests(SqlServerDatabaseFixture sqlServer)
{
    private const string LoginUrl = "/api/admin/auth/login";
    private const string LogoutUrl = "/api/admin/auth/logout";
    private const string WhoAmIUrl = "/api/admin/auth/whoami";
    private const string AntiforgeryUrl = "/api/admin/auth/antiforgery-token";
    private const string ChangePasswordUrl = "/api/admin/auth/change-password";
    private const string AuthorizedProbeUrl = "/api/admin/test-probe/authorized";
    private const string PermissionProbeUrl = "/api/admin/test-probe/permission";

    private const string BootstrapPassword = "Bootstrap-Password-1!";
    private const string NewPassword = "Brand-New-Password-2!";

    [Fact]
    public async Task A_user_who_must_change_their_password_is_blocked_everywhere_else_until_they_do()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("must-change");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await using var host = WithProbeEndpoints(factory);
        var userId = await CreateStaffUserAsync(host, email, BootstrapPassword, mustChangePassword: true);
        using var client = CreateClient(host);

        // Sign-in succeeds and says a change is required.
        using var login = await client.PostAsJsonAsync(LoginUrl, new { email, password = BootstrapPassword }, Ct);
        var loginBody = await login.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(loginBody.GetProperty("mustChangePassword").GetBoolean());

        // Who-am-I is reachable and says the same, so the admin app can redirect at startup.
        using var whoAmI = await client.GetAsync(new Uri(WhoAmIUrl, UriKind.Relative), Ct);
        var whoAmIBody = await whoAmI.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(HttpStatusCode.OK, whoAmI.StatusCode);
        Assert.True(whoAmIBody.GetProperty("mustChangePassword").GetBoolean());

        // Every other admin endpoint is refused, with a code the client can act on.
        await AssertPasswordChangeRequiredAsync(client, AuthorizedProbeUrl);
        await AssertPasswordChangeRequiredAsync(client, PermissionProbeUrl);

        using var change = await PostChangePasswordAsync(client, BootstrapPassword, NewPassword);
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        // Same client, same (re-issued) session: unblocked on the very next request.
        using var whoAmIAfter = await client.GetAsync(new Uri(WhoAmIUrl, UriKind.Relative), Ct);
        var whoAmIAfterBody = await whoAmIAfter.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(HttpStatusCode.OK, whoAmIAfter.StatusCode);
        Assert.False(whoAmIAfterBody.GetProperty("mustChangePassword").GetBoolean());

        using var authorizedProbe = await client.GetAsync(new Uri(AuthorizedProbeUrl, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, authorizedProbe.StatusCode);

        // The permission probe is now refused only because this user holds no permission at all: a plain 403
        // from the permission check, no longer the password-change code.
        using var permissionProbe = await client.GetAsync(new Uri(PermissionProbeUrl, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, permissionProbe.StatusCode);
        Assert.DoesNotContain("password_change_required", await permissionProbe.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var user = await FindUserAsync(host, userId);
        Assert.False(user.MustChangePassword);
        await AssertAuditActionWrittenAsync(host, email, "PasswordChanged");

        // The bootstrap password is dead; the new one works.
        using var anonymous = CreateClient(host);
        using var oldLogin = await anonymous.PostAsJsonAsync(LoginUrl, new { email, password = BootstrapPassword }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        using var newLogin = await anonymous.PostAsJsonAsync(LoginUrl, new { email, password = NewPassword }, Ct);
        var newLoginBody = await newLogin.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
        Assert.False(newLoginBody.GetProperty("mustChangePassword").GetBoolean());
    }

    [Fact]
    public async Task Logout_stays_reachable_while_a_password_change_is_pending()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("must-change-logout");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserAsync(factory, email, BootstrapPassword, mustChangePassword: true);
        using var client = CreateClient(factory);

        using var login = await client.PostAsJsonAsync(LoginUrl, new { email, password = BootstrapPassword }, Ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, LogoutUrl);
        logoutRequest.Headers.Add("X-XSRF-TOKEN", await GetAntiforgeryTokenAsync(client));
        using var logout = await client.SendAsync(logoutRequest, Ct);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
    }

    [Fact]
    public async Task Setting_the_flag_mid_session_blocks_the_very_next_request_without_waiting_for_revalidation()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        // The default 60-second revalidation interval is left as it is on purpose: the gate reads the flag from
        // the database itself, so it must not depend on the cookie being revalidated first.
        var email = UniqueEmail("flag-set-mid-session");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await using var host = WithProbeEndpoints(factory);
        var userId = await CreateStaffUserAsync(host, email, BootstrapPassword, mustChangePassword: false);
        using var client = CreateClient(host);

        using var login = await client.PostAsJsonAsync(LoginUrl, new { email, password = BootstrapPassword }, Ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var before = await client.GetAsync(new Uri(AuthorizedProbeUrl, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        await SetMustChangePasswordAsync(host, userId, value: true);

        await AssertPasswordChangeRequiredAsync(client, AuthorizedProbeUrl);
    }

    [Fact]
    public async Task A_wrong_current_password_is_rejected_audited_and_leaves_the_block_in_place()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("wrong-current");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await using var host = WithProbeEndpoints(factory);
        var userId = await CreateStaffUserAsync(host, email, BootstrapPassword, mustChangePassword: true);
        using var client = await SignedInClientAsync(host, email, BootstrapPassword);

        using var change = await PostChangePasswordAsync(client, "Not-The-Password-1!", NewPassword);
        var body = await change.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
        Assert.Equal("invalid_current_password", body.GetProperty("error").GetString());
        Assert.True((await FindUserAsync(host, userId)).MustChangePassword);
        await AssertAuditActionWrittenAsync(host, email, "PasswordChangeFailed");
        await AssertPasswordChangeRequiredAsync(client, AuthorizedProbeUrl);
    }

    [Fact]
    public async Task A_new_password_that_fails_the_policy_is_rejected_with_the_rules_and_changes_nothing()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("weak-new");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var userId = await CreateStaffUserAsync(factory, email, BootstrapPassword, mustChangePassword: true);
        using var client = await SignedInClientAsync(factory, email, BootstrapPassword);
        var stampBefore = (await FindUserAsync(factory, userId)).SecurityStamp;

        using var change = await PostChangePasswordAsync(client, BootstrapPassword, "short");
        var body = await change.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
        Assert.Equal("new_password_rejected", body.GetProperty("error").GetString());
        Assert.True(body.GetProperty("errors").GetProperty("newPassword").GetArrayLength() > 0);

        var user = await FindUserAsync(factory, userId);
        Assert.True(user.MustChangePassword);
        Assert.Equal(stampBefore, user.SecurityStamp);
    }

    [Fact]
    public async Task Reusing_the_current_password_as_the_new_one_is_rejected()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("same-password");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var userId = await CreateStaffUserAsync(factory, email, BootstrapPassword, mustChangePassword: true);
        using var client = await SignedInClientAsync(factory, email, BootstrapPassword);

        using var change = await PostChangePasswordAsync(client, BootstrapPassword, BootstrapPassword);
        var body = await change.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
        Assert.Equal("new_password_rejected", body.GetProperty("error").GetString());
        Assert.True((await FindUserAsync(factory, userId)).MustChangePassword);
    }

    [Fact]
    public async Task Change_password_requires_a_valid_antiforgery_token()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("no-xsrf");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var userId = await CreateStaffUserAsync(factory, email, BootstrapPassword, mustChangePassword: true);
        using var client = await SignedInClientAsync(factory, email, BootstrapPassword);

        using var change = await client.PostAsJsonAsync(
            ChangePasswordUrl,
            new { currentPassword = BootstrapPassword, newPassword = NewPassword },
            Ct);
        var body = await change.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
        Assert.Equal("invalid_antiforgery_token", body.GetProperty("error").GetString());
        Assert.True((await FindUserAsync(factory, userId)).MustChangePassword);
    }

    [Fact]
    public async Task Change_password_requires_authentication()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        using var client = CreateClient(factory);

        using var change = await PostChangePasswordAsync(client, BootstrapPassword, NewPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, change.StatusCode);
    }

    /// <summary>
    /// Changing the password rotates the stamp: the caller's own cookie is re-issued with the new one and keeps
    /// working, while a second session signed in earlier (another browser, or whoever else knew the bootstrap
    /// password) fails its next revalidation. The interval is shrunk to 1 second as in the #16 tests.
    /// </summary>
    [Fact]
    public async Task Changing_the_password_keeps_the_callers_session_and_ends_every_other_session()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("other-sessions");
        var configuration = new Dictionary<string, string?> { ["AdminSecurity:SecurityStampValidationIntervalSeconds"] = "1" };
        await using var factory = new ApiFactory(sqlServer.ConnectionString, additionalConfiguration: configuration);
        await CreateStaffUserAsync(factory, email, BootstrapPassword, mustChangePassword: true);
        using var caller = await SignedInClientAsync(factory, email, BootstrapPassword);
        using var otherSession = await SignedInClientAsync(factory, email, BootstrapPassword);

        using var change = await PostChangePasswordAsync(caller, BootstrapPassword, NewPassword);
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);

        using var otherWhoAmI = await otherSession.GetAsync(new Uri(WhoAmIUrl, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, otherWhoAmI.StatusCode);

        using var callerWhoAmI = await caller.GetAsync(new Uri(WhoAmIUrl, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, callerWhoAmI.StatusCode);
    }

    /// <summary>
    /// Tester finding on #17: clearing the flag must not unlock sessions opened before the change. The default
    /// 60-second revalidation interval is kept and nothing waits, so this proves the gate itself refuses the
    /// older session on its very next request, instead of the cookie revalidation doing so up to a minute later.
    /// </summary>
    [Fact]
    public async Task A_session_opened_before_the_change_is_refused_immediately_after_it()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("stale-session");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await using var host = WithProbeEndpoints(factory);
        await CreateStaffUserAsync(host, email, BootstrapPassword, mustChangePassword: true);
        using var caller = await SignedInClientAsync(host, email, BootstrapPassword);
        using var otherSession = await SignedInClientAsync(host, email, BootstrapPassword);

        // A newly issued cookie is revalidated on its first request; make that happen now, so the request after
        // the change falls inside the 60-second window and only the gate can refuse it.
        using var otherBefore = await otherSession.GetAsync(new Uri(WhoAmIUrl, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, otherBefore.StatusCode);

        using var change = await PostChangePasswordAsync(caller, BootstrapPassword, NewPassword);
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        // The older session: refused on a gated endpoint and on the exempt ones, with the session ended.
        using var otherProbe = await otherSession.GetAsync(new Uri(AuthorizedProbeUrl, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, otherProbe.StatusCode);
        var otherProbeBody = await otherProbe.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("session_ended", otherProbeBody.GetProperty("error").GetString());

        using var otherWhoAmI = await otherSession.GetAsync(new Uri(WhoAmIUrl, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, otherWhoAmI.StatusCode);

        // The caller's re-issued cookie carries the new stamp and keeps full access.
        using var callerProbe = await caller.GetAsync(new Uri(AuthorizedProbeUrl, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, callerProbe.StatusCode);
        using var callerWhoAmI = await caller.GetAsync(new Uri(WhoAmIUrl, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, callerWhoAmI.StatusCode);
    }

    [Fact]
    public async Task Repeated_wrong_current_passwords_lock_the_account_like_sign_in_does()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("change-lockout");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserAsync(factory, email, BootstrapPassword, mustChangePassword: true);
        using var client = await SignedInClientAsync(factory, email, BootstrapPassword);

        // AdminSecurityOptions default: the 5th consecutive failure locks the account (same as sign-in).
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var failed = await PostChangePasswordAsync(client, "Wrong-Password-1!", NewPassword);
            Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        }

        using var locked = await PostChangePasswordAsync(client, "Wrong-Password-1!", NewPassword);
        var lockedBody = await locked.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal((HttpStatusCode)423, locked.StatusCode);
        Assert.Equal("locked_out", lockedBody.GetProperty("error").GetString());
        await AssertAuditActionWrittenAsync(factory, email, "Lockout");

        // Even the right current password is refused while the lockout lasts.
        using var stillLocked = await PostChangePasswordAsync(client, BootstrapPassword, NewPassword);
        Assert.Equal((HttpStatusCode)423, stillLocked.StatusCode);
    }

    /// <summary>
    /// Issue #200. The password a staff member changes to must be at least as long as a customer's (ten,
    /// W3-01): nine is a 400 that names the rule, ten is accepted, and nothing about the account changes on
    /// a refusal.
    /// </summary>
    /// <remarks>
    /// <b>The same route is the forced first-sign-in change</b> (<c>MustChangePassword</c> is set here), so
    /// this covers that path as well as an ordinary change. Both candidates carry all four character classes,
    /// so length is the only thing that can decide the outcome. The lengths are written out rather than read
    /// from the policy so the test stays an independent oracle: a test built from the constant would follow
    /// it down if it were ever lowered, and pass.
    /// </remarks>
    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    public async Task The_new_password_must_reach_the_same_minimum_a_customers_does(int length, bool accepted)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail(accepted ? "at-minimum" : "under-minimum");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var userId = await CreateStaffUserAsync(factory, email, BootstrapPassword, mustChangePassword: true);
        using var client = await SignedInClientAsync(factory, email, BootstrapPassword);
        var stampBefore = (await FindUserAsync(factory, userId)).SecurityStamp;

        var candidate = StrongPasswordOfLength(length);
        using var change = await PostChangePasswordAsync(client, BootstrapPassword, candidate);

        var user = await FindUserAsync(factory, userId);
        if (accepted)
        {
            Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
            Assert.False(user.MustChangePassword);

            using var anonymous = CreateClient(factory);
            using var login = await anonymous.PostAsJsonAsync(LoginUrl, new { email, password = candidate }, Ct);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            return;
        }

        // Status first: an accepted change answers 204 with no body, and reading that as JSON would hide the
        // real failure ("it was accepted") behind a parser error.
        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
        var body = await change.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("new_password_rejected", body.GetProperty("error").GetString());

        // A 400 that names the rule, not a bare "rejected": the screen renders these lines as they are.
        var reasons = body.GetProperty("errors").GetProperty("newPassword").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(CustomerPasswordPolicy.Requirement, reasons);

        Assert.True(user.MustChangePassword);
        Assert.Equal(stampBefore, user.SecurityStamp);
    }

    /// <summary>
    /// Issue #200: the value that used to pass. Six characters with every class is what Identity's default
    /// allowed, and it must be refused now; a later change that put the default back would turn this red.
    /// </summary>
    [Fact]
    public async Task The_old_six_character_minimum_no_longer_passes()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("old-six");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var userId = await CreateStaffUserAsync(factory, email, BootstrapPassword, mustChangePassword: true);
        using var client = await SignedInClientAsync(factory, email, BootstrapPassword);

        using var change = await PostChangePasswordAsync(client, BootstrapPassword, "Aa1!xx");

        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
        var body = await change.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("new_password_rejected", body.GetProperty("error").GetString());
        Assert.True((await FindUserAsync(factory, userId)).MustChangePassword);
    }

    /// <summary>
    /// Issue #200, "existing staff are not locked out". An account whose password was set while the minimum
    /// was Identity's six keeps signing in, and keeps working as the <i>current</i> password of the change:
    /// the minimum is a rule about the password being chosen, never about the one being proved.
    /// </summary>
    /// <remarks>
    /// <b>Built the way such an account really exists</b>: the hash is made from the old, short password
    /// directly, because <c>UserManager.CreateAsync(user, password)</c> would now refuse to create it, and a
    /// fixture that quietly used a longer password would prove nothing about the accounts already in a
    /// database. It is also the forced-change case (the seeded Super Admin's bootstrap password is the
    /// likeliest short one), so the whole sign-in then change then sign-in sequence is exercised.
    /// </remarks>
    [Fact]
    public async Task A_staff_member_whose_password_predates_the_minimum_can_still_sign_in_and_change_it()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        const string legacyPassword = "Aa1!xx";
        Assert.True(legacyPassword.Length < CustomerPasswordPolicy.MinimumLength, "The premise: this password is under today's minimum.");

        var email = UniqueEmail("legacy-short");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var userId = await CreateStaffUserWithLegacyPasswordAsync(factory, email, legacyPassword, mustChangePassword: true);

        // Still accepted at sign-in: no password rule is applied to a password that is only being checked.
        using var client = await SignedInClientAsync(factory, email, legacyPassword);

        // A refused NEW password is about the new one: the legacy password was right, so the refusal is
        // reported against the new password and not as a wrong current password.
        using var refused = await PostChangePasswordAsync(client, legacyPassword, StrongPasswordOfLength(9));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var refusedBody = await refused.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("new_password_rejected", refusedBody.GetProperty("error").GetString());

        using var change = await PostChangePasswordAsync(client, legacyPassword, NewPassword);
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        Assert.False((await FindUserAsync(factory, userId)).MustChangePassword);

        using var anonymous = CreateClient(factory);
        using var oldLogin = await anonymous.PostAsJsonAsync(LoginUrl, new { email, password = legacyPassword }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        using var newLogin = await anonymous.PostAsJsonAsync(LoginUrl, new { email, password = NewPassword }, Ct);
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string UniqueEmail(string label) => $"{label}-{Guid.NewGuid():N}@example.com";

    /// <summary>
    /// A password with an upper-case letter, a lower-case letter, a digit and a symbol of exactly
    /// <paramref name="length"/> characters, so a test about length cannot be decided by anything else.
    /// </summary>
    private static string StrongPasswordOfLength(int length) => "Aa1!" + new string('x', length - 4);

    /// <summary>HTTPS base address so the Secure admin cookie round-trips (see <see cref="AdminAuthEndpointTests"/>).</summary>
    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    private static WebApplicationFactory<Program> WithProbeEndpoints(ApiFactory factory) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddTransient<IStartupFilter, ProbeEndpointsStartupFilter>()));

    private static async Task<HttpClient> SignedInClientAsync(WebApplicationFactory<Program> factory, string email, string password)
    {
        var client = CreateClient(factory);
        using var login = await client.PostAsJsonAsync(LoginUrl, new { email, password }, Ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using var tokenResponse = await client.GetAsync(new Uri(AntiforgeryUrl, UriKind.Relative), Ct);
        var tokenBody = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return tokenBody.GetProperty("token").GetString() ?? string.Empty;
    }

    private static async Task<HttpResponseMessage> PostChangePasswordAsync(HttpClient client, string currentPassword, string newPassword)
    {
        var token = await GetAntiforgeryTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, ChangePasswordUrl)
        {
            Content = JsonContent.Create(new { currentPassword, newPassword }),
        };
        request.Headers.Add("X-XSRF-TOKEN", token);
        return await client.SendAsync(request, Ct);
    }

    private static async Task AssertPasswordChangeRequiredAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("password_change_required", body.GetProperty("error").GetString());
        Assert.Equal(403, body.GetProperty("status").GetInt32());
    }

    private static async Task<int> CreateStaffUserAsync(WebApplicationFactory<Program> factory, string email, string password, bool mustChangePassword)
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
            IsActive = true,
            MustChangePassword = mustChangePassword,
            CreatedAtUtc = DateTime.UtcNow,
        };

        var result = await userManager.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        return user.Id;
    }

    /// <summary>
    /// A staff account whose password hash was made before the minimum was raised. <c>CreateAsync(user,
    /// password)</c> validates the password, so the account is created without one and the hash is written
    /// afterwards; <c>UpdateAsync</c> validates the user and never the password.
    /// </summary>
    private static async Task<int> CreateStaffUserWithLegacyPasswordAsync(
        WebApplicationFactory<Program> factory,
        string email,
        string legacyPassword,
        bool mustChangePassword)
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
            IsActive = true,
            MustChangePassword = mustChangePassword,
            CreatedAtUtc = DateTime.UtcNow,
        };

        var created = await userManager.CreateAsync(user);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

        user.PasswordHash = userManager.PasswordHasher.HashPassword(user, legacyPassword);
        var updated = await userManager.UpdateAsync(user);
        Assert.True(updated.Succeeded, string.Join("; ", updated.Errors.Select(e => e.Description)));

        return user.Id;
    }

    private static async Task<AppUser> FindUserAsync(WebApplicationFactory<Program> factory, int userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await dbContext.Users.AsNoTracking().SingleAsync(u => u.Id == userId, Ct);
    }

    /// <summary>Stands in for a Super Admin reset or the break-glass CLI setting the flag on a live account.</summary>
    private static async Task SetMustChangePasswordAsync(WebApplicationFactory<Program> factory, int userId, bool value)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));
        Assert.NotNull(user);
        user.MustChangePassword = value;
        var result = await userManager.UpdateAsync(user);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
    }

    private static async Task AssertAuditActionWrittenAsync(WebApplicationFactory<Program> factory, string email, string action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var found = await dbContext.AuditLogs.AnyAsync(a => a.UserEmail == email && a.Action == action, Ct);
        Assert.True(found, $"Expected an AuditLogs row with Action '{action}' for '{email}'.");
    }

    /// <summary>
    /// Maps two stand-in admin endpoints in front of the real pipeline, declared exactly the way future admin
    /// endpoints are: a bare <c>RequireAuthorization()</c> and a <c>RequirePermission(...)</c>. They run through
    /// the real authentication scheme, authorization policies, handlers and result handler from the API's own
    /// service registrations; only the routes themselves are test-only.
    /// </summary>
    private sealed class ProbeEndpointsStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet(AuthorizedProbeUrl, () => TypedResults.Ok("probe")).RequireAuthorization();
                endpoints.MapGet(PermissionProbeUrl, () => TypedResults.Ok("probe")).RequirePermission(Permissions.Orders.View);
            });

            next(app);
        };
    }
}
