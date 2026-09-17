using Microsoft.Extensions.Diagnostics.HealthChecks;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Health;

/// <summary>Readiness check: opens a connection to the configured SQL Server database.</summary>
internal sealed class DatabaseHealthCheck(AppDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);
            return canConnect
                ? HealthCheckResult.Healthy("SQL Server database is reachable.")
                : new HealthCheckResult(context.Registration.FailureStatus, "SQL Server database is not reachable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "SQL Server connectivity check timed out.");
        }
    }
}
