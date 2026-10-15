using SpencerDenim.Application.Admin.Reporting.Contracts;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Admin.Reporting;

/// <summary>
/// The dashboard's and the reports' reads (ADM-04–08, ADM-19). <b>Every figure is aggregated in SQL</b>
/// (doc 08 §8.2: "All sales widgets query live"); the handlers only arrange, compare and label what comes back.
/// </summary>
/// <remarks>
/// <para>
/// <b>The revenue rule is doc 08 §8.1's, in one sentence: an order counts on the Colombo day of its
/// <c>PaidAtUtc</c>, for its whole <c>GrandTotal</c>, whatever its status is now.</b> So a refunded order
/// still counts on the day it was paid (doc 11 P16: "Includes orders later refunded"), a bank transfer
/// approved after expiry counts on the approval day (doc 11 P16's first edge case — the approval sets
/// <c>PaidAtUtc</c>), and an order that was never paid — pending, cancelled or expired — has no
/// <c>PaidAtUtc</c> and never counts. This is the same rule the customer list's lifetime spend uses
/// (<c>AdminCustomerReader</c>), so the two screens agree.
/// </para>
/// <para>
/// <b>Refunds are subtracted only from net revenue, by the day they completed, and only when they refund the
/// settlement payment</b> (doc 08 §8.1: "completed settlement refunds"). Refunding a payment exception —
/// money taken for an order that was never settled — returns money that was never revenue.
/// </para>
/// </remarks>
public interface IReportingReader
{
    /// <summary>Gross revenue, paid orders, placed orders and completed refunds over one range.</summary>
    Task<SalesTotals> ReadTotalsAsync(UtcRange range, CancellationToken cancellationToken);

    /// <summary>The same figures and the items/delivery split, one row per Colombo day that has any.</summary>
    Task<IReadOnlyList<DailySales>> ReadDailyAsync(UtcRange range, CancellationToken cancellationToken);

    /// <summary>Line revenue (after promo, no delivery) on orders paid in the range, by department at purchase.</summary>
    Task<IReadOnlyList<SalesGroupTotal>> ReadDepartmentSalesAsync(UtcRange range, CancellationToken cancellationToken);

    /// <summary>The same by category at purchase, highest first, at most <paramref name="take"/>.</summary>
    Task<IReadOnlyList<SalesGroupTotal>> ReadTopCategoriesAsync(UtcRange range, int take, CancellationToken cancellationToken);

    /// <summary>The same by product, highest first, at most <paramref name="take"/>.</summary>
    Task<IReadOnlyList<ProductSalesTotal>> ReadTopProductsAsync(UtcRange range, int take, CancellationToken cancellationToken);

    /// <summary>Line revenue for each of <paramref name="productIds"/> over a range; a product that sold nothing is absent.</summary>
    Task<IReadOnlyDictionary<int, decimal>> ReadProductRevenueAsync(
        UtcRange range,
        IReadOnlyCollection<int> productIds,
        CancellationToken cancellationToken);

    /// <summary>Orders placed in the range, counted by their status now.</summary>
    Task<IReadOnlyDictionary<OrderStatus, int>> ReadStatusCountsAsync(UtcRange range, CancellationToken cancellationToken);

    /// <summary>Orders paid in the range, by payment method.</summary>
    Task<IReadOnlyList<PaymentMethodRow>> ReadPaymentMethodsAsync(UtcRange range, CancellationToken cancellationToken);

    /// <summary>Distinct buyer emails on orders paid in the range, by delivery district.</summary>
    Task<BuyersByDistrict> ReadBuyersByDistrictAsync(UtcRange range, CancellationToken cancellationToken);

    /// <summary>Active variants with 0 &lt; available ≤ threshold, lowest first, at most <paramref name="take"/>.</summary>
    Task<LowStockDto> ReadLowStockAsync(int take, CancellationToken cancellationToken);

    /// <summary>
    /// One page of the inventory report, or every matching row when <paramref name="pageSize"/> is null (the export).
    /// </summary>
    Task<InventoryReportPage> ReadInventoryAsync(
        InventoryReportFilter filter,
        int page,
        int? pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// One page of the customers report, or every buyer when <paramref name="pageSize"/> is null (the export).
    /// </summary>
    /// <param name="emailPrefix">The start of an email address, upper-cased, or null.</param>
    Task<(IReadOnlyList<CustomerReportRow> Rows, int TotalCount, decimal TotalRevenue)> ReadCustomersAsync(
        UtcRange range,
        string? emailPrefix,
        int page,
        int? pageSize,
        CancellationToken cancellationToken);
}

/// <summary>A half-open UTC range: <c>&gt;= FromUtc</c> and <c>&lt; ToUtcExclusive</c>.</summary>
public sealed record UtcRange(DateTime FromUtc, DateTime ToUtcExclusive)
{
    public static UtcRange Of(ReportPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);

        return new UtcRange(period.FromUtc, period.ToUtcExclusive);
    }
}

/// <summary>A range's headline figures.</summary>
public sealed record SalesTotals(decimal GrossRevenue, int PaidOrders, int Orders, decimal RefundsCompleted);

/// <summary>One Colombo day's figures. Each figure is on its own date: paid, placed or refund-completed.</summary>
public sealed record DailySales(
    DateOnly Day,
    decimal GrossRevenue,
    int PaidOrders,
    int Orders,
    decimal ItemsRevenue,
    decimal DeliveryRevenue,
    decimal RefundsCompleted);

/// <summary>A department's or a category's line sales.</summary>
public sealed record SalesGroupTotal(int Id, string Name, int Units, decimal Revenue);

/// <summary>A product's line sales.</summary>
public sealed record ProductSalesTotal(int ProductId, string Name, string ProductCode, int Units, decimal Revenue);
