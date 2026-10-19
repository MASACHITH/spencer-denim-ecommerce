using System.Text;
using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Application.Admin.Reporting.Contracts;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Application.Admin.Reporting;

/// <summary>
/// The three report exports (ADM-19): each writes one <c>Export</c> audit row of its shape — never its search
/// text — <b>before</b> the file exists, and fails with no file if the audit cannot be written.
/// </summary>
public sealed class ReportExportCommandHandlerTests
{
    private static readonly AdminActor Actor = new(42, "manager@example.com", "203.0.113.5", "xunit", "trace-1");

    /// <summary>15 October 2026, 20:30 UTC — already the 16th in Colombo, which the file name must say.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 20, 30, 0, TimeSpan.Zero);

    private static readonly ReportPeriod October = new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));

    [Fact]
    public async Task The_customers_export_audits_its_shape_and_never_the_search_text()
    {
        var audit = new RecordingAuditLog();
        var reader = new StubReader
        {
            Customers =
            [
                new CustomerReportRow("=1+2@example.com", "-Nimal", "Kandy", true, 2, 3000m, new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 9)),
            ],
        };

        var file = await new ExportCustomerReportCommandHandler(reader, audit, new FixedClock(Now))
            .HandleAsync(new ExportCustomerReportCommand(October, "  secret.address@", Actor), CancellationToken.None);

        var row = Assert.Single(audit.Written);
        Assert.Equal(AuditActions.Export, row.Action);
        Assert.Equal(ReportExportAudit.CustomersEntityType, row.EntityType);
        Assert.Equal(42, row.UserId);
        Assert.Equal("manager@example.com", row.UserEmail);
        Assert.Equal("203.0.113.5", row.IpAddress);
        Assert.Equal("trace-1", row.CorrelationId);
        Assert.Equal(Now.UtcDateTime, row.OccurredAtUtc);
        Assert.Null(row.EntityId);

        var shape = JsonDocument.Parse(row.AfterJson!).RootElement;
        Assert.Equal("customers", shape.GetProperty("report").GetString());
        Assert.Equal(1, shape.GetProperty("rows").GetInt32());
        Assert.Equal("2026-10-01", shape.GetProperty("from").GetString());
        Assert.Equal("2026-10-31", shape.GetProperty("to").GetString());
        Assert.True(shape.GetProperty("searchApplied").GetBoolean());

        foreach (var column in typeof(AuditLog).GetProperties().Where(property => property.PropertyType == typeof(string)))
        {
            Assert.DoesNotContain("secret", (string?)column.GetValue(row) ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        // The reader was asked for the trimmed, upper-cased prefix, every row (no page size).
        Assert.Equal("SECRET.ADDRESS@", reader.LastEmailPrefix);
        Assert.Null(reader.LastPageSize);

        var text = Text(file.Content);
        Assert.Contains("\"'=1+2@example.com\",\"'-Nimal\",\"Kandy\",\"Yes\",\"2\",\"3000.00\",\"2026-10-02\",\"2026-10-09\"", text, StringComparison.Ordinal);
        Assert.Equal("customers-report-2026-10-01-to-2026-10-31.csv", file.FileName);
    }

    [Fact]
    public async Task The_sales_export_is_the_report_plus_a_total_row()
    {
        var audit = new RecordingAuditLog();
        var reader = new StubReader
        {
            Daily = [new DailySales(new DateOnly(2026, 10, 3), 1234.5m, 1, 2, 1000m, 234.5m, 0m)],
        };

        var file = await new ExportSalesReportCommandHandler(reader, audit, new FixedClock(Now))
            .HandleAsync(new ExportSalesReportCommand(October, ReportInterval.Month, Actor), CancellationToken.None);

        var lines = Text(file.Content).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.Equal(string.Join(',', ExportSalesReportCommandHandler.Header.Select(cell => $"\"{cell}\"")), lines[0]);
        Assert.Equal("\"2026-10-01\",\"2026-10-31\",\"2\",\"1\",\"1234.50\",\"1000.00\",\"234.50\",\"0.00\",\"1234.50\"", lines[1]);
        Assert.StartsWith("\"Total\",\"2026-10-31\"", lines[2], StringComparison.Ordinal);

        var shape = JsonDocument.Parse(Assert.Single(audit.Written).AfterJson!).RootElement;
        Assert.Equal("sales", shape.GetProperty("report").GetString());
        Assert.Equal("Month", shape.GetProperty("groupBy").GetString());
        Assert.Equal(1, shape.GetProperty("rows").GetInt32());
    }

    [Fact]
    public async Task The_inventory_export_names_today_in_Colombo_and_audits_the_filter()
    {
        var audit = new RecordingAuditLog();
        var reader = new StubReader
        {
            Inventory =
            [
                new InventoryReportRow(1, 2, "+Linen shirt", "SD-1", "Men", "SKU-1", "White", "M", 4, 1, 3, 5, true, "LowStock"),
            ],
        };

        var file = await new ExportInventoryReportCommandHandler(reader, audit, new FixedClock(Now))
            .HandleAsync(
                new ExportInventoryReportCommand(new InventoryReportFilter("linen", InventoryStockFilter.LowStock), Actor),
                CancellationToken.None);

        Assert.Equal("inventory-report-2026-10-16.csv", file.FileName);
        Assert.Contains("\"'+Linen shirt\"", Text(file.Content), StringComparison.Ordinal);
        Assert.EndsWith("\"Shop default\",\"Low stock\"\r\n", Text(file.Content), StringComparison.Ordinal);
        Assert.Null(reader.LastPageSize);

        var row = Assert.Single(audit.Written);
        var shape = JsonDocument.Parse(row.AfterJson!).RootElement;
        Assert.Equal("LowStock", shape.GetProperty("status").GetString());
        Assert.True(shape.GetProperty("searchApplied").GetBoolean());
        Assert.DoesNotContain("linen", row.AfterJson!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The audit row is written before the file exists: if it cannot be, there is no file.</summary>
    [Fact]
    public async Task No_audit_row_means_no_file()
    {
        var reader = new StubReader { Daily = [] };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ExportSalesReportCommandHandler(reader, new FailingAuditLog(), new FixedClock(Now))
                .HandleAsync(new ExportSalesReportCommand(October, ReportInterval.Day, Actor), CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ExportCustomerReportCommandHandler(reader, new FailingAuditLog(), new FixedClock(Now))
                .HandleAsync(new ExportCustomerReportCommand(October, null, Actor), CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ExportInventoryReportCommandHandler(reader, new FailingAuditLog(), new FixedClock(Now))
                .HandleAsync(new ExportInventoryReportCommand(new InventoryReportFilter(null, InventoryStockFilter.All), Actor), CancellationToken.None));
    }

    [Fact]
    public async Task Every_export_starts_with_a_byte_order_mark()
    {
        var reader = new StubReader { Daily = [] };
        var file = await new ExportSalesReportCommandHandler(reader, new RecordingAuditLog(), new FixedClock(Now))
            .HandleAsync(new ExportSalesReportCommand(October, ReportInterval.Day, Actor), CancellationToken.None);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, file.Content[..3]);
    }

    private static string Text(byte[] content) => Encoding.UTF8.GetString(content, 3, content.Length - 3);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class RecordingAuditLog : IAuditLogWriter
    {
        public List<AuditLog> Written { get; } = [];

        public Task WriteAsync(AuditLog entry, CancellationToken cancellationToken)
        {
            Written.Add(entry);
            return Task.CompletedTask;
        }

        public void Enlist(AuditLog entry) => Written.Add(entry);
    }

    private sealed class FailingAuditLog : IAuditLogWriter
    {
        public Task WriteAsync(AuditLog entry, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The audit log is unavailable.");

        public void Enlist(AuditLog entry) => throw new InvalidOperationException("The audit log is unavailable.");
    }

    private sealed class StubReader : IReportingReader
    {
        public IReadOnlyList<DailySales> Daily { get; init; } = [];

        public IReadOnlyList<CustomerReportRow> Customers { get; init; } = [];

        public IReadOnlyList<InventoryReportRow> Inventory { get; init; } = [];

        public string? LastEmailPrefix { get; private set; }

        public int? LastPageSize { get; private set; } = -1;

        public Task<IReadOnlyList<DailySales>> ReadDailyAsync(UtcRange range, CancellationToken cancellationToken) =>
            Task.FromResult(Daily);

        public Task<(IReadOnlyList<CustomerReportRow> Rows, int TotalCount, decimal TotalRevenue)> ReadCustomersAsync(
            UtcRange range,
            string? emailPrefix,
            int page,
            int? pageSize,
            CancellationToken cancellationToken)
        {
            LastEmailPrefix = emailPrefix;
            LastPageSize = pageSize;
            return Task.FromResult((Customers, Customers.Count, Customers.Sum(row => row.GrossRevenue)));
        }

        public Task<InventoryReportPage> ReadInventoryAsync(
            InventoryReportFilter filter,
            int page,
            int? pageSize,
            CancellationToken cancellationToken)
        {
            LastPageSize = pageSize;
            return Task.FromResult(new InventoryReportPage(Inventory, page, Inventory.Count, Inventory.Count, 5, new InventoryStatusCounts(0, 0, 0)));
        }

        public Task<SalesTotals> ReadTotalsAsync(UtcRange range, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<SalesGroupTotal>> ReadDepartmentSalesAsync(UtcRange range, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<SalesGroupTotal>> ReadTopCategoriesAsync(UtcRange range, int take, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProductSalesTotal>> ReadTopProductsAsync(UtcRange range, int take, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<int, decimal>> ReadProductRevenueAsync(UtcRange range, IReadOnlyCollection<int> productIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<OrderStatus, int>> ReadStatusCountsAsync(UtcRange range, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PaymentMethodRow>> ReadPaymentMethodsAsync(UtcRange range, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BuyersByDistrict> ReadBuyersByDistrictAsync(UtcRange range, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LowStockDto> ReadLowStockAsync(int take, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BuyerCounts> ReadBuyerCountsAsync(ReportPeriod period, ReportInterval interval, string? emailPrefix, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
