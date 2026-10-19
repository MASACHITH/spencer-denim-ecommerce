namespace SpencerDenim.Application.Admin.Reporting.Contracts;

/// <summary>A period as the screen reads it: Colombo days, both included.</summary>
public sealed record ReportPeriodDto(DateOnly From, DateOnly To, int Days)
{
    public static ReportPeriodDto Of(ReportPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);

        return new ReportPeriodDto(period.From, period.To, period.Days);
    }
}

// ---------------------------------------------------------------------------------------------------------
// Dashboard (ADM-04, ADM-05, ADM-07, ADM-08; Dashboard.View)
// ---------------------------------------------------------------------------------------------------------

/// <summary>
/// Everything on the dashboard that depends on the chosen period (doc 11 P16 "KPI and widget definitions").
/// The trend and the low-stock list are separate requests: the trend has its own twelve-month window, and
/// low stock is live and has no period at all.
/// </summary>
/// <param name="Departments">
/// Sales by department: Σ order-line totals after promo, <b>excluding delivery</b>, on orders paid in the
/// period, by the department each line was bought under. Every department is listed, a zero included.
/// </param>
/// <param name="TopCategories">The ten categories with the most such revenue, by the category at purchase.</param>
/// <param name="TopProducts">The ten products with the most such revenue, each compared with the previous period.</param>
/// <param name="OrderStatuses">Orders <b>placed</b> in the period, by their status now. Sums to the Orders KPI.</param>
/// <param name="PaymentMethods">Orders <b>paid</b> in the period, by payment method. Sums to the Paid orders KPI.</param>
/// <param name="BuyersByDistrict">Distinct customer emails on orders paid in the period, by delivery district.</param>
public sealed record AdminDashboardDto(
    ReportPeriodDto Period,
    ReportPeriodDto PreviousPeriod,
    string Currency,
    DashboardKpis Kpis,
    IReadOnlyList<SalesShareRow> Departments,
    IReadOnlyList<SalesShareRow> TopCategories,
    IReadOnlyList<TopProductRow> TopProducts,
    IReadOnlyList<OrderStatusCount> OrderStatuses,
    IReadOnlyList<PaymentMethodRow> PaymentMethods,
    BuyersByDistrict BuyersByDistrict);

/// <summary>
/// The KPI cards (doc 11 P16; doc 08 §8.1), each against the same-length period immediately before.
/// </summary>
/// <param name="GrossRevenue">
/// Σ <c>GrandTotal</c> of orders by <b>paid date</b>, delivery included, <b>later refunds included</b>.
/// </param>
/// <param name="RefundsCompleted">
/// Settlement refunds completed in the period, by completion date (what doc 08 §8.1 subtracts).
/// </param>
/// <param name="NetRevenue">Gross revenue − refunds completed in the period.</param>
/// <param name="Orders">Orders <b>placed</b> in the period, paid or not.</param>
/// <param name="PaidOrders">Orders <b>paid</b> in the period — the divisor of the average.</param>
/// <param name="AverageOrderValue">Gross revenue ÷ paid orders; absent when nothing was paid.</param>
public sealed record DashboardKpis(
    ComparedFigure GrossRevenue,
    ComparedFigure RefundsCompleted,
    ComparedFigure NetRevenue,
    ComparedFigure Orders,
    ComparedFigure PaidOrders,
    ComparedFigure AverageOrderValue);

/// <summary>A department or category and what it sold (excluding delivery).</summary>
/// <param name="SharePercent">Its share of the period's line revenue, to one decimal place; 0 when nothing sold.</param>
public sealed record SalesShareRow(int Id, string Name, int Units, decimal Revenue, decimal SharePercent);

/// <summary>A product and what it sold, with its revenue compared with the previous period.</summary>
/// <param name="Name">The product's current name — its line snapshots may carry older ones.</param>
public sealed record TopProductRow(int ProductId, string Name, string ProductCode, int Units, ComparedFigure Revenue);

/// <summary>How many orders placed in the period are in one status now.</summary>
/// <param name="Status">The status's name — never its number, which is the database's (doc 08 §1.4).</param>
public sealed record OrderStatusCount(string Status, string Label, int Count);

/// <summary>Orders paid in the period through one payment method.</summary>
public sealed record PaymentMethodRow(string Code, string Name, int PaidOrders, decimal Revenue);

/// <summary>
/// "Customers by district" (doc 11 P16): <b>where buyers had their orders delivered</b>, not the default
/// district on a customer account (that is the Customers screen's column, a different figure).
/// </summary>
/// <param name="DistinctBuyers">
/// Distinct customer emails over the whole period. <b>Not the sum of the rows</b>: one buyer who had orders
/// delivered to two districts is counted once here and once in each district.
/// </param>
public sealed record BuyersByDistrict(int DistinctBuyers, IReadOnlyList<DistrictBuyersRow> Districts);

/// <summary>One delivery district: distinct buyers, their paid orders and the gross revenue from them.</summary>
public sealed record DistrictBuyersRow(int DistrictId, string Name, int Buyers, int PaidOrders, decimal Revenue);

/// <summary>The Revenue &amp; Orders trend (ADM-04): twelve months, by ISO week or by month.</summary>
/// <param name="Interval"><c>Week</c> or <c>Month</c>.</param>
/// <param name="Window">The days the points cover; the last point is the part of its week or month so far.</param>
public sealed record SalesTrendDto(string Interval, ReportPeriodDto Window, string Currency, IReadOnlyList<SalesTrendPoint> Points);

/// <summary>One week or month: gross revenue and paid orders by paid date, orders by placed date.</summary>
public sealed record SalesTrendPoint(DateOnly Start, DateOnly End, decimal GrossRevenue, int PaidOrders, int Orders);

/// <summary>
/// Low stock (ADM-08; doc 11 P16 "Live: 0 &lt; available ≤ threshold"), lowest first. A variant with nothing
/// available is out of stock, not low, and is on the inventory report instead.
/// </summary>
/// <param name="DefaultThreshold">The threshold a variant without its own uses.</param>
/// <param name="TotalCount">Every low variant, not only the rows shown.</param>
public sealed record LowStockDto(int DefaultThreshold, int TotalCount, IReadOnlyList<LowStockRow> Rows);

/// <summary>One low variant.</summary>
/// <param name="Threshold">The threshold that applied: the variant's own, or the default.</param>
public sealed record LowStockRow(
    int VariantId,
    int ProductId,
    string ProductName,
    string ProductCode,
    string Sku,
    string Colour,
    string Size,
    int Available,
    int Threshold);

// ---------------------------------------------------------------------------------------------------------
// Reports (ADM-19; Reports.View, CSV Reports.Export)
// ---------------------------------------------------------------------------------------------------------

/// <summary>The sales report: one row per day, ISO week or month of the period, and a total.</summary>
/// <param name="GroupBy"><c>Day</c>, <c>Week</c> or <c>Month</c>.</param>
public sealed record SalesReportDto(
    ReportPeriodDto Period,
    string GroupBy,
    string Currency,
    IReadOnlyList<SalesReportRow> Rows,
    SalesReportRow Totals);

/// <summary>
/// One row of the sales report. <b><see cref="ItemsRevenue"/> + <see cref="DeliveryRevenue"/> =
/// <see cref="GrossRevenue"/></b> for every paid order, which is why the department figures (items only) do
/// not add up to gross revenue and the report shows where the difference is.
/// </summary>
/// <param name="Orders">Orders placed on these days.</param>
/// <param name="PaidOrders">Orders paid on these days.</param>
/// <param name="GrossRevenue">Σ <c>GrandTotal</c> of orders paid on these days, later refunds included.</param>
/// <param name="ItemsRevenue">Σ order-line totals after promo of those orders.</param>
/// <param name="DeliveryRevenue">Σ delivery fee − delivery discount of those orders.</param>
/// <param name="RefundsCompleted">Settlement refunds completed on these days, whenever the order was paid.</param>
/// <param name="NetRevenue">Gross revenue − refunds completed.</param>
public sealed record SalesReportRow(
    DateOnly Start,
    DateOnly End,
    int Orders,
    int PaidOrders,
    decimal GrossRevenue,
    decimal ItemsRevenue,
    decimal DeliveryRevenue,
    decimal RefundsCompleted,
    decimal NetRevenue);

/// <summary>Which variants the inventory report lists.</summary>
public enum InventoryStockFilter
{
    All,

    InStock,

    LowStock,

    OutOfStock,
}

/// <summary>The inventory report's filters, shared by the screen and the export.</summary>
/// <param name="Search">Part of a product name, or the start of a SKU or product code; null for every variant.</param>
public sealed record InventoryReportFilter(string? Search, InventoryStockFilter Status)
{
    public const int MaxSearchLength = 100;

    /// <summary>The trimmed search, cut to <see cref="MaxSearchLength"/>, or null.</summary>
    public string? NormalisedSearch
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Search))
            {
                return null;
            }

            var trimmed = Search.Trim();

            return trimmed.Length > MaxSearchLength ? trimmed[..MaxSearchLength] : trimmed;
        }
    }
}

/// <summary>One page of the inventory report: active variants of products that are not deleted, live.</summary>
/// <param name="Counts">Over every active variant, ignoring the filters.</param>
public sealed record InventoryReportPage(
    IReadOnlyList<InventoryReportRow> Rows,
    int Page,
    int PageSize,
    int TotalCount,
    int DefaultThreshold,
    InventoryStatusCounts Counts);

/// <summary>How many active variants are in stock, low and out of stock.</summary>
public sealed record InventoryStatusCounts(int InStock, int LowStock, int OutOfStock);

/// <summary>One variant's stock now.</summary>
/// <param name="StockStatus"><c>InStock</c>, <c>LowStock</c> or <c>OutOfStock</c>.</param>
public sealed record InventoryReportRow(
    int VariantId,
    int ProductId,
    string ProductName,
    string ProductCode,
    string Department,
    string Sku,
    string Colour,
    string Size,
    int OnHand,
    int Reserved,
    int Available,
    int Threshold,
    bool ThresholdIsDefault,
    string StockStatus);

/// <summary>
/// The customers report: everyone who paid for an order in the period, by email address, highest spend
/// first — guests included, because a guest is a customer who did not register.
/// </summary>
/// <param name="TotalRevenue">Σ of every buyer's gross revenue: the period's gross revenue.</param>
public sealed record CustomerReportPage(
    ReportPeriodDto Period,
    string Currency,
    IReadOnlyList<CustomerReportRow> Rows,
    int Page,
    int PageSize,
    int TotalCount,
    decimal TotalRevenue);

/// <summary>One buyer in the period.</summary>
/// <param name="Email">As typed on their most recent paid order in the period.</param>
/// <param name="Name">The bill-to name on that order.</param>
/// <param name="District">That order's delivery district.</param>
/// <param name="HasAccount">Whether any of these orders is on a customer account.</param>
/// <param name="FirstPaidOn">The Colombo day of their first paid order in the period.</param>
/// <param name="LastPaidOn">The Colombo day of their last.</param>
public sealed record CustomerReportRow(
    string Email,
    string Name,
    string District,
    bool HasAccount,
    int PaidOrders,
    decimal GrossRevenue,
    DateOnly FirstPaidOn,
    DateOnly LastPaidOn);

/// <summary>
/// The customers report's chart (#186b): buyers per day, ISO week or month of the period, each split into
/// first-time and returning. The same population as <see cref="CustomerReportPage"/> — the same period, the
/// same email search — so <see cref="Buyers"/> is that page's <c>TotalCount</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A buyer</b> is a distinct email address (compared as <c>EmailNormalized</c>, so case does not matter)
/// on at least one order <b>paid</b> in the days counted: an order with a <c>PaidAtUtc</c> there. As
/// everywhere in the reports, an order refunded later keeps its paid date and still counts; an order never
/// paid — awaiting payment, cancelled or expired — does not.
/// </para>
/// <para>
/// <b>First-time</b>: the buyer's first paid order <b>in this shop, ever</b>, is in those days.
/// <b>Returning</b>: the buyer had a paid order here <b>before</b> those days began. Every buyer is exactly
/// one of the two. The shop's history starts when it opened, so in its first weeks nearly everyone is
/// first-time — which is the truth, not a gap.
/// </para>
/// </remarks>
/// <param name="Interval"><c>Day</c>, <c>Week</c> or <c>Month</c>, chosen from the period's length.</param>
/// <param name="Buyers">Distinct buyers over the whole period. <b>Not the sum of the points</b>: a buyer who
/// paid in two weeks is one buyer here and one in each week.</param>
/// <param name="FirstTimeBuyers">Buyers whose first paid order ever is in the period. This one <b>is</b> the sum
/// of the points' first-time figures: a first order happens once.</param>
/// <param name="ReturningBuyers">Buyers who had paid before the period began: <c>Buyers − FirstTimeBuyers</c>.</param>
public sealed record CustomerBuyersDto(
    ReportPeriodDto Period,
    string Interval,
    int Buyers,
    int FirstTimeBuyers,
    int ReturningBuyers,
    IReadOnlyList<CustomerBuyersPoint> Points);

/// <summary>
/// One day, ISO week or month (cut to the period, as the sales report's rows are). <c>FirstTimeBuyers</c> +
/// <c>ReturningBuyers</c> = <c>Buyers</c>; returning here means paid before <b>this point</b> began, which can
/// be earlier in the same period.
/// </summary>
public sealed record CustomerBuyersPoint(DateOnly Start, DateOnly End, int Buyers, int FirstTimeBuyers, int ReturningBuyers);

/// <summary>A report export, ready to send.</summary>
public sealed record ReportExportFile(byte[] Content, string FileName, int RowCount);
