using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Accounts;

/// <summary>
/// Issue #163: W3-01's response floor has to hold while other people are registering at the same time,
/// not only one request at a time.
/// </summary>
/// <remarks>
/// <b>What went wrong.</b> <see cref="AnonymousAccountTimingTests"/> sends one request at a time, and at
/// that pace every refusal path answered in the same time. W3-01's round-2 tester then ran 24 concurrent
/// clients and found sign-in for an unknown, staff or deactivated address taking seconds at p90 against a
/// few hundred milliseconds for a wrong password. The lookups for those addresses read the users table
/// while somebody else's registration had inserted a row and not yet committed it. On a small table SQL
/// Server scans rather than seeks, the scan reaches the locked row, and under locking
/// <c>READ COMMITTED</c> it waits for the commit. A lookup for an existing customer finds its row first
/// and never reaches the new one, so the wait itself told a stranger which kind of address they sent.
/// <para>
/// <b>The same waits made concurrent registrations deadlock.</b> Re-running that measurement for #163
/// found free-address registrations answering 500, with SQL Server error 1205 (deadlock victim) in the
/// log. So this test also requires every answer to be one the endpoint is meant to give.
/// </para>
/// <para>
/// <b>The fix is a database option, not code:</b> <c>READ_COMMITTED_SNAPSHOT ON</c>, set by
/// <c>tools/db/provision-database.sql</c>, which every database — live and test — is created with. A
/// plain read then takes the last committed version of a row instead of waiting for its writer.
/// <c>ReadCommittedSnapshotTests</c> holds the mechanism deterministically; this test holds the symptom.
/// </para>
/// <para>
/// <b>Why p90, and why 2x.</b> The leak lives in the tail, because most requests do not happen to overlap
/// an uncommitted registration. Figures from this machine are in <c>docs/journal/2026-10-15-rcsi.md</c>.
/// Before: unknown, staff and deactivated sign-in p90 2,988–5,360 ms against 280–613 ms for a wrong
/// password, 7.0–16.1x (three runs). After, over six runs, no such path's p90 exceeded the active
/// customer's on either endpoint, so 2x is far from both.
/// </para>
/// <para>
/// <b>What this test deliberately does not assert.</b> Registration timing is reported only: a free
/// address does work a taken one cannot (a new row, two queued emails). And the opposite direction is
/// left open, because with READ_COMMITTED_SNAPSHOT on an <i>active customer</i> still answers slower at
/// p90 under this load: forgot-password 2.0–3.8x the fastest other path, sign-in with a wrong password
/// 1.2–2.1x (six runs). Those are the paths that write: a reset token and a queued email in one
/// transaction, a failed-attempt count. The likely cause is that work outlasting the 250 ms floor at 24-way
/// load — the same oracle with its sign flipped — but that is inferred, not traced. It is not a read
/// waiting on a writer (<c>ReadCommittedSnapshotTests</c> shows the lookups no longer wait), and the
/// journal records it as open rather than hiding it behind a looser number here.
/// </para>
/// <para>
/// Set <c>RCSI_TIMING_OUT</c> to a file path to have every path's p50/p90/p95 appended to it.
/// </para>
/// </remarks>
public sealed class ConcurrentAccountTimingTests(SqlServerDatabaseFixture sqlServer)
    : CustomerAccountTestBase(sqlServer)
{
    private const string LoginUrl = "/api/account/login";
    private const string RegisterUrl = "/api/account/register";
    private const string ForgotUrl = "/api/account/forgot-password";
    private const string WrongPassword = "Wrong-Password-9!";

    /// <summary>Concurrent clients, as in the measurement that found #163.</summary>
    private const int Clients = 24;

    /// <summary>Requests each client sends to each path.</summary>
    private const int RequestsPerPath = 3;

    private const double AllowedP90Spread = 2.0;

    [Fact]
    public async Task Under_concurrent_registrations_every_path_answers_correctly_and_sign_in_and_forgot_password_keep_the_same_tail()
    {
        await ReadyAsync();

        await using var factory = CreateHost(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CustomerSecurity:AnonymousResponseFloorMilliseconds"] = "250",
            ["CustomerSecurity:SignInAttemptsPerWindow"] = "100000",
            ["CustomerSecurity:RegistrationsPerWindow"] = "100000",
            ["CustomerSecurity:LinkRequestsPerWindow"] = "100000",
            ["CustomerSecurity:LockoutMaxFailedAttempts"] = "100000",
        });

        var paths = await ArrangeAsync(factory);

        // Warm-up, one at a time: no path pays for a cold plan or the first hash in the process.
        using (var warm = CreateClient(factory))
        {
            foreach (var path in paths)
            {
                var sample = await TimeOneAsync(warm, path, clientIndex: 0);
                Assert.True(sample.Expected, $"{path.Endpoint} / {path.Name} answered {sample.Status} with nothing else running.");
            }
        }

        var samples = paths.ToDictionary(path => path, _ => new ConcurrentBag<Sample>());

        // Every client cycles through every path from a different starting point, so free-address
        // registrations — the uncommitted inserts — are always in flight alongside every other path.
        var clients = Enumerable.Range(0, Clients).Select(async index =>
        {
            using var client = CreateClient(factory);

            for (var request = 0; request < paths.Count * RequestsPerPath; request++)
            {
                var path = paths[(index + request) % paths.Count];
                samples[path].Add(await TimeOneAsync(client, path, index));
            }
        });

        await Task.WhenAll(clients);

        var tails = samples.ToDictionary(pair => pair.Key, pair => new Tail([.. pair.Value]));
        await ReportAsync(tails);

        var wrong = tails.Where(pair => pair.Value.Unexpected > 0).ToList();
        Assert.True(
            wrong.Count == 0,
            $"Under {Clients} concurrent clients some requests got an answer the endpoint never gives: "
            + string.Join(", ", wrong.Select(pair => $"{pair.Key.Endpoint} / {pair.Key.Name}: {pair.Value.Unexpected} x {pair.Value.UnexpectedStatuses}"))
            + ". A 500 from registration has been a deadlock between concurrent registrations (#163).");

        // One direction: an address with no active customer behind it must not wait longer than one
        // with. That is the #163 oracle. The other direction is open and is not #163: see the remarks.
        foreach (var endpoint in tails.Where(pair => pair.Key.Endpoint != "register").GroupBy(pair => pair.Key.Endpoint))
        {
            var customerP90 = endpoint.Single(pair => pair.Key.ActiveCustomer).Value.P90;

            foreach (var (path, tail) in endpoint.Where(pair => !pair.Key.ActiveCustomer))
            {
                Assert.True(
                    tail.P90 <= AllowedP90Spread * customerP90,
                    $"{endpoint.Key} under {Clients} concurrent clients: '{path.Name}' p90 {tail.P90:F0} ms against "
                    + $"{customerP90:F0} ms for an active customer (allowed {AllowedP90Spread}x). A lookup is waiting on "
                    + "someone else's uncommitted registration (#163): is READ_COMMITTED_SNAPSHOT on? All p90s: " + P90s(endpoint));
            }
        }
    }

    private static string P90s(IEnumerable<KeyValuePair<Path, Tail>> tails) =>
        string.Join(", ", tails.Select(pair => $"{pair.Key.Name}={pair.Value.P90:F0}"));

    private static async Task<List<Path>> ArrangeAsync(ApiFactory factory)
    {
        using var client = CreateClient(factory);
        // One customer per client. If all 24 clients sent wrong passwords to one account, its failure
        // counter UPDATE — a write, which READ_COMMITTED_SNAPSHOT does not and must not stop from
        // queueing — would serialise them, and the test would measure one hot row instead of the
        // lookups (the journal has that run too). The same goes for forgot-password's token writes.
        var customers = new List<string>(Clients);
        for (var index = 0; index < Clients; index++)
        {
            customers.Add(await RegisterAsync(client, UniqueEmail("load-customer")));
        }

        var locked = await RegisterAsync(client, UniqueEmail("load-locked"));
        var deactivated = await RegisterAsync(client, UniqueEmail("load-deactivated"));
        var staff = UniqueEmail("load-staff");
        await CreateStaffUserAsync(factory, staff, GoodPassword);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var lockedKey = locked.ToUpperInvariant();
            var deactivatedKey = deactivated.ToUpperInvariant();

            await db.Database.ExecuteSqlAsync(
                $"UPDATE [identity].[Users] SET [LockoutEnabled] = 1, [LockoutEnd] = DATEADD(HOUR, 1, SYSUTCDATETIME()) WHERE [NormalizedEmail] = {lockedKey}",
                Token);
            await db.Database.ExecuteSqlAsync(
                $"UPDATE [identity].[Users] SET [IsActive] = 0 WHERE [NormalizedEmail] = {deactivatedKey}",
                Token);
        }

        return
        [
            new Path("sign-in", "wrong password", LoginUrl, index => new { email = customers[index], password = WrongPassword }, ActiveCustomer: true),
            new Path("sign-in", "unknown address", LoginUrl, _ => new { email = UniqueEmail("load-nobody"), password = GoodPassword }),
            new Path("sign-in", "staff address", LoginUrl, _ => new { email = staff, password = GoodPassword }),
            new Path("sign-in", "deactivated", LoginUrl, _ => new { email = deactivated, password = GoodPassword }),
            new Path("sign-in", "locked (right password)", LoginUrl, _ => new { email = locked, password = GoodPassword }),
            new Path("register", "free address", RegisterUrl, _ => Registration(UniqueEmail("load-free"))),
            new Path("register", "customer address", RegisterUrl, index => Registration(customers[index]), ActiveCustomer: true),
            new Path("register", "staff address", RegisterUrl, _ => Registration(staff)),
            new Path("forgot-password", "customer address", ForgotUrl, index => new { email = customers[index] }, ActiveCustomer: true),
            new Path("forgot-password", "unknown address", ForgotUrl, _ => new { email = UniqueEmail("load-nobody") }),
            new Path("forgot-password", "staff address", ForgotUrl, _ => new { email = staff }),
        ];
    }

    private static object Registration(string email) =>
        new { firstName = "Nimal", lastName = "Perera", email, password = GoodPassword, phone = (string?)null };

    private static async Task<Sample> TimeOneAsync(HttpClient client, Path path, int clientIndex)
    {
        var stopwatch = Stopwatch.StartNew();

        using var response = await client.PostAsJsonAsync(path.Url, path.Body(clientIndex), Token);
        _ = await response.Content.ReadAsStringAsync(Token);

        stopwatch.Stop();

        // Sign-in refuses with 401; registration and forgot-password accept and say nothing (202).
        var expected = path.Url == LoginUrl ? HttpStatusCode.Unauthorized : HttpStatusCode.Accepted;

        return new Sample(stopwatch.Elapsed.TotalMilliseconds, (int)response.StatusCode, response.StatusCode == expected);
    }

    private static async Task ReportAsync(Dictionary<Path, Tail> tails)
    {
        if (Environment.GetEnvironmentVariable("RCSI_TIMING_OUT") is not { Length: > 0 } file)
        {
            return;
        }

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"## {Clients} clients, {DateTime.UtcNow:O}");

        foreach (var (path, tail) in tails)
        {
            text.AppendLine(
                CultureInfo.InvariantCulture,
                $"{path.Endpoint,-16} {path.Name,-26} n={tail.Count,4} p50={tail.P50,7:F0} p90={tail.P90,7:F0} p95={tail.P95,7:F0} max={tail.Max,7:F0} unexpected={tail.Unexpected} {tail.UnexpectedStatuses}");
        }

        await File.AppendAllTextAsync(file, text.ToString(), Token);
    }

    /// <param name="ActiveCustomer">The request names an active customer, so the endpoint does that customer's work.</param>
    private sealed record Path(string Endpoint, string Name, string Url, Func<int, object> Body, bool ActiveCustomer = false);

    private sealed record Sample(double Milliseconds, int Status, bool Expected);

    /// <summary>Nearest-rank percentiles over every sample of one path, whatever it answered.</summary>
    private sealed class Tail(List<Sample> samples)
    {
        private readonly List<double> _sorted = [.. samples.Select(sample => sample.Milliseconds).Order()];

        public int Count => _sorted.Count;

        public double P50 => Rank(0.50);

        public double P90 => Rank(0.90);

        public double P95 => Rank(0.95);

        public double Max => _sorted[^1];

        public int Unexpected => samples.Count(sample => !sample.Expected);

        public string UnexpectedStatuses =>
            string.Join("/", samples.Where(sample => !sample.Expected).Select(sample => sample.Status).Distinct().Order());

        private double Rank(double percentile) =>
            _sorted[Math.Max(0, (int)Math.Ceiling(percentile * _sorted.Count) - 1)];
    }
}
