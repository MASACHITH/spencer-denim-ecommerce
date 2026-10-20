using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Application.Admin.Reporting.Contracts;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Application.Admin.Reporting;

/// <summary>
/// The customers report's chart (#186b): which grouping a period gets, how the reader's counts are laid on the
/// period's buckets, and that returning is buyers less first-time — per point and over the period.
/// </summary>
public sealed class CustomerBuyersTests
{
    private static ReportPeriod Period(int fromMonth, int fromDay, int toMonth, int toDay) =>
        new(new DateOnly(2026, fromMonth, fromDay), new DateOnly(2026, toMonth, toDay));

    [Theory]
    [InlineData(1, ReportInterval.Day)]
    [InlineData(31, ReportInterval.Day)]
    [InlineData(32, ReportInterval.Week)]
    [InlineData(182, ReportInterval.Week)]
    [InlineData(183, ReportInterval.Month)]
    [InlineData(366, ReportInterval.Month)]
    public void The_grouping_follows_the_period_s_length(int days, ReportInterval expected)
    {
        var from = new DateOnly(2024, 1, 1);
        var period = new ReportPeriod(from, from.AddDays(days - 1));

        Assert.Equal(days, period.Days);
        Assert.Equal(expected, GetCustomerBuyersQueryHandler.IntervalFor(period));
    }

    [Fact]
    public void Every_bucket_is_a_point_and_an_empty_one_is_zero()
    {
        var dto = GetCustomerBuyersQueryHandler.Build(Period(10, 1, 10, 3), ReportInterval.Day, new BuyerCounts([], [], 0));

        Assert.Equal("Day", dto.Interval);
        Assert.Equal(3, dto.Points.Count);
        Assert.All(dto.Points, point => Assert.Equal((0, 0, 0), (point.Buyers, point.FirstTimeBuyers, point.ReturningBuyers)));
        Assert.Equal((0, 0, 0), (dto.Buyers, dto.FirstTimeBuyers, dto.ReturningBuyers));
        Assert.Equal(new DateOnly(2026, 10, 1), dto.Points[0].Start);
        Assert.Equal(new DateOnly(2026, 10, 3), dto.Points[2].End);
    }

    [Fact]
    public void Returning_is_buyers_less_first_time_on_each_point()
    {
        var counts = new BuyerCounts(
            Buyers: [new BucketCount(0, 3), new BucketCount(2, 5)],
            FirstTime: [new BucketCount(0, 1), new BucketCount(2, 5)],
            DistinctBuyers: 7);

        var dto = GetCustomerBuyersQueryHandler.Build(Period(10, 1, 10, 3), ReportInterval.Day, counts);

        Assert.Equal((3, 1, 2), (dto.Points[0].Buyers, dto.Points[0].FirstTimeBuyers, dto.Points[0].ReturningBuyers));
        Assert.Equal((0, 0, 0), (dto.Points[1].Buyers, dto.Points[1].FirstTimeBuyers, dto.Points[1].ReturningBuyers));
        Assert.Equal((5, 5, 0), (dto.Points[2].Buyers, dto.Points[2].FirstTimeBuyers, dto.Points[2].ReturningBuyers));
    }

    /// <summary>
    /// The period's buyers are the reader's distinct count, not the points added up (one buyer, two days);
    /// its first-time buyers are the points added up (a first order happens once).
    /// </summary>
    [Fact]
    public void The_period_s_figures_are_distinct_buyers_and_the_sum_of_first_time()
    {
        var counts = new BuyerCounts(
            Buyers: [new BucketCount(0, 2), new BucketCount(1, 2)],
            FirstTime: [new BucketCount(0, 2)],
            DistinctBuyers: 3);

        var dto = GetCustomerBuyersQueryHandler.Build(Period(10, 1, 10, 2), ReportInterval.Day, counts);

        Assert.Equal(3, dto.Buyers);
        Assert.Equal(2, dto.FirstTimeBuyers);
        Assert.Equal(1, dto.ReturningBuyers);
    }

    /// <summary>A week bucket is the period's own cut: Thursday 1 October to Sunday 4, then Monday to Sunday.</summary>
    [Fact]
    public void Week_points_are_cut_to_the_period_as_the_sales_report_s_rows_are()
    {
        var counts = new BuyerCounts([new BucketCount(1, 4)], [new BucketCount(1, 1)], 4);

        var dto = GetCustomerBuyersQueryHandler.Build(Period(10, 1, 11, 15), ReportInterval.Week, counts);

        Assert.Equal("Week", dto.Interval);
        Assert.Equal(new DateOnly(2026, 10, 1), dto.Points[0].Start);
        Assert.Equal(new DateOnly(2026, 10, 4), dto.Points[0].End);
        Assert.Equal(new DateOnly(2026, 10, 5), dto.Points[1].Start);
        Assert.Equal(4, dto.Points[1].Buyers);
        Assert.Equal(3, dto.Points[1].ReturningBuyers);
        Assert.Equal(new DateOnly(2026, 11, 15), dto.Points[^1].End);
    }

    [Fact]
    public async Task The_handler_passes_the_report_s_search_and_the_chosen_grouping_to_the_reader()
    {
        var reader = new Reader();
        var handler = new GetCustomerBuyersQueryHandler(reader);

        var dto = await handler.HandleAsync(
            new GetCustomerBuyersQuery(Period(1, 1, 3, 31), "  nimali@ "),
            TestContext.Current.CancellationToken);

        Assert.Equal("NIMALI@", reader.Prefix);
        Assert.Equal(ReportInterval.Week, reader.Interval);
        Assert.Equal("Week", dto.Interval);
        Assert.Equal(new DateOnly(2026, 1, 1), dto.Period.From);
        Assert.Equal(90, dto.Period.Days);
    }

    private sealed class Reader : IReportingReader
    {
        public string? Prefix { get; private set; }

        public ReportInterval? Interval { get; private set; }

        public Task<BuyerCounts> ReadBuyerCountsAsync(ReportPeriod period, ReportInterval interval, string? emailPrefix, CancellationToken cancellationToken)
        {
            Prefix = emailPrefix;
            Interval = interval;
            return Task.FromResult(new BuyerCounts([], [], 0));
        }

        public Task<SalesTotals> ReadTotalsAsync(UtcRange range, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<DailySales>> ReadDailyAsync(UtcRange range, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<SalesGroupTotal>> ReadDepartmentSalesAsync(UtcRange range, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<SalesGroupTotal>> ReadTopCategoriesAsync(UtcRange range, int take, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProductSalesTotal>> ReadTopProductsAsync(UtcRange range, int take, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<int, decimal>> ReadProductRevenueAsync(UtcRange range, IReadOnlyCollection<int> productIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<OrderStatus, int>> ReadStatusCountsAsync(UtcRange range, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PaymentMethodRow>> ReadPaymentMethodsAsync(UtcRange range, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BuyersByDistrict> ReadBuyersByDistrictAsync(UtcRange range, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LowStockDto> ReadLowStockAsync(int take, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<InventoryReportPage> ReadInventoryAsync(InventoryReportFilter filter, int page, int? pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<(IReadOnlyList<CustomerReportRow> Rows, int TotalCount, decimal TotalRevenue)> ReadCustomersAsync(UtcRange range, string? emailPrefix, int page, int? pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
