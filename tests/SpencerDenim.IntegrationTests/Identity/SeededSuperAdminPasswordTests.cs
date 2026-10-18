using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity.Seeding;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Identity;

/// <summary>
/// Issue #200, the seeded first Super Admin: <c>Seed__Admin__Password</c> (<c>SD_SEED_ADMIN_PASSWORD</c> in the
/// deployment) has to meet the same minimum as every other password, or no account is made.
/// </summary>
/// <remarks>
/// <b>It runs on a database of its own (<see cref="FreshDeployment"/>), not the suite's shared one</b>, for the
/// reason <c>AdminAuthEndpointTests</c> records at length: the seeder acts only while no Super Admin exists,
/// and on the shared database another test's holder would make "nobody was seeded" true for the wrong
/// reason. Here the three host starts see the same empty deployment, so the only thing that differs between
/// the starts that seed nobody and the one that does is the password.
/// <para>
/// <b>The refusal is not an HTTP status</b> — the seeder runs at host start, where a failure must not stop
/// the API (a deployment that has already got its Super Admin and left the flag on would otherwise fail to
/// boot). It is the host starting, no account being made, and an error in the log that states the rule.
/// All three are asserted.
/// </para>
/// </remarks>
public sealed class SeededSuperAdminPasswordTests(SqlServerDatabaseFixture sqlServer)
{
    /// <summary>
    /// Six characters is the old Identity default; nine is one under the customer minimum; ten is the minimum.
    /// Each carries all four character classes, so length alone decides. Written out rather than read from
    /// the policy so the test stays independent of the constant it guards.
    /// </summary>
    private const string OldDefaultOfSix = "Aa1!xx";
    private const string OneUnderTheMinimum = "Aa1!xxxxx";
    private const string ExactlyTheMinimum = "Aa1!xxxxxx";

    [Fact]
    public async Task A_seed_password_under_the_minimum_creates_no_account_and_the_minimum_does()
    {
        sqlServer.SkipIfUnavailable();

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, cancellationToken);

        var email = $"seed-minimum-{Guid.NewGuid():N}@example.com";

        foreach (var tooShort in new[] { OldDefaultOfSix, OneUnderTheMinimum })
        {
            var errors = await StartHostAsync(deployment, email, tooShort, cancellationToken);

            Assert.Equal(0, await CountAccountsAsync(deployment, email, cancellationToken));

            // The seeder reached the user step: the role it creates first is there. Without this, "no account"
            // could simply mean the seeder never ran.
            Assert.True(
                await SuperAdminRoleExistsAsync(deployment, cancellationToken),
                "The seeder did not run, so this proves nothing about the password.");

            // And the operator is told why, in the rule's own words.
            var logged = Assert.Single(errors, message => message.Contains("Failed to seed the initial Super Admin", StringComparison.Ordinal));
            Assert.Contains(PasswordPolicy.Requirement, logged, StringComparison.Ordinal);
        }

        // The same host, the same empty deployment, a password that is exactly long enough: now it seeds.
        var atMinimum = await StartHostAsync(deployment, email, ExactlyTheMinimum, cancellationToken);

        Assert.DoesNotContain(atMinimum, message => message.Contains("Failed to seed", StringComparison.Ordinal));
        Assert.Equal(1, await CountAccountsAsync(deployment, email, cancellationToken));
    }

    /// <summary>
    /// Starts the real API over <paramref name="deployment"/> with the seed configured, and returns the
    /// error-level log messages the start produced.
    /// </summary>
    private static async Task<IReadOnlyList<string>> StartHostAsync(
        FreshDeployment deployment,
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        var errors = new ErrorSink();

        // Serilog replaces the logging providers and reads its sinks from the container, so a sink is the only
        // way to see the host's own log lines (MalformedRequestBodyTests records the same). An ILoggerProvider
        // here would be accepted, never called, and look like a host that logged nothing.
        await using var factory = new ApiFactory(
            deployment.ConnectionString,
            additionalConfiguration: new Dictionary<string, string?>
            {
                ["Seed:Admin:Enabled"] = "true",
                ["Seed:Admin:Email"] = email,
                ["Seed:Admin:Password"] = password,
            },
            configureServices: services => services.AddSingleton<ILogEventSink>(errors));

        // The host starts, runs every hosted service to completion, and answers: a short seed password must not
        // stop the API from booting.
        using var client = factory.CreateClient();
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), cancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, ready.StatusCode);

        return errors.Messages;
    }

    private static async Task<int> CountAccountsAsync(FreshDeployment deployment, string email, CancellationToken cancellationToken)
    {
        await using var scope = deployment.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var normalised = email.ToUpper(CultureInfo.InvariantCulture);

        return await db.Users.AsNoTracking().CountAsync(user => user.NormalizedEmail == normalised, cancellationToken);
    }

    private static async Task<bool> SuperAdminRoleExistsAsync(FreshDeployment deployment, CancellationToken cancellationToken)
    {
        await using var scope = deployment.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Roles.AsNoTracking().AnyAsync(role => role.Name == AdminSeedHostedService.SuperAdminRoleName, cancellationToken);
    }

    /// <summary>Every message the host logged at Error, rendered.</summary>
    private sealed class ErrorSink : ILogEventSink
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => [.. _messages];

        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Level >= LogEventLevel.Error)
            {
                _messages.Enqueue(logEvent.RenderMessage(CultureInfo.InvariantCulture));
            }
        }
    }
}
