using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>Hosts the real API in memory with a given connection string and environment.</summary>
/// <param name="additionalConfiguration">
/// Extra configuration keys (e.g. <c>Seed:Admin:*</c>), applied after the connection string override — a test
/// can use this to exercise <c>AdminSeedHostedService</c> without touching real environment variables.
/// </param>
public sealed class ApiFactory(
    string connectionString,
    string environment = "Testing",
    IReadOnlyDictionary<string, string?>? additionalConfiguration = null) : WebApplicationFactory<Program>
{
    /// <summary>Loopback port 1 refuses connections at once: a SQL Server that is guaranteed to be unreachable.</summary>
    public const string UnreachableConnectionString =
        "Server=127.0.0.1,1;Database=SpencerDenim_Unreachable;User Id=unused;Password=unused;Connect Timeout=2;TrustServerCertificate=True";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);

        // Added last, so it wins over appsettings.Development.json, user-secrets and developer environment variables.
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = connectionString,
                ["Logging:ConsoleFormat"] = "text",
            });

            if (additionalConfiguration is not null)
            {
                configuration.AddInMemoryCollection(additionalConfiguration);
            }
        });
    }
}
