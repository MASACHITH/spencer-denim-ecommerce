using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>
/// Runs the catalogue seeder once at startup when <c>Seed__RunOnStartup</c> is set. It does not apply
/// migrations (doc 08 §10.4: the migration bundle is a separate CI step), so a database that is behind
/// fails loudly here rather than being silently upgraded by the web host.
/// </summary>
internal sealed partial class CatalogSeedHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<SeedOptions> options,
    ILogger<CatalogSeedHostedService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seedOptions = options.Value;
        if (!seedOptions.RunOnStartup)
        {
            return;
        }

        LogSeeding(logger, seedOptions.IncludeDemoCatalogue);

        using var scope = scopeFactory.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<CatalogSeedRunner>();
        await runner.RunAsync(seedOptions.IncludeDemoCatalogue, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Seeding the catalogue on startup (demo catalogue: {IncludeDemoCatalogue}).")]
    private static partial void LogSeeding(ILogger logger, bool includeDemoCatalogue);
}
