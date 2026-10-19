using System.Globalization;
using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.Exports;
using SpencerDenim.Application.Admin.Reporting.Contracts;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Admin.Reporting;

/// <summary>The sales report (ADM-19; <c>Reports.View</c>).</summary>
public sealed record GetSalesReportQuery(ReportPeriod Period, ReportInterval GroupBy);

/// <summary>The inventory report (ADM-19; <c>Reports.View</c>).</summary>
public sealed record GetInventoryReportQuery(InventoryReportFilter Filter, int? Page, int? PageSize);

/// <summary>The customers report (ADM-19; <c>Reports.View</c>).</summary>
/// <param name="Search">The start of an email address, or null.</param>
public sealed record GetCustomerReportQuery(ReportPeriod Period, string? Search, int? Page, int? PageSize);

/// <summary>The customers report's chart: buyers per day, week or month, first-time and returning (#186b; <c>Reports.View</c>).</summary>
/// <param name="Search">The same search as the report's: the start of an email address, or null.</param>
public sealed record GetCustomerBuyersQuery(ReportPeriod Period, string? Search);

/// <summary>Export the sales report (<c>Reports.Export</c>).</summary>
public sealed record ExportSalesReportCommand(ReportPeriod Period, ReportInterval GroupBy, AdminActor Actor);

/// <summary>Export the inventory report (<c>Reports.Export</c>).</summary>
public sealed record ExportInventoryReportCommand(InventoryReportFilter Filter, AdminActor Actor);

/// <summary>Export the customers report (<c>Reports.Export</c>).</summary>
public sealed record ExportCustomerReportCommand(ReportPeriod Period, string? Search, AdminActor Actor);

/// <summary>The paging rules the two paged reports share.</summary>
/// <remarks>
/// <b>Clamped rather than refused</b>, as the order and customer lists do. The ceiling is the customer list's:
/// a page of the customers report is names, addresses and money, and the bulk copy has its own permission
/// and its own audit row (the export).
/// </remarks>
public static class ReportPaging
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;

    /// <summary>The highest page honoured, so <c>(page - 1) * pageSize</c> cannot wrap negative.</summary>
    public const int MaxPage = int.MaxValue / MaxPageSize;

    public static (int Page, int PageSize) Clamp(int? page, int? pageSize) =>
        (Math.Clamp(page ?? 1, 1, MaxPage), Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));

    /// <summary>The customers report's search: the start of an address, trimmed, upper-cased, cut to 100.</summary>
    public static string? EmailPrefix(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return null;
        }

        var trimmed = search.Trim();

        return (trimmed.Length > 100 ? trimmed[..100] : trimmed).ToUpperInvariant();
    }
}

public sealed class GetSalesReportQueryHandler(IReportingReader reader) : IQueryHandler<GetSalesReportQuery, SalesReportDto>
{
    public async Task<SalesReportDto> HandleAsync(GetSalesReportQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return Build(
            query.Period,
            query.GroupBy,
            await reader.ReadDailyAsync(UtcRange.Of(query.Period), cancellationToken));
    }

    public static SalesReportDto Build(ReportPeriod period, ReportInterval groupBy, IReadOnlyList<DailySales> daily)
    {
        ArgumentNullException.ThrowIfNull(period);

        var rows = SalesReportBuilder.Sum(ReportBuckets.Split(period, groupBy), daily);

        return new SalesReportDto(
            ReportPeriodDto.Of(period),
            groupBy.ToString(),
            SalesCurrency.Lkr,
            rows,
            SalesReportBuilder.Total(period, rows));
    }
}

public sealed class GetInventoryReportQueryHandler(IReportingReader reader)
    : IQueryHandler<GetInventoryReportQuery, InventoryReportPage>
{
    public Task<InventoryReportPage> HandleAsync(GetInventoryReportQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var (page, pageSize) = ReportPaging.Clamp(query.Page, query.PageSize);

        return reader.ReadInventoryAsync(query.Filter, page, pageSize, cancellationToken);
    }
}

public sealed class GetCustomerReportQueryHandler(IReportingReader reader)
    : IQueryHandler<GetCustomerReportQuery, CustomerReportPage>
{
    public async Task<CustomerReportPage> HandleAsync(GetCustomerReportQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var (page, pageSize) = ReportPaging.Clamp(query.Page, query.PageSize);

        var (rows, total, revenue) = await reader.ReadCustomersAsync(
            UtcRange.Of(query.Period),
            ReportPaging.EmailPrefix(query.Search),
            page,
            pageSize,
            cancellationToken);

        return new CustomerReportPage(
            ReportPeriodDto.Of(query.Period),
            SalesCurrency.Lkr,
            rows,
            page,
            pageSize,
            total,
            revenue);
    }
}

/// <summary>
/// The customers report's chart (#186b). The reader counts in SQL; this picks the grouping, lays the counts
/// on the period's buckets (a bucket with nobody is a zero, so the chart has no gaps) and derives returning
/// as buyers − first-time, which holds because every buyer in a bucket either paid for the first time ever in
/// it or had paid before it began.
/// </summary>
public sealed class GetCustomerBuyersQueryHandler(IReportingReader reader)
    : IQueryHandler<GetCustomerBuyersQuery, CustomerBuyersDto>
{
    /// <summary>Up to this many days, one point per day (a month of bars).</summary>
    public const int MaxDailyDays = 31;

    /// <summary>Up to this many days, one point per ISO week (26 or 27 bars); longer, one per month.</summary>
    public const int MaxWeeklyDays = 182;

    public async Task<CustomerBuyersDto> HandleAsync(GetCustomerBuyersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var interval = IntervalFor(query.Period);

        var counts = await reader.ReadBuyerCountsAsync(
            query.Period,
            interval,
            ReportPaging.EmailPrefix(query.Search),
            cancellationToken);

        return Build(query.Period, interval, counts);
    }

    /// <summary>Day up to a month, ISO week up to half a year, month beyond: never more than 31 bars.</summary>
    public static ReportInterval IntervalFor(ReportPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);

        return period.Days <= MaxDailyDays ? ReportInterval.Day
            : period.Days <= MaxWeeklyDays ? ReportInterval.Week
            : ReportInterval.Month;
    }

    public static CustomerBuyersDto Build(ReportPeriod period, ReportInterval interval, BuyerCounts counts)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(counts);

        var buckets = ReportBuckets.Split(period, interval);
        var buyers = Totals(counts.Buyers);
        var firstTime = Totals(counts.FirstTime);

        var points = buckets
            .Select((bucket, index) =>
            {
                var all = buyers.GetValueOrDefault(index);
                var first = firstTime.GetValueOrDefault(index);

                return new CustomerBuyersPoint(bucket.Start, bucket.End, all, first, all - first);
            })
            .ToList();

        var firstTimeTotal = points.Sum(point => point.FirstTimeBuyers);

        return new CustomerBuyersDto(
            ReportPeriodDto.Of(period),
            interval.ToString(),
            counts.DistinctBuyers,
            firstTimeTotal,
            counts.DistinctBuyers - firstTimeTotal,
            points);
    }

    private static Dictionary<int, int> Totals(IReadOnlyList<BucketCount> counts) =>
        counts.GroupBy(count => count.Index).ToDictionary(group => group.Key, group => group.Sum(count => count.Count));
}

/// <summary>
/// What every report export shares: the audit row, written and committed <b>before</b> the file is returned.
/// </summary>
/// <remarks>
/// <para>
/// Doc 11 P16: "Every CSV export is audited (<c>Export</c>)". If the audit write fails, the export fails and no
/// file is produced — the record that the data left must exist whenever it could have.
/// </para>
/// <para>
/// <b>The audit row records the shape of the export, never its contents</b>, the rule the newsletter export
/// set (W3-07): the report, the row count, the dates and <i>whether</i> a search narrowed it — <b>not the
/// search text</b>, which on the customers report is the start of an email address that doc 08 §3.1's
/// anonymisation, scrubbing audit rows by entity and user id, could never find.
/// </para>
/// </remarks>
public static class ReportExportAudit
{
    /// <summary><c>ops.AuditLogs.EntityType</c> for each report.</summary>
    public const string SalesEntityType = "SalesReport";

    public const string InventoryEntityType = "InventoryReport";

    public const string CustomersEntityType = "CustomerReport";

    public static Task WriteAsync(
        IAuditLogWriter auditLog,
        AdminActor actor,
        DateTimeOffset now,
        string entityType,
        object shape,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(actor);

        return auditLog.WriteAsync(
            new AuditLog
            {
                OccurredAtUtc = now.UtcDateTime,
                UserId = actor.UserId,
                UserEmail = actor.Email,
                Action = AuditActions.Export,
                EntityType = entityType,
                EntityId = null,
                AfterJson = JsonSerializer.Serialize(shape),
                IpAddress = actor.IpAddress,
                UserAgent = actor.UserAgent,
                CorrelationId = actor.CorrelationId,
            },
            cancellationToken);
    }

    public static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Money as a spreadsheet reads it: no currency symbol, no thousands separator, two places.</summary>
    public static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The sales report as CSV, with a Total row last.</summary>
public sealed class ExportSalesReportCommandHandler(IReportingReader reader, IAuditLogWriter auditLog, IClock clock)
    : ICommandHandler<ExportSalesReportCommand, ReportExportFile>
{
    public static readonly IReadOnlyList<string> Header =
    [
        "From",
        "To",
        "Orders placed",
        "Paid orders",
        "Gross revenue (LKR)",
        "Items after promo (LKR)",
        "Delivery (LKR)",
        "Refunds completed (LKR)",
        "Net revenue (LKR)",
    ];

    public async Task<ReportExportFile> HandleAsync(ExportSalesReportCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var report = GetSalesReportQueryHandler.Build(
            command.Period,
            command.GroupBy,
            await reader.ReadDailyAsync(UtcRange.Of(command.Period), cancellationToken));

        await ReportExportAudit.WriteAsync(
            auditLog,
            command.Actor,
            clock.UtcNow,
            ReportExportAudit.SalesEntityType,
            new
            {
                report = "sales",
                rows = report.Rows.Count,
                from = ReportExportAudit.Day(command.Period.From),
                to = ReportExportAudit.Day(command.Period.To),
                groupBy = report.GroupBy,
            },
            cancellationToken);

        var csv = new CsvDocument().Row([.. Header]);

        foreach (var row in report.Rows)
        {
            csv.Row(Cells(ReportExportAudit.Day(row.Start), row));
        }

        csv.Row(Cells("Total", report.Totals));

        return new ReportExportFile(
            csv.ToBytes(),
            $"sales-report-{ReportExportAudit.Day(command.Period.From)}-to-{ReportExportAudit.Day(command.Period.To)}.csv",
            report.Rows.Count);
    }

    private static string[] Cells(string first, SalesReportRow row) =>
    [
        first,
        ReportExportAudit.Day(row.End),
        ReportExportAudit.Number(row.Orders),
        ReportExportAudit.Number(row.PaidOrders),
        ReportExportAudit.Money(row.GrossRevenue),
        ReportExportAudit.Money(row.ItemsRevenue),
        ReportExportAudit.Money(row.DeliveryRevenue),
        ReportExportAudit.Money(row.RefundsCompleted),
        ReportExportAudit.Money(row.NetRevenue),
    ];
}

/// <summary>The inventory report as CSV: every variant the filters match, not one page.</summary>
public sealed class ExportInventoryReportCommandHandler(IReportingReader reader, IAuditLogWriter auditLog, IClock clock)
    : ICommandHandler<ExportInventoryReportCommand, ReportExportFile>
{
    public static readonly IReadOnlyList<string> Header =
    [
        "Product",
        "Product code",
        "Department",
        "SKU",
        "Colour",
        "Size",
        "On hand",
        "Reserved",
        "Available",
        "Low-stock threshold",
        "Threshold from",
        "Status",
    ];

    public async Task<ReportExportFile> HandleAsync(ExportInventoryReportCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var report = await reader.ReadInventoryAsync(command.Filter, 1, null, cancellationToken);
        var now = clock.UtcNow;

        await ReportExportAudit.WriteAsync(
            auditLog,
            command.Actor,
            now,
            ReportExportAudit.InventoryEntityType,
            new
            {
                report = "inventory",
                rows = report.Rows.Count,
                status = command.Filter.Status.ToString(),
                searchApplied = command.Filter.NormalisedSearch is not null,
            },
            cancellationToken);

        var csv = new CsvDocument().Row([.. Header]);

        foreach (var row in report.Rows)
        {
            csv.Row(
                row.ProductName,
                row.ProductCode,
                row.Department,
                row.Sku,
                row.Colour,
                row.Size,
                ReportExportAudit.Number(row.OnHand),
                ReportExportAudit.Number(row.Reserved),
                ReportExportAudit.Number(row.Available),
                ReportExportAudit.Number(row.Threshold),
                row.ThresholdIsDefault ? "Shop default" : "This variant",
                StatusText(row.StockStatus));
        }

        return new ReportExportFile(
            csv.ToBytes(),
            $"inventory-report-{ReportExportAudit.Day(ReportPeriod.ColomboDayOf(now))}.csv",
            report.Rows.Count);
    }

    public static string StatusText(string status) => status switch
    {
        "OutOfStock" => "Out of stock",
        "LowStock" => "Low stock",
        "InStock" => "In stock",
        _ => status,
    };
}

/// <summary>The customers report as CSV: every buyer in the period, not one page.</summary>
public sealed class ExportCustomerReportCommandHandler(IReportingReader reader, IAuditLogWriter auditLog, IClock clock)
    : ICommandHandler<ExportCustomerReportCommand, ReportExportFile>
{
    public static readonly IReadOnlyList<string> Header =
    [
        "Email",
        "Name",
        "Delivery district (latest order)",
        "Has an account",
        "Paid orders",
        "Gross revenue (LKR)",
        "First paid (Colombo)",
        "Last paid (Colombo)",
    ];

    public async Task<ReportExportFile> HandleAsync(ExportCustomerReportCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var prefix = ReportPaging.EmailPrefix(command.Search);
        var (rows, _, _) = await reader.ReadCustomersAsync(
            UtcRange.Of(command.Period),
            prefix,
            1,
            null,
            cancellationToken);

        await ReportExportAudit.WriteAsync(
            auditLog,
            command.Actor,
            clock.UtcNow,
            ReportExportAudit.CustomersEntityType,
            new
            {
                report = "customers",
                rows = rows.Count,
                from = ReportExportAudit.Day(command.Period.From),
                to = ReportExportAudit.Day(command.Period.To),
                searchApplied = prefix is not null,
            },
            cancellationToken);

        var csv = new CsvDocument().Row([.. Header]);

        foreach (var row in rows)
        {
            csv.Row(
                row.Email,
                row.Name,
                row.District,
                row.HasAccount ? "Yes" : "No",
                ReportExportAudit.Number(row.PaidOrders),
                ReportExportAudit.Money(row.GrossRevenue),
                ReportExportAudit.Day(row.FirstPaidOn),
                ReportExportAudit.Day(row.LastPaidOn));
        }

        return new ReportExportFile(
            csv.ToBytes(),
            $"customers-report-{ReportExportAudit.Day(command.Period.From)}-to-{ReportExportAudit.Day(command.Period.To)}.csv",
            rows.Count);
    }
}
