using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Domain.Common;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Analytics;

/// <summary>
/// Doc 08 §8.4's purge of raw traffic: page views are kept <see cref="TrafficOptions.RawRetentionDays"/> Colombo
/// days (90), bot page views <see cref="TrafficOptions.BotRetentionDays"/> (7). The daily tables are kept
/// indefinitely (doc 08 §8.4: "tens of MB/year"), which is what lets the Traffic page show a year after the raw
/// rows are gone.
/// <para>
/// <b>Doc 08 §8.4's shape:</b> "<c>DELETE TOP (4000)</c> loops (below the lock-escalation threshold), time-boxed to
/// 10 min". Each delete is its own statement and transaction, so the table is never locked for longer than one
/// batch takes, and the oldest rows go first through <c>IX_PageViews_Occurred</c>.
/// </para>
/// <para>
/// <b>Whole Colombo days.</b> The cut-off is a Colombo midnight, so "90 days" keeps today and the 89 before it
/// whole, and a day the nightly rollup may still rebuild (the last three) is never purged: both periods must be at
/// least four days, which <c>TrafficOptions</c>' validation enforces.
/// </para>
/// </summary>
internal sealed class TrafficRetention(AppDbContext dbContext)
{
    /// <summary>Doc 08 §8.4: below SQL Server's lock-escalation threshold of 5,000.</summary>
    public const int BatchSize = 4000;

    /// <summary>Doc 08 §8.4: "time-boxed to 10 min".</summary>
    public static readonly TimeSpan TimeBox = TimeSpan.FromMinutes(10);

    /// <summary>Deletes what is past retention as of <paramref name="today"/>; answers how many human and bot rows went.</summary>
    public async Task<(int Human, int Bot)> PurgeAsync(DateOnly today, int rawDays, int botDays, CancellationToken cancellationToken)
    {
        var rawCutoff = ReportPeriod.ColomboMidnightUtc(today.AddDays(-(rawDays - 1)));
        var botCutoff = ReportPeriod.ColomboMidnightUtc(today.AddDays(-(botDays - 1)));
        var clock = Stopwatch.StartNew();

        var bot = await DeleteInBatchesAsync(
            "DELETE TOP (4000) FROM [analytics].[PageViews] WHERE [IsBot] = 1 AND [OccurredAtUtc] < @cutoff;",
            botCutoff,
            clock,
            cancellationToken);

        var human = await DeleteInBatchesAsync(
            "DELETE TOP (4000) FROM [analytics].[PageViews] WHERE [OccurredAtUtc] < @cutoff;",
            rawCutoff,
            clock,
            cancellationToken);

        return (human, bot);
    }

    private async Task<int> DeleteInBatchesAsync(string sql, DateTime cutoff, Stopwatch clock, CancellationToken cancellationToken)
    {
        var total = 0;

        while (clock.Elapsed < TimeBox)
        {
            var deleted = await dbContext.Database.ExecuteSqlRawAsync(sql, [TrafficRollup.Utc("@cutoff", cutoff)], cancellationToken);
            total += deleted;

            if (deleted < BatchSize)
            {
                break;
            }
        }

        return total;
    }
}

/// <summary>
/// Runs <see cref="TrafficRetention"/> at 01:30 Colombo (doc 08 §8.4: "run nightly at 01:30 Colombo"), after the
/// 00:30 rollup has counted the days being purged, and once shortly after start-up.
/// <para>
/// <b>Only traffic.</b> Doc 08 §3.8's <c>Retention</c> job also purges audit logs, outbox bodies, carts, IP
/// columns, media and alerts; none of that is here and all of it is still owed.
/// </para>
/// </summary>
internal sealed partial class TrafficRetentionService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    TrafficOptions options,
    ILogger<TrafficRetentionService> logger) : BackgroundService
{
    public static readonly TimeOnly RunsAt = new(1, 30);

    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    /// <summary>The next 01:30 Colombo strictly after <paramref name="now"/>.</summary>
    public static DateTimeOffset NextRun(DateTimeOffset now)
    {
        var local = ColomboTime.ToLocal(now);
        var candidate = new DateTimeOffset(local.Date + RunsAt.ToTimeSpan(), ColomboTime.Offset);

        return candidate > local ? candidate : candidate.AddDays(1);
    }

    public async Task<(int Human, int Bot)> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<TrafficRetention>()
            .PurgeAsync(ReportPeriod.ColomboDayOf(now), options.RawRetentionDays, options.BotRetentionDays, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.JobsEnabled)
        {
            return;
        }

        var delay = StartupDelay;

        while (true)
        {
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var (human, bot) = await RunOnceAsync(clock.UtcNow, stoppingToken);
                LogPurged(human, bot, options.RawRetentionDays, options.BotRetentionDays);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogFailed(exception);
            }

            var now = clock.UtcNow;
            delay = NextRun(now) - now;
        }
    }

    [LoggerMessage(
        EventId = 7131,
        Level = LogLevel.Information,
        Message = "Purged {Human} page view(s) older than {RawDays} days and {Bot} bot page view(s) older than {BotDays} days.")]
    private partial void LogPurged(int human, int bot, int rawDays, int botDays);

    [LoggerMessage(EventId = 7132, Level = LogLevel.Error, Message = "The traffic retention purge failed; it will try again tomorrow.")]
    private partial void LogFailed(Exception exception);
}
