using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SpencerDenim.Domain.Analytics;
using SpencerDenim.Infrastructure.Analytics;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using static SpencerDenim.IntegrationTests.Analytics.TrafficRows;

namespace SpencerDenim.IntegrationTests.Analytics;

/// <summary>
/// Doc 08 §8.4's raw-traffic retention: 90 Colombo days for page views, 7 for bot page views, purged in batches
/// of 4,000, the daily tables untouched. "Today" is 31 December 2000 here, so nothing a later year's test wrote can
/// be in range — a purge as of a real today would delete every other traffic test's rows.
/// </summary>
public sealed class TrafficRetentionTests(SqlServerDatabaseFixture sqlServer)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateOnly Today = new(2000, 12, 31);

    [Fact]
    public async Task Views_past_ninety_days_and_bots_past_seven_go_and_whole_days_are_kept()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);
        await ClearYearAsync(sqlServer, 2000, Token);

        await InsertAsync(
            sqlServer,
            [
                View(Colombo(2000, 10, 2, 23, 59, 59), Guid.NewGuid(), "/human-gone"),   // day 91: gone
                View(Colombo(2000, 10, 3, 0, 0, 0), Guid.NewGuid(), "/human-kept"),      // day 90: kept
                View(Colombo(2000, 12, 24, 23, 59, 59), Guid.NewGuid(), "/bot-gone", bot: true), // bot day 8: gone
                View(Colombo(2000, 12, 25, 0, 0, 0), Guid.NewGuid(), "/bot-kept", bot: true),    // bot day 7: kept
                View(Colombo(2000, 12, 25, 0, 0, 0), Guid.NewGuid(), "/human-recent"),
            ],
            Token);

        using (var seed = sqlServer.CreateScope())
        {
            await seed.ServiceProvider.GetRequiredService<TrafficRollup>().RebuildAsync(new DateOnly(2000, 10, 1), new DateOnly(2000, 12, 31), Token);
        }

        using var scope = sqlServer.CreateScope();
        var (human, bot) = await scope.ServiceProvider.GetRequiredService<TrafficRetention>().PurgeAsync(Today, 90, 7, Token);

        Assert.Equal((1, 1), (human, bot));

        var left = await Paths2000Async();
        Assert.Equal(["/bot-kept", "/human-kept", "/human-recent"], left);

        // The daily tables are kept indefinitely: the purged days still have their totals.
        var stored = await TrafficDailyAsync(sqlServer, new DateOnly(2000, 10, 2), new DateOnly(2000, 12, 24), Token);
        Assert.Contains(stored, row => row.Date == new DateOnly(2000, 10, 2) && row.PageViews == 1);
        Assert.Contains(stored, row => row.Date == new DateOnly(2000, 12, 24) && row.BotPageViews == 1);
    }

    [Fact]
    public async Task More_than_one_batch_is_purged_in_batches_of_four_thousand()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);
        await ClearYearAsync(sqlServer, 2000, Token);

        var old = Enumerable.Range(0, TrafficRetention.BatchSize + 1)
            .Select(i => View(Colombo(2000, 3, 1, 12, i % 60), Guid.NewGuid(), "/old"))
            .ToList();
        await InsertAsync(sqlServer, old, Token);

        // Rolled up first: since #193 the purge keeps the raw rows of a day the rollup has never built.
        using (var seed = sqlServer.CreateScope())
        {
            await seed.ServiceProvider.GetRequiredService<TrafficRollup>().RebuildAsync(new DateOnly(2000, 3, 1), new DateOnly(2000, 3, 1), Token);
        }

        using var scope = sqlServer.CreateScope();
        var (human, _) = await scope.ServiceProvider.GetRequiredService<TrafficRetention>().PurgeAsync(Today, 90, 7, Token);

        Assert.Equal(TrafficRetention.BatchSize + 1, human);
        Assert.Empty(await Paths2000Async());
    }

    // ---- #193: never purge a day the rollup has not built ----------------------------------------------

    /// <summary>
    /// <b>The gap #193's tester found.</b> The rollup failed silently on 1 June (no <c>TrafficDaily</c> row), the raw
    /// period is then shortened so 1 June is past it, and the catch-up no longer looks that far back. The purge
    /// keeps 1 June's raw rows — human and bot — names the day in a warning, and still deletes a neighbouring day
    /// that was built. Nothing else changes: the kept day is the only one reported.
    /// </summary>
    [Fact]
    public async Task An_unbuilt_old_day_keeps_its_raw_rows_and_is_logged_while_a_built_one_goes()
    {
        await StartAsync();

        await InsertAsync(
            sqlServer,
            [
                View(Colombo(2000, 6, 1, 10), Guid.NewGuid(), "/unbuilt-human"),
                View(Colombo(2000, 6, 1, 11), Guid.NewGuid(), "/unbuilt-bot", bot: true),
                View(Colombo(2000, 6, 2, 10), Guid.NewGuid(), "/built-human"),
                View(Colombo(2000, 6, 2, 11), Guid.NewGuid(), "/built-bot", bot: true),
            ],
            Token);

        await RebuildAsync(new DateOnly(2000, 6, 2), new DateOnly(2000, 6, 2));

        var logger = new ListLogger<TrafficRetention>();
        var result = await PurgeWithAsync(logger, 30, 7);

        Assert.Equal([new DateOnly(2000, 6, 1)], In2000(result.SkippedDays));
        Assert.Equal(["/unbuilt-bot", "/unbuilt-human"], await Paths2000Async());
        Assert.Equal((1, 1), (result.Human, result.Bot));

        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(7134, warning.EventId.Id);
        Assert.Contains("2000-06-01", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("2000-06-02", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>Once the rollup has caught up on the kept day, the next purge deletes it as normal and says nothing.</summary>
    [Fact]
    public async Task After_the_rollup_builds_the_kept_day_the_next_purge_deletes_it()
    {
        await StartAsync();

        await InsertAsync(
            sqlServer,
            [
                View(Colombo(2000, 6, 1, 10), Guid.NewGuid(), "/late-human"),
                View(Colombo(2000, 6, 1, 11), Guid.NewGuid(), "/late-bot", bot: true),
            ],
            Token);

        var first = await PurgeWithAsync(new ListLogger<TrafficRetention>(), 30, 7);
        Assert.Equal([new DateOnly(2000, 6, 1)], In2000(first.SkippedDays));
        Assert.Equal(2, (await Paths2000Async()).Count);

        await RebuildAsync(new DateOnly(2000, 6, 1), new DateOnly(2000, 6, 1));

        var logger = new ListLogger<TrafficRetention>();
        var second = await PurgeWithAsync(logger, 30, 7);

        Assert.Empty(In2000(second.SkippedDays));
        Assert.Equal((1, 1), (second.Human, second.Bot));
        Assert.Empty(await Paths2000Async());
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("2000-", StringComparison.Ordinal));

        // And the day's totals survived the purge, which is the point of having waited.
        var stored = await TrafficDailyAsync(sqlServer, new DateOnly(2000, 6, 1), new DateOnly(2000, 6, 1), Token);
        Assert.Equal(1, stored.Sum(row => row.PageViews));
        Assert.Equal(1, stored.Sum(row => row.BotPageViews));
    }

    /// <summary>
    /// A bot-only day is built (the rollup writes its <c>BotPageViews</c> row), so it is purged normally — the guard
    /// cannot hold a day for ever just because nobody human visited.
    /// </summary>
    [Fact]
    public async Task A_bot_only_day_is_built_by_the_rollup_and_purged_normally()
    {
        await StartAsync();

        await InsertAsync(sqlServer, [View(Colombo(2000, 6, 3, 9), Guid.NewGuid(), "/bot-only", bot: true)], Token);
        await RebuildAsync(new DateOnly(2000, 6, 1), new DateOnly(2000, 6, 30));

        var result = await PurgeWithAsync(new ListLogger<TrafficRetention>(), 30, 7);

        Assert.Empty(In2000(result.SkippedDays));
        Assert.Equal(1, result.Bot);
        Assert.Empty(await Paths2000Async());
    }

    /// <summary>
    /// A day inside the raw period but past the shorter bot period is reached only by the bot purge; if the rollup
    /// never built it, its bot rows are kept too.
    /// </summary>
    [Fact]
    public async Task An_unbuilt_day_reached_only_by_the_bot_period_keeps_its_bot_rows()
    {
        await StartAsync();

        await InsertAsync(
            sqlServer,
            [
                View(Colombo(2000, 12, 10, 9), Guid.NewGuid(), "/unbuilt-bot-inside-raw", bot: true),
                View(Colombo(2000, 12, 11, 9), Guid.NewGuid(), "/built-bot-inside-raw", bot: true),
            ],
            Token);

        await RebuildAsync(new DateOnly(2000, 12, 11), new DateOnly(2000, 12, 11));

        var result = await PurgeWithAsync(new ListLogger<TrafficRetention>(), 30, 7);

        Assert.Equal([new DateOnly(2000, 12, 10)], In2000(result.SkippedDays));
        Assert.Equal(["/unbuilt-bot-inside-raw"], await Paths2000Async());
    }

    /// <summary>
    /// The guard is per Colombo day, not per UTC day: a view at 23:59 Colombo on an unbuilt day is kept, and one at
    /// 00:00 the next (built) day — the same UTC date — goes. And a view at 02:00 Colombo on an unbuilt day, whose
    /// UTC date is the previous (built) day, is still recognised as unbuilt and kept.
    /// </summary>
    [Fact]
    public async Task The_kept_range_is_exactly_the_unbuilt_Colombo_day()
    {
        await StartAsync();

        await InsertAsync(
            sqlServer,
            [
                View(Colombo(2000, 6, 4, 23, 59, 59), Guid.NewGuid(), "/unbuilt-last-second"),
                View(Colombo(2000, 6, 5, 0, 0, 0), Guid.NewGuid(), "/built-first-second"),
                View(Colombo(2000, 6, 3, 23, 59, 59), Guid.NewGuid(), "/built-day-before"),
                View(Colombo(2000, 6, 6, 12), Guid.NewGuid(), "/built-noon"),
                View(Colombo(2000, 6, 7, 2), Guid.NewGuid(), "/unbuilt-early-morning"),
            ],
            Token);

        await RebuildAsync(new DateOnly(2000, 6, 5), new DateOnly(2000, 6, 6));
        await RebuildAsync(new DateOnly(2000, 6, 3), new DateOnly(2000, 6, 3));

        var result = await PurgeWithAsync(new ListLogger<TrafficRetention>(), 30, 7);

        Assert.Equal([new DateOnly(2000, 6, 4), new DateOnly(2000, 6, 7)], In2000(result.SkippedDays));
        Assert.Equal(["/unbuilt-early-morning", "/unbuilt-last-second"], await Paths2000Async());
    }

    // ---- tester, fix round (#193) ------------------------------------------------------------------------

    /// <summary>
    /// The UTC-midnight crossing inside one Colombo day. 05:29:59 and 05:30:00 Colombo on 9 August are 23:59:59
    /// on 8 August and 00:00:00 on 9 August in UTC: two UTC dates, one Colombo day. With 9 August unbuilt, both are
    /// kept; with 8 and 10 August built, a view at 05:29:59 Colombo on 10 August (UTC 9 August, the unbuilt day's
    /// UTC date) goes, and so does 8 August's last second.
    /// </summary>
    [Fact]
    public async Task An_unbuilt_Colombo_day_is_kept_whole_across_the_UTC_midnight_inside_it()
    {
        await StartAsync();

        await InsertAsync(
            sqlServer,
            [
                View(Colombo(2000, 8, 8, 23, 59, 59), Guid.NewGuid(), "/built-8-last-second"),
                View(Colombo(2000, 8, 9, 0, 0, 0), Guid.NewGuid(), "/unbuilt-9-first-second"),
                View(Colombo(2000, 8, 9, 5, 29, 59), Guid.NewGuid(), "/unbuilt-9-0529"),
                View(Colombo(2000, 8, 9, 5, 30, 0), Guid.NewGuid(), "/unbuilt-9-0530"),
                View(Colombo(2000, 8, 9, 5, 31, 0), Guid.NewGuid(), "/unbuilt-9-0531"),
                View(Colombo(2000, 8, 9, 23, 59, 59), Guid.NewGuid(), "/unbuilt-9-last-second"),
                View(Colombo(2000, 8, 10, 0, 0, 0), Guid.NewGuid(), "/built-10-first-second"),
                View(Colombo(2000, 8, 10, 5, 29, 59), Guid.NewGuid(), "/built-10-0529"),
            ],
            Token);

        await RebuildAsync(new DateOnly(2000, 8, 8), new DateOnly(2000, 8, 8));
        await RebuildAsync(new DateOnly(2000, 8, 10), new DateOnly(2000, 8, 10));

        var result = await PurgeWithAsync(new ListLogger<TrafficRetention>(), 30, 7);

        Assert.Equal([new DateOnly(2000, 8, 9)], In2000(result.SkippedDays));
        Assert.Equal(
            ["/unbuilt-9-0529", "/unbuilt-9-0530", "/unbuilt-9-0531", "/unbuilt-9-first-second", "/unbuilt-9-last-second"],
            await Paths2000Async());
        Assert.Equal(3, result.Human);
    }

    /// <summary>
    /// Scattered and adjacent unbuilt days with a raw period (30) and a bot period (7) that differ, in one purge:
    /// a run of three adjacent unbuilt days, a lone one, built days between and around them, and — inside the raw
    /// period but past the bot one — an unbuilt day whose bot row is kept beside a built day whose bot row goes and
    /// whose human row stays (still inside raw retention).
    /// </summary>
    [Fact]
    public async Task Scattered_and_adjacent_unbuilt_days_are_kept_under_different_raw_and_bot_periods()
    {
        await StartAsync();

        await InsertAsync(
            sqlServer,
            [
                View(Colombo(2000, 7, 31, 12), Guid.NewGuid(), "/built-jul31"),
                View(Colombo(2000, 8, 1, 12), Guid.NewGuid(), "/unbuilt-aug1"),
                View(Colombo(2000, 8, 2, 12), Guid.NewGuid(), "/unbuilt-aug2-bot", bot: true),
                View(Colombo(2000, 8, 3, 12), Guid.NewGuid(), "/unbuilt-aug3"),
                View(Colombo(2000, 8, 4, 12), Guid.NewGuid(), "/built-aug4-bot", bot: true),
                View(Colombo(2000, 8, 5, 12), Guid.NewGuid(), "/built-aug5"),
                View(Colombo(2000, 8, 10, 12), Guid.NewGuid(), "/unbuilt-aug10"),
                View(Colombo(2000, 8, 12, 12), Guid.NewGuid(), "/built-aug12"),
                View(Colombo(2000, 12, 15, 12), Guid.NewGuid(), "/unbuilt-dec15-bot", bot: true),
                View(Colombo(2000, 12, 16, 12), Guid.NewGuid(), "/built-dec16-bot", bot: true),
                View(Colombo(2000, 12, 16, 13), Guid.NewGuid(), "/built-dec16-human"),
            ],
            Token);

        await RebuildAsync(new DateOnly(2000, 7, 31), new DateOnly(2000, 7, 31));
        await RebuildAsync(new DateOnly(2000, 8, 4), new DateOnly(2000, 8, 5));
        await RebuildAsync(new DateOnly(2000, 8, 12), new DateOnly(2000, 8, 12));
        await RebuildAsync(new DateOnly(2000, 12, 16), new DateOnly(2000, 12, 16));

        var result = await PurgeWithAsync(new ListLogger<TrafficRetention>(), 30, 7);

        Assert.Equal(
            [new DateOnly(2000, 8, 1), new DateOnly(2000, 8, 2), new DateOnly(2000, 8, 3), new DateOnly(2000, 8, 10), new DateOnly(2000, 12, 15)],
            In2000(result.SkippedDays));
        Assert.Equal(
            ["/built-dec16-human", "/unbuilt-aug1", "/unbuilt-aug10", "/unbuilt-aug2-bot", "/unbuilt-aug3", "/unbuilt-dec15-bot"],
            await Paths2000Async());
        Assert.Equal((3, 2), (result.Human, result.Bot));
    }

    /// <summary>
    /// Only this class's year: the shared database can hold never-rolled-up rows from other classes' earlier years,
    /// which the purge (rightly) now keeps and reports too.
    /// </summary>
    private static List<DateOnly> In2000(IEnumerable<DateOnly> days) => days.Where(day => day.Year == 2000).ToList();

    private async Task StartAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);
        await ClearYearAsync(sqlServer, 2000, Token);
    }

    private async Task RebuildAsync(DateOnly from, DateOnly to)
    {
        using var scope = sqlServer.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TrafficRollup>().RebuildAsync(from, to, Token);
    }

    /// <summary>The purge as of 31 December 2000, with a logger the test can read.</summary>
    private async Task<TrafficPurgeResult> PurgeWithAsync(ListLogger<TrafficRetention> logger, int rawDays, int botDays)
    {
        using var scope = sqlServer.CreateScope();
        var retention = new TrafficRetention(scope.ServiceProvider.GetRequiredService<AppDbContext>(), logger);

        return await retention.PurgeAsync(Today, rawDays, botDays, Token);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId, formatter(state, exception)));
    }

    private async Task<List<string>> Paths2000Async()
    {
        using var scope = sqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var from = new DateTime(1999, 12, 31, 18, 30, 0, DateTimeKind.Utc);
        var to = new DateTime(2000, 12, 31, 18, 30, 0, DateTimeKind.Utc);

        return await db.PageViews
            .AsNoTracking()
            .Where(view => view.OccurredAtUtc >= from && view.OccurredAtUtc < to)
            .Select(view => view.Path)
            .OrderBy(path => path)
            .ToListAsync(Token);
    }
}
