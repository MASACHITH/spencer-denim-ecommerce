using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Ops.Email.Abstractions;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>Hosts the real API in memory with a given connection string and environment.</summary>
/// <param name="additionalConfiguration">
/// Extra configuration keys (e.g. <c>Seed:Admin:*</c>), applied after the connection string override — a test
/// can use this to exercise <c>AdminSeedHostedService</c> without touching real environment variables.
/// </param>
/// <param name="commandInterceptor">
/// Optional EF Core command interceptor, so a test can count the SQL round-trips the API makes.
/// </param>
/// <param name="configureServices">
/// Optional service overrides, applied after the application's own registrations — how a media test
/// substitutes a storage adapter that fails part way through, to force the rollback path.
/// </param>
public sealed class ApiFactory(
    string connectionString,
    string environment = "Testing",
    IReadOnlyDictionary<string, string?>? additionalConfiguration = null,
    DbCommandInterceptor? commandInterceptor = null,
    Action<IServiceCollection>? configureServices = null)
    : WebApplicationFactory<Program>
{
    /// <summary>Loopback port 1 refuses connections at once: a SQL Server that is guaranteed to be unreachable.</summary>
    public const string UnreachableConnectionString =
        "Server=127.0.0.1,1;Database=SpencerDenim_Unreachable;User Id=unused;Password=unused;Connect Timeout=2;TrustServerCertificate=True";

    /// <summary>
    /// The transport every test host gets: unable to send. Whether there is an account at all is decided by the
    /// account reader (the blanked configuration below plus the seeded, empty <c>Email.Smtp.Host</c> row), not by
    /// this class.
    /// </summary>
    private sealed class NoMailTransport : IEmailTransport
    {
        public Task SendAsync(OutboundEmail email, SmtpSettings account, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "A test asked the API host to send an email. No test may reach a mail server or a real "
                + "address: drive IEmailOutboxStore directly with a fake transport instead.");
    }

    /// <summary>
    /// This host's own Data Protection key ring. Production refuses to start without one (issue #96), and the API
    /// reads that setting while it registers its services, which is before the in-memory configuration below
    /// is applied, so it has to be a host setting. A directory per host keeps hosts from sharing a key ring.
    /// Created lazily by Data Protection, only if something is protected; removed with the host.
    /// </summary>
    private readonly string _dataProtectionKeys = Path.Combine(Path.GetTempPath(), "sd-it-dp-" + Guid.NewGuid().ToString("N"));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            try
            {
                Directory.Delete(_dataProtectionKeys, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a temp directory that is still locked or was never created is not a test result.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);

        // A default, not a rule: a test that is about the key ring sets its own (or blanks it) afterwards.
        builder.UseSetting("DataProtection:KeysPath", _dataProtectionKeys);

        // Added last, so it wins over appsettings.Development.json, user-secrets and developer environment variables.
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = connectionString,
                ["Logging:ConsoleFormat"] = "text",
                // The fixture migrates and seeds; the host under test must never do it again.
                ["Seed:RunOnStartup"] = "false",
                ["Seed:IncludeDemoCatalogue"] = "false",

                // The nightly traffic rollup and the raw-traffic purge (W4-03) write the shared database in the
                // background; a background writer is what once broke OrderExpiryTests depending on run order. Off
                // in every test host; the traffic tests run one pass directly, as of a day they choose.
                ["Traffic:JobsEnabled"] = "false",

                // The order-placement limit (issue #107) is per visitor address, and a test host has exactly
                // one: TestServer reports no address at all, so every shopper in every test lands in the
                // shared bucket. The checkout suites place hundreds of orders from it (the concurrency
                // rounds alone place several hundred), which no real address does, so the limit is lifted
                // out of their way. It is lifted, not removed: `CheckoutPlacementThrottleTests` sets it
                // back to a few permits through `additionalConfiguration`, which is applied after this, and
                // `OrderPlacementThrottleTests` pins the shipped defaults.
                ["OrderPlacementThrottle:PermitsPerWindow"] = "1000000",
                ["OrderPlacementThrottle:SharedAddressPermitsPerWindow"] = "1000000",

                // **No test may reach a real mail server or send to a real address** (W3-06). These two
                // keys are blanked rather than left out, because leaving them out is not the same thing:
                // the in-memory provider only wins for keys it holds, and a developer with
                // `Email__Smtp__Host` set in their own environment would otherwise give every test host a
                // working transport and the `SendEmails` job something to do with the queue. Blank is also
                // the shop's real state today — the client's credentials are outstanding — so the suite
                // exercises the unconfigured path by default, which is the one in production.
                //
                // `ConfigureTestServices` below replaces the transport as well, so this is the first of
                // two independent reasons nothing can be sent.
                ["Email:Smtp:Host"] = string.Empty,
                ["Email:Smtp:FromAddress"] = string.Empty,
            });

            // Added last so a test can still turn a seeder back on for itself.
            if (additionalConfiguration is not null)
            {
                configuration.AddInMemoryCollection(additionalConfiguration);
            }
        });

        // The second reason (see the blanked keys above): a transport with no socket in it. With no account
        // configured `SendEmails` leaves the ordinary queue alone exactly as it does in production, and this
        // throws if anything ever calls it — so a future test that accidentally configured a host (in
        // configuration or in the Email.Smtp rows) would fail loudly here rather than posting a message to
        // somebody's inbox. Tests that save an account run in the OutboxWipingTests collection and clear it.
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IEmailTransport, NoMailTransport>());

        if (configureServices is not null)
        {
            builder.ConfigureTestServices(configureServices);
        }

        if (commandInterceptor is not null)
        {
            // EF Core resolves IInterceptor registrations from the application service provider, so
            // this reaches the AppDbContext the API actually uses.
            builder.ConfigureTestServices(services => services.AddSingleton<IInterceptor>(commandInterceptor));
        }
    }
}
