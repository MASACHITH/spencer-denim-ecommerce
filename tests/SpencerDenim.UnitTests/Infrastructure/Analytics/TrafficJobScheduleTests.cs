using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Infrastructure.Analytics;

namespace SpencerDenim.UnitTests.Infrastructure.Analytics;

/// <summary>
/// The two traffic jobs' schedules (doc 08 §8.3 00:30, §8.4 01:30 Colombo) and the switch that keeps them out of
/// every test host — a background writer in the shared test database is what once broke <c>OrderExpiryTests</c>.
/// </summary>
public sealed class TrafficJobScheduleTests
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 15, 4, 0, 0, TimeSpan.Zero);
    }

    private static readonly IServiceScopeFactory NoScopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    [Fact]
    public async Task With_jobs_off_neither_job_does_anything_at_all()
    {
        var options = new TrafficOptions { JobsEnabled = false };
        using var rollup = new TrafficRollupService(NoScopes, new FixedClock(), options, NullLogger<TrafficRollupService>.Instance);
        using var retention = new TrafficRetentionService(NoScopes, new FixedClock(), options, NullLogger<TrafficRetentionService>.Instance);

        await rollup.StartAsync(TestContext.Current.CancellationToken);
        await retention.StartAsync(TestContext.Current.CancellationToken);

        // Finished within moments (ExecuteAsync runs on its own task in .NET 10): no start-up delay was even begun,
        // so nothing can run later in the host's life.
        await rollup.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await retention.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(rollup.ExecuteTask!.IsCompletedSuccessfully);
        Assert.True(retention.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task With_jobs_on_they_wait_for_their_start_up_delay_rather_than_running_at_once()
    {
        var options = new TrafficOptions { JobsEnabled = true };
        using var rollup = new TrafficRollupService(NoScopes, new FixedClock(), options, NullLogger<TrafficRollupService>.Instance);

        await rollup.StartAsync(TestContext.Current.CancellationToken);

        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.False(rollup.ExecuteTask!.IsCompleted);
        await rollup.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("2026-10-15T19:59:00+00:00", "2026-10-15T20:00:00+00:00")]
    [InlineData("2026-10-15T20:00:00+00:00", "2026-10-16T20:00:00+00:00")]
    [InlineData("2026-10-16T04:00:00+00:00", "2026-10-16T20:00:00+00:00")]
    public void The_purge_runs_at_half_past_one_Colombo_after_the_rollup(string now, string next)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal(DateTimeOffset.Parse(next, culture), TrafficRetentionService.NextRun(DateTimeOffset.Parse(now, culture)));
        Assert.True(TrafficRetentionService.RunsAt > TrafficRollupService.RunsAt);
    }
}
