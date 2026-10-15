using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Analytics;
using SpencerDenim.Infrastructure.Analytics;
using SpencerDenim.IntegrationTests.Infrastructure;
using static SpencerDenim.IntegrationTests.Analytics.TrafficRows;

namespace SpencerDenim.IntegrationTests.Analytics;

/// <summary>
/// Doc 08 §8.3's nightly traffic rollup against SQL Server: Colombo days, session attribution, bots kept apart,
/// the top-50 source cap, idempotence, the catch-up of days that were never built, and two rollups at once.
/// Each test owns a year in 2001–2006 (<see cref="TrafficRows"/>).
/// </summary>
public sealed class TrafficRollupTests(SqlServerDatabaseFixture sqlServer)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<TrafficRollup> RollupAsync(IServiceScope scope, int year)
    {
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);
        await ClearYearAsync(sqlServer, year, Token);
        return scope.ServiceProvider.GetRequiredService<TrafficRollup>();
    }

    private IServiceScope Scope()
    {
        sqlServer.SkipIfUnavailable();
        return sqlServer.CreateScope();
    }

    [Fact]
    public async Task A_view_at_23_59_and_one_at_00_01_Colombo_land_on_different_days()
    {
        using var scope = Scope();
        var rollup = await RollupAsync(scope, 2001);

        // 18:29 and 18:31 UTC on the same UTC day: 23:59 on 3 March and 00:01 on 4 March in Colombo.
        var before = new DateTime(2001, 3, 3, 18, 29, 0, DateTimeKind.Utc);
        var after = new DateTime(2001, 3, 3, 18, 31, 0, DateTimeKind.Utc);
        Assert.Equal(before.Date, after.Date);

        await InsertAsync(sqlServer, [View(before, Guid.NewGuid(), "/women"), View(after, Guid.NewGuid(), "/men")], Token);
        await rollup.RebuildAsync(new DateOnly(2001, 3, 3), new DateOnly(2001, 3, 4), Token);

        var traffic = await TrafficDailyAsync(sqlServer, new DateOnly(2001, 3, 1), new DateOnly(2001, 3, 31), Token);
        Assert.Equal([(new DateOnly(2001, 3, 3), 1), (new DateOnly(2001, 3, 4), 1)], traffic.Select(row => (row.Date, row.PageViews)));

        var pages = await PageDailyAsync(sqlServer, new DateOnly(2001, 3, 1), new DateOnly(2001, 3, 31), Token);
        Assert.Equal([(new DateOnly(2001, 3, 3), "/women"), (new DateOnly(2001, 3, 4), "/men")], pages.Select(row => (row.Date, row.Path)));
    }

    [Fact]
    public async Task A_sessions_views_are_credited_to_its_first_view_and_bots_are_never_visitors()
    {
        using var scope = Scope();
        var rollup = await RollupAsync(scope, 2002);
        var day = new DateOnly(2002, 5, 10);

        var fromGoogle = Guid.NewGuid();
        var typedIn = Guid.NewGuid();

        await InsertAsync(
            sqlServer,
            [
                // Arrived from Google on a phone, then clicked three times (the clicks are Direct in the raw rows).
                View(Colombo(2002, 5, 10, 9, 0), fromGoogle, "/", TrafficChannel.Organic, DeviceType.Mobile, referrerHost: "www.google.com"),
                View(Colombo(2002, 5, 10, 9, 1), fromGoogle, "/women", TrafficChannel.Direct, DeviceType.Mobile, pageType: PageType.Department),
                View(Colombo(2002, 5, 10, 9, 2), fromGoogle, "/women/dresses", TrafficChannel.Direct, DeviceType.Mobile, pageType: PageType.Category),
                View(Colombo(2002, 5, 10, 9, 3), fromGoogle, "/women", TrafficChannel.Direct, DeviceType.Mobile, pageType: PageType.Department),

                // Typed in on a desktop: one Direct view.
                View(Colombo(2002, 5, 10, 10, 0), typedIn, "/women", TrafficChannel.Direct, DeviceType.Desktop, pageType: PageType.Department),

                // Two crawler hits, Direct desktop, in two "sessions".
                View(Colombo(2002, 5, 10, 11, 0), Guid.NewGuid(), "/women", TrafficChannel.Direct, DeviceType.Desktop, bot: true, pageType: PageType.Department),
                View(Colombo(2002, 5, 10, 11, 1), Guid.NewGuid(), "/sale", TrafficChannel.Direct, DeviceType.Desktop, bot: true, pageType: PageType.Sale),
            ],
            Token);

        await rollup.RebuildAsync(day, day, Token);

        var rows = await TrafficDailyAsync(sqlServer, day, day, Token);
        Assert.Equal(
            [
                (TrafficChannel.Direct, DeviceType.Desktop, "(direct)", 1, 1, 2),
                (TrafficChannel.Organic, DeviceType.Mobile, "www.google.com", 4, 1, 0),
            ],
            rows.Select(row => (row.Channel, row.DeviceType, row.SourceKey, row.PageViews, row.Sessions, row.BotPageViews)));

        // Top pages count people only: the crawler's /women and /sale are not there.
        var pages = await PageDailyAsync(sqlServer, day, day, Token);
        Assert.Equal(
            [("/", 1, 1), ("/women", 3, 2), ("/women/dresses", 1, 1)],
            pages.Select(row => (row.Path, row.PageViews, row.Sessions)));
        Assert.All(pages, row => Assert.Equal(16, row.PathHash.Length));
        Assert.Equal(0, await MismatchedHashesAsync(day));
        Assert.Equal(PageType.Department, pages.Single(row => row.Path == "/women").PageType);
    }

    [Fact]
    public async Task Rebuilding_is_idempotent_and_picks_up_late_rows()
    {
        using var scope = Scope();
        var rollup = await RollupAsync(scope, 2003);
        var day = new DateOnly(2003, 7, 1);
        var session = Guid.NewGuid();

        await InsertAsync(sqlServer, [View(Colombo(2003, 7, 1, 8), session, "/")], Token);

        await rollup.RebuildAsync(day, day, Token);
        await rollup.RebuildAsync(day, day, Token);
        Assert.Equal(1, Assert.Single(await TrafficDailyAsync(sqlServer, day, day, Token)).PageViews);
        Assert.Single(await PageDailyAsync(sqlServer, day, day, Token));

        await InsertAsync(sqlServer, [View(Colombo(2003, 7, 1, 23, 59, 59), session, "/sale", pageType: PageType.Sale)], Token);
        await rollup.RebuildAsync(day, day, Token);

        var row = Assert.Single(await TrafficDailyAsync(sqlServer, day, day, Token));
        Assert.Equal((2, 1), (row.PageViews, row.Sessions));
        Assert.Equal(2, (await PageDailyAsync(sqlServer, day, day, Token)).Count);
    }

    [Fact]
    public async Task Only_the_top_fifty_sources_of_a_day_keep_their_name()
    {
        using var scope = Scope();
        var rollup = await RollupAsync(scope, 2004);
        var day = new DateOnly(2004, 2, 2);

        // 55 sources; source-00 has three sessions and source-01 two, the rest one each, so the rank is not a tie
        // at the top. The five that fall off are the alphabetically last of the one-session sources.
        var views = new List<PageView>();
        for (var i = 0; i < 55; i++)
        {
            var sessions = i switch { 0 => 3, 1 => 2, _ => 1 };
            for (var s = 0; s < sessions; s++)
            {
                views.Add(View(Colombo(2004, 2, 2, 12), Guid.NewGuid(), "/", TrafficChannel.Referral, utmSource: $"source-{i:D2}"));
            }
        }

        await InsertAsync(sqlServer, views, Token);
        await rollup.RebuildAsync(day, day, Token);

        var rows = await TrafficDailyAsync(sqlServer, day, day, Token);
        Assert.Equal(51, rows.Count);
        Assert.Equal((5, 5), rows.Where(row => row.SourceKey == "(other)").Select(row => (row.Sessions, row.PageViews)).Single());
        Assert.Equal(3, rows.Single(row => row.SourceKey == "source-00").Sessions);
        Assert.DoesNotContain(rows, row => row.SourceKey is "source-50" or "source-54");
        Assert.Equal(views.Count, rows.Sum(row => row.PageViews));
    }

    [Fact]
    public async Task One_pass_rebuilds_the_last_three_days_and_builds_older_days_that_were_never_built()
    {
        using var scope = Scope();
        await RollupAsync(scope, 2005);
        var service = scope.ServiceProvider.GetRequiredService<TrafficRollupService>();

        // "Now" is 20 June 2005, 01:00 Colombo: the last three completed days are 17–19 June.
        var now = new DateTimeOffset(2005, 6, 20, 1, 0, 0, TimeSpan.FromMinutes(330));

        await InsertAsync(
            sqlServer,
            [
                View(Colombo(2005, 5, 1), Guid.NewGuid(), "/a"),      // never built, inside 90 days: built
                View(Colombo(2005, 6, 18), Guid.NewGuid(), "/b"),     // in the last three days: rebuilt
                View(Colombo(2005, 6, 20, 0, 30), Guid.NewGuid(), "/c"), // today: never stored
                View(Colombo(2005, 1, 2), Guid.NewGuid(), "/d"),      // older than raw retention: left alone
            ],
            Token);

        var rebuilt = await service.RunOnceAsync(now, Token);

        Assert.Equal([new DateOnly(2005, 5, 1), new DateOnly(2005, 6, 17), new DateOnly(2005, 6, 18), new DateOnly(2005, 6, 19)], rebuilt);

        var stored = await TrafficDailyAsync(sqlServer, new DateOnly(2005, 1, 1), new DateOnly(2005, 12, 31), Token);
        Assert.Equal([new DateOnly(2005, 5, 1), new DateOnly(2005, 6, 18)], stored.Select(row => row.Date));

        // A second pass finds nothing older to build: 1 May has its row now.
        Assert.Equal([new DateOnly(2005, 6, 17), new DateOnly(2005, 6, 18), new DateOnly(2005, 6, 19)], await service.RunOnceAsync(now, Token));
    }

    [Fact]
    public async Task Two_rollups_of_the_same_days_at_once_queue_on_the_lock_and_agree()
    {
        using var scope = Scope();
        await RollupAsync(scope, 2006);
        var day = new DateOnly(2006, 9, 9);

        var views = Enumerable.Range(0, 200)
            .Select(i => View(Colombo(2006, 9, 9, 10, i % 60), Guid.NewGuid(), $"/p{i % 7}"))
            .ToList();
        await InsertAsync(sqlServer, views, Token);

        using var first = sqlServer.CreateScope();
        using var second = sqlServer.CreateScope();

        await Task.WhenAll(
            first.ServiceProvider.GetRequiredService<TrafficRollup>().RebuildAsync(day, day, Token),
            second.ServiceProvider.GetRequiredService<TrafficRollup>().RebuildAsync(day, day, Token));

        var row = Assert.Single(await TrafficDailyAsync(sqlServer, day, day, Token));
        Assert.Equal((200, 200), (row.PageViews, row.Sessions));
        Assert.Equal(7, (await PageDailyAsync(sqlServer, day, day, Token)).Count);
    }

    [Theory]
    [InlineData("2026-10-15T18:59:00+00:00", "2026-10-15T19:00:00+00:00")]
    [InlineData("2026-10-15T19:00:00+00:00", "2026-10-16T19:00:00+00:00")]
    [InlineData("2026-10-15T18:00:00+00:00", "2026-10-15T19:00:00+00:00")]
    public void The_rollup_runs_at_half_past_midnight_Colombo(string now, string next)
    {
        Assert.Equal(DateTimeOffset.Parse(next, System.Globalization.CultureInfo.InvariantCulture), TrafficRollupService.NextRun(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>Rows whose key is not the MD5 of their own path, as SQL Server computes it.</summary>
    private async Task<int> MismatchedHashesAsync(DateOnly day)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(sqlServer.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM [analytics].[PageDaily] WHERE [Date] = @day AND [PathHash] <> CAST(HASHBYTES('MD5', [Path]) AS binary(16))";
        command.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@day", System.Data.SqlDbType.Date) { Value = day.ToDateTime(TimeOnly.MinValue) });
        return (int)(await command.ExecuteScalarAsync(Token))!;
    }
}
