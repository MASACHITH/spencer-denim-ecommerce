using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

        using var scope = sqlServer.CreateScope();
        var (human, _) = await scope.ServiceProvider.GetRequiredService<TrafficRetention>().PurgeAsync(Today, 90, 7, Token);

        Assert.Equal(TrafficRetention.BatchSize + 1, human);
        Assert.Empty(await Paths2000Async());
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
