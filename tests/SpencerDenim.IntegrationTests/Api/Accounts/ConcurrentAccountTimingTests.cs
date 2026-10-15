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
/// an uncommitted registration. The before and after figures from this machine are in
/// <c>docs/journal/2026-10-15-rcsi.md</c>. Registration timing is reported but not asserted on: a free
/// address does work a taken one cannot (a new row, two queued emails), so its tail under 24-way load is
/// mostly that work, which <see cref="AnonymousAccountTimingTests"/> covers one request at a time.
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
                var sample = await TimeOneAsync(warm, path);
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
                samples[path].Add(await TimeOneAsync(client, path));
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

        foreach (var endpoint in tails.Where(pair => pair.Key.Endpoint != "register").GroupBy(pair => pair.Key.Endpoint))
        {
            var slowest = endpoint.MaxBy(pair => pair.Value.P90);
            var fastest = endpoint.MinBy(pair => pair.Value.P90);
            var spread = slowest.Value.P90 / fastest.Value.P90;

            Assert.True(
                spread <= AllowedP90Spread,
                $"{endpoint.Key} under {Clients} concurrent clients: '{slowest.Key.Name}' p90 {slowest.Value.P90:F0} ms, "
                + $"'{fastest.Key.Name}' p90 {fastest.Value.P90:F0} ms ({spread:F2}x, allowed {AllowedP90Spread}x). "
                + "A lookup is waiting on someone else's uncommitted registration (#163). Is READ_COMMITTED_SNAPSHOT on? "
                + "All p90s: " + string.Join(", ", endpoint.Select(pair => $"{pair.Key.Name}={pair.Value.P90:F0}")));
        }
    }

    private static async Task<List<Path>> ArrangeAsync(ApiFactory factory)
    {
        using var client = CreateClient(factory);
        var customer = await RegisterAsync(client, UniqueEmail("load-customer"));
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
            new Path("sign-in", "wrong password", LoginUrl, () => new { email = customer, password = WrongPassword }),
            new Path("sign-in", "unknown address", LoginUrl, () => new { email = UniqueEmail("load-nobody"), password = GoodPassword }),
            new Path("sign-in", "staff address", LoginUrl, () => new { email = staff, password = GoodPassword }),
            new Path("sign-in", "deactivated", LoginUrl, () => new { email = deactivated, password = GoodPassword }),
            new Path("sign-in", "locked (right password)", LoginUrl, () => new { email = locked, password = GoodPassword }),
            new Path("register", "free address", RegisterUrl, () => Registration(UniqueEmail("load-free"))),
            new Path("register", "customer address", RegisterUrl, () => Registration(customer)),
            new Path("register", "staff address", RegisterUrl, () => Registration(staff)),
            new Path("forgot-password", "customer address", ForgotUrl, () => new { email = customer }),
            new Path("forgot-password", "unknown address", ForgotUrl, () => new { email = UniqueEmail("load-nobody") }),
            new Path("forgot-password", "staff address", ForgotUrl, () => new { email = staff }),
        ];
    }

    private static object Registration(string email) =>
        new { firstName = "Nimal", lastName = "Perera", email, password = GoodPassword, phone = (string?)null };

    private static async Task<Sample> TimeOneAsync(HttpClient client, Path path)
    {
        var stopwatch = Stopwatch.StartNew();

        using var response = await client.PostAsJsonAsync(path.Url, path.Body(), Token);
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

    private sealed record Path(string Endpoint, string Name, string Url, Func<object> Body);

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
