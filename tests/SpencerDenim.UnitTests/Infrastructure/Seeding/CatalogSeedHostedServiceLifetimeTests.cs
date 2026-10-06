using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpencerDenim.Infrastructure.Health;
using SpencerDenim.Infrastructure.Persistence.Seeding;

namespace SpencerDenim.UnitTests.Infrastructure.Seeding;

/// <summary>
/// <c>StopAsync</c> and <c>Dispose</c> in every order, because nothing orders them.
/// </summary>
/// <remarks>
/// <b>This is the test the CI failure needed and did not have.</b> The service is a singleton, so the
/// service provider disposes it — and <c>WebApplicationFactory.DisposeAsync()</c> disposes the provider
/// and <i>then</i> calls <c>Host.StopAsync</c>. <c>CancelAsync()</c> on an already-disposed
/// <c>CancellationTokenSource</c> throws <c>ObjectDisposedException</c>, so shutdown threw out of
/// <c>StopAsync</c> and took down <c>SizeGuideContentTests</c> — a test whose own body had passed, which
/// is why the victim looked arbitrary: a teardown exception belongs to whichever host happens to be
/// disposing.
/// <para>
/// <b>The natural order already passed</b>, which is exactly why this file exists. Every case below is
/// one the framework is free to produce, and none of them may throw: a container stop that disposes
/// before it stops must not log an exception on a healthy shutdown.
/// </para>
/// <para>
/// No database is needed. The seeder's own work is irrelevant here — it is the lifetime that is under
/// test, and <see cref="EmptyScopeFactory"/> makes the first attempt fail immediately so the retry loop
/// is running by the time these methods are called, which is the state that broke.
/// </para>
/// </remarks>
public sealed class CatalogSeedHostedServiceLifetimeTests
{
    [Fact]
    public async Task Dispose_before_StopAsync_does_not_throw()
    {
        var service = Create();
        await StartAndLeaveItRetryingAsync(service);

        // The order `WebApplicationFactory.DisposeAsync()` produces.
        service.Dispose();

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StopAsync_before_Dispose_does_not_throw()
    {
        var service = Create();
        await StartAndLeaveItRetryingAsync(service);

        await service.StopAsync(TestContext.Current.CancellationToken);

        service.Dispose();
    }

    [Fact]
    public async Task Both_are_safe_to_call_twice_in_either_order()
    {
        var service = Create();
        await StartAndLeaveItRetryingAsync(service);

        service.Dispose();
        service.Dispose();

        await service.StopAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Stopping a service that never started, which is what happens when an earlier hosted service's
    /// <c>StartAsync</c> throws and the host unwinds the ones behind it.
    /// </summary>
    [Fact]
    public async Task Stopping_a_service_that_never_started_does_not_throw()
    {
        var service = Create();

        await service.StopAsync(TestContext.Current.CancellationToken);
        service.Dispose();
    }

    /// <summary>
    /// Starts the service against a provider that cannot seed, so the first attempt fails and the retry
    /// loop is live — the state in which <c>StopAsync</c> and <c>Dispose</c> actually interact.
    /// </summary>
    private static async Task StartAndLeaveItRetryingAsync(CatalogSeedHostedService service)
    {
        await service.StartAsync(TestContext.Current.CancellationToken);
    }

    private static CatalogSeedHostedService Create() =>
        new(
            new EmptyScopeFactory(),
            new OptionsWrapper<SeedOptions>(new SeedOptions()),
            new ReferenceDataState(),
            NullLogger<CatalogSeedHostedService>.Instance);

    /// <summary>
    /// A scope whose provider resolves nothing, so the first seeding attempt fails at
    /// <c>GetRequiredService</c> and lands in the broad catch — no database, no SQL, and the retry loop
    /// started.
    /// </summary>
    private sealed class EmptyScopeFactory : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType) => null;

        public void Dispose()
        {
        }
    }
}
