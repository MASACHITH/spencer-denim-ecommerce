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
        Assert.StartsWith("otpauth://totp/Spencer%20Denim%20Admin:", uri, StringComparison.Ordinal);
        Assert.Contains("issuer=Spencer%20Denim%20Admin", uri, StringComparison.Ordinal);
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

    private async Task<WebApplicationFactory<Program>> StartAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Ct);

        // The probe host is derived from (and owned by) this factory: disposing it disposes both.
        var factory = new ApiFactory(sqlServer.ConnectionString);
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
