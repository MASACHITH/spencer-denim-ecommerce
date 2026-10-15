using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.Reporting.Contracts;
using SpencerDenim.Application.Sales.Orders.Contracts;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Admin.Reporting;

/// <summary>The dashboard's period widgets (ADM-04, ADM-05, ADM-07, ADM-08; <c>Dashboard.View</c>).</summary>
public sealed record GetAdminDashboardQuery(ReportPeriod Period);

/// <summary>The Revenue &amp; Orders trend (ADM-04): twelve months to <paramref name="LastDay"/>, by week or month.</summary>
public sealed record GetSalesTrendQuery(ReportInterval Interval, DateOnly LastDay);

/// <summary>The low-stock list (ADM-08).</summary>
public sealed record GetLowStockQuery(int Take);

public sealed class GetAdminDashboardQueryHandler(IReportingReader reader)
    : IQueryHandler<GetAdminDashboardQuery, AdminDashboardDto>
{
    /// <summary>How many categories and products the "top" widgets show.</summary>
    public const int TopCount = 10;

    public async Task<AdminDashboardDto> HandleAsync(GetAdminDashboardQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var period = query.Period;
        var previousPeriod = period.Previous;
        var current = UtcRange.Of(period);
        var previous = UtcRange.Of(previousPeriod);

        // One DbContext, so one query at a time: these run in sequence, each aggregated in SQL.
        var now = await reader.ReadTotalsAsync(current, cancellationToken);
        var before = await reader.ReadTotalsAsync(previous, cancellationToken);

        var departments = await reader.ReadDepartmentSalesAsync(current, cancellationToken);
        var lineRevenue = departments.Sum(department => department.Revenue);

        var categories = await reader.ReadTopCategoriesAsync(current, TopCount, cancellationToken);

        var products = await reader.ReadTopProductsAsync(current, TopCount, cancellationToken);
        var previousProductRevenue = products.Count == 0
            ? new Dictionary<int, decimal>()
            : await reader.ReadProductRevenueAsync(
                previous,
                products.Select(product => product.ProductId).ToList(),
                cancellationToken);

        var statuses = await reader.ReadStatusCountsAsync(current, cancellationToken);
        var paymentMethods = await reader.ReadPaymentMethodsAsync(current, cancellationToken);
        var buyers = await reader.ReadBuyersByDistrictAsync(current, cancellationToken);

        return new AdminDashboardDto(
            ReportPeriodDto.Of(period),
            ReportPeriodDto.Of(previousPeriod),
            SalesCurrency.Lkr,
            Kpis(now, before),
            departments.Select(department => Share(department, lineRevenue)).ToList(),
            categories.Select(category => Share(category, lineRevenue)).ToList(),
            products
                .Select(product => new TopProductRow(
                    product.ProductId,
                    product.Name,
                    product.ProductCode,
                    product.Units,
                    ComparedFigure.Of(
                        product.Revenue,
                        previousProductRevenue.TryGetValue(product.ProductId, out var earlier) ? earlier : 0m)))
                .ToList(),
            StatusDistribution(statuses),
            paymentMethods,
            buyers);
    }

    /// <summary>The KPI cards, each this period against the previous one.</summary>
    public static DashboardKpis Kpis(SalesTotals now, SalesTotals before)
    {
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(before);

        return new DashboardKpis(
            ComparedFigure.Of(now.GrossRevenue, before.GrossRevenue),
            ComparedFigure.Of(now.RefundsCompleted, before.RefundsCompleted),
            ComparedFigure.Of(now.GrossRevenue - now.RefundsCompleted, before.GrossRevenue - before.RefundsCompleted),
            ComparedFigure.Of(now.Orders, before.Orders),
            ComparedFigure.Of(now.PaidOrders, before.PaidOrders),
            ComparedFigure.Of(
                ComparedFigure.Average(now.GrossRevenue, now.PaidOrders),
                ComparedFigure.Average(before.GrossRevenue, before.PaidOrders)));
    }

    /// <summary>
    /// Every status, in workflow order, with its count — a zero included, so the widget never implies a status
    /// does not exist. The labels name both readings of Shipped and Delivered, because a store pickup order
    /// in those statuses is "Ready for collection" and "Collected" (doc 11 P9's pickup variant).
    /// </summary>
    public static IReadOnlyList<OrderStatusCount> StatusDistribution(IReadOnlyDictionary<OrderStatus, int> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        return Enum.GetValues<OrderStatus>()
            .Select(status => new OrderStatusCount(
                status.ToString(),
                status switch
                {
                    OrderStatus.Shipped => "Shipped or ready for collection",
                    OrderStatus.Delivered => "Delivered or collected",
                    _ => OrderStatusLabels.For(status, isPickup: false),
                },
                counts.TryGetValue(status, out var count) ? count : 0))
            .ToList();
    }

    private static SalesShareRow Share(SalesGroupTotal group, decimal total) =>
        new(
            group.Id,
            group.Name,
            group.Units,
            group.Revenue,
            total == 0m ? 0m : Math.Round(group.Revenue / total * 100m, 1, MidpointRounding.AwayFromZero));
}

public sealed class GetSalesTrendQueryHandler(IReportingReader reader)
    : IQueryHandler<GetSalesTrendQuery, SalesTrendDto>
{
    public async Task<SalesTrendDto> HandleAsync(GetSalesTrendQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var window = ReportBuckets.TrendWindow(query.LastDay, query.Interval);
        var buckets = ReportBuckets.Split(window, query.Interval);
        var daily = await reader.ReadDailyAsync(UtcRange.Of(window), cancellationToken);

        var points = SalesReportBuilder.Sum(buckets, daily)
            .Select(row => new SalesTrendPoint(row.Start, row.End, row.GrossRevenue, row.PaidOrders, row.Orders))
            .ToList();

        return new SalesTrendDto(query.Interval.ToString(), ReportPeriodDto.Of(window), SalesCurrency.Lkr, points);
    }
}

public sealed class GetLowStockQueryHandler(IReportingReader reader) : IQueryHandler<GetLowStockQuery, LowStockDto>
{
    /// <summary>How many rows the widget shows; the total is always reported.</summary>
    public const int DefaultTake = 10;

    public Task<LowStockDto> HandleAsync(GetLowStockQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return reader.ReadLowStockAsync(Math.Clamp(query.Take, 1, 50), cancellationToken);
    }
}

/// <summary>Sums daily figures into buckets — shared by the trend and the sales report, so they cannot disagree.</summary>
public static class SalesReportBuilder
{
    public static IReadOnlyList<SalesReportRow> Sum(IReadOnlyList<ReportBucket> buckets, IReadOnlyList<DailySales> daily)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        ArgumentNullException.ThrowIfNull(daily);

        var index = ReportBuckets.IndexByDay(buckets);
        var sums = new Accumulator[buckets.Count];

        foreach (var day in daily)
        {
            // A day outside the buckets cannot come back from a reader asked for exactly these days; if one
            // ever did, dropping it silently would make the rows stop adding up to the KPI, so it throws.
            if (!index.TryGetValue(day.Day, out var position))
            {
                throw new InvalidOperationException($"The reader returned {day.Day:yyyy-MM-dd}, outside the requested days.");
            }

            sums[position] = sums[position].Add(day);
        }

        return buckets.Select((bucket, position) => sums[position].Row(bucket.Start, bucket.End)).ToList();
    }

    /// <summary>The totals row over every row.</summary>
    public static SalesReportRow Total(ReportPeriod period, IReadOnlyList<SalesReportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(rows);

        return new SalesReportRow(
            period.From,
            period.To,
            rows.Sum(row => row.Orders),
            rows.Sum(row => row.PaidOrders),
            rows.Sum(row => row.GrossRevenue),
            rows.Sum(row => row.ItemsRevenue),
            rows.Sum(row => row.DeliveryRevenue),
            rows.Sum(row => row.RefundsCompleted),
            rows.Sum(row => row.NetRevenue));
    }

    private readonly record struct Accumulator(
        decimal Gross,
        int Paid,
        int Placed,
        decimal Items,
        decimal Delivery,
        decimal Refunds)
    {
        public Accumulator Add(DailySales day) =>
            new(
                Gross + day.GrossRevenue,
                Paid + day.PaidOrders,
                Placed + day.Orders,
                Items + day.ItemsRevenue,
                Delivery + day.DeliveryRevenue,
                Refunds + day.RefundsCompleted);

        public SalesReportRow Row(DateOnly start, DateOnly end) =>
            new(start, end, Placed, Paid, Gross, Items, Delivery, Refunds, Gross - Refunds);
    }
}
