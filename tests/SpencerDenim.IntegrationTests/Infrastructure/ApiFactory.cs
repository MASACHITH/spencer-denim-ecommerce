using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>Hosts the real API in memory with a given connection string and environment.</summary>
/// <param name="additionalConfiguration">
/// Extra configuration keys (e.g. <c>Seed:Admin:*</c>), applied after the connection string override — a test
/// can use this to exercise <c>AdminSeedHostedService</c> without touching real environment variables.
/// </param>
/// <param name="commandInterceptor">
/// Optional EF Core command interceptor, so a test can count the SQL round-trips the API makes.
/// </param>
public sealed class ApiFactory(
    string connectionString,
    string environment = "Testing",
    IReadOnlyDictionary<string, string?>? additionalConfiguration = null,
    DbCommandInterceptor? commandInterceptor = null)
    : WebApplicationFactory<Program>
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
                // The fixture migrates and seeds; the host under test must never do it again.
                ["Seed:RunOnStartup"] = "false",
                ["Seed:IncludeDemoCatalogue"] = "false",
            });

            // Added last so a test can still turn a seeder back on for itself.
            if (additionalConfiguration is not null)
            {
                configuration.AddInMemoryCollection(additionalConfiguration);
            }
        });

        if (commandInterceptor is not null)
        {
            // EF Core resolves IInterceptor registrations from the application service provider, so
            // this reaches the AppDbContext the API actually uses.
            builder.ConfigureTestServices(services => services.AddSingleton<IInterceptor>(commandInterceptor));
        }
    }
}
