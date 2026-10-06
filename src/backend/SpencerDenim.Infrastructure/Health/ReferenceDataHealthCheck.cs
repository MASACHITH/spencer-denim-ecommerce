using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SpencerDenim.Infrastructure.Health;

/// <summary>
/// Whether doc 08 §9's reference data has been established on this host (see
/// <c>CatalogSeedHostedService</c>), for readiness to answer with.
/// </summary>
/// <remarks>
/// <b>This exists because a log line is not a signal.</b> The seeder is deliberately allowed to fail
/// without taking the host down — the API has to keep answering liveness, OpenAPI and the readiness probe
/// that explains the problem, and a SQL Server slower to accept connections than the API must not become a
/// crash loop. The first version of that trade rested on "readiness reports the database", and
/// <b>it did not</b>: the only <c>ready</c>-tagged check was <c>DatabaseHealthCheck</c>, which is
/// <c>CanConnectAsync()</c> and therefore blind to a database that is connectable but has no migrations
/// applied. A host in that state answered <c>/health/ready</c> with <c>200 Healthy</c> while every
/// <c>/api/checkout</c> returned 500, so an orchestrator routed live traffic to a shop that could not take
/// an order, and the only trace was one Error in the log. Found by the W3-02 round-2 tester.
/// <para>
/// It is a <b>reported state, not a probe</b>: it never queries, so a readiness probe every few seconds
/// costs nothing, and it says what the seeder actually experienced rather than re-deriving it. The seeder
/// retries in the background until it succeeds, so this check goes green on its own once whatever was
/// wrong is put right — no restart needed.
/// </para>
/// <para>
/// It is not a data-integrity monitor. If somebody deletes <c>ref.Districts</c> at run time this will
/// still say Healthy, and that is the right scope: readiness answers "did this host come up able to
/// serve", and doc 08 §5.9's reconciliation job is where ongoing data invariants belong.
/// </para>
/// </remarks>
internal sealed class ReferenceDataHealthCheck(ReferenceDataState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (state.IsEstablished)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Reference data is established."));
        }

        return Task.FromResult(
            new HealthCheckResult(
                context.Registration.FailureStatus,
                state.Failure ?? "Reference data has not been established yet."));
    }
}
