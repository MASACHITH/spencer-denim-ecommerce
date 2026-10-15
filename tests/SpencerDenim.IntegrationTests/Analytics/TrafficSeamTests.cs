using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Application.Analytics.Reporting;
using SpencerDenim.Domain.Analytics;
using SpencerDenim.Infrastructure.Analytics;
using SpencerDenim.IntegrationTests.Infrastructure;
using static SpencerDenim.IntegrationTests.Analytics.TrafficRows;

namespace SpencerDenim.IntegrationTests.Analytics;

/// <summary>
/// Where the stored days meet the live window (W4-03 tester). The Traffic page reads the days before
/// <c>liveFrom</c> from <c>TrafficDaily</c>/<c>PageDaily</c> and the days from <c>liveFrom</c> from the raw views, and
/// in production the day <i>at</i> <c>liveFrom</c> is in both places (the rollup rebuilt it last night). So the same
/// days must give the same answer wherever the seam falls — all stored, split, or all live — or a day is counted
/// twice, or not at all, depending on the date the owner looks.
/// <para>
/// Year 2017 is this class's own (<see cref="TrafficRows"/>: the traffic tests own 2001–2006; 2017 is used nowhere
/// else in the suite).
/// </para>
/// </summary>
public sealed class TrafficSeamTests(SqlServerDatabaseFixture sqlServer)
{
    private const int Year = 2017;

    private static readonly DateOnly First = new(Year, 3, 10);
    private static readonly DateOnly Second = new(Year, 3, 11);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stored_and_live_days_agree_wherever_the_seam_falls_and_a_visit_across_Colombo_midnight_is_one_on_each_day()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);
        await ClearYearAsync(sqlServer, Year, Token);

        var acrossMidnight = Guid.NewGuid();
        var nextMorning = Guid.NewGuid();

        await InsertAsync(
            sqlServer,
            [
                // Arrived from Google on a phone at 23:50 Colombo, clicked once at 00:10 (a Direct raw row).
                View(Colombo(Year, 3, 10, 23, 50), acrossMidnight, "/", TrafficChannel.Organic, DeviceType.Mobile, referrerHost: "www.google.com"),
                View(Colombo(Year, 3, 11, 0, 10), acrossMidnight, "/women", TrafficChannel.Direct, DeviceType.Mobile, pageType: PageType.Department),

                // Someone else typed the address in on the second morning.
                View(Colombo(Year, 3, 11, 9, 0), nextMorning, "/sale", TrafficChannel.Direct, DeviceType.Desktop, pageType: PageType.Sale),

                // A crawler on the first day: in no visitor figure.
                View(Colombo(Year, 3, 10, 12, 0), Guid.NewGuid(), "/women", TrafficChannel.Direct, DeviceType.Other, bot: true, pageType: PageType.Department),
            ],
            Token);

        using var scope = sqlServer.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TrafficRollup>().RebuildAsync(First, Second, Token);

        var reader = scope.ServiceProvider.GetRequiredService<ITrafficReader>();
        var period = new ReportPeriod(First, Second);

        // liveFrom after the period: all stored. On the second day: split. On the first, or before it: all live.
        DateOnly[] seams = [Second.AddDays(1), Second, First, First.AddDays(-1)];
        var answers = new List<(string Cells, string Sources, string Pages)>();

        foreach (var liveFrom in seams)
        {
            var cells = await reader.ReadCellsAsync(period, liveFrom, Token);
            var sources = await reader.ReadTopSourcesAsync(period, liveFrom, 10, Token);
            var pages = await reader.ReadTopPagesAsync(period, liveFrom, 10, Token);

            answers.Add((
                string.Join(" | ", cells.OrderBy(c => c.Date).ThenBy(c => c.Channel).ThenBy(c => c.DeviceType)
                    .Select(c => $"{c.Date:yyyy-MM-dd} {c.Channel} {c.DeviceType} pv={c.PageViews} s={c.Sessions} bots={c.BotPageViews}")),
                string.Join(" | ", sources.Select(s => $"{s.SourceKey} s={s.Sessions} pv={s.PageViews}")),
                string.Join(" | ", pages.Select(p => $"{p.Path} pv={p.PageViews} s={p.Sessions}"))));
        }

        // The visit that crossed midnight is one visit on each day; its second day is credited to its first view
        // of that day, a click inside the shop (Direct), as doc 08 §8.3 attributes per day.
        Assert.Equal(
            "2017-03-10 Direct Other pv=0 s=0 bots=1 | 2017-03-10 Organic Mobile pv=1 s=1 bots=0 | "
            + "2017-03-11 Direct Desktop pv=1 s=1 bots=0 | 2017-03-11 Direct Mobile pv=1 s=1 bots=0",
            answers[0].Cells);
        Assert.Equal("(direct) s=2 pv=2 | www.google.com s=1 pv=1", answers[0].Sources);
        Assert.Equal("/ pv=1 s=1 | /sale pv=1 s=1 | /women pv=1 s=1", answers[0].Pages);

        for (var index = 1; index < seams.Length; index++)
        {
            Assert.True(answers[index] == answers[0], $"liveFrom {seams[index]:yyyy-MM-dd} answered {answers[index]}, all-stored answered {answers[0]}");
        }
    }
}
