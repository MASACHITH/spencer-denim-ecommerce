using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>
/// Establishes doc 08 §9's reference data at startup, and seeds the demo catalogue when
/// <c>Seed__RunOnStartup</c> asks for it. It does not apply migrations (doc 08 §10.4: the migration bundle
/// is a separate CI step), so a database that is behind is reported here rather than being silently
/// upgraded by the web host.
/// </summary>
/// <remarks>
/// <b>The reference data is not behind a flag, and that is the fix for a shop that could not take an
/// order.</b> It used to be: both phases sat behind <c>Seed__RunOnStartup</c>, which defaults to
/// <c>false</c> and which nothing in this repository sets. A fresh deploy on the documented defaults —
/// create the database with §1.1's collation, <c>dotnet ef database update</c>, start the API — came up
/// with no districts, no delivery methods, no zone rates and no payment providers, which is a checkout
/// that refuses every order and an <c>Orders.DeliveryDistrictId NOT NULL</c> that cannot be satisfied.
/// <see cref="CatalogSeedRunner"/> carries the reasoning; the short version is that a district is not
/// production data and the flag means "production data belongs to the client".
/// <para>
/// <b>Why it may not fail the host.</b> The API is required to start without a database: its liveness
/// endpoint, its OpenAPI document and its readiness check all answer, and the readiness check is the thing
/// that reports a database that is unreachable or behind — loudly, in the place an operator and a
/// container orchestrator both already look. A seeder that threw out of <c>StartAsync</c> would instead
/// take down the endpoints that explain the problem, and would turn a SQL Server that is thirty seconds
/// slower to accept connections than the API into a crash loop. So the failure is logged at
/// <see cref="LogLevel.Error"/>, naming what is missing and the command that fixes it, and the host starts.
/// </para>
/// <para>
/// <b>Why it is not left to deployment instead.</b> It could be: a documented "run the seeder after
/// migrating" step, or an init container. Two things decided against it. There is no compose file and no
/// deploy script in the repository for such a step to live in, so the instruction would exist only in
/// prose — and the defect being fixed here <i>is</i> an instruction that existed only in prose and was
/// not followed. And the data is not optional in the way a deployment step implies; the application cannot
/// serve its purpose without it, which makes establishing it the application's business. If the owner
/// prefers it at deployment level, the phase to call is
/// <see cref="CatalogSeedRunner.RunReferenceDataAsync"/> and nothing else here changes.
/// </para>
/// </remarks>
internal sealed partial class CatalogSeedHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<SeedOptions> options,
    ILogger<CatalogSeedHostedService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seedOptions = options.Value;

        // The demo catalogue needs both flags: `RunOnStartup` keeps its meaning as "this host may seed
        // optional data", so an existing deployment's variables do exactly what they did before.
        var includeDemoCatalogue = seedOptions.RunOnStartup && seedOptions.IncludeDemoCatalogue;

        using var scope = scopeFactory.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<CatalogSeedRunner>();

        try
        {
            var counts = await runner.RunReferenceDataAsync(cancellationToken);

            if (includeDemoCatalogue)
            {
                LogSeedingDemoCatalogue(logger);
                await runner.RunDemoCatalogueAsync(counts, cancellationToken);
            }

            runner.LogCounts(counts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host is shutting down while starting. Not a seeding failure.
            throw;
        }
        catch (Exception exception)
        {
            // Deliberately broad: anything that stops the reference data landing — an unreachable server,
            // a database behind on migrations, a permission — has the same consequence and the same
            // remedy, and none of them may stop the host explaining itself. See the class remarks.
            LogSeedingFailed(logger, exception);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Seeding the demo catalogue on startup (Seed__IncludeDemoCatalogue).")]
    private static partial void LogSeedingDemoCatalogue(ILogger logger);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Error,
        Message = "Could not establish the reference data at startup. Until this succeeds the shop has no "
            + "districts, delivery methods, zone rates or payment providers and cannot take an order. "
            + "Check the database connection and run 'dotnet ef database update'.")]
    private static partial void LogSeedingFailed(ILogger logger, Exception exception);
}
