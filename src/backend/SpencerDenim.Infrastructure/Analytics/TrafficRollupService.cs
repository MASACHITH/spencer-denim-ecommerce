using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Domain.Common;

namespace SpencerDenim.Infrastructure.Analytics;

/// <summary>
/// The traffic half of doc 08 §8.3's <c>NightlyStats</c> job, at 00:30 Colombo, and the start-up catch-up the
/// same section asks for.
/// <para>
/// <b>What one pass does</b> (<see cref="RunOnceAsync"/>): rebuild the last three completed Colombo days, then
/// build any older day within raw retention that has page views and was never rolled up — the host was down at
/// 00:30, or for longer than three days. Today is never stored: the Traffic page reads it live.
/// </para>
/// <para>
/// <b>No <c>ops.JobLeases</c>.</b> Doc 08 §3.8 specifies it and it does not exist, so "last completion" cannot be
/// read. The job instead runs once shortly after start-up and then nightly; a rebuild is idempotent and takes an
/// application lock, so running it more often than needed, or on two hosts, costs time and never a wrong row.
/// </para>
/// <para>
/// <b>Not doc 08 §8.3's <c>ProductStats</c> half</b> (30-day units, drift checks), which is not part of traffic
/// and is not built here.
/// </para>
/// <para>
/// <b>Off in the test hosts</b> (<c>Traffic:JobsEnabled=false</c> in <c>ApiFactory</c>): a job that writes the
/// shared test database in the background is exactly what broke <c>OrderExpiryTests</c> once. The tests drive
/// <see cref="RunOnceAsync"/> and <see cref="TrafficRollup"/> directly instead.
/// </para>
/// </summary>
internal sealed partial class TrafficRollupService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    TrafficOptions options,
    ILogger<TrafficRollupService> logger) : BackgroundService
{
    /// <summary>Doc 08 §8.3: "rebuild … for the last 3 Colombo days".</summary>
    public const int RebuildDays = 3;

    /// <summary>Doc 08 §8.3: "NightlyStats job (00:30 Colombo)".</summary>
    public static readonly TimeOnly RunsAt = new(0, 30);

    /// <summary>How long after start-up the catch-up runs: after the host is listening, not before.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    private int _consecutiveFailures;

    /// <summary>One pass, as of <paramref name="now"/>. Returns the days rebuilt.</summary>
    public async Task<IReadOnlyList<DateOnly>> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var rollup = scope.ServiceProvider.GetRequiredService<TrafficRollup>();

        var today = ReportPeriod.ColomboDayOf(now);
        var firstRecent = today.AddDays(-RebuildDays);
        var rebuilt = new List<DateOnly>();

        await rollup.RebuildAsync(firstRecent, today.AddDays(-1), cancellationToken);
        for (var day = firstRecent; day < today; day = day.AddDays(1))
        {
            rebuilt.Add(day);
        }

        var rawDays = options.RawRetentionDays;

        foreach (var day in await rollup.FindUnbuiltDaysAsync(today.AddDays(-rawDays), firstRecent, cancellationToken))
        {
            await rollup.RebuildAsync(day, day, cancellationToken);
            rebuilt.Add(day);
        }

        return rebuilt.Order().ToList();
    }

    /// <summary>The next 00:30 Colombo strictly after <paramref name="now"/>.</summary>
    public static DateTimeOffset NextRun(DateTimeOffset now)
    {
        var local = ColomboTime.ToLocal(now);
        var candidate = new DateTimeOffset(local.Date + RunsAt.ToTimeSpan(), ColomboTime.Offset);

        return candidate > local ? candidate : candidate.AddDays(1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.JobsEnabled)
        {
            return;
        }

        if (!await DelayAsync(StartupDelay, stoppingToken))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await PassAsync(stoppingToken);

            var now = clock.UtcNow;
            if (!await DelayAsync(NextRun(now) - now, stoppingToken))
            {
                return;
            }
        }
    }

    private async Task PassAsync(CancellationToken stoppingToken)
    {
        try
        {
            var rebuilt = await RunOnceAsync(clock.UtcNow, stoppingToken);
            LogRolledUp(rebuilt.Count);
            _consecutiveFailures = 0;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _consecutiveFailures++;

            if (_consecutiveFailures == 1)
            {
                LogFailed(exception);
            }
            else
            {
                LogStillFailing(exception, _consecutiveFailures);
            }
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(EventId = 7121, Level = LogLevel.Information, Message = "Traffic rolled up for {Days} Colombo day(s).")]
    private partial void LogRolledUp(int days);

    [LoggerMessage(EventId = 7122, Level = LogLevel.Error, Message = "The traffic rollup failed; the Traffic page reads recent days live meanwhile.")]
    private partial void LogFailed(Exception exception);

    [LoggerMessage(EventId = 7123, Level = LogLevel.Warning, Message = "The traffic rollup has failed {Attempts} times in a row.")]
    private partial void LogStillFailing(Exception exception, int attempts);
}
