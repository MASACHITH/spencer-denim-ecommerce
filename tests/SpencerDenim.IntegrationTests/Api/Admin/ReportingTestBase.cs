using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Common;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.IntegrationTests.Sales;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// What the dashboard and report tests (W4-02: ADM-04–08, ADM-19) need: orders with exactly the dates,
/// statuses and amounts a test chooses, on real catalogue rows, in a <b>year nobody else uses</b>.
/// <para>
/// <b>Why orders are inserted rather than placed.</b> The rules under test are about <i>dates</i> — a paid
/// date, a placed date, a refund's completion date, each on one side or the other of a Colombo midnight —
/// and checkout stamps every one of them with "now". So the rows are written with <see cref="SalesRowWriter"/>
/// (raw SQL, every check constraint still enforced by the server), and the totals are computed here so
/// <c>CK_Orders_Totals</c> and <c>CK_OrderLines_*</c> hold. The reconciliation test also places real
/// orders through checkout and then moves their dates, so the arithmetic is proved on the real path too.
/// </para>
/// <para>
/// <b>Why a year of its own per test.</b> The dashboard is the whole shop's figures, and the assembly shares
/// one database: any other suite's order would be in an answer about today. Every test here asks about a
/// period in a year between 2007 and 2016, which no other test writes orders into (the latest survey found
/// 2000, 2019, 2020 and 2026 in use), and each test owns its own year. The low-stock tests cannot be
/// moved in time, so they assert on differences and on rows they own instead.
/// </para>
/// </summary>
public abstract class ReportingTestBase(SqlServerDatabaseFixture sqlServer)
{
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected SqlServerDatabaseFixture SqlServer { get; } = sqlServer;

    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A marker unique to this test, for email addresses.</summary>
    protected string Marker { get; } = "w402" + Guid.NewGuid().ToString("N")[..10];

    protected async Task<MediaTestHost> StartAsync()
    {
        SqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(SqlServer.ConnectionString, Token);

        return MediaTestHost.Create(SqlServer.ConnectionString);
    }

    /// <summary>The UTC instant of a Colombo wall-clock time.</summary>
    protected static DateTime Colombo(int year, int month, int day, int hour = 12, int minute = 0) =>
        new DateTimeOffset(year, month, day, hour, minute, 0, ColomboTime.Offset).UtcDateTime;

    protected static string Day(int year, int month, int day) =>
        new DateOnly(year, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    protected string Email(string name) => $"{name}.{Marker}@example.com";

    /// <summary>Real catalogue rows to hang lines on: two products from each department, in different categories.</summary>
    protected async Task<CatalogueRows> CatalogueAsync()
    {
        using var scope = SqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var variants = await db.ProductVariants
            .AsNoTracking()
            .OrderBy(variant => variant.Product!.DepartmentId)
            .ThenBy(variant => variant.ProductId)
            .ThenBy(variant => variant.Id)
            .Select(variant => new
            {
                variant.Id,
                variant.ProductId,
                variant.Product!.DepartmentId,
                CategoryId = variant.Product.PrimaryCategoryId,
                Department = variant.Product.Department!.Name,
            })
            .ToListAsync(Token);

        var picks = variants
            .GroupBy(variant => variant.DepartmentId)
            .SelectMany(department => department
                .GroupBy(variant => variant.CategoryId)
                .Take(2)
                .Select(category => category.First()))
            .Select(row => new Pick(row.ProductId, row.Id, row.DepartmentId, row.CategoryId, row.Department))
            .ToList();

        Assert.True(picks.Select(pick => pick.DepartmentId).Distinct().Count() >= 3, "The seeded catalogue has three departments.");

        var districts = await db.Districts.AsNoTracking().ToDictionaryAsync(district => district.Name, district => district.Id, Token);
        var standard = await db.DeliveryMethods
            .AsNoTracking()
            .Where(method => method.Code == DeliveryMethod.StandardCode)
            .Select(method => method.Id)
            .SingleAsync(Token);

        return new CatalogueRows(picks, districts, standard);
    }

    /// <summary>
    /// Inserts one order with its lines. Totals are derived so every check constraint holds:
    /// items subtotal = Σ unit price × quantity, promo = Σ allocations, grand total = items − promo + delivery
    /// fee − delivery discount.
    /// </summary>
    protected async Task<InsertedOrder> InsertOrderAsync(CatalogueRows catalogue, OrderSpec spec)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(spec);

        using var scope = SqlServer.CreateScope();
        var writer = new SalesRowWriter(scope.ServiceProvider.GetRequiredService<AppDbContext>());

        var subtotal = spec.Lines.Sum(line => line.UnitPrice * line.Quantity);
        var promo = spec.Lines.Sum(line => line.Promo);
        var grand = subtotal - promo + spec.DeliveryFee - spec.DeliveryDiscount;
        var district = spec.District ?? "Colombo";

        var row = SalesRowWriter.OrderRow(spec.Provider, catalogue.StandardDeliveryId, DeliveryMethod.StandardCode);
        row["Status"] = (byte)spec.Status;
        row["StockState"] = (byte)(spec.PaidUtc is null ? OrderStockState.Released : OrderStockState.Committed);
        row["Email"] = spec.Email;
        row["EmailNormalized"] = spec.Email.ToUpperInvariant();
        row["FirstName"] = spec.FirstName;
        row["LastName"] = spec.LastName;
        row["CustomerUserId"] = spec.CustomerUserId;
        row["DeliveryDistrictId"] = catalogue.Districts[district];
        row["DeliveryDistrictName"] = district;
        row["ItemsOriginalTotal"] = subtotal;
        row["ItemsListTotal"] = subtotal;
        row["ItemsSubtotal"] = subtotal;
        row["MarkdownDiscountTotal"] = 0m;
        row["CampaignDiscountTotal"] = 0m;
        row["PromoDiscountAmount"] = promo;
        row["DeliveryFeeAmount"] = spec.DeliveryFee;
        row["DeliveryDiscountAmount"] = spec.DeliveryDiscount;
        row["GrandTotal"] = grand;
        row["PlacedAtUtc"] = spec.PlacedUtc;
        // An unpaid order this base writes must never be due: OrderExpiryTests assert that nothing else in the
        // database is pending and past its deadline before 2020, and a PendingPayment row dated 2007 with its
        // deadline 48 hours later was exactly that — it broke two of them whenever they ran after this suite
        // (W4-02 tester, found in a whole-assembly run). The reporting rules never read the deadline.
        row["PaymentDueAtUtc"] = spec.Status == OrderStatus.PendingPayment
            ? new DateTime(2099, 12, 31, 0, 0, 0, DateTimeKind.Utc)
            : spec.PlacedUtc.AddHours(48);
        row["PaidAtUtc"] = spec.PaidUtc;

        var orderId = await writer.InsertAsync("[sales].[Orders]", row, Token);

        short number = 1;
        foreach (var line in spec.Lines)
        {
            var lineRow = SalesRowWriter.OrderLineRow(
                orderId,
                line.Pick.ProductId,
                line.Pick.VariantId,
                line.Pick.DepartmentId,
                line.Pick.CategoryId,
                number++);
            lineRow["Quantity"] = (short)line.Quantity;
            lineRow["UnitOriginalPrice"] = line.UnitPrice;
            lineRow["UnitListPrice"] = line.UnitPrice;
            lineRow["UnitPrice"] = line.UnitPrice;
            lineRow["LineSubtotal"] = line.UnitPrice * line.Quantity;
            lineRow["PromoDiscountAllocated"] = line.Promo;
            lineRow["LineTotal"] = line.UnitPrice * line.Quantity - line.Promo;

            await writer.InsertAsync("[sales].[OrderLines]", lineRow, Token);
        }

        return new InsertedOrder(orderId, grand, subtotal - promo, spec.DeliveryFee - spec.DeliveryDiscount);
    }

    /// <summary>
    /// A refund of <paramref name="amount"/> against a payment on the order: the settlement payment (created
    /// on first use and passed back for the next refund, because an order has at most one —
    /// <c>UX_Payments_OneSettlement</c>), or a payment exception when <paramref name="settlement"/> is false.
    /// </summary>
    /// <returns>The payment refunded, to pass as <paramref name="paymentId"/> for another refund of it.</returns>
    protected async Task<int> InsertRefundAsync(
        int orderId,
        decimal amount,
        DateTime? completedUtc,
        RefundStatus status = RefundStatus.Completed,
        bool settlement = true,
        int? paymentId = null)
    {
        using var scope = SqlServer.CreateScope();
        var writer = new SalesRowWriter(scope.ServiceProvider.GetRequiredService<AppDbContext>());

        if (paymentId is null)
        {
            var payment = SalesRowWriter.PaymentRow(
                orderId,
                PaymentProviderCodes.BankTransfer,
                PaymentStatus.Refunded,
                isOrderSettlement: settlement);
            payment["Amount"] = amount;

            paymentId = await writer.InsertAsync("[sales].[Payments]", payment, Token);
        }

        var refund = SalesRowWriter.RefundRow(orderId, paymentId.Value);
        refund["Amount"] = amount;
        refund["Status"] = (byte)status;
        refund["CompletedAtUtc"] = completedUtc;

        await writer.InsertAsync("[sales].[Refunds]", refund, Token);

        return paymentId.Value;
    }

    protected static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative), Token);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{url} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Token)}");

        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
    }

    protected static decimal Money(JsonElement element) => element.GetDecimal();

    protected async Task<long> LastAuditIdAsync()
    {
        using var scope = SqlServer.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .AsNoTracking()
            .MaxAsync(row => (long?)row.Id, Token) ?? 0;
    }

    /// <summary>Every audit row written after <paramref name="id"/>, oldest first.</summary>
    protected async Task<List<AuditLog>> AuditRowsAfterAsync(long id)
    {
        using var scope = SqlServer.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .AsNoTracking()
            .Where(row => row.Id > id)
            .OrderBy(row => row.Id)
            .ToListAsync(Token);
    }

    /// <summary>One catalogue row to sell.</summary>
    protected sealed record Pick(int ProductId, int VariantId, int DepartmentId, int CategoryId, string Department);

    protected sealed record CatalogueRows(
        IReadOnlyList<Pick> Picks,
        IReadOnlyDictionary<string, int> Districts,
        int StandardDeliveryId)
    {
        /// <summary>The first pick in each of the first three departments, in department order.</summary>
        public IReadOnlyList<Pick> OnePerDepartment =>
            Picks.GroupBy(pick => pick.DepartmentId).Select(group => group.First()).Take(3).ToList();

        /// <summary>Two picks in one department, different categories.</summary>
        public (Pick First, Pick Second) TwoInOneDepartment()
        {
            var group = Picks.GroupBy(pick => pick.DepartmentId).First(department => department.Count() >= 2);

            return (group.First(), group.Skip(1).First());
        }
    }

    protected sealed record LineSpec(Pick Pick, int Quantity, decimal UnitPrice, decimal Promo = 0m);

    protected sealed record OrderSpec(
        string Email,
        DateTime PlacedUtc,
        DateTime? PaidUtc,
        OrderStatus Status,
        IReadOnlyList<LineSpec> Lines,
        decimal DeliveryFee = 400m,
        decimal DeliveryDiscount = 0m,
        string Provider = PaymentProviderCodes.BankTransfer,
        string? District = null,
        string FirstName = "Nimali",
        string? LastName = "Perera",
        int? CustomerUserId = null);

    /// <summary>The order's id and the three figures a test adds up: grand total, items after promo, delivery.</summary>
    protected sealed record InsertedOrder(int OrderId, decimal GrandTotal, decimal Items, decimal Delivery);
}
