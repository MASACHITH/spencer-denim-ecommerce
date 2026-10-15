using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Application.Admin.Reporting.Contracts;

namespace SpencerDenim.Application.Analytics.Reporting;

/// <summary>
/// The Traffic page (ADM-20; doc 11 P16 step 4: "visits, sources, top pages, devices").
/// <para>
/// <b>Visits are human sessions</b>: one visit is a visit-session cookie's page views on one Colombo day, bots
/// excluded (doc 08 §8.3). A visit over midnight is a visit on each day. <b>Page views</b> are human page views;
/// bot page views are reported apart and in nothing else.
/// </para>
/// </summary>
/// <param name="Period">The days asked for (Colombo, both ends included).</param>
/// <param name="PreviousPeriod">The same number of days immediately before.</param>
/// <param name="LiveFrom">
/// Days on or after this are counted live from the raw page views (today and the three days the nightly rollup
/// rebuilds); days before it come from the daily tables. The figures are the same either way — one set of rules
/// — and the screen says which days are live.
/// </param>
/// <param name="Visits">Human sessions, against the previous period.</param>
/// <param name="PageViews">Human page views, against the previous period.</param>
/// <param name="BotPageViews">Page views by crawlers and automated browsers, kept out of every other figure.</param>
/// <param name="Days">Every day of the period, zeros included, oldest first.</param>
/// <param name="Channels">All seven channels in doc 08 §3.8's order, zeros included.</param>
/// <param name="Devices">All four device types, zeros included.</param>
/// <param name="Sources">The top sources by visits.</param>
/// <param name="TopPages">The most-viewed pages by human page views.</param>
public sealed record TrafficReportDto(
    ReportPeriodDto Period,
    ReportPeriodDto PreviousPeriod,
    DateOnly LiveFrom,
    ComparedFigure Visits,
    ComparedFigure PageViews,
    int BotPageViews,
    IReadOnlyList<TrafficDayRow> Days,
    IReadOnlyList<TrafficChannelRow> Channels,
    IReadOnlyList<TrafficDeviceRow> Devices,
    IReadOnlyList<TrafficSourceRow> Sources,
    IReadOnlyList<TrafficPageRow> TopPages);

/// <param name="Date">A Colombo day.</param>
public sealed record TrafficDayRow(DateOnly Date, int Visits, int PageViews, int BotPageViews);

/// <param name="Channel">The name: <c>Direct</c>, <c>Organic</c>, <c>Social</c>, <c>Referral</c>, <c>Email</c>, <c>Paid</c>, <c>Other</c>.</param>
/// <param name="Label">What the screen prints.</param>
/// <param name="SharePercent">Of the period's visits, one decimal place; 0 when there were none.</param>
public sealed record TrafficChannelRow(string Channel, string Label, int Visits, int PageViews, decimal SharePercent);

/// <param name="Device">The name: <c>Desktop</c>, <c>Mobile</c>, <c>Tablet</c>, <c>Other</c>.</param>
public sealed record TrafficDeviceRow(string Device, string Label, int Visits, int PageViews, decimal SharePercent);

/// <param name="Source">The UTM source or referring host; <c>(direct)</c>, <c>(not set)</c> or <c>(other)</c> otherwise.</param>
public sealed record TrafficSourceRow(string Source, int Visits, int PageViews);

/// <param name="Path">The stored path: no query string beyond the listing's filters, no secret.</param>
/// <param name="PageType">The page family's name (<c>Product</c>, <c>Category</c>, …).</param>
/// <param name="ProductName">For a product page, the product's name; otherwise null.</param>
/// <param name="Visits">Visits that included the page (summed day by day).</param>
public sealed record TrafficPageRow(string Path, string PageType, string? ProductName, int PageViews, int Visits);

/// <summary>
/// The dashboard's Traffic Sources widget (ADM-06) and the conversion rate doc 11 P16's KPI table defines
/// ("Paid orders ÷ human sessions").
/// </summary>
/// <param name="Visits">Human sessions in the period.</param>
/// <param name="PaidOrders">Orders paid in the period (by paid date, the dashboard's own rule).</param>
/// <param name="ConversionPercent">Paid orders ÷ visits × 100, one decimal place; null when there were no visits.</param>
public sealed record TrafficSourcesDto(
    ReportPeriodDto Period,
    int Visits,
    int PaidOrders,
    decimal? ConversionPercent,
    IReadOnlyList<TrafficChannelRow> Channels);
