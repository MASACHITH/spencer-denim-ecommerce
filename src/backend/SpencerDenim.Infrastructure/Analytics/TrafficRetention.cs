using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Domain.Analytics;
using SpencerDenim.Domain.Common;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Analytics;

/// <summary>
/// Doc 08 §8.4's purge of raw traffic: page views are kept <c>Traffic.RawRetentionDays</c> Colombo days (90), bot
/// page views <c>Traffic.BotRetentionDays</c> (7) — the settings rows, falling back to
/// <see cref="TrafficOptions"/> (issue #193; see <see cref="TrafficRetentionSettings"/>). The daily tables are kept
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
/// least four days, which <c>TrafficOptions</c>' validation enforces for configuration and the settings form's
/// bounds (30 and 7) enforce, more strictly, for the rows.
/// </para>
/// </summary>
/// <param name="Human">Page views deleted (human and bot rows past the raw period).</param>
/// <param name="Bot">Bot page views deleted by the shorter bot period.</param>
/// <param name="SkippedDays">
/// Colombo days past retention whose raw rows were kept because the rollup has never built them — see
/// <see cref="TrafficRetention.PurgeAsync"/>. Empty in a healthy shop.
/// </param>
internal sealed record TrafficPurgeResult(int Human, int Bot, IReadOnlyList<DateOnly> SkippedDays)
{
    /// <summary>The two counts, which is what most callers want.</summary>
    public void Deconstruct(out int human, out int bot)
    {
        human = Human;
        bot = Bot;
    }
}

internal sealed partial class TrafficRetention(AppDbContext dbContext, ILogger<TrafficRetention> logger)
{
    /// <summary>Doc 08 §8.4: below SQL Server's lock-escalation threshold of 5,000.</summary>
    public const int BatchSize = 4000;

    /// <summary>Doc 08 §8.4: "time-boxed to 10 min".</summary>
    public static readonly TimeSpan TimeBox = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Days past retention that still have raw rows and no <c>TrafficDaily</c> row — the rollup never built them.
    /// <para>
    /// One range scan of <c>IX_PageViews_Occurred</c> over the rows the purge is about to delete anyway, with an
    /// anti-semi-join to <c>TrafficDaily</c>'s clustered key (whose leading column is <c>Date</c>). It is
    /// <c>TrafficSql.UnbuiltDays</c>' own definition, bounded below by nothing because everything older than the
    /// cut-off is a candidate.
    /// </para>
    /// </summary>
    internal const string UnbuiltDaysBefore = """
        SELECT DISTINCT d.[Date] AS [Value]
        FROM (
            SELECT CONVERT(date, DATEADD(minute, 330, pv.[OccurredAtUtc])) AS [Date]
            FROM [analytics].[PageViews] AS pv
            WHERE pv.[OccurredAtUtc] < @cutoff
        ) AS d
        WHERE NOT EXISTS (SELECT 1 FROM [analytics].[TrafficDaily] AS t WHERE t.[Date] = d.[Date])
        """;

    /// <summary>
    /// Deletes what is past retention as of <paramref name="today"/>, <b>except on days the rollup has never
    /// built</b>, and answers how many human and bot rows went and which days were kept.
    /// <para>
    /// <b>Why the guard (#193).</b> The raw rows are the only source a missing day can be rebuilt from. Shortening the
    /// raw period (365 → 30, say) after the rollup has silently failed for a while would otherwise delete days that
    /// were never counted — a permanent gap in the Traffic page's history. So a Colombo day past retention with no
    /// <c>TrafficDaily</c> row keeps its raw rows and a warning names it. The 00:30 rollup's catch-up looks for
    /// never-built days with raw rows <b>with no lower bound</b> (<c>TrafficRollupService</c>), so it builds the kept
    /// day on its next run (oldest first, up to a month of days a night) and the 01:30 purge then deletes it.
    /// </para>
    /// <para>
    /// <b>It cannot block for ever on an empty day.</b> The rollup writes at least one <c>TrafficDaily</c> row for
    /// every Colombo day that has any page view — human rows, or a bot-only row with <c>BotPageViews</c> — so "has
    /// raw rows and no daily row" means exactly "never built". A day with no visits has no raw rows to delete and is
    /// never reported. A day that stays unbuilt is one the rollup keeps failing on, and the daily warning says so.
    /// </para>
    /// <para>
    /// <b>Set-based, on the existing index.</b> The unbuilt days are found once (above); the deletes then run over
    /// contiguous UTC <i>ranges</i> between those days, so each batch is a range seek on
    /// <c>IX_PageViews_Occurred</c> with no per-row lookup and never re-reads the kept rows. With no unbuilt day —
    /// the normal case — there is one range per period, exactly the delete this method ran before the guard.
    /// </para>
    /// </summary>
    public async Task<TrafficPurgeResult> PurgeAsync(DateOnly today, int rawDays, int botDays, CancellationToken cancellationToken)
    {
        var rawCutoff = ReportPeriod.ColomboMidnightUtc(today.AddDays(-(rawDays - 1)));
        var botCutoff = ReportPeriod.ColomboMidnightUtc(today.AddDays(-(botDays - 1)));
        var clock = Stopwatch.StartNew();

        // The later of the two cut-offs covers every row either delete could reach.
        var widest = rawCutoff > botCutoff ? rawCutoff : botCutoff;

        var unbuilt = (await dbContext.Database
                .SqlQueryRaw<DateOnly>(UnbuiltDaysBefore, TrafficRollup.Utc("@cutoff", widest))
                .ToListAsync(cancellationToken))
            .Order()
            .ToList();

        var bot = 0;
        foreach (var (from, to) in Ranges(botCutoff, unbuilt))
        {
            bot += await DeleteInBatchesAsync(
                "DELETE TOP (4000) FROM [analytics].[PageViews] WHERE [IsBot] = 1 AND [OccurredAtUtc] >= @from AND [OccurredAtUtc] < @to;",
                from,
                to,
                clock,
                cancellationToken);
        }

        var human = 0;
        foreach (var (from, to) in Ranges(rawCutoff, unbuilt))
        {
            human += await DeleteInBatchesAsync(
                "DELETE TOP (4000) FROM [analytics].[PageViews] WHERE [OccurredAtUtc] >= @from AND [OccurredAtUtc] < @to;",
                from,
                to,
                clock,
                cancellationToken);
        }

        if (unbuilt.Count > 0)
        {
            LogKeptUnbuiltDays(unbuilt.Count, string.Join(", ", unbuilt.Select(day => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))));
        }

        return new TrafficPurgeResult(human, bot, unbuilt);
    }

    /// <summary>
    /// The UTC ranges before <paramref name="cutoff"/> that are safe to delete: everything before it, minus each
    /// unbuilt Colombo day (whole days, Colombo midnight to Colombo midnight). Pure, so its edges are unit-tested.
    /// </summary>
    internal static IReadOnlyList<(DateTime From, DateTime To)> Ranges(DateTime cutoff, IReadOnlyList<DateOnly> unbuiltDays)
    {
        var ranges = new List<(DateTime From, DateTime To)>();
        var start = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

        foreach (var day in unbuiltDays.Distinct().Order())
        {
            var dayStart = ReportPeriod.ColomboMidnightUtc(day);
            if (dayStart >= cutoff)
            {
                break;
            }

            if (dayStart > start)
            {
                ranges.Add((start, dayStart));
            }

            var dayEnd = ReportPeriod.ColomboMidnightUtc(day.AddDays(1));
            if (dayEnd > start)
            {
                start = dayEnd;
            }
        }

        if (start < cutoff)
        {
            ranges.Add((start, cutoff));
        }

        return ranges;
    }

    private async Task<int> DeleteInBatchesAsync(string sql, DateTime from, DateTime to, Stopwatch clock, CancellationToken cancellationToken)
    {
        var total = 0;

        while (clock.Elapsed < TimeBox)
        {
            var deleted = await dbContext.Database.ExecuteSqlRawAsync(
                sql,
                [TrafficRollup.Utc("@from", from), TrafficRollup.Utc("@to", to)],
                cancellationToken);
            total += deleted;

            if (deleted < BatchSize)
            {
                break;
            }
        }

        return total;
    }

    [LoggerMessage(
        EventId = 7134,
        Level = LogLevel.Warning,
        Message = "The traffic purge kept the raw page views of {Count} day(s) past retention because the nightly rollup "
            + "has not built them yet: {Days}. The 00:30 rollup builds such days (oldest first, up to 31 a night) and "
            + "the next purge deletes them; if the same day is reported night after night, the rollup is failing.")]
    private partial void LogKeptUnbuiltDays(int count, string days);
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

    /// <summary>
    /// One purge as of <paramref name="now"/>, with the periods read from the settings rows at the start of the run
    /// (configuration as the fallback), and those periods returned so the log line says what was applied.
    /// </summary>
    public async Task<(int Human, int Bot, TrafficRetentionPeriods Periods)> RunOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        var periods = await scope.ServiceProvider.GetRequiredService<TrafficRetentionSettings>().ReadAsync(cancellationToken);

        // Days the rollup never built are kept and logged by the purge itself (event 7134).
        var (human, bot) = await scope.ServiceProvider.GetRequiredService<TrafficRetention>()
            .PurgeAsync(ReportPeriod.ColomboDayOf(now), periods.RawDays, periods.BotDays, cancellationToken);

        return (human, bot, periods);
    }

    /// <summary>One purge of <c>analytics.SearchQueriesDaily</c> as of <paramref name="now"/>: rows older than 13 months.</summary>
    public async Task<int> RunSearchPurgeOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<SearchQueryRetention>()
            .PurgeAsync(ReportPeriod.ColomboDayOf(now), cancellationToken);
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
                var (human, bot, periods) = await RunOnceAsync(clock.UtcNow, stoppingToken);
                LogPurged(human, bot, periods.RawDays, periods.RawSource, periods.BotDays, periods.BotSource);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogFailed(exception);
            }

            // Doc 08 §8.4's 13 months of search counts (#78), in the same nightly window and on its own: a failure in
            // one purge must not skip the other.
            try
            {
                var searches = await RunSearchPurgeOnceAsync(clock.UtcNow, stoppingToken);
                LogSearchesPurged(searches, SearchQueryRetention.RetentionMonths);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogSearchPurgeFailed(exception);
            }

            var now = clock.UtcNow;
            delay = NextRun(now) - now;
        }
    }

    [LoggerMessage(
        EventId = 7131,
        Level = LogLevel.Information,
        Message = "Purged {Human} page view(s) older than {RawDays} days (from {RawSource}) and {Bot} bot page view(s) older "
            + "than {BotDays} days (from {BotSource}).")]
    private partial void LogPurged(
        int human,
        int bot,
        int rawDays,
        TrafficRetentionSource rawSource,
        int botDays,
        TrafficRetentionSource botSource);

    [LoggerMessage(EventId = 7132, Level = LogLevel.Error, Message = "The traffic retention purge failed; it will try again tomorrow.")]
    private partial void LogFailed(Exception exception);

    [LoggerMessage(
        EventId = 7156,
        Level = LogLevel.Information,
        Message = "Purged {Rows} search-count row(s) older than {Months} months.")]
    private partial void LogSearchesPurged(int rows, int months);

    [LoggerMessage(EventId = 7157, Level = LogLevel.Error, Message = "The search-count retention purge failed; it will try again tomorrow.")]
    private partial void LogSearchPurgeFailed(Exception exception);
}
