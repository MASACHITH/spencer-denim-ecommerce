using System.Data;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Application.Admin.Reporting.Contracts;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Inventory;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Admin.Reporting;

/// <summary>
/// The dashboard's and the reports' SQL (ADM-04–08, ADM-19; doc 08 §8.2). See <see cref="IReportingReader"/>
/// for the revenue rule; this class is where it becomes a <c>WHERE</c> clause.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every figure is a <c>GROUP BY</c> or a <c>SUM</c> on the server.</b> Nothing loads orders to add them up
/// in memory: a twelve-month window is up to ~30k orders and ~90k lines (doc 08 §8.2), and the per-day rows
/// that come back are at most 366.
/// </para>
/// <para>
/// <b>Filtered and ordered on the entity, projected last</b> (#126, and the promo-code list's 500): every
/// <c>Where</c> and <c>OrderBy</c> is on <c>Order</c>, <c>OrderLine</c>, <c>StockLevel</c> or
/// <c>ProductVariant</c>, and the positional records are built only in the final <c>Select</c>.
/// </para>
/// <para>
/// <b>The Colombo day of an instant is <c>CONVERT(date, DATEADD(minute, 330, x))</c></b> — doc 08 §1.4's own
/// conversion, written in LINQ as <c>x.AddMinutes(330).Date</c>. The range itself is compared on the raw
/// UTC column (<c>&gt;= FromUtc AND &lt; ToUtcExclusive</c>), so <c>IX_Orders_Paid</c> can seek it;
/// <c>ReportingQueryPlanTests</c> proves the paid-date queries can be answered from that filtered index.
/// </para>
/// <para>
/// <b>Read committed, no transaction, no isolation hint.</b> These are reads of the shop's own figures; a
/// connection left at another isolation level poisons the pool (the <c>CartStore</c> leak, W3-07).
/// </para>
/// </remarks>
internal sealed class ReportingReader(AppDbContext dbContext, IOptions<CatalogOptions> options) : IReportingReader
{
    /// <summary>Colombo is UTC+05:30: doc 08 §1.4's <c>DATEADD(minute, 330, x)</c>.</summary>
    private const int ColomboOffsetMinutes = 330;

    private readonly int _defaultThreshold = options.Value.DefaultLowStockThreshold;

    public async Task<SalesTotals> ReadTotalsAsync(UtcRange range, CancellationToken cancellationToken)
    {
        // One row, or none when nothing was paid (GROUP BY over no rows returns no group).
        var paid = await PaidTotalsQuery(range).SingleOrDefaultAsync(cancellationToken);

        var gross = paid?.Gross ?? 0m;
        var paidOrders = paid?.PaidOrders ?? 0;
        var orders = await PlacedIn(range).CountAsync(cancellationToken);
        var refunds = await SettlementRefundsIn(range).SumAsync(refund => refund.Amount, cancellationToken);

        return new SalesTotals(gross, paidOrders, orders, refunds);
    }

    public async Task<IReadOnlyList<DailySales>> ReadDailyAsync(UtcRange range, CancellationToken cancellationToken)
    {
        var paid = await PaidDailyQuery(range).ToListAsync(cancellationToken);

        var placed = await PlacedIn(range)
            .GroupBy(order => order.PlacedAtUtc.AddMinutes(ColomboOffsetMinutes).Date)
            .Select(day => new { Day = day.Key, Orders = day.Count() })
            .ToListAsync(cancellationToken);

        var items = await LinesPaidIn(range)
            .GroupBy(line => line.Order!.PaidAtUtc!.Value.AddMinutes(ColomboOffsetMinutes).Date)
            .Select(day => new { Day = day.Key, Items = day.Sum(line => line.LineTotal) })
            .ToListAsync(cancellationToken);

        var refunds = await RefundsDailyQuery(range).ToListAsync(cancellationToken);

        var days = paid.Select(row => row.Day)
            .Concat(placed.Select(row => row.Day))
            .Concat(items.Select(row => row.Day))
            .Concat(refunds.Select(row => row.Day))
            .Distinct()
            .Order();

        var paidByDay = paid.ToDictionary(row => row.Day);
        var placedByDay = placed.ToDictionary(row => row.Day, row => row.Orders);
        var itemsByDay = items.ToDictionary(row => row.Day, row => row.Items);
        var refundsByDay = refunds.ToDictionary(row => row.Day, row => row.Amount);

        return days
            .Select(day => new DailySales(
                DateOnly.FromDateTime(day),
                paidByDay.TryGetValue(day, out var money) ? money.Gross : 0m,
                paidByDay.TryGetValue(day, out var counted) ? counted.PaidOrders : 0,
                placedByDay.GetValueOrDefault(day),
                itemsByDay.GetValueOrDefault(day),
                paidByDay.TryGetValue(day, out var delivered) ? delivered.Delivery : 0m,
                refundsByDay.GetValueOrDefault(day)))
            .ToList();
    }

    public async Task<IReadOnlyList<SalesGroupTotal>> ReadDepartmentSalesAsync(
        UtcRange range,
        CancellationToken cancellationToken)
    {
        var sold = await DepartmentSalesQuery(range).ToDictionaryAsync(row => row.Id, cancellationToken);

        var departments = await dbContext.Departments
            .AsNoTracking()
            .OrderBy(department => department.SortOrder)
            .ThenBy(department => department.Id)
            .Select(department => new { department.Id, department.Name })
            .ToListAsync(cancellationToken);

        // Every department, a zero included — and any department id a line carries that is not in the table
        // (there is a foreign key, so none can be), rather than dropping its revenue.
        return departments
            .Select(department => sold.TryGetValue(department.Id, out var row)
                ? new SalesGroupTotal(department.Id, department.Name, row.Units, row.Revenue)
                : new SalesGroupTotal(department.Id, department.Name, 0, 0m))
            .ToList();
    }

    public async Task<IReadOnlyList<SalesGroupTotal>> ReadTopCategoriesAsync(
        UtcRange range,
        int take,
        CancellationToken cancellationToken)
    {
        var top = await LinesPaidIn(range)
            .GroupBy(line => line.CategoryId)
            .Select(group => new
            {
                Id = group.Key,
                Units = group.Sum(line => (int)line.Quantity),
                Revenue = group.Sum(line => line.LineTotal),
            })
            .OrderByDescending(row => row.Revenue)
            .ThenBy(row => row.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        var ids = top.Select(row => row.Id).ToList();

        // The category at purchase may since have been removed (soft delete) — its sales still happened.
        var names = await dbContext.Categories
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(category => ids.Contains(category.Id))
            .Select(category => new
            {
                category.Id,
                Department = category.Department!.Name,
                Parent = category.ParentCategory != null ? category.ParentCategory.Name : null,
                category.Name,
            })
            .ToDictionaryAsync(category => category.Id, cancellationToken);

        return top
            .Select(row => new SalesGroupTotal(
                row.Id,
                names.TryGetValue(row.Id, out var name) ? CategoryPath(name.Department, name.Parent, name.Name) : $"Category {row.Id}",
                row.Units,
                row.Revenue))
            .ToList();
    }

    public async Task<IReadOnlyList<ProductSalesTotal>> ReadTopProductsAsync(
        UtcRange range,
        int take,
        CancellationToken cancellationToken)
    {
        var top = await LinesPaidIn(range)
            .GroupBy(line => line.ProductId)
            .Select(group => new
            {
                Id = group.Key,
                Units = group.Sum(line => (int)line.Quantity),
                Revenue = group.Sum(line => line.LineTotal),
            })
            .OrderByDescending(row => row.Revenue)
            .ThenBy(row => row.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        var ids = top.Select(row => row.Id).ToList();

        var products = await dbContext.Products
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(product => ids.Contains(product.Id))
            .Select(product => new { product.Id, product.Name, product.ProductCode })
            .ToDictionaryAsync(product => product.Id, cancellationToken);

        return top
            .Select(row => products.TryGetValue(row.Id, out var product)
                ? new ProductSalesTotal(row.Id, product.Name, product.ProductCode, row.Units, row.Revenue)
                : new ProductSalesTotal(row.Id, $"Product {row.Id}", string.Empty, row.Units, row.Revenue))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<int, decimal>> ReadProductRevenueAsync(
        UtcRange range,
        IReadOnlyCollection<int> productIds,
        CancellationToken cancellationToken)
    {
        var ids = productIds.ToList();

        return await LinesPaidIn(range)
            .Where(line => ids.Contains(line.ProductId))
            .GroupBy(line => line.ProductId)
            .Select(group => new { Id = group.Key, Revenue = group.Sum(line => line.LineTotal) })
            .ToDictionaryAsync(row => row.Id, row => row.Revenue, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<OrderStatus, int>> ReadStatusCountsAsync(
        UtcRange range,
        CancellationToken cancellationToken) =>
        await PlacedIn(range)
            .GroupBy(order => order.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Status, row => row.Count, cancellationToken);

    public async Task<IReadOnlyList<PaymentMethodRow>> ReadPaymentMethodsAsync(
        UtcRange range,
        CancellationToken cancellationToken)
    {
        var paid = await PaidIn(range)
            .GroupBy(order => order.PaymentProviderCode)
            .Select(group => new
            {
                Code = group.Key,
                Orders = group.Count(),
                Revenue = group.Sum(order => order.GrandTotal),
            })
            .ToDictionaryAsync(row => row.Code, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var providers = await dbContext.PaymentProviders
            .AsNoTracking()
            .OrderBy(provider => provider.DisplayOrder)
            .ThenBy(provider => provider.Code)
            .Select(provider => new { provider.Code, provider.DisplayName })
            .ToListAsync(cancellationToken);

        // Every provider the shop has, a zero included, so "Bank transfer vs PayPal" always shows both.
        return providers
            .Select(provider => paid.TryGetValue(provider.Code, out var row)
                ? new PaymentMethodRow(provider.Code, provider.DisplayName, row.Orders, row.Revenue)
                : new PaymentMethodRow(provider.Code, provider.DisplayName, 0, 0m))
            .ToList();
    }

    public async Task<BuyersByDistrict> ReadBuyersByDistrictAsync(UtcRange range, CancellationToken cancellationToken)
    {
        var paid = PaidIn(range);

        var byDistrict = await BuyersByDistrictQuery(range).ToListAsync(cancellationToken);

        var distinct = await paid.Select(order => order.EmailNormalized).Distinct().CountAsync(cancellationToken);

        var ids = byDistrict.Select(row => row.Id).ToList();
        var names = await dbContext.Districts
            .AsNoTracking()
            .Where(district => ids.Contains(district.Id))
            .Select(district => new { district.Id, district.Name })
            .ToDictionaryAsync(district => district.Id, district => district.Name, cancellationToken);

        return new BuyersByDistrict(
            distinct,
            byDistrict
                .Select(row => new DistrictBuyersRow(
                    row.Id,
                    names.TryGetValue(row.Id, out var name) ? name : $"District {row.Id}",
                    row.Buyers,
                    row.Orders,
                    row.Revenue))
                .OrderByDescending(row => row.Buyers)
                .ThenByDescending(row => row.Revenue)
                .ThenBy(row => row.Name, StringComparer.Ordinal)
                .ToList());
    }

    public async Task<LowStockDto> ReadLowStockAsync(int take, CancellationToken cancellationToken)
    {
        var low = LowStockQuery();

        var total = await low.CountAsync(cancellationToken);

        var rows = total == 0
            ? []
            : await low
                .OrderBy(stock => stock.Available)
                .ThenBy(stock => stock.Variant!.Sku)
                .Take(take)
                .Select(stock => new LowStockRow(
                    stock.VariantId,
                    stock.Variant!.ProductId,
                    stock.Variant.Product!.Name,
                    stock.Variant.Product.ProductCode,
                    stock.Variant.Sku,
                    stock.Variant.ProductColour!.Name,
                    stock.Variant.Size!.Label,
                    stock.Available,
                    stock.LowStockThreshold ?? _defaultThreshold))
                .ToListAsync(cancellationToken);

        return new LowStockDto(_defaultThreshold, total, rows);
    }

    public async Task<InventoryReportPage> ReadInventoryAsync(
        InventoryReportFilter filter,
        int page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var all = ActiveVariants();
        var threshold = _defaultThreshold;

        var counts = new InventoryStatusCounts(
            await all.Where(InStock(threshold)).CountAsync(cancellationToken),
            await all.Where(Low(threshold)).CountAsync(cancellationToken),
            await all.Where(Out).CountAsync(cancellationToken));

        var matching = all;

        if (filter.NormalisedSearch is { } search)
        {
            matching = matching.Where(variant =>
                variant.Sku.StartsWith(search)
                || variant.Product!.ProductCode.StartsWith(search)
                || variant.Product.Name.Contains(search));
        }

        matching = filter.Status switch
        {
            InventoryStockFilter.InStock => matching.Where(InStock(threshold)),
            InventoryStockFilter.LowStock => matching.Where(Low(threshold)),
            InventoryStockFilter.OutOfStock => matching.Where(Out),
            _ => matching,
        };

        var total = await matching.CountAsync(cancellationToken);

        var ordered = matching
            .OrderBy(variant => variant.Product!.Name)
            .ThenBy(variant => variant.ProductId)
            .ThenBy(variant => variant.SortOrder)
            .ThenBy(variant => variant.Id);

        var slice = pageSize is { } size ? ordered.Skip((page - 1) * size).Take(size) : ordered;

        var rows = total == 0
            ? []
            : await slice
                .Select(variant => new
                {
                    variant.Id,
                    variant.ProductId,
                    ProductName = variant.Product!.Name,
                    variant.Product.ProductCode,
                    Department = variant.Product.Department!.Name,
                    variant.Sku,
                    Colour = variant.ProductColour!.Name,
                    Size = variant.Size!.Label,
                    OnHand = variant.StockLevel == null ? 0 : variant.StockLevel.OnHand,
                    Reserved = variant.StockLevel == null ? 0 : variant.StockLevel.Reserved,
                    Available = variant.StockLevel == null ? 0 : variant.StockLevel.Available,
                    OwnThreshold = variant.StockLevel == null ? null : variant.StockLevel.LowStockThreshold,
                })
                .ToListAsync(cancellationToken);

        return new InventoryReportPage(
            rows
                .Select(row =>
                {
                    var applied = row.OwnThreshold ?? threshold;

                    return new InventoryReportRow(
                        row.Id,
                        row.ProductId,
                        row.ProductName,
                        row.ProductCode,
                        row.Department,
                        row.Sku,
                        row.Colour,
                        row.Size,
                        row.OnHand,
                        row.Reserved,
                        row.Available,
                        applied,
                        row.OwnThreshold is null,
                        StockStatusOf(row.Available, applied));
                })
                .ToList(),
            page,
            pageSize ?? rows.Count,
            total,
            threshold,
            counts);
    }

    public async Task<(IReadOnlyList<CustomerReportRow> Rows, int TotalCount, decimal TotalRevenue)> ReadCustomersAsync(
        UtcRange range,
        string? emailPrefix,
        int page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var paid = PaidIn(range);

        if (emailPrefix is not null)
        {
            paid = paid.Where(order => order.EmailNormalized.StartsWith(emailPrefix));
        }

        var buyers = paid
            .GroupBy(order => order.EmailNormalized)
            .Select(group => new
            {
                Email = group.Key,
                Orders = group.Count(),
                Revenue = group.Sum(order => order.GrandTotal),
                FirstPaid = group.Min(order => order.PaidAtUtc!.Value),
                LastPaid = group.Max(order => order.PaidAtUtc!.Value),
                AccountId = group.Max(order => order.CustomerUserId),
            });

        var total = await buyers.CountAsync(cancellationToken);
        var revenue = await paid.SumAsync(order => order.GrandTotal, cancellationToken);

        if (total == 0)
        {
            return ([], 0, revenue);
        }

        var ordered = buyers.OrderByDescending(buyer => buyer.Revenue).ThenBy(buyer => buyer.Email);
        var rows = await (pageSize is { } size ? ordered.Skip((page - 1) * size).Take(size) : ordered)
            .ToListAsync(cancellationToken);

        // The name, the address as typed and the district come from each buyer's latest paid order in the
        // range: one query for the page's buyers (or the whole range, for an export).
        var details = paid;

        if (pageSize is not null)
        {
            var emails = rows.Select(row => row.Email).ToList();
            details = details.Where(order => emails.Contains(order.EmailNormalized));
        }

        var latest = (await details
                .Select(order => new
                {
                    order.Id,
                    order.EmailNormalized,
                    order.Email,
                    order.FirstName,
                    order.LastName,
                    order.DeliveryDistrictName,
                    PaidAtUtc = order.PaidAtUtc!.Value,
                })
                .ToListAsync(cancellationToken))
            .GroupBy(order => order.EmailNormalized, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(order => order.PaidAtUtc).ThenByDescending(order => order.Id).First(),
                StringComparer.Ordinal);

        return (
            rows
                .Select(row =>
                {
                    var order = latest[row.Email];

                    return new CustomerReportRow(
                        order.Email,
                        string.IsNullOrWhiteSpace(order.LastName) ? order.FirstName : $"{order.FirstName} {order.LastName}",
                        order.DeliveryDistrictName,
                        row.AccountId is not null,
                        row.Orders,
                        row.Revenue,
                        ColomboDay(row.FirstPaid),
                        ColomboDay(row.LastPaid));
                })
                .ToList(),
            total,
            revenue);
    }

    public async Task<BuyerCounts> ReadBuyerCountsAsync(
        ReportPeriod period,
        ReportInterval interval,
        string? emailPrefix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);

        // One statement, so one consistent view of the orders (see BuyerCountsQuery).
        var rows = await BuyerCountsQuery(period, interval, emailPrefix).ToListAsync(cancellationToken);

        var buckets = rows.Where(row => !row.IsTotal).ToList();
        var total = rows.SingleOrDefault(row => row.IsTotal);

        return new BuyerCounts(
            buckets.Select(row => new BucketCount(row.Bucket!.Value, row.Buyers)).ToList(),
            buckets.Where(row => row.FirstTime > 0).Select(row => new BucketCount(row.Bucket!.Value, row.FirstTime)).ToList(),
            total?.Buyers ?? 0);
    }

    // -----------------------------------------------------------------------------------------------------
    // The shared predicates. Internal so the plan tests can render exactly these queries.
    // -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Orders paid in the range: doc 08 §8.1's revenue population. <b>No status test</b> — a refunded order
    /// keeps its <c>PaidAtUtc</c> and is revenue on that day; an order never paid has none. The explicit
    /// <c>IS NOT NULL</c> repeats <c>IX_Orders_Paid</c>'s filter so SQL Server can match the index whatever
    /// the parameter values.
    /// </summary>
    internal IQueryable<Order> PaidIn(UtcRange range) =>
        dbContext.Orders
            .AsNoTracking()
            .Where(order => order.PaidAtUtc != null
                && order.PaidAtUtc >= range.FromUtc
                && order.PaidAtUtc < range.ToUtcExclusive);

    /// <summary>Orders placed in the range, any status (doc 08 §8.1: "Orders: counted by PlacedAtUtc").</summary>
    internal IQueryable<Order> PlacedIn(UtcRange range) =>
        dbContext.Orders
            .AsNoTracking()
            .Where(order => order.PlacedAtUtc >= range.FromUtc && order.PlacedAtUtc < range.ToUtcExclusive);

    /// <summary>The lines of orders paid in the range, with the same paid-date rule as <see cref="PaidIn"/>.</summary>
    internal IQueryable<OrderLine> LinesPaidIn(UtcRange range) =>
        dbContext.OrderLines
            .AsNoTracking()
            .Where(line => line.Order!.PaidAtUtc != null
                && line.Order.PaidAtUtc >= range.FromUtc
                && line.Order.PaidAtUtc < range.ToUtcExclusive);

    /// <summary>
    /// Completed refunds of a settlement payment, by completion date (doc 08 §8.1). A refund of a payment
    /// exception (<c>IsOrderSettlement = 0</c>) gives back money that was never revenue, so it is not here.
    /// </summary>
    internal IQueryable<Refund> SettlementRefundsIn(UtcRange range) =>
        dbContext.Refunds
            .AsNoTracking()
            .Where(refund => refund.Status == RefundStatus.Completed
                && refund.CompletedAtUtc != null
                && refund.CompletedAtUtc >= range.FromUtc
                && refund.CompletedAtUtc < range.ToUtcExclusive
                && refund.Payment!.IsOrderSettlement);

    /// <summary>Completed settlement refunds per Colombo day of completion.</summary>
    internal IQueryable<RefundDay> RefundsDailyQuery(UtcRange range) =>
        SettlementRefundsIn(range)
            .GroupBy(refund => refund.CompletedAtUtc!.Value.AddMinutes(ColomboOffsetMinutes).Date)
            .Select(day => new RefundDay(day.Key, day.Sum(refund => refund.Amount)));

    /// <summary>Gross revenue and paid orders over a range, as one grouped row (or none).</summary>
    internal IQueryable<PaidTotal> PaidTotalsQuery(UtcRange range) =>
        PaidIn(range)
            .GroupBy(order => 1)
            .Select(all => new PaidTotal(all.Sum(order => order.GrandTotal), all.Count()));

    /// <summary>Distinct buyers, paid orders and revenue per delivery district.</summary>
    internal IQueryable<DistrictTotal> BuyersByDistrictQuery(UtcRange range) =>
        PaidIn(range)
            .GroupBy(order => order.DeliveryDistrictId)
            .Select(group => new DistrictTotal(
                group.Key,
                group.Select(order => order.EmailNormalized).Distinct().Count(),
                group.Count(),
                group.Sum(order => order.GrandTotal)));

    /// <summary>
    /// The customers chart's counts (#186b) in <b>one statement</b>: per bucket, distinct buyers and how many of
    /// them paid for the first time ever in it, plus a grand-total row (<c>GROUPING SETS ((Bucket), ())</c>) with
    /// the period's distinct buyers and first-time buyers.
    /// <para>
    /// <b>Why one statement.</b> As three statements (the first version), each saw the orders at its own moment
    /// under READ COMMITTED SNAPSHOT, so an order paid between them could make a bar's first-time count exceed its
    /// buyers (returning −1) or the total disagree with the bars (#186b tester). A SNAPSHOT transaction would fix
    /// that, but the database has only READ_COMMITTED_SNAPSHOT on, not ALLOW_SNAPSHOT_ISOLATION
    /// (<c>tools/db/provision-database.sql</c>), and turning it on is a database change this chart does not
    /// justify. Under RCSI <b>one statement reads one committed snapshot</b> across every table reference, so
    /// buyers, first-time and the total are taken from the same data, and no transaction or isolation level is
    /// touched — nothing can leak into the pooled connection (#172).
    /// </para>
    /// <para>
    /// <c>p</c> is the period's paid orders as (email, bucket); <c>f</c> is each email's first paid instant over
    /// its whole history up to the period's end. An inner join keeps only the period's buyers. On the total row
    /// the first-time test still compares each order row's own bucket with the first order's bucket, which
    /// holds for exactly the buyers whose first order is in the period. The bucket is
    /// <c>DATEDIFF(day|month, @anchor, DATEADD(minute, 330, PaidAtUtc))</c> — the midnights or month starts
    /// crossed, which for a midnight anchor is the difference in Colombo days or months; weeks count days from
    /// the Monday on or before the period's first day, divided by seven, so the partial first week is bucket 0
    /// as in <see cref="ReportBuckets.Split"/>. Both references to the orders table are filtered on
    /// <c>PaidAtUtc IS NOT NULL</c> and a range, so <c>IX_Orders_Paid</c>, which carries
    /// <c>EmailNormalized</c>, covers them (<c>ReportingQueryPlanTests</c>).
    /// </para>
    /// <para>
    /// Raw SQL rather than LINQ because LINQ cannot say <c>GROUPING SETS</c>. Every value is a parameter; the
    /// only text spliced in is one of three fixed bucket expressions and an optional fixed <c>LIKE</c> clause.
    /// </para>
    /// </summary>
    internal IQueryable<BuyerBucketRow> BuyerCountsQuery(ReportPeriod period, ReportInterval interval, string? emailPrefix)
    {
        ArgumentNullException.ThrowIfNull(period);

        var range = UtcRange.Of(period);
        var anchor = (interval == ReportInterval.Week ? ReportBuckets.WeekStart(period.From) : period.From).ToDateTime(TimeOnly.MinValue);

        string Bucket(string column) => interval switch
        {
            ReportInterval.Day => $"DATEDIFF(day, {{2}}, DATEADD(minute, {ColomboOffsetMinutes}, {column}))",
            ReportInterval.Week => $"DATEDIFF(day, {{2}}, DATEADD(minute, {ColomboOffsetMinutes}, {column})) / 7",
            ReportInterval.Month => $"DATEDIFF(month, {{2}}, DATEADD(minute, {ColomboOffsetMinutes}, {column}))",
            _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, null),
        };

        string Search(string alias) => emailPrefix is null ? "" : $" AND [{alias}].[EmailNormalized] LIKE {{3}} ESCAPE N'\\'";

        var format =
            "SELECT [p].[Bucket] AS [Bucket], CAST(GROUPING([p].[Bucket]) AS bit) AS [IsTotal], "
            + "COUNT(DISTINCT [p].[Email]) AS [Buyers], "
            + "COUNT(DISTINCT CASE WHEN [f].[FirstPaidAtUtc] >= {0} AND " + Bucket("[f].[FirstPaidAtUtc]") + " = [p].[Bucket] THEN [p].[Email] END) AS [FirstTime] "
            + "FROM ("
            + "SELECT [o].[EmailNormalized] AS [Email], " + Bucket("[o].[PaidAtUtc]") + " AS [Bucket] "
            + "FROM [sales].[Orders] AS [o] "
            + "WHERE [o].[PaidAtUtc] IS NOT NULL AND [o].[PaidAtUtc] >= {0} AND [o].[PaidAtUtc] < {1}" + Search("o")
            + ") AS [p] "
            + "INNER JOIN ("
            + "SELECT [h].[EmailNormalized] AS [Email], MIN([h].[PaidAtUtc]) AS [FirstPaidAtUtc] "
            + "FROM [sales].[Orders] AS [h] "
            + "WHERE [h].[PaidAtUtc] IS NOT NULL AND [h].[PaidAtUtc] < {1}" + Search("h") + " "
            + "GROUP BY [h].[EmailNormalized]"
            + ") AS [f] ON [f].[Email] = [p].[Email] "
            + "GROUP BY GROUPING SETS (([p].[Bucket]), ())";

        // Named and typed parameters: datetime2(3) as the column is, nvarchar(256) as EmailNormalized is.
        object[] arguments = emailPrefix is null
            ? [Instant("@from", range.FromUtc), Instant("@to", range.ToUtcExclusive), Instant("@anchor", anchor)]
            : [Instant("@from", range.FromUtc), Instant("@to", range.ToUtcExclusive), Instant("@anchor", anchor),
                new SqlParameter("@emailPattern", SqlDbType.NVarChar, 256) { Value = LikePrefix(emailPrefix) }];

        return dbContext.Database.SqlQuery<BuyerBucketRow>(FormattableStringFactory.Create(format, arguments));
    }

    private static SqlParameter Instant(string name, DateTime value) =>
        new(name, SqlDbType.DateTime2) { Scale = 3, Value = value };

    /// <summary>A <c>LIKE</c> pattern for "starts with", with the search's own wildcards escaped (as EF's <c>StartsWith</c>).</summary>
    internal static string LikePrefix(string prefix) =>
        prefix.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
        + "%";

    /// <summary>Line revenue per department at purchase, on orders paid in the range.</summary>
    internal IQueryable<GroupSales> DepartmentSalesQuery(UtcRange range) =>
        LinesPaidIn(range)
            .GroupBy(line => line.DepartmentId)
            .Select(group => new GroupSales(
                group.Key,
                group.Sum(line => (int)line.Quantity),
                group.Sum(line => line.LineTotal)));

    /// <summary>The paid-date half of <see cref="ReadDailyAsync"/>, exposed for the plan test.</summary>
    internal IQueryable<PaidDay> PaidDailyQuery(UtcRange range) =>
        PaidIn(range)
            .GroupBy(order => order.PaidAtUtc!.Value.AddMinutes(ColomboOffsetMinutes).Date)
            .Select(day => new PaidDay(
                day.Key,
                day.Sum(order => order.GrandTotal),
                day.Count(),
                day.Sum(order => order.DeliveryFeeAmount - order.DeliveryDiscountAmount)));

    /// <summary>
    /// Doc 11 P16's low-stock rule: an active variant of a product that is not deleted, with
    /// <c>0 &lt; Available ≤ COALESCE(LowStockThreshold, default)</c>. Starts from <c>StockLevels</c> so
    /// <c>IX_StockLevels_Available</c> (which includes the threshold) can serve it.
    /// </summary>
    internal IQueryable<StockLevel> LowStockQuery()
    {
        var threshold = _defaultThreshold;

        return dbContext.StockLevels
            .AsNoTracking()
            .Where(stock => stock.Available > 0
                && stock.Available <= (stock.LowStockThreshold ?? threshold)
                && stock.Variant!.IsActive
                && !stock.Variant.Product!.IsDeleted);
    }

    /// <summary>Variants the inventory report covers: active, of a product that is not deleted.</summary>
    private IQueryable<ProductVariant> ActiveVariants() =>
        dbContext.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.IsActive && !variant.Product!.IsDeleted);

    // The inventory report's three statuses, on the entity. Low is the same rule as LowStockQuery's; a variant
    // with no stock row has nothing available and is out of stock.
    private static Expression<Func<ProductVariant, bool>> Low(int threshold) =>
        variant => variant.StockLevel != null
            && variant.StockLevel.Available > 0
            && variant.StockLevel.Available <= (variant.StockLevel.LowStockThreshold ?? threshold);

    private static Expression<Func<ProductVariant, bool>> InStock(int threshold) =>
        variant => variant.StockLevel != null
            && variant.StockLevel.Available > (variant.StockLevel.LowStockThreshold ?? threshold);

    private static readonly Expression<Func<ProductVariant, bool>> Out =
        variant => variant.StockLevel == null || variant.StockLevel.Available <= 0;

    internal static string StockStatusOf(int available, int threshold) =>
        available <= 0 ? "OutOfStock" : available <= threshold ? "LowStock" : "InStock";

    private static DateOnly ColomboDay(DateTime utc) => DateOnly.FromDateTime(utc.AddMinutes(ColomboOffsetMinutes));

    private static string CategoryPath(string department, string? parent, string name) =>
        parent is null ? $"{department} · {name}" : $"{department} · {parent} · {name}";

    /// <summary>One Colombo day of paid orders.</summary>
    internal sealed record PaidDay(DateTime Day, decimal Gross, int PaidOrders, decimal Delivery);

    /// <summary>One Colombo day of completed settlement refunds.</summary>
    internal sealed record RefundDay(DateTime Day, decimal Amount);

    /// <summary>A range's gross revenue and paid orders.</summary>
    internal sealed record PaidTotal(decimal Gross, int PaidOrders);

    /// <summary>One delivery district's buyers.</summary>
    internal sealed record DistrictTotal(int Id, int Buyers, int Orders, decimal Revenue);

    /// <summary>One department's line sales.</summary>
    internal sealed record GroupSales(int Id, int Units, decimal Revenue);

    /// <summary>One row of <see cref="BuyerCountsQuery"/>: a bucket, or the period's total when <see cref="IsTotal"/>.</summary>
    internal sealed class BuyerBucketRow
    {
        public int? Bucket { get; init; }

        public bool IsTotal { get; init; }

        public int Buyers { get; init; }

        public int FirstTime { get; init; }
    }
}
