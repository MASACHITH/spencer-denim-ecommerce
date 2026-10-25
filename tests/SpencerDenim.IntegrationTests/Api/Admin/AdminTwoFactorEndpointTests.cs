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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Identity.Seeding;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// W1-10 end to end against a real SQL Server database: staff 2FA with an authenticator app. Enrolment (setup,
/// confirm, recovery codes), the two-step sign-in (authenticator or single-use recovery code, with wrong codes
/// counting towards the lockout), the gate that confines a GrantsAll holder without 2FA to enrolment (after the
/// forced password change), turning it off (refused for GrantsAll), and the audit trail. Codes are computed from
/// the key the API hands out, exactly as an authenticator app would.
/// <para>
/// GrantsAll holders are made by adding the user to the seeded Super Admin role and are always removed again at the
/// end of the test (<see cref="SuperAdminMembership"/>): <see cref="AdminAuthEndpointTests"/>' seeding test needs a
/// database with no holder, and the assembly runs its tests one at a time.
/// </para>
/// </summary>
public sealed class AdminTwoFactorEndpointTests(SqlServerDatabaseFixture sqlServer)
{
    private const string LoginUrl = "/api/admin/auth/login";
    private const string LoginTwoFactorUrl = "/api/admin/auth/login/two-factor";
    private const string WhoAmIUrl = "/api/admin/auth/whoami";
    private const string AntiforgeryUrl = "/api/admin/auth/antiforgery-token";
    private const string ChangePasswordUrl = "/api/admin/auth/change-password";
    private const string StatusUrl = "/api/admin/auth/two-factor";
    private const string SetupUrl = "/api/admin/auth/two-factor/setup";
    private const string EnableUrl = "/api/admin/auth/two-factor/enable";
    private const string RecoveryCodesUrl = "/api/admin/auth/two-factor/recovery-codes";
    private const string DisableUrl = "/api/admin/auth/two-factor/disable";
    private const string AuthorizedProbeUrl = "/api/admin/test-probe/authorized";
    private const string PermissionProbeUrl = "/api/admin/test-probe/permission";

    /// <summary>
    /// A real feature area, not a probe. The gate is built into the admin policies, so every area gets
    /// it by construction and none of them mention 2FA — which is exactly why one of them is swept here
    /// as well as the probes: a policy refactor that quietly stopped applying the gate would leave both
    /// probes passing if it were only ever proved against endpoints that exist to be probed.
    /// </summary>
    private const string MediaLibraryUrl = "/api/admin/media/00000000-0000-0000-0000-000000000001";

    private const string Password = "Correct-Horse-Battery-1!";
    private const string NewPassword = "Brand-New-Password-2!";

    [Fact]
    public async Task A_super_admin_without_two_factor_reaches_only_enrolment_until_they_enrol()
    {
        await using var host = await StartAsync();
        var email = UniqueEmail("sa-enrol");
        var userId = await CreateStaffUserAsync(host, email);
        await using var membership = await SuperAdminMembership.AddAsync(host, userId);
        using var client = CreateClient(host);

        using var login = await PostJsonAsync(client, LoginUrl, new { email, password = Password });
        var loginBody = await ReadJsonAsync(login);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.False(loginBody.GetProperty("twoFactorRequired").GetBoolean());
        Assert.True(loginBody.GetProperty("twoFactorEnrolmentRequired").GetBoolean());
        Assert.False(loginBody.GetProperty("twoFactorEnabled").GetBoolean());

        var whoAmI = await GetJsonAsync(client, WhoAmIUrl);
        Assert.True(whoAmI.GetProperty("twoFactorEnrolmentRequired").GetBoolean());

        await AssertRefusedAsync(client, AuthorizedProbeUrl, "two_factor_enrolment_required");
        await AssertRefusedAsync(client, PermissionProbeUrl, "two_factor_enrolment_required");

        // The media library (W1-04, ADM-18) is refused for the same reason, and the 403 arrives before
        // the asset is looked up — the GUID below belongs to nothing, so a 404 here would mean the gate
        // had let the request through to the handler.
        await AssertRefusedAsync(client, MediaLibraryUrl, "two_factor_enrolment_required");

        var status = await GetJsonAsync(client, StatusUrl);
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.True(status.GetProperty("required").GetBoolean());
        Assert.False(status.GetProperty("canDisable").GetBoolean());

        using var setup = await PostWithTokenAsync(client, SetupUrl, null);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        Assert.Contains("no-store", setup.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
        var setupBody = await ReadJsonAsync(setup);
        var uri = setupBody.GetProperty("authenticatorUri").GetString()!;
        var sharedKey = setupBody.GetProperty("sharedKey").GetString()!;
        var key = TotpTestCodes.SecretFromUri(uri);
        Assert.StartsWith("otpauth://totp/Colombo%20Wardrobe%20Admin:", uri, StringComparison.Ordinal);
        Assert.Contains("issuer=Colombo%20Wardrobe%20Admin", uri, StringComparison.Ordinal);
        Assert.Equal(key.ToLowerInvariant(), sharedKey.Replace(" ", string.Empty, StringComparison.Ordinal));

        using var wrong = await PostWithTokenAsync(client, EnableUrl, new { code = TotpTestCodes.Wrong(key) });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("invalid_code", (await ReadJsonAsync(wrong)).GetProperty("error").GetString());
        await AssertRefusedAsync(client, AuthorizedProbeUrl, "two_factor_enrolment_required");

        // Apps display the code in two groups; the API accepts it that way too.
        var code = TotpTestCodes.Current(key);
        using var enable = await PostWithTokenAsync(client, EnableUrl, new { code = $"{code[..3]} {code[3..]}" });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.Contains("no-store", enable.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
        var recoveryCodes = RecoveryCodesFrom(await ReadJsonAsync(enable));
        Assert.Equal(10, recoveryCodes.Count);
        Assert.Equal(10, recoveryCodes.Distinct(StringComparer.Ordinal).Count());

        // The caller's re-issued cookie now passes every gate.
        using var probe = await client.GetAsync(new Uri(AuthorizedProbeUrl, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        using var permissionProbe = await client.GetAsync(new Uri(PermissionProbeUrl, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, permissionProbe.StatusCode);

        var whoAmIAfter = await GetJsonAsync(client, WhoAmIUrl);
        Assert.True(whoAmIAfter.GetProperty("twoFactorEnabled").GetBoolean());
        Assert.False(whoAmIAfter.GetProperty("twoFactorEnrolmentRequired").GetBoolean());

        var statusAfter = await GetJsonAsync(client, StatusUrl);
        Assert.True(statusAfter.GetProperty("enabled").GetBoolean());
        Assert.Equal(10, statusAfter.GetProperty("recoveryCodesLeft").GetInt32());

        // Setting up again while it is on would break the registered app.
        using var setupAgain = await PostWithTokenAsync(client, SetupUrl, null);
        Assert.Equal(HttpStatusCode.Conflict, setupAgain.StatusCode);
        Assert.Equal("two_factor_already_enabled", (await ReadJsonAsync(setupAgain)).GetProperty("error").GetString());

        await AssertAuditAsync(host, email, "TwoFactorEnabled");
        await AssertNoSecretsAuditedAsync(host, email, [key, code, .. recoveryCodes]);
    }

    [Fact]
    public async Task The_forced_password_change_comes_before_two_factor_enrolment()
    {
        await using var host = await StartAsync();
        var email = UniqueEmail("sa-order");
        var userId = await CreateStaffUserAsync(host, email, mustChangePassword: true);
        await using var membership = await SuperAdminMembership.AddAsync(host, userId);
        using var client = await SignedInClientAsync(host, email);

        // Both gates refuse; the password change is reported, even on the enrolment endpoints.
        await AssertRefusedAsync(client, AuthorizedProbeUrl, "password_change_required");
        await AssertRefusedAsync(client, StatusUrl, "password_change_required");
        using (var setup = await PostWithTokenAsync(client, SetupUrl, null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, setup.StatusCode);
            Assert.Equal("password_change_required", (await ReadJsonAsync(setup)).GetProperty("error").GetString());
        }

        var whoAmI = await GetJsonAsync(client, WhoAmIUrl);
        Assert.True(whoAmI.GetProperty("mustChangePassword").GetBoolean());
        Assert.True(whoAmI.GetProperty("twoFactorEnrolmentRequired").GetBoolean());

        using var change = await PostWithTokenAsync(client, ChangePasswordUrl, new { currentPassword = Password, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        // Then enrolment is next, and the enrolment endpoints open.
        await AssertRefusedAsync(client, AuthorizedProbeUrl, "two_factor_enrolment_required");
        var status = await GetJsonAsync(client, StatusUrl);
        Assert.True(status.GetProperty("required").GetBoolean());
    }

    [Fact]
    public async Task Sign_in_with_two_factor_on_takes_a_password_then_a_code()
    {
        await using var host = await StartAsync();
        var email = UniqueEmail("two-step");
        await CreateStaffUserAsync(host, email);
        var key = await EnrolThroughApiAsync(host, email);
        using var client = CreateClient(host);

        using var login = await PostJsonAsync(client, LoginUrl, new { email, password = Password });
        var loginBody = await ReadJsonAsync(login);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(loginBody.GetProperty("twoFactorRequired").GetBoolean());
        Assert.False(loginBody.TryGetProperty("email", out _));

        // The password alone opens no session.
        using var whoAmIBefore = await client.GetAsync(new Uri(WhoAmIUrl, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, whoAmIBefore.StatusCode);

        using var noToken = await PostJsonAsync(client, LoginTwoFactorUrl, new { code = TotpTestCodes.Current(key), isRecoveryCode = false });
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Equal("invalid_antiforgery_token", (await ReadJsonAsync(noToken)).GetProperty("error").GetString());

        using var wrong = await PostWithTokenAsync(client, LoginTwoFactorUrl, new { code = TotpTestCodes.Wrong(key), isRecoveryCode = false });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("invalid_two_factor_code", (await ReadJsonAsync(wrong)).GetProperty("error").GetString());
        await AssertAuditAsync(host, email, "TwoFactorFailed");

        // A wrong code leaves the pending sign-in open for another try. (Enrolment used the current step.)
        using var right = await PostWithTokenAsync(client, LoginTwoFactorUrl, new { code = TotpTestCodes.Next(key), isRecoveryCode = false });
        var rightBody = await ReadJsonAsync(right);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.Equal(email, rightBody.GetProperty("email").GetString());
        Assert.True(rightBody.GetProperty("twoFactorEnabled").GetBoolean());

        var whoAmI = await GetJsonAsync(client, WhoAmIUrl);
        Assert.True(whoAmI.GetProperty("twoFactorEnabled").GetBoolean());
        await AssertAuditAsync(host, email, "Login", afterJsonContains: "Authenticator");

        // The pending sign-in is spent.
        using var again = await PostWithTokenAsync(client, LoginTwoFactorUrl, new { code = TotpTestCodes.Current(key), isRecoveryCode = false });
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.Equal("two_factor_session_expired", (await ReadJsonAsync(again)).GetProperty("error").GetString());

        // A 2FA account can still change its password: SignInManager's password check takes its 2FA branch here.
        using var change = await PostWithTokenAsync(client, ChangePasswordUrl, new { currentPassword = Password, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
    }

    /// <summary>
    /// Tester finding on W1-10: the pending cookie was only a signed pointer, so a copy taken before a successful
    /// sign-in, replayed from another client within its five minutes with a fresh code, opened a second session
    /// without the password. The pending sign-in is now single-use on the server.
    /// </summary>
    [Fact]
    public async Task A_copied_pending_cookie_cannot_be_used_after_the_sign_in_it_belonged_to()
    {
        await using var host = await StartAsync();
        var email = UniqueEmail("pending-replay");
        await CreateStaffUserAsync(host, email);
        var key = await EnrolThroughApiAsync(host, email);

        // The attacker's copy: a client that manages its cookies by hand, so the pending cookie can be lifted.
        using var raw = CreateClient(host, handleCookies: false);
        using var login = await PostJsonAsync(raw, LoginUrl, new { email, password = Password });
        Assert.True((await ReadJsonAsync(login)).GetProperty("twoFactorRequired").GetBoolean());
        var pendingCookie = CookiePair(login, "sd_admin_2fa");

        using (var victim = CreateClient(host, handleCookies: false))
        {
            using var completed = await PostTwoFactorWithCookieAsync(victim, pendingCookie, TotpTestCodes.Next(key, 1));
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        }

        // Same cookie, a different client, a fresh valid code, well inside the cookie's own lifetime.
        using var replayed = await PostTwoFactorWithCookieAsync(raw, pendingCookie, TotpTestCodes.Next(key, 2));
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
        Assert.Equal("two_factor_session_expired", (await ReadJsonAsync(replayed)).GetProperty("error").GetString());
        Assert.Null(CookiePairOrNull(replayed, "sd_admin_auth"));
    }

    [Fact]
    public async Task A_newer_password_step_ends_an_older_pending_sign_in()
    {
        await using var host = await StartAsync();
        var email = UniqueEmail("pending-superseded");
        await CreateStaffUserAsync(host, email);
        var key = await EnrolThroughApiAsync(host, email);

        using var older = await PendingSignInAsync(host, email);
        using var newer = await PendingSignInAsync(host, email);

        using var olderAttempt = await PostWithTokenAsync(older, LoginTwoFactorUrl, new { code = TotpTestCodes.Next(key), isRecoveryCode = false });
        Assert.Equal("two_factor_session_expired", (await ReadJsonAsync(olderAttempt)).GetProperty("error").GetString());

        using var newerAttempt = await PostWithTokenAsync(newer, LoginTwoFactorUrl, new { code = TotpTestCodes.Next(key), isRecoveryCode = false });
        Assert.Equal(HttpStatusCode.OK, newerAttempt.StatusCode);
    }

    /// <summary>
    /// Tester finding on W1-10: an authenticator code stays valid for about two minutes (drift window), and Identity
    /// accepted it any number of times. Each time step is now accepted once per account, across enrolment and
    /// sign-in. A refused reuse counts as a failed attempt, exactly like a wrong code: the server cannot tell a
    /// replay by someone who watched the code from a wrong guess, and answering differently would tell an attacker
    /// the code had been right. The real user only waits for the next code.
    /// </summary>
    [Fact]
    public async Task An_authenticator_code_is_accepted_once_only()
    {
        await using var host = await StartAsync();
        var email = UniqueEmail("code-replay");
        var userId = await CreateStaffUserAsync(host, email);

        // Enrolment accepts the current code; the same code cannot then sign in.
        string key;
        string enrolmentCode;
        using (var enrolling = await SignedInClientAsync(host, email))
        {
            using var setup = await PostWithTokenAsync(enrolling, SetupUrl, null);
            key = TotpTestCodes.SecretFromUri((await ReadJsonAsync(setup)).GetProperty("authenticatorUri").GetString()!);
            enrolmentCode = TotpTestCodes.Current(key);
            using var enable = await PostWithTokenAsync(enrolling, EnableUrl, new { code = enrolmentCode });
            Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        }

        // Captured once and reused as the same string below: TotpTestCodes.Next(key, 1) is derived from
        // DateTimeOffset.UtcNow at the instant it's called, so calling it a second time to "reproduce" this code
        // is not guaranteed to return the same value — if a 30-second step boundary falls between the two calls,
        // the second call computes a genuinely later, unused code, which the server correctly accepts, making the
        // test fail intolerantly around that boundary (round 2 finding).
        string laterCode;
        using (var client = await PendingSignInAsync(host, email))
        {
            using var reused = await PostWithTokenAsync(client, LoginTwoFactorUrl, new { code = enrolmentCode, isRecoveryCode = false });
            Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
            Assert.Equal("invalid_two_factor_code", (await ReadJsonAsync(reused)).GetProperty("error").GetString());
            Assert.Equal(1, (await FindUserAsync(host, userId)).AccessFailedCount);

            // A later code signs in.
            laterCode = TotpTestCodes.Next(key, 1);
            using var fresh = await PostWithTokenAsync(client, LoginTwoFactorUrl, new { code = laterCode, isRecoveryCode = false });
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        }

        // That code is spent too, and so is every earlier step.
        using var again = await PendingSignInAsync(host, email);
        foreach (var code in new[] { laterCode, enrolmentCode })
        {
            using var response = await PostWithTokenAsync(again, LoginTwoFactorUrl, new { code, isRecoveryCode = false });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var nextStep = await PostWithTokenAsync(again, LoginTwoFactorUrl, new { code = TotpTestCodes.Next(key, 2), isRecoveryCode = false });
        Assert.Equal(HttpStatusCode.OK, nextStep.StatusCode);
    }

    /// <summary>
    /// #48, end to end: the sequential test above, fired all at once. Twenty second steps carry the same pending
    /// cookie and the same valid code and are released together; exactly one opens a session, and every other one
    /// is refused as an ordinary wrong or spent code (or, once the winner has closed the pending sign-in, as an
    /// expired one) — a 401, never a 500.
    /// <para>
    /// <b>This is the contract end to end, not the reproducer.</b> Each second step makes several round trips
    /// (the user, the pending sign-in, the stamp, the lockout) before it reaches the code, so how many of the
    /// twenty arrive at the watermark together depends on the machine — on a loaded test server this passed
    /// against the unfixed provider too. The race itself is reproduced, and its fix proved, directly against
    /// the provider by <see cref="SpencerDenim.IntegrationTests.Identity.AuthenticatorCodeRaceTests"/>, which
    /// the unfixed provider fails every time. What this test adds is that the whole sign-in, pending cookie
    /// and all, keeps "one code, one session" and answers every loser with a 401 rather than a 500.
    /// </para>
    /// <para>
    /// The lockout threshold is raised for this host so nineteen refusals do not lock the account part way
    /// through. A lockout closes the pending sign-in from a losing request while the winner may be closing it
    /// too, and whether that is safe under concurrency is #46's question (the failed-attempt counter race), not
    /// this one. The lockout's own behaviour is covered by the sequential tests further down.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Concurrent_second_steps_with_one_code_open_exactly_one_session()
    {
        const int requests = 20;
        await using var host = await StartAsync(new Dictionary<string, string?>
        {
            ["AdminSecurity:LockoutMaxFailedAttempts"] = "1000",
        });
        var email = UniqueEmail("code-race");
        await CreateStaffUserAsync(host, email);
        var key = await EnrolThroughApiAsync(host, email);

        using var raw = CreateClient(host, handleCookies: false);
        using var login = await PostJsonAsync(raw, LoginUrl, new { email, password = Password });
        Assert.True((await ReadJsonAsync(login)).GetProperty("twoFactorRequired").GetBoolean());
        var pendingCookie = CookiePair(login, "sd_admin_2fa");

        using var tokenResponse = await raw.GetAsync(new Uri(AntiforgeryUrl, UriKind.Relative), Ct);
        var antiforgeryToken = (await ReadJsonAsync(tokenResponse)).GetProperty("token").GetString()!;
        var antiforgeryCookie = CookiePair(tokenResponse, "sd_admin_xsrf");

        // Enrolment used the current step; this is the next one, captured once and sent on every request.
        var code = TotpTestCodes.Next(key, 1);
        var sends = Enumerable.Range(0, requests).Select(_ =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, LoginTwoFactorUrl)
            {
                Content = JsonContent.Create(new { code, isRecoveryCode = false }),
            };
            request.Headers.Add("X-XSRF-TOKEN", antiforgeryToken);
            request.Headers.Add("Cookie", $"{antiforgeryCookie}; {pendingCookie}");
            return request;
        }).ToList();

        var responses = await Task.WhenAll(sends.Select(request => raw.SendAsync(request, Ct)));
        try
        {
            var statuses = responses.Select(r => (int)r.StatusCode).OrderBy(s => s).ToList();
            Assert.True(
                responses.Count(r => r.StatusCode == HttpStatusCode.OK) == 1,
                $"Expected exactly one 200; got [{string.Join(", ", statuses)}].");
            Assert.Single(responses, r => CookiePairOrNull(r, "sd_admin_auth") is not null);

            foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
            {
                Assert.True(
                    refused.StatusCode == HttpStatusCode.Unauthorized,
                    $"Expected every other request to be a 401; got [{string.Join(", ", statuses)}].");
                var error = (await ReadJsonAsync(refused)).GetProperty("error").GetString();
                Assert.True(
                    error is "invalid_two_factor_code" or "two_factor_session_expired",
                    $"Unexpected refusal '{error}'.");
            }
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }

            foreach (var request in sends)
            {
                request.Dispose();
            }
        }
    }

    [Fact]
    public async Task The_second_step_without_a_pending_sign_in_is_refused()
    {
        await using var host = await StartAsync();
        using var client = CreateClient(host);

        using var response = await PostWithTokenAsync(client, LoginTwoFactorUrl, new { code = "123456", isRecoveryCode = false });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("two_factor_session_expired", (await ReadJsonAsync(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_recovery_code_signs_in_once_and_never_again()
    {
        await using var host = await StartAsync();
        var email = UniqueEmail("recovery");
        await CreateStaffUserAsync(host, email);
        var (_, recoveryCodes) = await EnrolThroughApiWithCodesAsync(host, email);

        using (var client = await PendingSignInAsync(host, email))
        {
            // Typed in lower case with stray spaces: still the same code.
            var typed = $" {recoveryCodes[0].ToLowerInvariant()} ";
            using var response = await PostWithTokenAsync(client, LoginTwoFactorUrl, new { code = typed, isRecoveryCode = true });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var status = await GetJsonAsync(client, StatusUrl);
            Assert.Equal(9, status.GetProperty("recoveryCodesLeft").GetInt32());
        }

        // Typed without its hyphen, or with a space in its place: still the same code (tester finding on W1-10).
        foreach (var typed in new[] { recoveryCodes[2].Replace("-", string.Empty, StringComparison.Ordinal), recoveryCodes[3].Replace('-', ' ') })
        {
            using var client = await PendingSignInAsync(host, email);
            using var response = await PostWithTokenAsync(client, LoginTwoFactorUrl, new { code = typed, isRecoveryCode = true });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await AssertAuditAsync(host, email, "RecoveryCodeUsed", afterJsonContains: "\"recoveryCodesLeft\":9");
        await AssertAuditAsync(host, email, "Login", afterJsonContains: "RecoveryCode");

        using (var client = await PendingSignInAsync(host, email))
        {
            using var reused = await PostWithTokenAsync(client, LoginTwoFactorUrl, new { code = recoveryCodes[0], isRecoveryCode = true });
            Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
            Assert.Equal("invalid_two_factor_code", (await ReadJsonAsync(reused)).GetProperty("error").GetString());

            // Identity's recovery-code store matches an empty string when it has no codes; it must never be tried.
            using var blank = await PostWithTokenAsync(client, LoginTwoFactorUrl, new { code = "   ", isRecoveryCode = true });
            Assert.Equal(HttpStatusCode.Unauthorized, blank.StatusCode);

            // An authenticator code is not accepted as a recovery code, nor the reverse.
            using var wrongKind = await PostWithTokenAsync(client, LoginTwoFactorUrl, new { code = recoveryCodes[1], isRecoveryCode = false });
            Assert.Equal(HttpStatusCode.Unauthorized, wrongKind.StatusCode);
        }

        await AssertNoSecretsAuditedAsync(host, email, recoveryCodes);
    }

    [Fact]
    public async Task Wrong_codes_lock_the_account_and_a_right_password_does_not_reset_the_count()
    {
        await using var host = await StartAsync();
        var email = UniqueEmail("code-lockout");
        await CreateStaffUserAsync(host, email);
        var key = await EnrolThroughApiAsync(host, email);

        using (var first = await PendingSignInAsync(host, email))
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var failed = await PostWithTokenAsync(first, LoginTwoFactorUrl, new { code = TotpTestCodes.Wrong(key), isRecoveryCode = false });
                Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
            }
        }

        // Signing in with the right password again must not wipe the two failures.
        using var second = await PendingSignInAsync(host, email);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var failed = await PostWithTokenAsync(second, LoginTwoFactorUrl, new { code = TotpTestCodes.Wrong(key), isRecoveryCode = false });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        using var locked = await PostWithTokenAsync(second, LoginTwoFactorUrl, new { code = TotpTestCodes.Wrong(key), isRecoveryCode = false });
        var lockedBody = await ReadJsonAsync(locked);
        Assert.Equal((HttpStatusCode)423, locked.StatusCode);
        Assert.Equal("locked_out", lockedBody.GetProperty("error").GetString());
        Assert.True(lockedBody.TryGetProperty("lockoutEndUtc", out var lockoutEnd) && lockoutEnd.ValueKind == JsonValueKind.String);
        await AssertAuditAsync(host, email, "Lockout");

        // The lockout ended the pending sign-in; and the password step is locked too.
        using var afterLock = await PostWithTokenAsync(second, LoginTwoFactorUrl, new { code = TotpTestCodes.Current(key), isRecoveryCode = false });
        Assert.Equal(HttpStatusCode.Unauthorized, afterLock.StatusCode);
        Assert.Equal("two_factor_session_expired", (await ReadJsonAsync(afterLock)).GetProperty("error").GetString());

        using var anonymous = CreateClient(host);
        using var passwordStep = await PostJsonAsync(anonymous, LoginUrl, new { email, password = Password });
        Assert.Equal((HttpStatusCode)423, passwordStep.StatusCode);
    }

    /// <summary>
    /// Issue #46 for the second step: wrong codes sent all at once used to get 8–17 checks per lockout window instead
    /// of five, for the same lost-update reason as parallel wrong passwords. Exactly four are now answered as wrong
    /// codes and audited as <c>InvalidCode</c>, one locks, and the rest are refused (locked, or the pending sign-in
    /// the lockout ended) without counting.
    /// </summary>
    [Fact]
    public async Task Parallel_wrong_codes_cannot_outrun_the_lockout()
    {
        const int parallelAttempts = 30;
        const int maxFailedAttempts = 5; // AdminSecurityOptions.LockoutMaxFailedAttempts default.
        await using var host = await StartAsync();
        var email = UniqueEmail("parallel-code-lockout");
        var userId = await CreateStaffUserAsync(host, email);
        var key = await EnrolThroughApiAsync(host, email);

        using var client = await PendingSignInAsync(host, email);

        // One antiforgery pair for every request: parallel token requests would each replace the cookie the others'
        // header tokens belong to.
        var token = await GetAntiforgeryTokenAsync(client);
        var wrongCode = TotpTestCodes.Wrong(key);
        var replies = await Task.WhenAll(Enumerable.Range(0, parallelAttempts).Select(async _ =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, LoginTwoFactorUrl)
            {
                Content = JsonContent.Create(new { code = wrongCode, isRecoveryCode = false }),
            };
            request.Headers.Add("X-XSRF-TOKEN", token);
            using var response = await client.SendAsync(request, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);
            return (response.StatusCode, Error: ErrorCodeOf(body));
        }));

        Assert.All(replies, reply => Assert.True(
            reply.StatusCode is HttpStatusCode.Unauthorized or (HttpStatusCode)423,
            $"Every parallel attempt is refused or locked out, never anything else; got {reply.StatusCode} {reply.Error}."));
        Assert.Equal(maxFailedAttempts - 1, replies.Count(r => r.Error == "invalid_two_factor_code"));
        Assert.Contains(replies, r => r.StatusCode == (HttpStatusCode)423);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var codeFailures = await dbContext.AuditLogs.AsNoTracking()
                .Where(a => a.UserEmail == email && a.Action == "TwoFactorFailed")
                .Select(a => a.AfterJson)
                .ToListAsync(Ct);
            Assert.Equal(maxFailedAttempts - 1, codeFailures.Count(json => json != null && json.Contains("\"InvalidCode\"", StringComparison.Ordinal)));
            Assert.Equal(1, await dbContext.AuditLogs.CountAsync(a => a.UserEmail == email && a.Action == "Lockout", Ct));
        }

        var user = await FindUserAsync(host, userId);
        Assert.True(user.LockoutEnd > DateTimeOffset.UtcNow);

        using var anonymous = CreateClient(host);
        using var passwordStep = await PostJsonAsync(anonymous, LoginUrl, new { email, password = Password });
        Assert.Equal((HttpStatusCode)423, passwordStep.StatusCode);
    }

    /// <summary>
    /// Issue #46 follow-up (tester D1): the pending sign-in is a claim. A recovery code is redeemed by a read-modify-write
    /// save, so twelve requests carrying the same code all pass the code check; only the one whose <c>DELETE</c> of
    /// the pending sign-in removes the row may open a session. The rest are told the sign-in is already over (a
    /// 401, never a 200 and never a 500), and exactly one auth cookie and one recovery-code <c>Login</c> audit row result.
    /// </summary>
    [Fact]
    public async Task Parallel_second_steps_with_the_same_recovery_code_open_exactly_one_session()
    {
        var tokenWriteDelay = new TokenWriteDelayInterceptor(TimeSpan.FromMilliseconds(750));
        await using var host = await StartAsync(NoLockoutDuringTheRace, tokenWriteDelay);
        var email = UniqueEmail("parallel-recovery");
        await CreateStaffUserAsync(host, email);
        var (_, recoveryCodes) = await EnrolThroughApiWithCodesAsync(host, email);

        var replies = await ParallelSecondStepsAsync(host, tokenWriteDelay, email, recoveryCodes[0], isRecoveryCode: true, count: 12);

        AssertNoServerErrors(replies);
        Assert.True(replies.Count(r => r.StatusCode == HttpStatusCode.OK) == 1, $"Exactly one request completes the sign-in. {Describe(replies)}");
        Assert.True(replies.Count(r => r.HasSessionCookie) == 1, $"Exactly one auth cookie is issued. {Describe(replies)}");
        Assert.All(replies.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));

        await using var scope = host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The enrolment sign-in wrote its own Login row (2FA was still off); the race wrote exactly one more.
        var recoveryLogins = await dbContext.AuditLogs.AsNoTracking()
            .Where(a => a.UserEmail == email && a.Action == "Login" && a.AfterJson != null && a.AfterJson.Contains("RecoveryCode"))
            .CountAsync(Ct);
        Assert.Equal(1, recoveryLogins);
        Assert.Equal(1, await dbContext.AuditLogs.CountAsync(a => a.UserEmail == email && a.Action == "RecoveryCodeUsed", Ct));
    }

    /// <summary>
    /// The same claim for an authenticator code: whatever the code check lets through (the replay protection of the
    /// TOTP provider is a separate fix; the delayed token writes make every request pass it here), at most one of
    /// the twelve opens a session.
    /// </summary>
    [Fact]
    public async Task Parallel_second_steps_with_the_same_authenticator_code_open_at_most_one_session()
    {
        var tokenWriteDelay = new TokenWriteDelayInterceptor(TimeSpan.FromMilliseconds(750));
        await using var host = await StartAsync(NoLockoutDuringTheRace, tokenWriteDelay);
        var email = UniqueEmail("parallel-totp");
        await CreateStaffUserAsync(host, email);
        var key = await EnrolThroughApiAsync(host, email);

        var replies = await ParallelSecondStepsAsync(host, tokenWriteDelay, email, TotpTestCodes.Next(key), isRecoveryCode: false, count: 12);

        AssertNoServerErrors(replies);
        Assert.True(replies.Count(r => r.StatusCode == HttpStatusCode.OK) == 1, $"Exactly one request completes the same pending sign-in. {Describe(replies)}");
        Assert.True(replies.Count(r => r.HasSessionCookie) == 1, $"Exactly one auth cookie is issued for the same pending sign-in. {Describe(replies)}");
        Assert.All(replies.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
    }

    /// <summary>
    /// Issue #46, the second step's twin of <c>A_right_password_that_a_burst_of_wrong_ones_outruns_is_refused_as_locked</c>:
    /// <c>AdminAuthenticator</c> accepts a right code only if the account is still unlocked <i>after</i> the code was
    /// verified, because the lock check before it read the user as loaded and a burst of wrong passwords may have locked
    /// the account since. The right code is stopped at that second look (<see cref="LockoutRecheckGateInterceptor"/>), the
    /// account is locked with five wrong passwords while it waits, and it is let go: it must be refused as locked, with
    /// no session. With the second look deleted the same request comes back 200 with an auth cookie.
    /// </summary>
    [Fact]
    public async Task A_right_code_that_a_burst_of_wrong_passwords_outruns_is_refused_as_locked()
    {
        const int maxFailedAttempts = 5; // AdminSecurityOptions.LockoutMaxFailedAttempts default.
        var gate = new LockoutRecheckGateInterceptor();
        await using var host = await StartAsync(commandInterceptor: gate);
        var email = UniqueEmail("right-code-after-burst");
        await CreateStaffUserAsync(host, email);
        var key = await EnrolThroughApiAsync(host, email);

        using var client = await PendingSignInAsync(host, email);
        var token = await GetAntiforgeryTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, LoginTwoFactorUrl)
        {
            Content = JsonContent.Create(new { code = TotpTestCodes.Next(key), isRecoveryCode = false }),
        };
        request.Headers.Add("X-XSRF-TOKEN", token);

        gate.Arm();
        var rightCode = client.SendAsync(request, Ct);
        try
        {
            // From here the right code has been verified against an account that was unlocked.
            await gate.WaitUntilHeldAsync(TimeSpan.FromSeconds(30), Ct);

            // Wrong passwords, from another client: a wrong password opens no pending sign-in, so this cannot replace
            // the one the held request is completing.
            using var anonymous = CreateClient(host);
            for (var attempt = 1; attempt <= maxFailedAttempts; attempt++)
            {
                using var wrong = await PostJsonAsync(anonymous, LoginUrl, new { email, password = "wrong" });
                Assert.Equal(attempt < maxFailedAttempts ? HttpStatusCode.Unauthorized : (HttpStatusCode)423, wrong.StatusCode);
            }
        }
        finally
        {
            gate.Release();
        }

        using var response = await rightCode;
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(
            response.StatusCode == (HttpStatusCode)423,
            $"A right code the burst outran must be refused as locked, not accepted. Got {(int)response.StatusCode}: {body}");
        Assert.Null(CookiePairOrNull(response, "sd_admin_auth"));
    }

    /// <summary>
    /// Issue #145: a password step racing the second step of the sign-in it is about to replace. The password step has
    /// read the account (and the pending sign-in it will overwrite); the second step then completes the sign-in, which
    /// deletes that pending row; the password step's write finds the row gone. That used to be an unhandled exception
    /// (a 500). The password was right and nothing is wrong with the account,
    /// so it is the same reply a stale pending sign-in gets, and it is not a failed login: no <c>LoginFailed</c> audit
    /// row, no pending cookie, and the next attempt works.
    /// </summary>
    [Fact]
    public async Task A_password_step_that_loses_to_the_second_step_is_a_401_not_a_500()
    {
        var gate = new PendingSignInWriteGateInterceptor();
        await using var host = await StartAsync(commandInterceptor: gate);
        var email = UniqueEmail("password-vs-code");
        await CreateStaffUserAsync(host, email);
        var key = await EnrolThroughApiAsync(host, email);

        using var codeClient = await PendingSignInAsync(host, email);
        using var racingClient = CreateClient(host);

        gate.Arm();
        var racingPasswordStep = PostJsonAsync(racingClient, LoginUrl, new { email, password = Password });
        HttpResponseMessage secondStep;
        try
        {
            await gate.WaitUntilHeldAsync(TimeSpan.FromSeconds(30), Ct);

            // The sign-in the first browser was in the middle of finishes while the other password step waits at its write.
            var token = await GetAntiforgeryTokenAsync(codeClient);
            using var request = new HttpRequestMessage(HttpMethod.Post, LoginTwoFactorUrl)
            {
                Content = JsonContent.Create(new { code = TotpTestCodes.Next(key), isRecoveryCode = false }),
            };
            request.Headers.Add("X-XSRF-TOKEN", token);
            secondStep = await codeClient.SendAsync(request, Ct);
        }
        finally
        {
            gate.Release();
        }

        using var _ = secondStep;
        using var racing = await racingPasswordStep;
        var racingBody = await racing.Content.ReadAsStringAsync(Ct);

        Assert.True(secondStep.StatusCode == HttpStatusCode.OK, $"The second step completes. Got {(int)secondStep.StatusCode}.");
        Assert.NotNull(CookiePairOrNull(secondStep, "sd_admin_auth"));

        Assert.True(
            racing.StatusCode == HttpStatusCode.Unauthorized,
            $"A password step that lost the race is a 401, not a 500. Got {(int)racing.StatusCode}: {racingBody}");
        Assert.Equal("two_factor_session_expired", JsonDocument.Parse(racingBody).RootElement.GetProperty("error").GetString());
        Assert.Null(CookiePairOrNull(racing, "sd_admin_2fa"));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await dbContext.AuditLogs.CountAsync(a => a.UserEmail == email && a.Action == "LoginFailed", Ct));
        }

        // Nothing is left half-written: the next password step opens a pending sign-in as usual.
        using var again = await PendingSignInAsync(host, email);
    }

    /// <summary>
    /// The same defect from the other side, and the likelier one: the Sign in button pressed twice, or two tabs, on an
    /// account with no pending sign-in yet. Both password steps read "none", both try to insert it, and the second
    /// insert hits the primary key. The one that got its row in holds the live pending sign-in (the newest password
    /// step is the one that can finish, as <c>AdminAuthenticator</c> has always said); the other is told its sign-in
    /// is over rather than answered 500, and must not be handed a pending cookie for a row that is not its own.
    /// </summary>
    [Fact]
    public async Task Two_password_steps_that_both_open_the_pending_sign_in_are_one_winner_and_one_401()
    {
        var gate = new PendingSignInWriteGateInterceptor();
        await using var host = await StartAsync(commandInterceptor: gate);
        var email = UniqueEmail("password-vs-password");
        await CreateStaffUserAsync(host, email);
        await EnrolThroughApiAsync(host, email);

        using var heldClient = CreateClient(host);
        using var winnerClient = CreateClient(host);

        gate.Arm();
        var heldPasswordStep = PostJsonAsync(heldClient, LoginUrl, new { email, password = Password });
        HttpResponseMessage winner;
        try
        {
            await gate.WaitUntilHeldAsync(TimeSpan.FromSeconds(30), Ct);
            winner = await PostJsonAsync(winnerClient, LoginUrl, new { email, password = Password });
        }
        finally
        {
            gate.Release();
        }

        using var _ = winner;
        using var held = await heldPasswordStep;
        var heldBody = await held.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, winner.StatusCode);
        Assert.True((await ReadJsonAsync(winner)).GetProperty("twoFactorRequired").GetBoolean());
        Assert.NotNull(CookiePairOrNull(winner, "sd_admin_2fa"));

        Assert.True(
            held.StatusCode == HttpStatusCode.Unauthorized,
            $"The password step that lost the insert is a 401, not a 500. Got {(int)held.StatusCode}: {heldBody}");
        Assert.Equal("two_factor_session_expired", JsonDocument.Parse(heldBody).RootElement.GetProperty("error").GetString());
        Assert.Null(CookiePairOrNull(held, "sd_admin_2fa"));
    }

    // ---- issue #250: a sign-in that overlaps a change to the account must not undo it ----------------------------
    //
    // Identity saves a user as a whole row, so a sign-in that loaded the account, spent its time on the password hash
    // and then wrote, put back every column it had loaded: a deactivation became "active" again, and a password change
    // was replaced by the old hash and the old security stamp. The window is the hash verification, a few hundred
    // milliseconds; the gates stop the request AT its write so the change can be made inside it deterministically.
    // These tests were red on the whole-row save and say which part of it did what.

    /// <summary>
    /// A password step held at its pending-sign-in write while an administrator deactivates the account. The account
    /// must stay deactivated, and the held step must not hand out a pending sign-in for it (its write is conditional
    /// on the account still being active and unchanged, so it finds nothing to write and is told it is too late).
    /// </summary>
    [Fact]
    public async Task A_password_step_held_at_its_write_cannot_re_activate_an_account_deactivated_meanwhile()
    {
        var gate = new PendingSignInWriteGateInterceptor();
        await using var host = await StartAsync(commandInterceptor: gate);
        var email = UniqueEmail("held-vs-deactivation");
        var userId = await CreateStaffUserAsync(host, email);
        await EnrolThroughApiAsync(host, email);

        using var heldClient = CreateClient(host);
        gate.Arm();
        var heldStep = PostJsonAsync(heldClient, LoginUrl, new { email, password = Password });
        try
        {
            await gate.WaitUntilHeldAsync(TimeSpan.FromSeconds(30), Ct);
            await SetActiveAsync(host, userId, active: false);
        }
        finally
        {
            gate.Release();
        }

        using var held = await heldStep;
        var body = await held.Content.ReadAsStringAsync(Ct);

        Assert.False((await FindUserAsync(host, userId)).IsActive, "The deactivated account was made active again by the sign-in's write.");
        Assert.True(
            held.StatusCode == HttpStatusCode.Unauthorized,
            $"A sign-in for an account that was deactivated while it ran is refused. Got {(int)held.StatusCode}: {body}");
        Assert.Equal("two_factor_session_expired", ErrorCodeOf(body));
        Assert.Null(CookiePairOrNull(held, "sd_admin_2fa"));
        Assert.Equal(0, await PendingSignInRowsAsync(host, userId));
    }

    /// <summary>
    /// The same overlap for the SECOND step: the code has been accepted and the session is about to be issued when the
    /// account is deactivated. The write that records the sign-in is the last look at the account, so it must refuse
    /// and no session may come out; the account stays deactivated.
    /// </summary>
    [Fact]
    public async Task A_second_step_held_at_its_sign_in_write_gets_no_session_for_an_account_deactivated_meanwhile()
    {
        var gate = new UserRowWriteGateInterceptor();
        await using var host = await StartAsync(commandInterceptor: gate);
        var email = UniqueEmail("second-step-vs-deactivation");
        var userId = await CreateStaffUserAsync(host, email);
        var key = await EnrolThroughApiAsync(host, email);

        using var client = await PendingSignInAsync(host, email);

        gate.Arm();
        var heldStep = SendSecondStepAsync(client, TotpTestCodes.Next(key));
        try
        {
            await gate.WaitUntilHeldAsync(TimeSpan.FromSeconds(30), Ct);
            await SetActiveAsync(host, userId, active: false);
        }
        finally
        {
            gate.Release();
        }

        using var held = await heldStep;
        var body = await held.Content.ReadAsStringAsync(Ct);

        Assert.False((await FindUserAsync(host, userId)).IsActive, "The deactivated account was made active again by the sign-in's write.");
        Assert.True(
            held.StatusCode == HttpStatusCode.Unauthorized,
            $"No session for an account deactivated while the code was being checked. Got {(int)held.StatusCode}: {body}");
        Assert.Equal("two_factor_session_expired", ErrorCodeOf(body));
        Assert.Null(CookiePairOrNull(held, "sd_admin_auth"));
    }

    /// <summary>
    /// A password step held at its pending-sign-in write while the owner changes the password from another session.
    /// The new hash and the rotated security stamp must survive: the old pair coming back would make the new password
    /// stop working, the old one work again, and bring back every session the change was meant to end.
    /// </summary>
    [Fact]
    public async Task A_password_step_held_at_its_write_cannot_undo_a_password_change_made_meanwhile()
    {
        var gate = new PendingSignInWriteGateInterceptor();
        await using var host = await StartAsync(commandInterceptor: gate);
        var email = UniqueEmail("held-vs-password-change");
        var userId = await CreateStaffUserAsync(host, email);
        var key = await EnrolThroughApiAsync(host, email);

        // The session that will change the password: password step, then code.
        using var session = await PendingSignInAsync(host, email);
        using (var signedIn = await SendSecondStepAsync(session, TotpTestCodes.Next(key)))
        {
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        }

        var stampBefore = (await FindUserAsync(host, userId)).SecurityStamp;

        using var heldClient = CreateClient(host);
        gate.Arm();
        var heldStep = PostJsonAsync(heldClient, LoginUrl, new { email, password = Password });
        string? stampAfterChange;
        string? hashAfterChange;
        try
        {
            await gate.WaitUntilHeldAsync(TimeSpan.FromSeconds(30), Ct);

            using var change = await PostWithTokenAsync(session, ChangePasswordUrl, new { currentPassword = Password, newPassword = NewPassword });
            Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

            var changed = await FindUserAsync(host, userId);
            stampAfterChange = changed.SecurityStamp;
            hashAfterChange = changed.PasswordHash;
            Assert.NotEqual(stampBefore, stampAfterChange);
        }
        finally
        {
            gate.Release();
        }

        using var held = await heldStep;
        var body = await held.Content.ReadAsStringAsync(Ct);

        var after = await FindUserAsync(host, userId);
        Assert.True(
            string.Equals(after.SecurityStamp, stampAfterChange, StringComparison.Ordinal),
            "The sign-in's write put the old security stamp back: every session the password change ended is alive again.");
        Assert.True(
            string.Equals(after.PasswordHash, hashAfterChange, StringComparison.Ordinal),
            "The sign-in's write put the old password hash back.");

        // The step verified the OLD password against the account as it was; the account is not that any more.
        Assert.True(
            held.StatusCode == HttpStatusCode.Unauthorized,
            $"A sign-in that verified a password that was changed meanwhile is refused. Got {(int)held.StatusCode}: {body}");
        Assert.Null(CookiePairOrNull(held, "sd_admin_2fa"));

        using var newPassword = await PostJsonAsync(CreateClient(host), LoginUrl, new { email, password = NewPassword });
        Assert.Equal(HttpStatusCode.OK, newPassword.StatusCode);
        Assert.True((await ReadJsonAsync(newPassword)).GetProperty("twoFactorRequired").GetBoolean());

        using var oldPassword = await PostJsonAsync(CreateClient(host), LoginUrl, new { email, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
    }

    /// <summary>
    /// The second step, held at the write that records the sign-in, while the owner changes the password from another
    /// session. The pending sign-in was opened against the old security stamp, so the write must refuse: no session, and
    /// the new hash and the rotated stamp stay. (A later time step's code is used because the first session spent the
    /// earlier one: a code is accepted once.)
    /// </summary>
    [Fact]
    public async Task A_second_step_held_at_its_sign_in_write_cannot_undo_a_password_change_made_meanwhile()
    {
        var gate = new UserRowWriteGateInterceptor();
        await using var host = await StartAsync(commandInterceptor: gate);
        var email = UniqueEmail("second-step-vs-password-change");
        var userId = await CreateStaffUserAsync(host, email);
        var key = await EnrolThroughApiAsync(host, email);

        using var session = await PendingSignInAsync(host, email);
        using (var signedIn = await SendSecondStepAsync(session, TotpTestCodes.Next(key)))
        {
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        }

        using var client = await PendingSignInAsync(host, email);
        var stampBefore = (await FindUserAsync(host, userId)).SecurityStamp;

        gate.Arm();
        var heldStep = SendSecondStepAsync(client, TotpTestCodes.Next(key, stepsAhead: 2));
        string? stampAfterChange;
        string? hashAfterChange;
        try
        {
            await gate.WaitUntilHeldAsync(TimeSpan.FromSeconds(30), Ct);

            using var change = await PostWithTokenAsync(session, ChangePasswordUrl, new { currentPassword = Password, newPassword = NewPassword });
            Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

            var changed = await FindUserAsync(host, userId);
            stampAfterChange = changed.SecurityStamp;
            hashAfterChange = changed.PasswordHash;
            Assert.NotEqual(stampBefore, stampAfterChange);
        }
        finally
        {
            gate.Release();
        }

        using var held = await heldStep;
        var body = await held.Content.ReadAsStringAsync(Ct);

        var after = await FindUserAsync(host, userId);
        Assert.True(
            string.Equals(after.SecurityStamp, stampAfterChange, StringComparison.Ordinal),
            "The sign-in's write put the old security stamp back: every session the password change ended is alive again.");
        Assert.True(
            string.Equals(after.PasswordHash, hashAfterChange, StringComparison.Ordinal),
            "The sign-in's write put the old password hash back.");
        Assert.True(
            held.StatusCode == HttpStatusCode.Unauthorized,
            $"A second step whose pending sign-in was opened against the old password gets no session. Got {(int)held.StatusCode}: {body}");
        Assert.Equal("two_factor_session_expired", ErrorCodeOf(body));
        Assert.Null(CookiePairOrNull(held, "sd_admin_auth"));
    }

    /// <summary>
    /// An account WITHOUT 2FA: one request checks the password and issues the session, so the write that records the
    /// sign-in is the only place the deactivation can be lost. Held there, with the account deactivated meanwhile:
    /// no session, and the account stays deactivated.
    /// </summary>
    [Fact]
    public async Task A_single_step_sign_in_held_at_its_write_gets_no_session_for_an_account_deactivated_meanwhile()
    {
        var gate = new UserRowWriteGateInterceptor();
        await using var host = await StartAsync(commandInterceptor: gate);
        var email = UniqueEmail("single-step-vs-deactivation");
        var userId = await CreateStaffUserAsync(host, email);

        using var heldClient = CreateClient(host);
        gate.Arm();
        var heldStep = PostJsonAsync(heldClient, LoginUrl, new { email, password = Password });
        try
        {
            await gate.WaitUntilHeldAsync(TimeSpan.FromSeconds(30), Ct);
            await SetActiveAsync(host, userId, active: false);
        }
        finally
        {
            gate.Release();
        }

        using var held = await heldStep;
        var body = await held.Content.ReadAsStringAsync(Ct);

        Assert.False((await FindUserAsync(host, userId)).IsActive, "The deactivated account was made active again by the sign-in's write.");
        Assert.True(
            held.StatusCode == HttpStatusCode.Unauthorized,
            $"No session for an account deactivated while its password was being checked. Got {(int)held.StatusCode}: {body}");
        Assert.Equal("invalid_credentials", ErrorCodeOf(body));
        Assert.Null(CookiePairOrNull(held, "sd_admin_auth"));
    }

    /// <summary>
    /// An account without 2FA, the password changed from another session while the old password's sign-in is held at
    /// its write: the new hash and stamp survive and the held sign-in gets no session.
    /// </summary>
    [Fact]
    public async Task A_single_step_sign_in_held_at_its_write_cannot_undo_a_password_change_made_meanwhile()
    {
        var gate = new UserRowWriteGateInterceptor();
        await using var host = await StartAsync(commandInterceptor: gate);
        var email = UniqueEmail("single-step-vs-password-change");
        var userId = await CreateStaffUserAsync(host, email);

        using var session = await SignedInClientAsync(host, email);
        var stampBefore = (await FindUserAsync(host, userId)).SecurityStamp;

        using var heldClient = CreateClient(host);
        gate.Arm();
        var heldStep = PostJsonAsync(heldClient, LoginUrl, new { email, password = Password });
        string? stampAfterChange;
        string? hashAfterChange;
        try
        {
            await gate.WaitUntilHeldAsync(TimeSpan.FromSeconds(30), Ct);

            using var change = await PostWithTokenAsync(session, ChangePasswordUrl, new { currentPassword = Password, newPassword = NewPassword });
            Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

            var changed = await FindUserAsync(host, userId);
            stampAfterChange = changed.SecurityStamp;
            hashAfterChange = changed.PasswordHash;
            Assert.NotEqual(stampBefore, stampAfterChange);
        }
        finally
        {
            gate.Release();
        }

        using var held = await heldStep;
        var body = await held.Content.ReadAsStringAsync(Ct);

        var after = await FindUserAsync(host, userId);
        Assert.True(
            string.Equals(after.SecurityStamp, stampAfterChange, StringComparison.Ordinal),
            "The sign-in's write put the old security stamp back: every session the password change ended is alive again.");
        Assert.True(
            string.Equals(after.PasswordHash, hashAfterChange, StringComparison.Ordinal),
            "The sign-in's write put the old password hash back.");
        Assert.True(
            held.StatusCode == HttpStatusCode.Unauthorized,
            $"A sign-in that verified a password changed meanwhile gets no session. Got {(int)held.StatusCode}: {body}");
        Assert.Null(CookiePairOrNull(held, "sd_admin_auth"));

        using var newPassword = await PostJsonAsync(CreateClient(host), LoginUrl, new { email, password = NewPassword });
        Assert.Equal(HttpStatusCode.OK, newPassword.StatusCode);

        using var oldPassword = await PostJsonAsync(CreateClient(host), LoginUrl, new { email, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
    }

    /// <summary>
    /// A lockout that lands while a password step is held at its write: the lockout must survive it (it was already
    /// protected by <c>AppUserStore</c>, which keeps the two lockout columns out of a save that did not change them),
    /// and the step must not be handed a pending sign-in for a locked account. The write's condition names the lockout,
    /// so it finds nothing to write, and the step is answered as locked, 423, which is what the next attempt would get
    /// and what the endpoint already says for a locked account: no pending cookie, no pending row.
    /// </summary>
    [Fact]
    public async Task A_lockout_that_lands_while_a_password_step_is_held_survives_it()
    {
        var gate = new PendingSignInWriteGateInterceptor();
        await using var host = await StartAsync(commandInterceptor: gate);
        var email = UniqueEmail("held-vs-lockout");
        var userId = await CreateStaffUserAsync(host, email);
        await EnrolThroughApiAsync(host, email);

        var held = await SendPasswordStepHeldAsync(host, gate, email, () => LockAccountAsync(host, email));

        var after = await FindUserAsync(host, userId);
        Assert.True(after.LockoutEnd > DateTimeOffset.UtcNow, "The held sign-in's write wiped the lockout that landed while it waited.");
        Assert.True(
            held.Status == (HttpStatusCode)423,
            $"A password step that finds the account locked when it writes is answered as locked. Got {(int)held.Status}: {held.Body}");
        Assert.Null(held.PendingCookie);
        Assert.Equal(0, await PendingSignInRowsAsync(host, userId));
    }

    /// <summary>
    /// The same lockout, but the account already has a pending sign-in, so the held write is the <c>UPDATE</c> that
    /// replaces it and not the <c>INSERT</c> that opens the first one. Both carry the condition, and each needs a test
    /// of its own: the branch is chosen by a read made before the write, so a test of one proves nothing of the other.
    /// The row that was there is left exactly as it was.
    /// </summary>
    [Fact]
    public async Task A_lockout_that_lands_while_a_password_step_is_held_at_the_replace_write_opens_no_new_pending_sign_in()
    {
        var gate = new PendingSignInWriteGateInterceptor();
        await using var host = await StartAsync(commandInterceptor: gate);
        var email = UniqueEmail("replace-vs-lockout");
        var userId = await CreateStaffUserAsync(host, email);
        await EnrolThroughApiAsync(host, email);

        using var firstBrowser = await PendingSignInAsync(host, email);
        var rowBefore = await PendingSignInValueAsync(host, userId);
        Assert.NotNull(rowBefore);

        var held = await SendPasswordStepHeldAsync(host, gate, email, () => LockAccountAsync(host, email));

        Assert.True(
            held.Status == (HttpStatusCode)423,
            $"A password step that finds the account locked when it writes is answered as locked. Got {(int)held.Status}: {held.Body}");
        Assert.Null(held.PendingCookie);
        Assert.Equal(rowBefore, await PendingSignInValueAsync(host, userId));
        Assert.True((await FindUserAsync(host, userId)).LockoutEnd > DateTimeOffset.UtcNow);
    }

    // ---- issue #250, the replace branch -------------------------------------------------------------------------
    //
    // A password step on an account that already has a pending sign-in (a second tab, a second browser, a retry) writes
    // an UPDATE of that row instead of an INSERT. The six tests above all use an account with no pending sign-in, so
    // they reach only the INSERT; these hold the UPDATE and change the account while it waits. In each the write must
    // find nothing to replace: the step is told its sign-in is over (401 two_factor_session_expired), gets no pending
    // cookie, and the row that was there is untouched.

    [Fact]
    public async Task A_password_step_held_at_the_replace_write_opens_no_pending_sign_in_for_an_account_deactivated_meanwhile()
    {
        var gate = new PendingSignInWriteGateInterceptor();
        await using var host = await StartAsync(commandInterceptor: gate);
        var email = UniqueEmail("replace-vs-deactivation");
        var userId = await CreateStaffUserAsync(host, email);
        await EnrolThroughApiAsync(host, email);

        using var firstBrowser = await PendingSignInAsync(host, email);
        var rowBefore = await PendingSignInValueAsync(host, userId);
        Assert.NotNull(rowBefore);

        var held = await SendPasswordStepHeldAsync(host, gate, email, () => SetActiveAsync(host, userId, active: false));

        Assert.False((await FindUserAsync(host, userId)).IsActive, "The deactivated account was made active again by the sign-in's write.");
        AssertSessionExpired(held, "deactivated");
        Assert.Equal(rowBefore, await PendingSignInValueAsync(host, userId));
    }

    [Fact]
    public async Task A_password_step_held_at_the_replace_write_opens_no_pending_sign_in_for_an_account_whose_security_stamp_rotated_meanwhile()
    {
        var gate = new PendingSignInWriteGateInterceptor();
        await using var host = await StartAsync(commandInterceptor: gate);
        var email = UniqueEmail("replace-vs-stamp");
        var userId = await CreateStaffUserAsync(host, email);
        await EnrolThroughApiAsync(host, email);

        using var firstBrowser = await PendingSignInAsync(host, email);
        var rowBefore = await PendingSignInValueAsync(host, userId);
        Assert.NotNull(rowBefore);

        // What a role change, a reset of the authenticator or a password change from elsewhere does to the account.
        string? rotated = null;
        var held = await SendPasswordStepHeldAsync(host, gate, email, async () =>
        {
            rotated = await RotateSecurityStampAsync(host, userId);
        });

        Assert.Equal(rotated, (await FindUserAsync(host, userId)).SecurityStamp);
        AssertSessionExpired(held, "stamp-rotated");
        Assert.Equal(rowBefore, await PendingSignInValueAsync(host, userId));
    }

    [Fact]
    public async Task A_password_step_held_at_the_replace_write_opens_no_pending_sign_in_for_an_account_whose_password_was_changed_meanwhile()
    {
        var gate = new PendingSignInWriteGateInterceptor();
        await using var host = await StartAsync(commandInterceptor: gate);
        var email = UniqueEmail("replace-vs-password-change");
        var userId = await CreateStaffUserAsync(host, email);
        var key = await EnrolThroughApiAsync(host, email);

        // The session that will change the password: password step, then code. Its pending sign-in is spent.
        using var session = await PendingSignInAsync(host, email);
        using (var signedIn = await SendSecondStepAsync(session, TotpTestCodes.Next(key)))
        {
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        }

        // A later browser's pending sign-in: the row the held step will try to replace.
        using var otherBrowser = await PendingSignInAsync(host, email);
        var rowBefore = await PendingSignInValueAsync(host, userId);
        Assert.NotNull(rowBefore);
        var stampBefore = (await FindUserAsync(host, userId)).SecurityStamp;

        string? hashAfterChange = null;
        var held = await SendPasswordStepHeldAsync(host, gate, email, async () =>
        {
            using var change = await PostWithTokenAsync(session, ChangePasswordUrl, new { currentPassword = Password, newPassword = NewPassword });
            Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
            hashAfterChange = (await FindUserAsync(host, userId)).PasswordHash;
        });

        var after = await FindUserAsync(host, userId);
        Assert.NotEqual(stampBefore, after.SecurityStamp);
        Assert.True(string.Equals(after.PasswordHash, hashAfterChange, StringComparison.Ordinal), "The sign-in's write put the old password hash back.");
        AssertSessionExpired(held, "password-changed");
        Assert.Equal(rowBefore, await PendingSignInValueAsync(host, userId));
    }

    // ---- issue #250, a lockout that lands at the last write ------------------------------------------------------
    //
    // The password or the code was checked while the account was unlocked; a burst of wrong passwords then locked it
    // while the sign-in waited at its last write. Without the lockout in that write's condition the sign-in went on to
    // issue a session for a locked account (the audit log showed Lockout, then Login). It is answered with the same
    // locked answer the endpoint gives everywhere else (423), not a new status.

    [Fact]
    public async Task A_second_step_held_at_its_sign_in_write_is_refused_as_locked_when_the_account_locks_meanwhile()
    {
        var gate = new UserRowWriteGateInterceptor();
        await using var host = await StartAsync(commandInterceptor: gate);
        var email = UniqueEmail("second-step-vs-lockout");
        var userId = await CreateStaffUserAsync(host, email);
        var key = await EnrolThroughApiAsync(host, email);

        using var client = await PendingSignInAsync(host, email);
        var loginRowsBefore = await CountAuditRowsAsync(host, email, "Login");

        gate.Arm();
        var heldStep = SendSecondStepAsync(client, TotpTestCodes.Next(key));
        try
        {
            await gate.WaitUntilHeldAsync(TimeSpan.FromSeconds(30), Ct);
            await LockAccountAsync(host, email);
        }
        finally
        {
            gate.Release();
        }

        using var held = await heldStep;
        var body = await held.Content.ReadAsStringAsync(Ct);

        Assert.True(
            held.StatusCode == (HttpStatusCode)423,
            $"A second step that finds the account locked at its last write is answered as locked. Got {(int)held.StatusCode}: {body}");
        Assert.Null(CookiePairOrNull(held, "sd_admin_auth"));
        Assert.True((await FindUserAsync(host, userId)).LockoutEnd > DateTimeOffset.UtcNow, "The lockout did not survive the sign-in.");
        Assert.Equal(loginRowsBefore, await CountAuditRowsAsync(host, email, "Login"));
    }

    [Fact]
    public async Task A_single_step_sign_in_held_at_its_write_is_refused_as_locked_when_the_account_locks_meanwhile()
    {
        var gate = new UserRowWriteGateInterceptor();
        await using var host = await StartAsync(commandInterceptor: gate);
        var email = UniqueEmail("single-step-vs-lockout");
        var userId = await CreateStaffUserAsync(host, email);

        using var heldClient = CreateClient(host);
        gate.Arm();
        var heldStep = PostJsonAsync(heldClient, LoginUrl, new { email, password = Password });
        try
        {
            await gate.WaitUntilHeldAsync(TimeSpan.FromSeconds(30), Ct);
            await LockAccountAsync(host, email);
        }
        finally
        {
            gate.Release();
        }

        using var held = await heldStep;
        var body = await held.Content.ReadAsStringAsync(Ct);

        Assert.True(
            held.StatusCode == (HttpStatusCode)423,
            $"A sign-in that finds the account locked at its last write is answered as locked. Got {(int)held.StatusCode}: {body}");
        Assert.Null(CookiePairOrNull(held, "sd_admin_auth"));
        Assert.True((await FindUserAsync(host, userId)).LockoutEnd > DateTimeOffset.UtcNow, "The lockout did not survive the sign-in.");
        Assert.Equal(0, await CountAuditRowsAsync(host, email, "Login"));
    }

    // ---- issue #250, what the recording write leaves behind --------------------------------------------------------

    /// <summary>
    /// The last-login write goes around <c>SaveChanges</c>, so the audit interceptor never sees it and the write sets the
    /// two audit columns itself. <c>UpdatedAtUtc</c> is the time it records; <c>UpdatedByUserId</c> is what the
    /// interceptor would have stamped for an anonymous sign-in request, which is null, and not what a previous save left
    /// there. Both account kinds end in the same write, and both are checked.
    /// </summary>
    [Fact]
    public async Task A_completed_sign_in_stamps_the_audit_columns_with_the_time_it_records()
    {
        await using var host = await StartAsync();
        var plainEmail = UniqueEmail("audit-columns-plain");
        var plainId = await CreateStaffUserAsync(host, plainEmail);
        var twoFactorEmail = UniqueEmail("audit-columns-two-factor");
        var twoFactorId = await CreateStaffUserAsync(host, twoFactorEmail);
        var key = await EnrolThroughApiAsync(host, twoFactorEmail);

        var longAgo = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var id in new[] { plainId, twoFactorId })
        {
            await using var scope = host.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await dbContext.Users.Where(u => u.Id == id).ExecuteUpdateAsync(
                s => s.SetProperty(u => u.UpdatedAtUtc, longAgo).SetProperty(u => u.UpdatedByUserId, (int?)id), Ct);
        }

        using (var plain = await SignedInClientAsync(host, plainEmail))
        {
            Assert.NotNull(plain);
        }

        using (var pending = await PendingSignInAsync(host, twoFactorEmail))
        using (var signedIn = await SendSecondStepAsync(pending, TotpTestCodes.Next(key)))
        {
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        }

        foreach (var id in new[] { plainId, twoFactorId })
        {
            var user = await FindUserAsync(host, id);
            Assert.NotNull(user.LastLoginAtUtc);
            Assert.True(user.UpdatedAtUtc > longAgo, $"User {id}: the sign-in's write left UpdatedAtUtc at {user.UpdatedAtUtc:O}.");
            Assert.True(
                Math.Abs((user.UpdatedAtUtc!.Value - user.LastLoginAtUtc!.Value).TotalSeconds) < 5,
                $"User {id}: UpdatedAtUtc ({user.UpdatedAtUtc:O}) is not the time the sign-in recorded ({user.LastLoginAtUtc:O}).");
            Assert.True(user.UpdatedByUserId is null, $"User {id}: UpdatedByUserId is {user.UpdatedByUserId}; an anonymous sign-in stamps null.");
        }
    }

    // ---- issue #250, only a lost race is a 401 ---------------------------------------------------------------------

    /// <summary>
    /// The pending write answers 401 for a unique-key violation (a lost race, #145) and for nothing else. A SQL error that
    /// is not one (here: a trigger that refuses the row) is a bug, and must stay a 500 rather than become a quiet 401 that
    /// looks like an expired sign-in. The trigger is named for one user and dropped afterwards, so it cannot touch another
    /// test's sign-in on the shared database.
    /// </summary>
    [Fact]
    public async Task A_sql_error_that_is_not_a_unique_violation_on_the_pending_write_is_a_500_not_a_401()
    {
        await using var host = await StartAsync();
        var email = UniqueEmail("pending-write-fails");
        var userId = await CreateStaffUserAsync(host, email);
        await EnrolThroughApiAsync(host, email);

        var trigger = "TR_Test250_" + Guid.NewGuid().ToString("N");
        await ExecuteSqlAsync(
            host,
            "CREATE TRIGGER [identity].[" + trigger + "] ON [identity].[UserTokens] AFTER INSERT AS BEGIN "
            + "IF EXISTS (SELECT 1 FROM inserted WHERE [UserId] = " + userId.ToString(CultureInfo.InvariantCulture)
            + " AND [Name] = N'PendingTwoFactorSignIn') THROW 50250, N'Injected by the test: not a unique violation.', 1; END");
        try
        {
            using var client = CreateClient(host);
            using var response = await PostJsonAsync(client, LoginUrl, new { email, password = Password });
            var body = await response.Content.ReadAsStringAsync(Ct);

            Assert.True(
                response.StatusCode == HttpStatusCode.InternalServerError,
                $"A SQL error that is not a lost race must surface as a 500, not be swallowed. Got {(int)response.StatusCode}: {body}");
            Assert.Null(CookiePairOrNull(response, "sd_admin_2fa"));
            Assert.Equal(0, await PendingSignInRowsAsync(host, userId));
        }
        finally
        {
            await ExecuteSqlAsync(host, "DROP TRIGGER [identity].[" + trigger + "]");
        }
    }

    /// <summary>A password step stopped at the pending-sign-in write, with something done to the account meanwhile.</summary>
    private sealed record HeldReply(HttpStatusCode Status, string Body, string? PendingCookie);

    private static async Task<HeldReply> SendPasswordStepHeldAsync(
        WebApplicationFactory<Program> host, PendingSignInWriteGateInterceptor gate, string email, Func<Task> whileHeld)
    {
        using var client = CreateClient(host);
        gate.Arm();
        var step = PostJsonAsync(client, LoginUrl, new { email, password = Password });
        try
        {
            await gate.WaitUntilHeldAsync(TimeSpan.FromSeconds(30), Ct);
            await whileHeld();
        }
        finally
        {
            gate.Release();
        }

        using var response = await step;
        return new HeldReply(response.StatusCode, await response.Content.ReadAsStringAsync(Ct), CookiePairOrNull(response, "sd_admin_2fa"));
    }

    private static void AssertSessionExpired(HeldReply held, string what)
    {
        Assert.True(
            held.Status == HttpStatusCode.Unauthorized,
            $"A password step for an account {what} while it ran is refused. Got {(int)held.Status}: {held.Body}");
        Assert.Equal("two_factor_session_expired", ErrorCodeOf(held.Body));
        Assert.Null(held.PendingCookie);
    }

    /// <summary>Locks the account the way an attacker does: the configured number of wrong passwords, the last one answered 423.</summary>
    private static async Task LockAccountAsync(WebApplicationFactory<Program> host, string email)
    {
        const int maxFailedAttempts = 5; // AdminSecurityOptions.LockoutMaxFailedAttempts default.
        using var anonymous = CreateClient(host);
        for (var attempt = 1; attempt <= maxFailedAttempts; attempt++)
        {
            using var wrong = await PostJsonAsync(anonymous, LoginUrl, new { email, password = "wrong" });
            Assert.Equal(attempt < maxFailedAttempts ? HttpStatusCode.Unauthorized : (HttpStatusCode)423, wrong.StatusCode);
        }
    }

    private static async Task<string> RotateSecurityStampAsync(WebApplicationFactory<Program> host, int userId)
    {
        var stamp = Guid.NewGuid().ToString("N");
        await using var scope = host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, stamp), Ct);
        return stamp;
    }

    private static async Task<string?> PendingSignInValueAsync(WebApplicationFactory<Program> host, int userId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await dbContext.Set<IdentityUserToken<int>>().AsNoTracking()
            .Where(t => t.UserId == userId && t.Name == "PendingTwoFactorSignIn")
            .Select(t => t.Value)
            .SingleOrDefaultAsync(Ct);
    }

    private static async Task<int> CountAuditRowsAsync(WebApplicationFactory<Program> host, string email, string action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await dbContext.AuditLogs.AsNoTracking().CountAsync(a => a.UserEmail == email && a.Action == action, Ct);
    }

    private static async Task ExecuteSqlAsync(WebApplicationFactory<Program> host, string sql)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(sql, Ct);
    }

    private static async Task SetActiveAsync(WebApplicationFactory<Program> host, int userId, bool active)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, active), Ct);
    }

    private static async Task<int> PendingSignInRowsAsync(WebApplicationFactory<Program> host, int userId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await dbContext.Set<IdentityUserToken<int>>().AsNoTracking()
            .CountAsync(t => t.UserId == userId && t.Name == "PendingTwoFactorSignIn", Ct);
    }

    /// <summary>The second sign-in step from a client that already holds the pending cookie (and the antiforgery pair).</summary>
    private static async Task<HttpResponseMessage> SendSecondStepAsync(HttpClient client, string code)
    {
        var token = await GetAntiforgeryTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, LoginTwoFactorUrl)
        {
            Content = JsonContent.Create(new { code, isRecoveryCode = false }),
        };
        request.Headers.Add("X-XSRF-TOKEN", token);
        return await client.SendAsync(request, Ct);
    }

    /// <summary>
    /// The lockout threshold is raised for the same-code races above. Twelve requests with one code is the point of
    /// those tests, but the ones that read the code after the winner redeemed it count as wrong codes, and at the
    /// default five the account can lock before the winner's own check, which would make "exactly one session"
    /// depend on timing. The lockout itself is covered by the parallel wrong-code test, at the default.
    /// </summary>
    private static readonly Dictionary<string, string?> NoLockoutDuringTheRace = new()
    {
        ["AdminSecurity:LockoutMaxFailedAttempts"] = "100",
    };

    private readonly record struct SecondStepReply(HttpStatusCode StatusCode, bool HasSessionCookie, string? Error);

    /// <summary>
    /// Passes the password step once with a cookie-less client, then sends <paramref name="count"/> second steps
    /// with that one pending cookie and one antiforgery pair, all released together. Each one's record of the code
    /// as used is delayed by <paramref name="tokenWriteDelay"/> (switched on after the password step), so none of
    /// them can see that another has used the code: the worst case, on every run.
    /// </summary>
    private static async Task<SecondStepReply[]> ParallelSecondStepsAsync(
        WebApplicationFactory<Program> host, TokenWriteDelayInterceptor tokenWriteDelay, string email, string code, bool isRecoveryCode, int count)
    {
        using var client = CreateClient(host, handleCookies: false);
        using var login = await PostJsonAsync(client, LoginUrl, new { email, password = Password });
        Assert.True((await ReadJsonAsync(login)).GetProperty("twoFactorRequired").GetBoolean());
        var pendingCookie = CookiePair(login, "sd_admin_2fa");

        using var tokenResponse = await client.GetAsync(new Uri(AntiforgeryUrl, UriKind.Relative), Ct);
        var token = (await ReadJsonAsync(tokenResponse)).GetProperty("token").GetString()!;
        var antiforgeryCookie = CookiePair(tokenResponse, "sd_admin_xsrf");

        // From here every record of a used code is held back, so all the requests below pass the code check first.
        tokenWriteDelay.Enabled = true;

        // RunContinuationsAsynchronously: without it SetResult() below runs all the continuations inline on this
        // thread, one after another, so the requests are dispatched in turn rather than together.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = Enumerable.Range(0, count).Select(async _ =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, LoginTwoFactorUrl)
            {
                Content = JsonContent.Create(new { code, isRecoveryCode }),
            };
            request.Headers.Add("X-XSRF-TOKEN", token);
            request.Headers.Add("Cookie", $"{antiforgeryCookie}; {pendingCookie}");

            await gate.Task;
            using var response = await client.SendAsync(request, Ct);
            return new SecondStepReply(
                response.StatusCode,
                CookiePairOrNull(response, "sd_admin_auth") is not null,
                ErrorCodeOf(await response.Content.ReadAsStringAsync(Ct)));
        }).ToArray();

        gate.SetResult();
        return await Task.WhenAll(sends);
    }

    private static string Describe(IEnumerable<SecondStepReply> replies) =>
        "Replies: " + string.Join(", ", replies.Select(r => $"{(int)r.StatusCode}/{r.Error}{(r.HasSessionCookie ? "/cookie" : string.Empty)}"));

    private static void AssertNoServerErrors(IEnumerable<SecondStepReply> replies) =>
        Assert.All(replies, r => Assert.True(
            (int)r.StatusCode < 500,
            $"A parallel second step must be answered, not crash: {r.StatusCode} {r.Error}"));

    [Fact]
    public async Task A_right_code_resets_the_failure_count()
    {
        await using var host = await StartAsync();
        var email = UniqueEmail("code-reset");
        var userId = await CreateStaffUserAsync(host, email);
        var key = await EnrolThroughApiAsync(host, email);

        using var client = await PendingSignInAsync(host, email);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var failed = await PostWithTokenAsync(client, LoginTwoFactorUrl, new { code = TotpTestCodes.Wrong(key), isRecoveryCode = false });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        Assert.Equal(3, (await FindUserAsync(host, userId)).AccessFailedCount);
        using var right = await PostWithTokenAsync(client, LoginTwoFactorUrl, new { code = TotpTestCodes.Next(key), isRecoveryCode = false });
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.Equal(0, (await FindUserAsync(host, userId)).AccessFailedCount);
    }

    [Fact]
    public async Task A_pending_sign_in_is_refused_when_the_account_changes_before_the_code_arrives()
    {
        await using var host = await StartAsync();
        var email = UniqueEmail("stale-pending");
        var userId = await CreateStaffUserAsync(host, email);
        var key = await EnrolThroughApiAsync(host, email);
        using var client = await PendingSignInAsync(host, email);

        // Stands in for a password reset or a 2FA reset by another admin in between the two steps.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));
            await userManager.UpdateSecurityStampAsync(user!);
        }

        using var response = await PostWithTokenAsync(client, LoginTwoFactorUrl, new { code = TotpTestCodes.Current(key), isRecoveryCode = false });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("two_factor_session_expired", (await ReadJsonAsync(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Turning_two_factor_on_ends_every_other_session_at_once()
    {
        await using var host = await StartAsync();
        var email = UniqueEmail("other-sessions");
        await CreateStaffUserAsync(host, email);
        using var caller = await SignedInClientAsync(host, email);
        using var otherSession = await SignedInClientAsync(host, email);

        // Revalidate the other cookie now, so its next request falls inside the 60-second window and only the
        // gate's stamp comparison can refuse it.
        using (var warmUp = await otherSession.GetAsync(new Uri(AuthorizedProbeUrl, UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, warmUp.StatusCode);
        }

        await EnrolAsync(caller);

        using var otherProbe = await otherSession.GetAsync(new Uri(AuthorizedProbeUrl, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, otherProbe.StatusCode);
        Assert.Equal("session_ended", (await ReadJsonAsync(otherProbe)).GetProperty("error").GetString());

        using var callerProbe = await caller.GetAsync(new Uri(AuthorizedProbeUrl, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, callerProbe.StatusCode);
    }

    [Fact]
    public async Task A_super_admin_cannot_turn_two_factor_off()
    {
        await using var host = await StartAsync();
        var email = UniqueEmail("sa-disable");
        var userId = await CreateStaffUserAsync(host, email);
        await using var membership = await SuperAdminMembership.AddAsync(host, userId);
        using var client = await SignedInClientAsync(host, email);
        await EnrolAsync(client);

        var status = await GetJsonAsync(client, StatusUrl);
        Assert.False(status.GetProperty("canDisable").GetBoolean());

        using var disable = await PostWithTokenAsync(client, DisableUrl, new { currentPassword = Password });
        Assert.Equal(HttpStatusCode.Forbidden, disable.StatusCode);
        Assert.Equal("two_factor_required", (await ReadJsonAsync(disable)).GetProperty("error").GetString());
        Assert.True((await FindUserAsync(host, userId)).TwoFactorEnabled);
    }

    [Fact]
    public async Task Other_staff_can_turn_two_factor_off_with_their_password_and_back_on_with_a_new_key()
    {
        await using var host = await StartAsync();
        var email = UniqueEmail("staff-disable");
        var userId = await CreateStaffUserAsync(host, email);
        using var client = await SignedInClientAsync(host, email);

        // Optional for staff who are not GrantsAll holders: not required, and never gated.
        var before = await GetJsonAsync(client, StatusUrl);
        Assert.False(before.GetProperty("required").GetBoolean());
        var (firstKey, _) = await EnrolAsync(client);
        Assert.True((await GetJsonAsync(client, StatusUrl)).GetProperty("canDisable").GetBoolean());

        using var wrong = await PostWithTokenAsync(client, DisableUrl, new { currentPassword = "Wrong-Password-1!" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("invalid_current_password", (await ReadJsonAsync(wrong)).GetProperty("error").GetString());
        await AssertAuditAsync(host, email, "TwoFactorFailed");
        Assert.True((await FindUserAsync(host, userId)).TwoFactorEnabled);

        using var disable = await PostWithTokenAsync(client, DisableUrl, new { currentPassword = Password });
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
        await AssertAuditAsync(host, email, "TwoFactorDisabled");

        // The caller's session was re-issued with the rotated stamp and carries on.
        var after = await GetJsonAsync(client, StatusUrl);
        Assert.False(after.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, after.GetProperty("recoveryCodesLeft").GetInt32());

        // Password-only sign-in again.
        using var anonymous = CreateClient(host);
        using var login = await PostJsonAsync(anonymous, LoginUrl, new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.False((await ReadJsonAsync(login)).GetProperty("twoFactorRequired").GetBoolean());

        using var disableAgain = await PostWithTokenAsync(client, DisableUrl, new { currentPassword = Password });
        Assert.Equal(HttpStatusCode.Conflict, disableAgain.StatusCode);

        var (secondKey, _) = await EnrolAsync(client);
        Assert.NotEqual(firstKey, secondKey);
    }

    [Fact]
    public async Task Regenerating_recovery_codes_needs_the_password_and_retires_the_old_codes()
    {
        await using var host = await StartAsync();
        var email = UniqueEmail("regenerate");
        await CreateStaffUserAsync(host, email);
        using var client = await SignedInClientAsync(host, email);

        using (var notYet = await PostWithTokenAsync(client, RecoveryCodesUrl, new { currentPassword = Password }))
        {
            Assert.Equal(HttpStatusCode.Conflict, notYet.StatusCode);
            Assert.Equal("two_factor_not_enabled", (await ReadJsonAsync(notYet)).GetProperty("error").GetString());
        }

        var (_, oldCodes) = await EnrolAsync(client);

        using (var wrong = await PostWithTokenAsync(client, RecoveryCodesUrl, new { currentPassword = "Wrong-Password-1!" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }

        using var regenerate = await PostWithTokenAsync(client, RecoveryCodesUrl, new { currentPassword = Password });
        Assert.Equal(HttpStatusCode.OK, regenerate.StatusCode);
        Assert.Contains("no-store", regenerate.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
        var newCodes = RecoveryCodesFrom(await ReadJsonAsync(regenerate));
        Assert.Equal(10, newCodes.Count);
        Assert.Empty(newCodes.Intersect(oldCodes, StringComparer.Ordinal));
        await AssertAuditAsync(host, email, "RecoveryCodesRegenerated");

        using (var pending = await PendingSignInAsync(host, email))
        {
            using var old = await PostWithTokenAsync(pending, LoginTwoFactorUrl, new { code = oldCodes[0], isRecoveryCode = true });
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
            using var fresh = await PostWithTokenAsync(pending, LoginTwoFactorUrl, new { code = newCodes[0], isRecoveryCode = true });
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        }

        await AssertNoSecretsAuditedAsync(host, email, [.. oldCodes, .. newCodes]);
    }

    [Fact]
    public async Task Wrong_passwords_on_a_two_factor_change_count_towards_the_lockout()
    {
        await using var host = await StartAsync();
        var email = UniqueEmail("change-lockout");
        await CreateStaffUserAsync(host, email);
        using var client = await SignedInClientAsync(host, email);
        await EnrolAsync(client);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var failed = await PostWithTokenAsync(client, DisableUrl, new { currentPassword = "Wrong-Password-1!" });
            Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        }

        using var locked = await PostWithTokenAsync(client, DisableUrl, new { currentPassword = "Wrong-Password-1!" });
        Assert.Equal((HttpStatusCode)423, locked.StatusCode);
        Assert.Equal("locked_out", (await ReadJsonAsync(locked)).GetProperty("error").GetString());
        await AssertAuditAsync(host, email, "Lockout");

        using var stillLocked = await PostWithTokenAsync(client, RecoveryCodesUrl, new { currentPassword = Password });
        Assert.Equal((HttpStatusCode)423, stillLocked.StatusCode);
    }

    [Fact]
    public async Task Setup_resumes_the_same_key_until_it_is_confirmed()
    {
        await using var host = await StartAsync();
        var email = UniqueEmail("resume");
        await CreateStaffUserAsync(host, email);
        using var client = await SignedInClientAsync(host, email);

        using var first = await PostWithTokenAsync(client, SetupUrl, null);
        using var second = await PostWithTokenAsync(client, SetupUrl, null);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(
            (await ReadJsonAsync(first)).GetProperty("sharedKey").GetString(),
            (await ReadJsonAsync(second)).GetProperty("sharedKey").GetString());
    }

    [Fact]
    public async Task Enabling_before_setup_is_refused()
    {
        await using var host = await StartAsync();
        var email = UniqueEmail("not-set-up");
        await CreateStaffUserAsync(host, email);
        using var client = await SignedInClientAsync(host, email);

        using var enable = await PostWithTokenAsync(client, EnableUrl, new { code = "123456" });

        Assert.Equal(HttpStatusCode.Conflict, enable.StatusCode);
        Assert.Equal("two_factor_not_set_up", (await ReadJsonAsync(enable)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Every_two_factor_change_requires_the_antiforgery_token_and_a_session()
    {
        await using var host = await StartAsync();
        var email = UniqueEmail("xsrf");
        await CreateStaffUserAsync(host, email);
        using var client = await SignedInClientAsync(host, email);
        using var anonymous = CreateClient(host);

        foreach (var url in new[] { SetupUrl, EnableUrl, RecoveryCodesUrl, DisableUrl })
        {
            using var noToken = await PostJsonAsync(client, url, new { code = "123456", currentPassword = Password });
            Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
            Assert.Equal("invalid_antiforgery_token", (await ReadJsonAsync(noToken)).GetProperty("error").GetString());

            using var signedOut = await PostWithTokenAsync(anonymous, url, new { code = "123456", currentPassword = Password });
            Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
        }

        using var status = await anonymous.GetAsync(new Uri(StatusUrl, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string UniqueEmail(string label) => $"{label}-{Guid.NewGuid():N}@example.com";

    private async Task<WebApplicationFactory<Program>> StartAsync(
        IReadOnlyDictionary<string, string?>? configuration = null,
        DbCommandInterceptor? commandInterceptor = null)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Ct);

        // The probe host is derived from (and owned by) this factory: disposing it disposes both.
        var factory = new ApiFactory(sqlServer.ConnectionString, additionalConfiguration: configuration, commandInterceptor: commandInterceptor);
        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddTransient<IStartupFilter, ProbeEndpointsStartupFilter>()));
    }

    /// <summary>HTTPS base address so the Secure admin cookies round-trip (see <see cref="AdminAuthEndpointTests"/>).</summary>
    private static HttpClient CreateClient(WebApplicationFactory<Program> factory, bool handleCookies = true) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = handleCookies });

    /// <summary>"name=value" of a cookie a response set, for clients that manage cookies by hand.</summary>
    private static string CookiePair(HttpResponseMessage response, string name) =>
        CookiePairOrNull(response, name) ?? throw new InvalidOperationException($"The response set no '{name}' cookie.");

    private static string? CookiePairOrNull(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';', 2)[0]).FirstOrDefault(pair => pair.StartsWith(name + "=", StringComparison.Ordinal) && pair.Length > name.Length + 1)
            : null;

    /// <summary>The second sign-in step from a cookie-less client: a fresh antiforgery pair plus the given pending cookie.</summary>
    private static async Task<HttpResponseMessage> PostTwoFactorWithCookieAsync(HttpClient client, string pendingCookie, string code)
    {
        using var tokenResponse = await client.GetAsync(new Uri(AntiforgeryUrl, UriKind.Relative), Ct);
        var token = (await ReadJsonAsync(tokenResponse)).GetProperty("token").GetString()!;
        var antiforgeryCookie = CookiePair(tokenResponse, "sd_admin_xsrf");

        using var request = new HttpRequestMessage(HttpMethod.Post, LoginTwoFactorUrl)
        {
            Content = JsonContent.Create(new { code, isRecoveryCode = false }),
        };
        request.Headers.Add("X-XSRF-TOKEN", token);
        request.Headers.Add("Cookie", $"{antiforgeryCookie}; {pendingCookie}");
        return await client.SendAsync(request, Ct);
    }

    private static async Task<HttpClient> SignedInClientAsync(WebApplicationFactory<Program> factory, string email)
    {
        var client = CreateClient(factory);
        using var login = await PostJsonAsync(client, LoginUrl, new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.False((await ReadJsonAsync(login)).GetProperty("twoFactorRequired").GetBoolean());
        return client;
    }

    /// <summary>A client that has passed the password step of a 2FA account and holds only the pending cookie.</summary>
    private static async Task<HttpClient> PendingSignInAsync(WebApplicationFactory<Program> factory, string email)
    {
        var client = CreateClient(factory);
        using var login = await PostJsonAsync(client, LoginUrl, new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True((await ReadJsonAsync(login)).GetProperty("twoFactorRequired").GetBoolean());
        return client;
    }

    /// <summary>Enrols a signed-in client through the API, as the enrolment page does. Returns the key and the codes.</summary>
    private static async Task<(string Key, IReadOnlyList<string> RecoveryCodes)> EnrolAsync(HttpClient client)
    {
        using var setup = await PostWithTokenAsync(client, SetupUrl, null);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var key = TotpTestCodes.SecretFromUri((await ReadJsonAsync(setup)).GetProperty("authenticatorUri").GetString()!);

        using var enable = await PostWithTokenAsync(client, EnableUrl, new { code = TotpTestCodes.Current(key) });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        return (key, RecoveryCodesFrom(await ReadJsonAsync(enable)));
    }

    private static async Task<string> EnrolThroughApiAsync(WebApplicationFactory<Program> factory, string email) =>
        (await EnrolThroughApiWithCodesAsync(factory, email)).Key;

    private static async Task<(string Key, IReadOnlyList<string> RecoveryCodes)> EnrolThroughApiWithCodesAsync(
        WebApplicationFactory<Program> factory, string email)
    {
        using var client = await SignedInClientAsync(factory, email);
        return await EnrolAsync(client);
    }

    private static List<string> RecoveryCodesFrom(JsonElement body) =>
        [.. body.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!)];

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using var tokenResponse = await client.GetAsync(new Uri(AntiforgeryUrl, UriKind.Relative), Ct);
        return (await ReadJsonAsync(tokenResponse)).GetProperty("token").GetString() ?? string.Empty;
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, object body) =>
        await client.PostAsJsonAsync(url, body, Ct);

    private static async Task<HttpResponseMessage> PostWithTokenAsync(HttpClient client, string url, object? body)
    {
        var token = await GetAntiforgeryTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        request.Headers.Add("X-XSRF-TOKEN", token);
        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    /// <summary>The machine-readable <c>error</c> of a reply, or the raw body when it has none (a 500, say), for assertion messages.</summary>
    private static string? ErrorCodeOf(string body)
    {
        if (!body.StartsWith('{'))
        {
            return body;
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("error", out var code) ? code.GetString() : body;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task AssertRefusedAsync(HttpClient client, string url, string expectedCode)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expectedCode, (await ReadJsonAsync(response)).GetProperty("error").GetString());
    }

    private static async Task<int> CreateStaffUserAsync(WebApplicationFactory<Program> factory, string email, bool mustChangePassword = false)
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

        var result = await userManager.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        return user.Id;
    }

    private static async Task<AppUser> FindUserAsync(WebApplicationFactory<Program> factory, int userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await dbContext.Users.AsNoTracking().SingleAsync(u => u.Id == userId, Ct);
    }

    private static async Task AssertAuditAsync(
        WebApplicationFactory<Program> factory, string email, string action, string? afterJsonContains = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await dbContext.AuditLogs.AsNoTracking().Where(a => a.UserEmail == email && a.Action == action).ToListAsync(Ct);
        var found = rows.Exists(r =>
            r.UserId is not null
            && (afterJsonContains is null || (r.AfterJson?.Contains(afterJsonContains, StringComparison.Ordinal) ?? false)));
        Assert.True(found, $"Expected an AuditLogs row with Action '{action}' for '{email}' (after JSON containing '{afterJsonContains}').");
    }

    /// <summary>Doc 08 §3.8: authenticator keys and recovery codes are never logged, in any column of any row.</summary>
    private static async Task AssertNoSecretsAuditedAsync(WebApplicationFactory<Program> factory, string email, IEnumerable<string> secrets)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await dbContext.AuditLogs.AsNoTracking().Where(a => a.UserEmail == email).ToListAsync(Ct);
        Assert.NotEmpty(rows);
        var text = string.Join('\n', rows.Select(r => $"{r.Action}|{r.BeforeJson}|{r.AfterJson}|{r.EntityId}|{r.UserAgent}"));
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Makes a user a GrantsAll holder for one test through the seeded Super Admin role (created here if no test
    /// has created it yet), and removes the membership again on dispose, so the database is left with the same
    /// holders it had before.
    /// </summary>
    private sealed class SuperAdminMembership : IAsyncDisposable
    {
        private readonly WebApplicationFactory<Program> _factory;
        private readonly int _userId;

        private SuperAdminMembership(WebApplicationFactory<Program> factory, int userId)
        {
            _factory = factory;
            _userId = userId;
        }

        public static async Task<SuperAdminMembership> AddAsync(WebApplicationFactory<Program> factory, int userId)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

            if (await roleManager.FindByNameAsync(AdminSeedHostedService.SuperAdminRoleName) is null)
            {
                var created = await roleManager.CreateAsync(new AppRole
                {
                    Name = AdminSeedHostedService.SuperAdminRoleName,
                    IsSystem = true,
                    GrantsAllPermissions = true,
                    CreatedAtUtc = DateTime.UtcNow,
                });
                Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
            }

            var user = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));
            var added = await userManager.AddToRoleAsync(user!, AdminSeedHostedService.SuperAdminRoleName);
            Assert.True(added.Succeeded, string.Join("; ", added.Errors.Select(e => e.Description)));
            return new SuperAdminMembership(factory, userId);
        }

        public async ValueTask DisposeAsync()
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = await userManager.FindByIdAsync(_userId.ToString(CultureInfo.InvariantCulture));
            if (user is not null)
            {
                await userManager.RemoveFromRoleAsync(user, AdminSeedHostedService.SuperAdminRoleName);
            }
        }
    }

    /// <summary>
    /// The same stand-in admin endpoints as <see cref="AdminPasswordChangeEndpointTests"/>: a bare
    /// <c>RequireAuthorization()</c> and a <c>RequirePermission(...)</c>, the two ways every admin endpoint is declared.
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
