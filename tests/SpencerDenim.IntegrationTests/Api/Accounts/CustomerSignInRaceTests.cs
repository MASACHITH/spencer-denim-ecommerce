using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Infrastructure.Identity;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Accounts;

/// <summary>
/// The customer sign-in under contention: parallel failures cannot outrun the lockout, and a correct
/// password cannot open a session on an account that has been locked while it was being verified.
/// </summary>
/// <remarks>
/// <b>Tharanga measured the staff side of this on #146: 60 parallel wrong passwords for one admin left
/// the count at 4 and the account unlocked.</b> Identity's failed-attempt counter is a
/// read-modify-write on <c>AccessFailedCount</c> with no concurrency token on the user row, so nothing
/// detects a concurrent edit. The customer threshold is twice as high, which makes the window twice as
/// wide — and that PR is open, not merged, so the customer side has its own atomic counting
/// (<c>CustomerAccountLockout</c>) and its own tests here.
/// <para>
/// <b>Not every test here can fail, and the tester pass measured which.</b> Removing the
/// post-verification <c>IsStillUnlockedAsync</c> check is killed by
/// <c>A_correct_password_is_refused_if_the_account_locks_while_it_is_being_verified</c>. Reverting
/// <c>RecordFailureAsync</c> to a read-then-write is <b>not</b> killed by anything in this file — only by
/// <c>CustomerLockoutConcurrencyTests</c>. The reason is not that these requests fail to overlap: with a
/// 30 ms delay injected at the counter statement, 24 HTTP sign-ins had 20 in flight at once. It is that
/// thirty attempts against a threshold of five reach the threshold even when increments are lost, so the
/// end state asserted here is the same either way. Treat the HTTP tests as end-to-end checks of the
/// lockout reply, and the store-level tests as the atomicity proof.
/// </para>
/// <para>
/// <b>Separate <see cref="HttpClient"/> instances, not one client sending in parallel.</b> A shared
/// client shares a cookie jar and a connection pool; the point here is many independent browsers, and a
/// shared jar would have later requests carrying a session an earlier one established.
/// </para>
/// </remarks>
public sealed class CustomerSignInRaceTests(SqlServerDatabaseFixture sqlServer)
    : CustomerAccountTestBase(sqlServer)
{
    private const string LoginUrl = "/api/account/login";
    private const string MeUrl = "/api/account/me";

    /// <summary>
    /// Thirty parallel wrong passwords against a threshold of five: the account ends up locked, and the
    /// count was not lost.
    /// </summary>
    /// <remarks>
    /// <b>What is asserted is the <i>end state</i>, not the number of 401s.</b> Under contention the
    /// split between 401 and 423 is genuinely non-deterministic — it depends on how many requests get
    /// past the pre-check before the one that trips the lock commits — so asserting "exactly four 401s
    /// then 423" would be a flaky test asserting an implementation detail. What must hold, and what the
    /// read-modify-write gets wrong, is that the account is locked afterwards and that a correct password
    /// is then refused.
    /// </remarks>
    [Fact]
    public async Task Thirty_parallel_wrong_passwords_still_lock_the_account()
    {
        await ReadyAsync();

        await using var factory = CreateHost(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CustomerSecurity:LockoutMaxFailedAttempts"] = "5",
            ["CustomerSecurity:LockoutMinutes"] = "15",

            // Above the thirty attempts below, because this test is about the lockout and not about the
            // rate limiter: at the production default of 30 the thirty-first request — the correct
            // password at the end — answers 429 and the test measures the wrong control. The two exist
            // for different reasons (see CustomerSecurityOptions.SignInAttemptsPerWindow) and a test of
            // one must not be capped by the other.
            ["CustomerSecurity:SignInAttemptsPerWindow"] = "200",
        });

        using var setup = CreateClient(factory);
        var email = UniqueEmail("race-lockout");
        await RegisterAsync(setup, email);

        var statuses = await StormAsync(factory, email, _ => "Wrong-Password-9!", attempts: 30);

        Assert.All(
            statuses,
            status => Assert.True(
                status is HttpStatusCode.Unauthorized,
                $"A wrong password answered {(int)status}."));

        // The end state: locked. A read-modify-write counter loses increments under this load and leaves
        // the account open — #146 measured 60 attempts leaving a count of 4.
        var locked = await ReadCustomerAsync(factory, email);
        Assert.NotNull(locked);
        Assert.NotNull(locked.LockoutEnd);
        Assert.True(
            locked.LockoutEnd > DateTimeOffset.UtcNow,
            "The account is not locked after 30 parallel wrong passwords: the failure counter lost "
            + "increments, which is #146's finding on the staff side.");

        // And the lock is real, not merely recorded.
        using var correct = CreateClient(factory);
        using var refused = await correct.PostAsJsonAsync(LoginUrl, new { email, password = GoodPassword }, Token);

        // 401 and not 423: a locked account answers what a wrong password answers (W3-01 Major 4).
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    /// <summary>
    /// Exactly one <c>Lockout</c> audit row, however many attempts tripped over the threshold together.
    /// </summary>
    /// <remarks>
    /// <b>This is the half of the atomic counter that is not about security at all.</b> Reporting the
    /// lockout to every attempt that saw it would write thirty rows for one event, and an audit trail
    /// that says an account was locked thirty times is a trail nobody can read. Only the attempt whose
    /// own statement set <c>LockoutEnd</c> is told it tripped it.
    /// <para>
    /// <b>The rows are counted by action and time, not by user id</b>, because the customer sign-in's
    /// lockout row deliberately carries no user id — a result that named the account would make the reply
    /// "this address exists and is now locked", which is the enumeration leak the whole flow avoids. That
    /// decision is in <c>CustomerSignInCommandHandler</c>'s remarks and in the task report.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task One_lockout_is_audited_once_however_many_attempts_raced()
    {
        await ReadyAsync();

        await using var factory = CreateHost(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CustomerSecurity:LockoutMaxFailedAttempts"] = "3",
            ["CustomerSecurity:LockoutMinutes"] = "15",
            ["CustomerSecurity:SignInAttemptsPerWindow"] = "200",
        });

        using var setup = CreateClient(factory);
        var email = UniqueEmail("race-audit");
        await RegisterAsync(setup, email);

        var startedAtUtc = DateTime.UtcNow;

        await StormAsync(factory, email, _ => "Wrong-Password-9!", attempts: 20);

        DbContext.ChangeTracker.Clear();

        var rows = await DbContext.AuditLogs
            .AsNoTracking()
            .CountAsync(
                row => row.Action == AuditActions.Lockout && row.OccurredAtUtc >= startedAtUtc,
                Token);

        Assert.Equal(1, rows);
    }

    /// <summary>
    /// <b>The guard #146 names, and the one that shipped on the staff side with zero coverage.</b> A
    /// correct password must not open a session if the account was locked while it was being verified.
    /// </summary>
    /// <remarks>
    /// <b>Why it is a race at all.</b> Every request checks the lockout <i>before</i> it verifies the
    /// password. So an attacker fires the right password alongside enough wrong ones to trip the lock:
    /// all of them pass that pre-check, and by the time the right one has finished its PBKDF2
    /// verification the account is locked. Without a fresh read <b>after</b> the hash, it answers 200 and
    /// issues a cookie for a locked account — a complete bypass of the lockout for anybody who knows the
    /// password, and a way to tell a <i>guessed</i> password from a wrong one even after the lock.
    /// <para>
    /// <b>It is forced rather than raced, and that is the only honest way to test it.</b> The first
    /// version of this test fired three wrong passwords and one right one together and asserted that no
    /// 200 came back beside a locked row. It failed four times in six — and it was the <i>test</i> that
    /// was wrong: the correct password legitimately wins when it commits before the lock is set, and the
    /// account is then locked a moment later, which looks identical to a bypass if you only read the row
    /// afterwards. A race observed from outside cannot distinguish "signed in, then locked" from
    /// "locked, then signed in"; this project's own history is full of race tests that measured the
    /// wrong thing.
    /// </para>
    /// <para>
    /// So the interleaving is made deterministic. The host's
    /// <see cref="IPasswordHasher{TUser}"/> is replaced with one that locks the account <b>during</b> the
    /// one verification that is going to succeed, before returning success. That is exactly the state
    /// the race produces, every time, with no sleeping and no flakiness — and it is the state the guard
    /// exists for.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_correct_password_is_refused_if_the_account_locks_while_it_is_being_verified()
    {
        await ReadyAsync();

        var email = UniqueEmail("race-locked-mid-verify");
        var lockTheAccount = new TaskCompletionSource<Func<Task>>();

        await using var factory = CreateHost(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["CustomerSecurity:LockoutMaxFailedAttempts"] = "3",
                ["CustomerSecurity:LockoutMinutes"] = "15",
            },
            services => services.AddSingleton<IPasswordHasher<AppUser>>(
                new LocksDuringVerification(lockTheAccount.Task)));

        using var client = CreateClient(factory);
        await RegisterAsync(client, email);

        var customer = await ReadCustomerAsync(factory, email);
        Assert.NotNull(customer);

        // The hook the decorator runs the moment it is about to return "the password is right". Writing
        // the lock straight to the row is what the three parallel wrong passwords would have done; the
        // guard cannot tell the difference, and neither should it.
        lockTheAccount.SetResult(() => LockAsync(customer.Id));

        using var response = await client.PostAsJsonAsync(LoginUrl, new { email, password = GoodPassword }, Token);

        // The wrong-password reply, because a lock is never announced (W3-01 Major 4).
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // And no session was issued, which is the part that matters.
        using var me = await client.GetAsync(new Uri(MeUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    /// <summary>
    /// The mirror of the test above: with no lock in the way, the same correct password signs in.
    /// </summary>
    /// <remarks>
    /// <b>Without this the guard could be "refuse every correct password" and the test above would still
    /// pass.</b> It uses the same decorator with a hook that does nothing, so the only difference between
    /// the two tests is whether the account is locked — which is the one variable under test.
    /// </remarks>
    [Fact]
    public async Task The_same_correct_password_signs_in_when_nothing_locks_the_account()
    {
        await ReadyAsync();

        var email = UniqueEmail("race-not-locked");
        var doNothing = new TaskCompletionSource<Func<Task>>();
        doNothing.SetResult(() => Task.CompletedTask);

        await using var factory = CreateHost(
            null,
            services => services.AddSingleton<IPasswordHasher<AppUser>>(
                new LocksDuringVerification(doNothing.Task)));

        using var client = CreateClient(factory);
        await RegisterAsync(client, email);

        using var response = await client.PostAsJsonAsync(LoginUrl, new { email, password = GoodPassword }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var me = await client.GetAsync(new Uri(MeUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    /// <summary>Locks the account with the same two columns a real lockout writes.</summary>
    private async Task LockAsync(int userId)
    {
        DbContext.ChangeTracker.Clear();

        await DbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE [identity].[Users]
            SET [LockoutEnd] = {DateTime.UtcNow.AddMinutes(15)}, [AccessFailedCount] = 0
            WHERE [Id] = {userId}
            """,
            Token);
    }

    /// <summary>
    /// Identity's own hasher, with a hook that runs the instant before it reports a <b>successful</b>
    /// verification.
    /// </summary>
    /// <remarks>
    /// <b>Only the success path is hooked</b>, so the registration's own hashing and every failed
    /// verification behave exactly as they do in production. The hook is awaited synchronously, which is
    /// acceptable in a test decorator and is what makes the ordering deterministic: by the time
    /// <c>VerifyHashedPassword</c> returns, the account is locked.
    /// </remarks>
    private sealed class LocksDuringVerification(Task<Func<Task>> hook) : IPasswordHasher<AppUser>
    {
        private readonly PasswordHasher<AppUser> _inner = new();

        public string HashPassword(AppUser user, string password) => _inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(
            AppUser user,
            string hashedPassword,
            string providedPassword)
        {
            var result = _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);

            if (result != PasswordVerificationResult.Failed)
            {
                hook.GetAwaiter().GetResult().Invoke().GetAwaiter().GetResult();
            }

            return result;
        }
    }


    /// <summary>
    /// Doc 08 §3.1 DB10's "it expires by itself": parallel attempts against a locked account do not push
    /// the end time out.
    /// </summary>
    [Fact]
    public async Task Parallel_attempts_against_a_locked_account_do_not_extend_the_lock()
    {
        await ReadyAsync();

        await using var factory = CreateHost(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CustomerSecurity:LockoutMaxFailedAttempts"] = "2",
            ["CustomerSecurity:LockoutMinutes"] = "15",
            ["CustomerSecurity:SignInAttemptsPerWindow"] = "200",
        });

        using var setup = CreateClient(factory);
        var email = UniqueEmail("race-no-extend");
        await RegisterAsync(setup, email);

        await StormAsync(factory, email, _ => "Wrong-Password-9!", attempts: 4);

        var locked = await ReadCustomerAsync(factory, email);
        Assert.NotNull(locked);
        Assert.NotNull(locked.LockoutEnd);

        await StormAsync(factory, email, _ => "Wrong-Password-9!", attempts: 20);

        var after = await ReadCustomerAsync(factory, email);
        Assert.NotNull(after);
        Assert.Equal(locked.LockoutEnd, after.LockoutEnd);
    }

    /// <summary>
    /// <paramref name="attempts"/> sign-ins released together, each from its own client.
    /// </summary>
    /// <remarks>
    /// <b>A <see cref="TaskCompletionSource"/> gate, so they start together rather than merely being
    /// started in a loop.</b> Without it the first request has usually finished before the last is
    /// dispatched, and the test measures nothing — which is the shape of the race test that passed for
    /// thirteen days because four shoppers' stock updates serialised them before the section under test.
    /// </remarks>
    private static async Task<List<HttpStatusCode>> StormAsync(
        ApiFactory factory,
        string email,
        Func<int, string> password,
        int attempts)
    {
        var clients = Enumerable.Range(0, attempts).Select(_ => CreateClient(factory)).ToList();

        try
        {
            var gate = new TaskCompletionSource();

            var requests = clients.Select(async (client, index) =>
            {
                await gate.Task;

                using var response = await client.PostAsJsonAsync(
                    LoginUrl,
                    new { email, password = password(index) },
                    Token);

                return response.StatusCode;
            }).ToList();

            gate.SetResult();

            return [.. await Task.WhenAll(requests)];
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }
}
