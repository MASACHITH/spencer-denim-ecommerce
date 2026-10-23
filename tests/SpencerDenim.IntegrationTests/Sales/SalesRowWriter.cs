using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Inserts <c>sales</c> rows with raw SQL, so the schema tests exercise the <b>database's</b> rules rather
/// than EF's model.
/// <para>
/// <b>Why not EF.</b> A check constraint, a filtered unique index and a foreign key are only proved by a
/// statement the server rejects. Going through <c>DbSet.Add</c> would let EF's own validation — required
/// properties, navigation fixups, the audit interceptor — reject some of these rows before SQL Server ever
/// sees them, and the test would pass without the constraint existing. Everything here therefore builds the
/// INSERT itself and asserts on the <see cref="SqlException"/>.
/// </para>
/// <para>
/// Every row starts from a <b>valid</b> default set (<see cref="OrderRow"/> and friends) and the test
/// overrides exactly the one column whose rule is under test. That is what makes a failure attributable: if
/// the insert is refused, the only thing wrong with it is the override.
/// </para>
/// </summary>
public sealed class SalesRowWriter(AppDbContext dbContext)
{
    /// <summary>A valid, ordinary delivered order. Tests override one column at a time.</summary>
    public static Dictionary<string, object?> OrderRow(
        string paymentProviderCode,
        int deliveryMethodId,
        string deliveryMethodCode) => new(StringComparer.Ordinal)
    {
        ["OrderNumber"] = Unique("SD-2026-"),
        ["PublicId"] = Guid.NewGuid(),
        ["CheckoutIdempotencyKey"] = Guid.NewGuid(),
        ["Status"] = (byte)OrderStatus.PendingPayment,
        ["StockState"] = (byte)OrderStockState.Reserved,
        ["PaymentProviderCode"] = paymentProviderCode,
        ["Email"] = "shopper@example.com",
        ["EmailNormalized"] = "SHOPPER@EXAMPLE.COM",
        ["FirstName"] = "Nimal",

        // Owner decision 2026-10-21: a Sri Lankan order. The columns are NOT NULL with no default.
        ["DeliveryCountryCode"] = "LK",
        ["DeliveryCountryName"] = "Sri Lanka",
        ["DeliveryDistrictId"] = 1,
        ["DeliveryDistrictName"] = "Colombo",
        ["DeliveryZone"] = (byte)DeliveryZone.Colombo,
        ["DeliveryMethodId"] = deliveryMethodId,
        ["PickupLocationId"] = null,
        ["DeliveryMethodCode"] = deliveryMethodCode,
        ["DeliveryMethodName"] = "Standard delivery",
        ["EstimatedDaysMin"] = (byte)2,
        ["EstimatedDaysMax"] = (byte)5,
        ["Ship_RecipientName"] = "Nimal Perera",
        ["Ship_Phone"] = "0771234567",
        ["Ship_Line1"] = "221 Galle Road",
        ["Ship_City"] = "Colombo",
        ["Currency"] = SalesCurrency.Lkr,
        ["ItemsOriginalTotal"] = 1200m,
        ["ItemsListTotal"] = 1000m,
        ["ItemsSubtotal"] = 900m,
        ["MarkdownDiscountTotal"] = 200m,
        ["CampaignDiscountTotal"] = 100m,
        ["PromoDiscountAmount"] = 0m,
        ["DeliveryFeeAmount"] = 350m,
        ["DeliveryDiscountAmount"] = 0m,
        ["TaxAmount"] = 0m,
        ["GrandTotal"] = 1250m,
        ["RefundedAmount"] = 0m,
        ["PlacedAtUtc"] = DateTime.UtcNow,
        ["PaymentDueAtUtc"] = DateTime.UtcNow.AddHours(48),
    };

    /// <summary>A valid line on <paramref name="orderId"/>, priced so every <c>CK_OrderLines_*</c> holds.</summary>
    public static Dictionary<string, object?> OrderLineRow(
        int orderId,
        int productId,
        int variantId,
        int departmentId,
        int categoryId,
        short lineNumber = 1) => new(StringComparer.Ordinal)
    {
        ["OrderId"] = orderId,
        ["LineNumber"] = lineNumber,
        ["ProductId"] = productId,
        ["VariantId"] = variantId,
        ["DepartmentId"] = departmentId,
        ["CategoryId"] = categoryId,
        ["ProductName"] = "Slim jean",
        ["ProductCode"] = "SD-TEST-0001",
        ["Sku"] = Unique("SKU-"),
        ["ColourName"] = "Indigo",
        ["SizeLabel"] = "32",
        ["Quantity"] = (short)3,
        ["UnitOriginalPrice"] = 400m,
        ["UnitListPrice"] = 350m,
        ["UnitPrice"] = 300m,
        ["LineSubtotal"] = 900m,
        ["PromoDiscountAllocated"] = 0m,
        ["LineTotal"] = 900m,
        ["RefundedQuantity"] = (short)0,
    };

    public static Dictionary<string, object?> PaymentRow(
        int orderId,
        string providerCode,
        PaymentStatus status = PaymentStatus.Initiated,
        bool isOrderSettlement = false) => new(StringComparer.Ordinal)
    {
        ["OrderId"] = orderId,
        ["ProviderCode"] = providerCode,
        ["Status"] = (byte)status,
        ["IsOrderSettlement"] = isOrderSettlement,
        ["Amount"] = 1250m,
        ["Currency"] = SalesCurrency.Lkr,
        ["RefundedAmount"] = 0m,
        ["CreatedAtUtc"] = DateTime.UtcNow,
    };

    public static Dictionary<string, object?> RefundRow(int orderId, int paymentId) => new(StringComparer.Ordinal)
    {
        ["OrderId"] = orderId,
        ["PaymentId"] = paymentId,
        ["Amount"] = 1250m,
        ["Currency"] = SalesCurrency.Lkr,
        ["Method"] = (byte)RefundMethod.ManualBankTransfer,
        ["Status"] = (byte)RefundStatus.Completed,
        ["Reason"] = "Customer returned the goods",
        ["RestockItems"] = false,
        ["CreatedAtUtc"] = DateTime.UtcNow,
    };

    public static Dictionary<string, object?> PaymentEventRow(string providerCode) => new(StringComparer.Ordinal)
    {
        ["ProviderCode"] = providerCode,
        ["EventSource"] = (byte)PaymentEventSource.VerifiedWebhook,
        ["EventType"] = "PAYMENT.CAPTURE.COMPLETED",
        ["IdempotencyKey"] = Unique("PayPal:"),
        ["PayloadJson"] = """{"id":"CAP-1"}""",
        ["ProcessingStatus"] = (byte)PaymentEventProcessingStatus.Received,
        ["AttemptCount"] = (short)0,
        ["ReceivedAtUtc"] = DateTime.UtcNow,
    };

    public static Dictionary<string, object?> StatusHistoryRow(int orderId) => new(StringComparer.Ordinal)
    {
        ["OrderId"] = orderId,
        ["FromStatus"] = null,
        ["ToStatus"] = (byte)OrderStatus.PendingPayment,
        ["Source"] = (byte)OrderStatusChangeSource.Customer,
        ["OccurredAtUtc"] = DateTime.UtcNow,
    };

    public static Dictionary<string, object?> DeliveryMethodRow() => new(StringComparer.Ordinal)
    {
        ["Code"] = Unique("M"),
        ["Name"] = "Test method",
        ["IsEnabled"] = true,
        ["RequiresAddress"] = true,
        ["SortOrder"] = (short)1,
        ["CreatedAtUtc"] = DateTime.UtcNow,
    };

    public static Dictionary<string, object?> ZoneRateRow(int deliveryMethodId, DeliveryZone zone) =>
        new(StringComparer.Ordinal)
        {
            ["DeliveryMethodId"] = deliveryMethodId,
            ["DeliveryZone"] = (byte)zone,
            ["IsAvailable"] = true,
            ["Fee"] = 350m,
            ["FreeAboveAmount"] = 10000m,
            ["EstimatedDaysMin"] = (byte)2,
            ["EstimatedDaysMax"] = (byte)5,
            ["CreatedAtUtc"] = DateTime.UtcNow,
        };

    public static Dictionary<string, object?> CourierRow() => new(StringComparer.Ordinal)
    {
        ["Name"] = Unique("Courier "),
        ["TrackingUrlTemplate"] = $"https://track.example.com/{Courier.TrackingNumberPlaceholder}",
        ["IsActive"] = true,
        ["SortOrder"] = (short)1,
        ["CreatedAtUtc"] = DateTime.UtcNow,
    };

    public static Dictionary<string, object?> NumberSeriesRow() => new(StringComparer.Ordinal)
    {
        ["SeriesKey"] = Unique("ORDER-"),
        ["Prefix"] = "SD",
        ["NextValue"] = 1,
        ["UpdatedAtUtc"] = DateTime.UtcNow,
    };

    /// <summary>A value nothing else in the shared test database can collide with.</summary>
    public static string Unique(string prefix) =>
        prefix + Guid.NewGuid().ToString("N")[..10];

    /// <summary>Overrides one column and returns the row, so a test reads as one expression.</summary>
    public static Dictionary<string, object?> With(
        Dictionary<string, object?> row,
        string column,
        object? value)
    {
        ArgumentNullException.ThrowIfNull(row);

        row[column] = value;
        return row;
    }

    /// <summary>Inserts and returns the identity value.</summary>
    public async Task<int> InsertAsync(
        string table,
        Dictionary<string, object?> row,
        CancellationToken cancellationToken)
    {
        var (sql, parameters) = Build(table, row, withIdentity: true);
        var identity = await ExecuteAsync(sql, parameters, cancellationToken);

        return identity ?? throw new InvalidOperationException($"{table} returned no identity value.");
    }

    /// <summary>Inserts a row in a table with no identity column.</summary>
    public async Task InsertWithoutIdentityAsync(
        string table,
        Dictionary<string, object?> row,
        CancellationToken cancellationToken)
    {
        var (sql, parameters) = Build(table, row, withIdentity: false);

        await ExecuteAsync(sql, parameters, cancellationToken);
    }

    /// <summary>
    /// Inserts and expects SQL Server to refuse it, naming <paramref name="constraintOrIndex"/>. Asserting on
    /// the name matters: a test that only asserted "some SqlException" would pass when the row was rejected
    /// for an unrelated reason, which is exactly how a missing constraint hides.
    /// </summary>
    public async Task AssertRejectedAsync(
        string table,
        Dictionary<string, object?> row,
        string constraintOrIndex,
        CancellationToken cancellationToken)
    {
        var (sql, parameters) = Build(table, row, withIdentity: false);

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(sql, parameters, cancellationToken));

        Assert.Contains(constraintOrIndex, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs the statement on the context's own connection with ADO.NET.
    /// <para>
    /// <b>Not <c>SqlQueryRaw</c>.</b> EF composes a query over the SQL it is given and refuses anything
    /// non-composable, so <c>INSERT … ; SELECT SCOPE_IDENTITY()</c> cannot go through it. Going to the command
    /// directly also means the <see cref="SqlException"/> a test asserts on is the server's own, not one EF has
    /// re-wrapped.
    /// </para>
    /// </summary>
    private async Task<int?> ExecuteAsync(
        string sql,
        IReadOnlyList<SqlParameter> parameters,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100 // Every column name is a literal in this file; the values are parameters.
        command.CommandText = sql;
#pragma warning restore CA2100

        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        var scalar = await command.ExecuteScalarAsync(cancellationToken);

        return scalar is null or DBNull ? null : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static (string Sql, SqlParameter[] Parameters) Build(
        string table,
        Dictionary<string, object?> row,
        bool withIdentity)
    {
        var columns = row.Keys.ToList();
        var columnList = string.Join(", ", columns.Select(column => $"[{column}]"));
        var valueList = string.Join(", ", columns.Select((_, index) => $"@p{index}"));

        var sql = string.Create(
            CultureInfo.InvariantCulture,
            $"INSERT INTO {table} ({columnList}) VALUES ({valueList});");

        if (withIdentity)
        {
            sql += " SELECT CAST(SCOPE_IDENTITY() AS int);";
        }

        var parameters = columns
            .Select((column, index) => new SqlParameter($"@p{index}", row[column] ?? DBNull.Value))
            .ToArray();

        return (sql, parameters);
    }
}
