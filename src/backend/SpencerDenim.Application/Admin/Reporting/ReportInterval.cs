namespace SpencerDenim.Application.Admin.Reporting;

/// <summary>How a run of days is grouped into rows or points.</summary>
public enum ReportInterval
{
    Day,

    /// <summary>ISO 8601 weeks, Monday to Sunday (doc 11 P16: "grouped by ISO week or month of Colombo date").</summary>
    Week,

    Month,
}

/// <summary>One row or point: a run of Colombo days, both ends included.</summary>
public sealed record ReportBucket(DateOnly Start, DateOnly End);

/// <summary>
/// Splits a period into days, ISO weeks or calendar months.
/// </summary>
/// <remarks>
/// <b>The first and last buckets are cut to the period</b>, never widened to a whole week or month: a sales
/// report for 3–30 October grouped by week starts with Friday 3 to Sunday 5 October, and that row says so.
/// Widening it would add days nobody asked for and the totals would stop matching the period's KPIs.
/// </remarks>
public static class ReportBuckets
{
    /// <summary>How many points the dashboard trend shows: twelve months, or the 52 weeks that span them.</summary>
    public const int TrendMonths = 12;

    public const int TrendWeeks = 52;

    public static IReadOnlyList<ReportBucket> Split(ReportPeriod period, ReportInterval interval)
    {
        ArgumentNullException.ThrowIfNull(period);

        var buckets = new List<ReportBucket>();
        var start = period.From;

        while (start <= period.To)
        {
            var naturalEnd = interval switch
            {
                ReportInterval.Day => start,
                ReportInterval.Week => WeekStart(start).AddDays(6),
                ReportInterval.Month => new DateOnly(start.Year, start.Month, 1).AddMonths(1).AddDays(-1),
                _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, null),
            };

            var end = naturalEnd > period.To ? period.To : naturalEnd;
            buckets.Add(new ReportBucket(start, end));
            start = end.AddDays(1);
        }

        return buckets;
    }

    /// <summary>
    /// The dashboard trend's window (ADM-04: "weekly/monthly, 12 months"), ending on <paramref name="lastDay"/>:
    /// the current month and the eleven before it, or the current ISO week and the 51 before it. The last
    /// bucket is the part of the current month or week that has happened.
    /// </summary>
    public static ReportPeriod TrendWindow(DateOnly lastDay, ReportInterval interval) => interval switch
    {
        ReportInterval.Month => new ReportPeriod(
            new DateOnly(lastDay.Year, lastDay.Month, 1).AddMonths(-(TrendMonths - 1)),
            lastDay),
        ReportInterval.Week => new ReportPeriod(WeekStart(lastDay).AddDays(-7 * (TrendWeeks - 1)), lastDay),
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, "The trend is weekly or monthly."),
    };

    /// <summary>The Monday on or before <paramref name="day"/> (ISO 8601 weeks start on Monday).</summary>
    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>
    /// Maps each day of the buckets to its bucket's index, so a list of daily totals can be summed into them.
    /// </summary>
    public static Dictionary<DateOnly, int> IndexByDay(IReadOnlyList<ReportBucket> buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);

        var index = new Dictionary<DateOnly, int>();

        for (var position = 0; position < buckets.Count; position++)
        {
            for (var day = buckets[position].Start; day <= buckets[position].End; day = day.AddDays(1))
            {
                index[day] = position;
            }
        }

        return index;
    }
}
