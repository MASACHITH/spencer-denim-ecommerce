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
/// W3-01 Major 3: the time sign-in, registration and forgot-password take to answer says nothing about
/// the address in the request.
/// </summary>
/// <remarks>
/// <b>Why this replaced a 10x ratio test.</b> The round-1 test asserted only that an unknown address was
/// not an <i>order of magnitude</i> faster than a wrong password, on sign-in alone. The independent
/// tester then measured registration at 76.9 ms (free) / 2.5 ms (customer) / 1.1 ms (staff) and
/// forgot-password at 4.6 ms (customer) / 1.2 ms (unknown or staff): forgot-password's 3.8x passed the
/// 10x test, and registration was never measured at all. These tests measure every refusal path on all
/// three endpoints and allow <b>1.5x</b> between the slowest and fastest median on one endpoint, so a
/// 3x difference fails.
/// <para>
/// <b>Two tests, because two things equalise the time.</b> The response floor
/// (<c>CustomerSecurity:AnonymousResponseFloorMilliseconds</c>) is what makes the replies equal; the
/// dummy password hash is what keeps them equal when a path runs past the floor. The first test runs at
/// the production floor and covers every path. The second turns the floor off and checks the hash is
/// still spent on the paths where the hash is the dominant cost (sign-in and registration) — without it,
/// removing the hash would pass silently because the floor hides it.
/// </para>
/// <para>
/// Medians over interleaved rounds, after a warm-up round, because one sample on a machine that is also
/// running SQL Server and a test host is noise. Set <c>W301_TIMING_OUT</c> to a file path to have the
/// medians written there for the task report.
/// </para>
/// </remarks>
public sealed class AnonymousAccountTimingTests(SqlServerDatabaseFixture sqlServer)
    : CustomerAccountTestBase(sqlServer)
{
    private const string LoginUrl = "/api/account/login";
    private const string RegisterUrl = "/api/account/register";
    private const string ForgotUrl = "/api/account/forgot-password";
    private const string WrongPassword = "Wrong-Password-9!";

    /// <summary>The tightest spread allowed between paths on one endpoint. A 3x oracle fails it.</summary>
    private const double AllowedSpread = 1.5;

    private const int Rounds = 9;

    [Fact]
    public async Task At_the_configured_floor_every_path_on_each_endpoint_answers_in_the_same_time()
    {
        await ReadyAsync();

        const int floor = 250;
        await using var factory = CreateHost(Settings(floor));
        using var client = CreateClient(factory);
        var paths = await ArrangeAsync(factory, client);

        var medians = await MeasureAsync(client, paths.All);
        await ReportAsync("floor 250 ms", medians);

        foreach (var endpoint in medians.GroupBy(pair => pair.Key.Endpoint))
        {
            AssertSpread(endpoint.Key, endpoint.ToDictionary(pair => pair.Key.Name, pair => pair.Value));

            foreach (var (path, median) in endpoint)
            {
                // The floor is actually in force: no path answers before it. 5% under, for the timer's
                // own granularity on Windows.
                Assert.True(
                    median >= floor * 0.95,
                    $"{path.Endpoint} / {path.Name} answered in {median:F1} ms, under the {floor} ms floor.");
            }
        }
    }

    [Fact]
    public async Task With_no_floor_the_password_hash_still_costs_the_same_on_every_sign_in_and_registration_path()
    {
        await ReadyAsync();

        await using var factory = CreateHost(Settings(0));
        using var client = CreateClient(factory);
        var paths = await ArrangeAsync(factory, client);

        var hashed = paths.All.Where(path => path.Endpoint is "sign-in" or "register").ToList();
        var medians = await MeasureAsync(client, hashed);
        await ReportAsync("floor off", medians);

        foreach (var endpoint in medians.GroupBy(pair => pair.Key.Endpoint))
        {
            AssertSpread(endpoint.Key, endpoint.ToDictionary(pair => pair.Key.Name, pair => pair.Value));
        }
    }

    private static Dictionary<string, string?> Settings(int floor) => new(StringComparer.Ordinal)
    {
        ["CustomerSecurity:AnonymousResponseFloorMilliseconds"] = floor.ToString(CultureInfo.InvariantCulture),

        // The rate limiter and the lockout are other controls; a timing test must not be capped by them.
        ["CustomerSecurity:SignInAttemptsPerWindow"] = "100000",
        ["CustomerSecurity:RegistrationsPerWindow"] = "100000",
        ["CustomerSecurity:LinkRequestsPerWindow"] = "100000",
        ["CustomerSecurity:LockoutMaxFailedAttempts"] = "100000",
    };

    private static void AssertSpread(string endpoint, IReadOnlyDictionary<string, double> medians)
    {
        var slowest = medians.MaxBy(pair => pair.Value);
        var fastest = medians.MinBy(pair => pair.Value);
        var spread = slowest.Value / fastest.Value;

        Assert.True(
            spread <= AllowedSpread,
            $"{endpoint}: '{slowest.Key}' took {slowest.Value:F1} ms and '{fastest.Key}' {fastest.Value:F1} ms "
            + $"({spread:F2}x, allowed {AllowedSpread}x). The time this endpoint takes tells a caller which "
            + "kind of address they sent. All medians: "
            + string.Join(", ", medians.Select(pair => $"{pair.Key}={pair.Value:F1}")));
    }

    private static async Task<Paths> ArrangeAsync(ApiFactory factory, HttpClient client)
    {
        var customer = await RegisterAsync(client, UniqueEmail("timing-customer"));
        var locked = await RegisterAsync(client, UniqueEmail("timing-locked"));
        var deactivated = await RegisterAsync(client, UniqueEmail("timing-deactivated"));
        var staff = UniqueEmail("timing-staff");
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

        return new Paths(
        [
            new Path("sign-in", "wrong password", LoginUrl, () => new { email = customer, password = WrongPassword }),
            new Path("sign-in", "unknown address", LoginUrl, () => new { email = UniqueEmail("timing-nobody"), password = GoodPassword }),
            new Path("sign-in", "staff address", LoginUrl, () => new { email = staff, password = GoodPassword }),
            new Path("sign-in", "deactivated", LoginUrl, () => new { email = deactivated, password = GoodPassword }),
            new Path("sign-in", "locked (right password)", LoginUrl, () => new { email = locked, password = GoodPassword }),
            new Path("register", "free address", RegisterUrl, () => Registration(UniqueEmail("timing-free"))),
            new Path("register", "customer address", RegisterUrl, () => Registration(customer)),
            new Path("register", "staff address", RegisterUrl, () => Registration(staff)),
            new Path("forgot-password", "customer address", ForgotUrl, () => new { email = customer }),
            new Path("forgot-password", "unknown address", ForgotUrl, () => new { email = UniqueEmail("timing-nobody") }),
            new Path("forgot-password", "staff address", ForgotUrl, () => new { email = staff }),
        ]);
    }

    private static object Registration(string email) =>
        new { firstName = "Nimal", lastName = "Perera", email, password = GoodPassword, phone = (string?)null };

    private static async Task<Dictionary<Path, double>> MeasureAsync(HttpClient client, IReadOnlyList<Path> paths)
    {
        // Warm-up: every path once, so none pays for a cold query plan or the first hash in the process.
        foreach (var path in paths)
        {
            await TimeOneAsync(client, path);
        }

        var samples = paths.ToDictionary(path => path, _ => new List<double>(Rounds));

        // Interleaved, so a slow patch on the machine lands on every path rather than on one.
        for (var round = 0; round < Rounds; round++)
        {
            foreach (var path in paths)
            {
                samples[path].Add(await TimeOneAsync(client, path));
            }
        }

        return samples.ToDictionary(pair => pair.Key, pair => Median(pair.Value));
    }

    private static async Task<double> TimeOneAsync(HttpClient client, Path path)
    {
        var stopwatch = Stopwatch.StartNew();

        using var response = await client.PostAsJsonAsync(path.Url, path.Body(), Token);
        _ = await response.Content.ReadAsStringAsync(Token);

        stopwatch.Stop();

        Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static double Median(List<double> samples)
    {
        var ordered = samples.Order().ToList();

        return ordered.Count % 2 == 1
            ? ordered[ordered.Count / 2]
            : (ordered[(ordered.Count / 2) - 1] + ordered[ordered.Count / 2]) / 2;
    }

    private static async Task ReportAsync(string label, Dictionary<Path, double> medians)
    {
        if (Environment.GetEnvironmentVariable("W301_TIMING_OUT") is not { Length: > 0 } file)
        {
            return;
        }

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"## {label}");

        foreach (var (path, median) in medians)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{path.Endpoint,-16} {path.Name,-26} {median,8:F1} ms");
        }

        await File.AppendAllTextAsync(file, text.ToString(), Token);
    }

    private sealed record Path(string Endpoint, string Name, string Url, Func<object> Body);

    private sealed record Paths(IReadOnlyList<Path> All);
}
