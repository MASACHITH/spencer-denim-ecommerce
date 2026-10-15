using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Application.Admin.Reporting.Contracts;
using SpencerDenim.Domain.Analytics;

namespace SpencerDenim.Application.Analytics.Reporting;

/// <summary>One Colombo day's traffic for one channel and device.</summary>
public sealed record TrafficCell(DateOnly Date, TrafficChannel Channel, DeviceType DeviceType, int PageViews, int Sessions, int BotPageViews);

/// <summary>One source's traffic over a period.</summary>
public sealed record TrafficSourceTotal(string SourceKey, int Sessions, int PageViews);

/// <summary>One stored path's human traffic over a period.</summary>
public sealed record TrafficPageTotal(string Path, PageType PageType, int? ProductId, string? ProductName, int PageViews, int Sessions);

/// <summary>
/// Reads traffic for a period: stored days from <c>TrafficDaily</c>/<c>PageDaily</c>, days from
/// <paramref name="liveFrom"/> live from the raw page views by the same rules (doc 11 P16: "today is live").
/// </summary>
public interface ITrafficReader
{
    Task<IReadOnlyList<TrafficCell>> ReadCellsAsync(ReportPeriod period, DateOnly liveFrom, CancellationToken cancellationToken);

    Task<IReadOnlyList<TrafficSourceTotal>> ReadTopSourcesAsync(ReportPeriod period, DateOnly liveFrom, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<TrafficPageTotal>> ReadTopPagesAsync(ReportPeriod period, DateOnly liveFrom, int take, CancellationToken cancellationToken);
}

public sealed record GetTrafficReportQuery(ReportPeriod Period, DateOnly Today);

public sealed record GetTrafficSourcesQuery(ReportPeriod Period, DateOnly Today);

/// <summary>
/// What the screens call each channel and device: doc 08 §3.8's and doc 11 P16's own names, so the screen, the
/// documents and the client's conversations use one word for one thing. The Traffic page explains each.
/// </summary>
public static class TrafficLabels
{
    public static string For(TrafficChannel channel) => channel.ToString();

    public static string For(DeviceType device) => device.ToString();
}

/// <summary>
/// The arithmetic both traffic screens share, pure so it can be tested without a database: the live window, the
/// totals, the shares.
/// </summary>
public static class TrafficReport
{
    /// <summary>How many top sources and pages the Traffic page lists.</summary>
    public const int TopCount = 10;

    /// <summary>
    /// The live window: today and the three days before it — exactly the days the nightly rollup rebuilds, so a
    /// day is read live until the rollup has had its last word on it, and a rollup that did not run cannot leave
    /// a recent day empty.
    /// </summary>
    public static DateOnly LiveFrom(DateOnly today) => today.AddDays(-3);

    public static IReadOnlyList<TrafficChannelRow> Channels(IReadOnlyList<TrafficCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        var visits = cells.Sum(cell => cell.Sessions);

        return Enum.GetValues<TrafficChannel>()
            .Select(channel =>
            {
                var mine = cells.Where(cell => cell.Channel == channel).ToList();
                var sessions = mine.Sum(cell => cell.Sessions);
                return new TrafficChannelRow(channel.ToString(), TrafficLabels.For(channel), sessions, mine.Sum(cell => cell.PageViews), Share(sessions, visits));
            })
            .ToList();
    }

    public static IReadOnlyList<TrafficDeviceRow> Devices(IReadOnlyList<TrafficCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        var visits = cells.Sum(cell => cell.Sessions);

        return Enum.GetValues<DeviceType>()
            .Select(device =>
            {
                var mine = cells.Where(cell => cell.DeviceType == device).ToList();
                var sessions = mine.Sum(cell => cell.Sessions);
                return new TrafficDeviceRow(device.ToString(), TrafficLabels.For(device), sessions, mine.Sum(cell => cell.PageViews), Share(sessions, visits));
            })
            .ToList();
    }

    public static IReadOnlyList<TrafficDayRow> Days(ReportPeriod period, IReadOnlyList<TrafficCell> cells)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(cells);

        var byDay = cells.GroupBy(cell => cell.Date).ToDictionary(group => group.Key, group => group.ToList());

        return period.EachDay()
            .Select(day => byDay.TryGetValue(day, out var mine)
                ? new TrafficDayRow(day, mine.Sum(cell => cell.Sessions), mine.Sum(cell => cell.PageViews), mine.Sum(cell => cell.BotPageViews))
                : new TrafficDayRow(day, 0, 0, 0))
            .ToList();
    }

    /// <summary>Paid orders ÷ visits, as a percentage to one decimal place; null with no visits (never a division by zero, never a made-up 0 %).</summary>
    public static decimal? Conversion(int paidOrders, int visits) =>
        visits == 0 ? null : Math.Round(paidOrders * 100m / visits, 1, MidpointRounding.AwayFromZero);

    public static decimal Share(int part, int whole) =>
        whole == 0 ? 0m : Math.Round(part * 100m / whole, 1, MidpointRounding.AwayFromZero);
}

public sealed class GetTrafficReportQueryHandler(ITrafficReader reader) : IQueryHandler<GetTrafficReportQuery, TrafficReportDto>
{
    public async Task<TrafficReportDto> HandleAsync(GetTrafficReportQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var period = query.Period;
        var previous = period.Previous;
        var liveFrom = TrafficReport.LiveFrom(query.Today);

        // One DbContext, so one query at a time.
        var cells = await reader.ReadCellsAsync(period, liveFrom, cancellationToken);
        var earlier = await reader.ReadCellsAsync(previous, liveFrom, cancellationToken);
        var sources = await reader.ReadTopSourcesAsync(period, liveFrom, TrafficReport.TopCount, cancellationToken);
        var pages = await reader.ReadTopPagesAsync(period, liveFrom, TrafficReport.TopCount, cancellationToken);

        return new TrafficReportDto(
            ReportPeriodDto.Of(period),
            ReportPeriodDto.Of(previous),
            liveFrom,
            ComparedFigure.Of(cells.Sum(cell => cell.Sessions), earlier.Sum(cell => cell.Sessions)),
            ComparedFigure.Of(cells.Sum(cell => cell.PageViews), earlier.Sum(cell => cell.PageViews)),
            cells.Sum(cell => cell.BotPageViews),
            TrafficReport.Days(period, cells),
            TrafficReport.Channels(cells),
            TrafficReport.Devices(cells),
            sources.Select(source => new TrafficSourceRow(source.SourceKey, source.Sessions, source.PageViews)).ToList(),
            pages.Select(page => new TrafficPageRow(page.Path, page.PageType.ToString(), page.ProductName, page.PageViews, page.Sessions)).ToList());
    }
}

public sealed class GetTrafficSourcesQueryHandler(ITrafficReader traffic, IReportingReader sales)
    : IQueryHandler<GetTrafficSourcesQuery, TrafficSourcesDto>
{
    public async Task<TrafficSourcesDto> HandleAsync(GetTrafficSourcesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var cells = await traffic.ReadCellsAsync(query.Period, TrafficReport.LiveFrom(query.Today), cancellationToken);
        var totals = await sales.ReadTotalsAsync(UtcRange.Of(query.Period), cancellationToken);
        var visits = cells.Sum(cell => cell.Sessions);

        return new TrafficSourcesDto(
            ReportPeriodDto.Of(query.Period),
            visits,
            totals.PaidOrders,
            TrafficReport.Conversion(totals.PaidOrders, visits),
            TrafficReport.Channels(cells));
    }
}
