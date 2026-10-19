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
/// build the oldest never-built days that still have raw page views — the host was down at 00:30, or the rollup
/// failed — up to <see cref="MaxCatchUpDaysPerRun"/> a night, <b>however old they are</b>. Today is never stored:
/// the Traffic page reads it live.
/// <para>
/// <b>Why no lower bound (#193).</b> The 01:30 purge never deletes a day this job has not built, so such a day keeps
/// its raw rows past retention and this catch-up is the only thing that can count it; bounding the search by the raw
/// period would leave it kept and warned about for ever. A day the purge has emptied has no raw rows and is never
/// found, so the catch-up cannot store a zero over a real day.
/// </para>
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

    /// <summary>
    /// The most never-built days one pass builds, oldest first. A month: after a long outage the backlog clears over
    /// a few nights (the purge keeps those days meanwhile) instead of one pass holding the rollup lock for hours.
    /// </summary>
    public const int MaxCatchUpDaysPerRun = 31;

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

        // Every never-built day that still has raw rows, wherever it is: the purge keeps such days until this has
        // counted them (#193). Only days with raw rows are found, oldest first, capped per night.
        foreach (var day in await rollup.FindOldestUnbuiltDaysAsync(firstRecent, MaxCatchUpDaysPerRun, cancellationToken))
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
