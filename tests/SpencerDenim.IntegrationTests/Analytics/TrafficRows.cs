using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Analytics;
using SpencerDenim.Domain.Common;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Analytics;

/// <summary>
/// Raw page views written straight into <c>analytics.PageViews</c> with the dates a test chooses, for the rollup
/// and the Traffic page.
/// <para>
/// <b>Years of their own.</b> The reporting suites own 2007–2016 one year per test, and 2000, 2019, 2020 and
/// 2026 are used elsewhere (ReportingTestBase). The traffic tests use 2001–2006, one year per test class or test,
/// and every assertion is about those days, so the shared database's other rows are never in an answer.
/// </para>
/// </summary>
internal static class TrafficRows
{
    /// <summary>The UTC instant of a Colombo wall-clock time.</summary>
    public static DateTime Colombo(int year, int month, int day, int hour = 12, int minute = 0, int second = 0) =>
        new DateTimeOffset(year, month, day, hour, minute, second, ColomboTime.Offset).UtcDateTime;

    public static PageView View(
        DateTime occurredUtc,
        Guid session,
        string path = "/",
        TrafficChannel channel = TrafficChannel.Direct,
        DeviceType device = DeviceType.Desktop,
        bool bot = false,
        string? utmSource = null,
        string? referrerHost = null,
        PageType pageType = PageType.Home,
        int? productId = null) =>
        new()
        {
            OccurredAtUtc = occurredUtc,
            SessionId = session,
            Path = path,
            PageType = pageType,
            ProductId = productId,
            Channel = channel,
            DeviceType = device,
            IsBot = bot,
            UtmSource = utmSource,
            ReferrerHost = referrerHost,
        };

    public static async Task InsertAsync(SqlServerDatabaseFixture sqlServer, IEnumerable<PageView> views, CancellationToken cancellationToken)
    {
        using var scope = sqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PageViews.AddRange(views);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Removes a year's raw and aggregated traffic, so a test can be run twice against one database.</summary>
    public static async Task ClearYearAsync(SqlServerDatabaseFixture sqlServer, int year, CancellationToken cancellationToken)
    {
        using var scope = sqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var from = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(-6);
        var to = new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(6);
        var firstDay = new DateOnly(year, 1, 1);
        var lastDay = new DateOnly(year, 12, 31);

        await db.PageViews.Where(view => view.OccurredAtUtc >= from && view.OccurredAtUtc < to).ExecuteDeleteAsync(cancellationToken);
        await db.TrafficDaily.Where(row => row.Date >= firstDay && row.Date <= lastDay).ExecuteDeleteAsync(cancellationToken);
        await db.PageDaily.Where(row => row.Date >= firstDay && row.Date <= lastDay).ExecuteDeleteAsync(cancellationToken);
    }

    public static async Task<List<TrafficDaily>> TrafficDailyAsync(SqlServerDatabaseFixture sqlServer, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        using var scope = sqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.TrafficDaily
            .AsNoTracking()
            .Where(row => row.Date >= from && row.Date <= to)
            .OrderBy(row => row.Date).ThenBy(row => row.Channel).ThenBy(row => row.DeviceType).ThenBy(row => row.SourceKey)
            .ToListAsync(cancellationToken);
    }

    public static async Task<List<PageDaily>> PageDailyAsync(SqlServerDatabaseFixture sqlServer, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        using var scope = sqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.PageDaily
            .AsNoTracking()
            .Where(row => row.Date >= from && row.Date <= to)
            .OrderBy(row => row.Date).ThenBy(row => row.Path)
            .ToListAsync(cancellationToken);
    }
}
