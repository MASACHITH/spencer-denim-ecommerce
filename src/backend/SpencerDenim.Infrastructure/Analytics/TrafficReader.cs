using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Application.Analytics.Reporting;
using SpencerDenim.Domain.Analytics;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Analytics;

/// <summary>
/// The Traffic page's and the dashboard widget's reads (ADM-06, ADM-20). Every statement is
/// <see cref="TrafficSql"/>'s, so a stored day and a live day are counted by the same rules; the reads are
/// <c>AsNoTracking</c>-equivalent raw queries at the connection's default isolation level.
/// </summary>
internal sealed class TrafficReader(AppDbContext dbContext) : ITrafficReader
{
    public async Task<IReadOnlyList<TrafficCell>> ReadCellsAsync(ReportPeriod period, DateOnly liveFrom, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);

        var rows = await dbContext.Database
            .SqlQueryRaw<CellRow>(TrafficSql.TrafficByDay(), Parameters(period, liveFrom))
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new TrafficCell(row.Date, (TrafficChannel)row.Channel, (DeviceType)row.DeviceType, row.PageViews, row.Sessions, row.BotPageViews))
            .ToList();
    }

    public async Task<IReadOnlyList<TrafficSourceTotal>> ReadTopSourcesAsync(
        ReportPeriod period,
        DateOnly liveFrom,
        int take,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);

        var rows = await dbContext.Database
            .SqlQueryRaw<SourceRow>(TrafficSql.TopSources(), [.. Parameters(period, liveFrom), Take(take)])
            .ToListAsync(cancellationToken);

        return rows.Select(row => new TrafficSourceTotal(row.SourceKey, row.Sessions, row.PageViews)).ToList();
    }

    public async Task<IReadOnlyList<TrafficPageTotal>> ReadTopPagesAsync(
        ReportPeriod period,
        DateOnly liveFrom,
        int take,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);

        var rows = await dbContext.Database
            .SqlQueryRaw<PageRow>(TrafficSql.TopPages(), [.. Parameters(period, liveFrom), Take(take)])
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new TrafficPageTotal(row.Path, (PageType)row.PageType, row.ProductId, row.ProductName, row.PageViews, row.Sessions))
            .ToList();
    }

    /// <summary>
    /// The stored days are <c>[From, LiveFrom)</c>; the live window is <c>[max(From, LiveFrom), To]</c> as UTC instants of
    /// Colombo midnights, empty when the period ends before the live window starts.
    /// </summary>
    internal static object[] Parameters(ReportPeriod period, DateOnly liveFrom)
    {
        var liveStart = period.From > liveFrom ? period.From : liveFrom;
        var liveFromUtc = ReportPeriod.ColomboMidnightUtc(liveStart);
        var liveToUtc = period.ToUtcExclusive > liveFromUtc ? period.ToUtcExclusive : liveFromUtc;

        return
        [
            TrafficRollup.Day("@fromDay", period.From),
            TrafficRollup.Day("@toDay", period.To),
            TrafficRollup.Day("@liveFrom", liveFrom),
            TrafficRollup.Utc(TrafficSql.FromUtc, liveFromUtc),
            TrafficRollup.Utc(TrafficSql.ToUtc, liveToUtc),
        ];
    }

    private static SqlParameter Take(int take) => new("@take", System.Data.SqlDbType.Int) { Value = Math.Clamp(take, 1, 100) };

    private sealed record CellRow(DateOnly Date, byte Channel, byte DeviceType, int PageViews, int Sessions, int BotPageViews);

    private sealed record SourceRow(string SourceKey, int Sessions, int PageViews);

    private sealed record PageRow(string Path, byte PageType, int? ProductId, string? ProductName, int PageViews, int Sessions);
}
