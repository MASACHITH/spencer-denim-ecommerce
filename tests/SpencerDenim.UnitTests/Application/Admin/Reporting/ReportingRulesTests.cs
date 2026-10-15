using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Application.Admin.Reporting.Contracts;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Application.Admin.Reporting;

/// <summary>"vs previous period", ISO weeks and months, and how daily figures become rows and KPIs.</summary>
public sealed class ReportingRulesTests
{
    // ---- vs previous period ----------------------------------------------------------------------------

    [Theory]
    [InlineData(1500, 1000, 500, 50.0)]
    [InlineData(1000, 1500, -500, -33.3)]
    [InlineData(0, 1000, -1000, -100.0)]   // a fall to nothing is an ordinary -100 %
    [InlineData(2, 3, -1, -33.3)]
    [InlineData(1, 3, -2, -66.7)]          // rounded half away from zero, one place
    [InlineData(1000, 1000, 0, 0.0)]
    public void A_change_is_a_percentage_of_the_previous_value(double current, double previous, double change, double percent)
    {
        var figure = ComparedFigure.Of((decimal)current, (decimal)previous);

        Assert.Equal((decimal)change, figure.Change);
        Assert.Equal((decimal)percent, figure.PercentChange);
    }

    /// <summary>A rise from nothing is not a percentage of anything; both zero is no change.</summary>
    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(0, 0)]
    public void A_previous_value_of_zero_has_no_percentage(double current, double change)
    {
        var figure = ComparedFigure.Of((decimal)current, 0m);

        Assert.Null(figure.PercentChange);
        Assert.Equal((decimal)change, figure.Change);
    }

    [Fact]
    public void An_absent_value_has_no_change_and_no_percentage()
    {
        Assert.Equal(new ComparedFigure(null, 100m, null, null), ComparedFigure.Of(null, 100m));
        Assert.Equal(new ComparedFigure(100m, null, null, null), ComparedFigure.Of(100m, null));
    }

    [Fact]
    public void An_average_of_nothing_is_absent_not_zero()
    {
        Assert.Null(ComparedFigure.Average(0m, 0));
        Assert.Equal(333.33m, ComparedFigure.Average(1000m, 3));
    }

    [Fact]
    public void The_kpis_compare_like_with_like_and_net_subtracts_refunds()
    {
        var kpis = GetAdminDashboardQueryHandler.Kpis(
            new SalesTotals(GrossRevenue: 9000m, PaidOrders: 3, Orders: 5, RefundsCompleted: 1000m),
            new SalesTotals(GrossRevenue: 0m, PaidOrders: 0, Orders: 2, RefundsCompleted: 0m));

        Assert.Equal(9000m, kpis.GrossRevenue.Current);
        Assert.Equal(8000m, kpis.NetRevenue.Current);
        Assert.Equal(1000m, kpis.RefundsCompleted.Current);
        Assert.Equal(5m, kpis.Orders.Current);
        Assert.Equal(150.0m, kpis.Orders.PercentChange);
        Assert.Equal(3m, kpis.PaidOrders.Current);
        Assert.Equal(3000m, kpis.AverageOrderValue.Current);
        Assert.Null(kpis.AverageOrderValue.Previous);
        Assert.Null(kpis.AverageOrderValue.PercentChange);
        Assert.Null(kpis.GrossRevenue.PercentChange);
    }

    // ---- buckets ---------------------------------------------------------------------------------------

    [Fact]
    public void Weeks_are_ISO_weeks_cut_to_the_period()
    {
        // Thursday 1 January 2026 to Tuesday 13 January 2026.
        var buckets = ReportBuckets.Split(new ReportPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 13)), ReportInterval.Week);

        Assert.Equal(
            [
                new ReportBucket(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 4)),
                new ReportBucket(new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 11)),
                new ReportBucket(new DateOnly(2026, 1, 12), new DateOnly(2026, 1, 13)),
            ],
            buckets);
    }

    [Fact]
    public void Months_are_calendar_months_cut_to_the_period()
    {
        var buckets = ReportBuckets.Split(new ReportPeriod(new DateOnly(2024, 1, 31), new DateOnly(2024, 3, 1)), ReportInterval.Month);

        Assert.Equal(
            [
                new ReportBucket(new DateOnly(2024, 1, 31), new DateOnly(2024, 1, 31)),
                new ReportBucket(new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 29)),
                new ReportBucket(new DateOnly(2024, 3, 1), new DateOnly(2024, 3, 1)),
            ],
            buckets);
    }

    [Fact]
    public void Days_are_one_bucket_each_and_every_day_is_in_exactly_one_bucket()
    {
        var period = new ReportPeriod(new DateOnly(2026, 2, 20), new DateOnly(2026, 3, 10));

        foreach (var interval in Enum.GetValues<ReportInterval>())
        {
            var buckets = ReportBuckets.Split(period, interval);

            Assert.Equal(period.From, buckets[0].Start);
            Assert.Equal(period.To, buckets[^1].End);
            Assert.Equal(period.Days, buckets.Sum(bucket => bucket.End.DayNumber - bucket.Start.DayNumber + 1));
            Assert.Equal(period.Days, ReportBuckets.IndexByDay(buckets).Count);

            for (var index = 1; index < buckets.Count; index++)
            {
                Assert.Equal(buckets[index - 1].End.AddDays(1), buckets[index].Start);
            }
        }

        Assert.Equal(period.Days, ReportBuckets.Split(period, ReportInterval.Day).Count);
    }

    [Theory]
    [InlineData("2026-10-15", "2025-11-01")] // Thursday: the month so far and the eleven before
    [InlineData("2026-01-01", "2025-02-01")]
    public void The_monthly_trend_is_twelve_months_ending_on_the_day(string lastDay, string expectedStart)
    {
        var window = ReportBuckets.TrendWindow(D(lastDay), ReportInterval.Month);

        Assert.Equal(D(expectedStart), window.From);
        Assert.Equal(D(lastDay), window.To);
        Assert.Equal(12, ReportBuckets.Split(window, ReportInterval.Month).Count);
    }

    [Theory]
    [InlineData("2026-10-15")] // Thursday
    [InlineData("2026-10-12")] // Monday
    [InlineData("2026-10-18")] // Sunday
    public void The_weekly_trend_is_52_iso_weeks_ending_on_the_day(string lastDay)
    {
        var window = ReportBuckets.TrendWindow(D(lastDay), ReportInterval.Week);
        var weeks = ReportBuckets.Split(window, ReportInterval.Week);

        Assert.Equal(52, weeks.Count);
        Assert.All(weeks, week => Assert.Equal(DayOfWeek.Monday, week.Start.DayOfWeek));
        Assert.Equal(new DateOnly(2026, 10, 12), weeks[^1].Start);
        Assert.Equal(D(lastDay), weeks[^1].End);
    }

    [Fact]
    public void The_trend_has_no_daily_window() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ReportBuckets.TrendWindow(new DateOnly(2026, 10, 15), ReportInterval.Day));

    // ---- daily figures into rows -----------------------------------------------------------------------

    [Fact]
    public void Daily_figures_sum_into_their_bucket_and_the_total_is_the_sum_of_the_rows()
    {
        var period = new ReportPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 13));
        var daily = new[]
        {
            new DailySales(new DateOnly(2026, 1, 1), 1000m, 1, 2, 800m, 200m, 0m),
            new DailySales(new DateOnly(2026, 1, 4), 500m, 1, 1, 500m, 0m, 0m),
            new DailySales(new DateOnly(2026, 1, 5), 0m, 0, 1, 0m, 0m, 300m),
            new DailySales(new DateOnly(2026, 1, 13), 2000m, 2, 2, 1600m, 400m, 0m),
        };

        var report = GetSalesReportQueryHandler.Build(period, ReportInterval.Week, daily);

        Assert.Equal(3, report.Rows.Count);
        Assert.Equal(new SalesReportRow(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 4), 3, 2, 1500m, 1300m, 200m, 0m, 1500m), report.Rows[0]);
        Assert.Equal(new SalesReportRow(new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 11), 1, 0, 0m, 0m, 0m, 300m, -300m), report.Rows[1]);
        Assert.Equal(new SalesReportRow(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 13), 6, 4, 3500m, 2900m, 600m, 300m, 3200m), report.Totals);
        Assert.Equal("Week", report.GroupBy);
    }

    [Fact]
    public void A_day_outside_the_requested_days_is_an_error_not_a_silent_drop()
    {
        var period = new ReportPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2));

        Assert.Throws<InvalidOperationException>(() => GetSalesReportQueryHandler.Build(
            period,
            ReportInterval.Day,
            [new DailySales(new DateOnly(2026, 1, 3), 1m, 1, 1, 1m, 0m, 0m)]));
    }

    [Fact]
    public void The_status_distribution_lists_every_status_in_workflow_order_with_zeros()
    {
        var rows = GetAdminDashboardQueryHandler.StatusDistribution(
            new Dictionary<OrderStatus, int> { [OrderStatus.Paid] = 4, [OrderStatus.Refunded] = 1 });

        Assert.Equal(Enum.GetNames<OrderStatus>(), rows.Select(row => row.Status));
        Assert.Equal(4, rows.Single(row => row.Status == "Paid").Count);
        Assert.Equal(0, rows.Single(row => row.Status == "Shipped").Count);
        Assert.Equal("Awaiting payment", rows[0].Label);
        Assert.Equal("Shipped or ready for collection", rows.Single(row => row.Status == "Shipped").Label);
        Assert.Equal(5, rows.Sum(row => row.Count));
    }

    private static DateOnly D(string value) => DateOnly.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
