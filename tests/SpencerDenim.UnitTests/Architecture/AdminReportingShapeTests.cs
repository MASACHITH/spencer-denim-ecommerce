using System.Reflection;
using System.Text.RegularExpressions;
using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Application.Admin.Reporting.Contracts;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// Pins the dashboard and report payloads (W4-02) against their hand-written TypeScript copies, in both
/// directions, and the Playwright stub's row builders against the same records — a stub that answered a
/// field the server does not (or missed one it does) would let a dead screen look healthy (#126).
/// </summary>
public sealed class AdminReportingShapeTests
{
    private const string Model = "src/frontend/projects/admin/src/app/core/reporting/reporting.model.ts";
    private const string Stub = "src/frontend/e2e/support/admin-reporting-stub.mjs";

    private static readonly Regex Property = new(@"readonly\s+(?<name>\w+)\??:", RegexOptions.CultureInvariant);

    [Theory]
    [InlineData(typeof(ReportPeriodDto), "ReportPeriod")]
    [InlineData(typeof(ComparedFigure), "ComparedFigure")]
    [InlineData(typeof(DashboardKpis), "DashboardKpis")]
    [InlineData(typeof(SalesShareRow), "SalesShareRow")]
    [InlineData(typeof(TopProductRow), "TopProductRow")]
    [InlineData(typeof(OrderStatusCount), "OrderStatusCount")]
    [InlineData(typeof(PaymentMethodRow), "PaymentMethodRow")]
    [InlineData(typeof(BuyersByDistrict), "BuyersByDistrict")]
    [InlineData(typeof(DistrictBuyersRow), "DistrictBuyersRow")]
    [InlineData(typeof(AdminDashboardDto), "AdminDashboard")]
    [InlineData(typeof(SalesTrendPoint), "SalesTrendPoint")]
    [InlineData(typeof(SalesTrendDto), "SalesTrend")]
    [InlineData(typeof(LowStockRow), "LowStockRow")]
    [InlineData(typeof(LowStockDto), "LowStock")]
    [InlineData(typeof(SalesReportRow), "SalesReportRow")]
    [InlineData(typeof(SalesReportDto), "SalesReport")]
    [InlineData(typeof(InventoryStatusCounts), "InventoryStatusCounts")]
    [InlineData(typeof(InventoryReportRow), "InventoryReportRow")]
    [InlineData(typeof(InventoryReportPage), "InventoryReportPage")]
    [InlineData(typeof(CustomerReportRow), "CustomerReportRow")]
    [InlineData(typeof(CustomerReportPage), "CustomerReportPage")]
    [InlineData(typeof(CustomerBuyersPoint), "CustomerBuyersPoint")]
    [InlineData(typeof(CustomerBuyersDto), "CustomerBuyers")]
    public void A_payload_has_exactly_the_properties_the_screen_reads(Type dto, string tsInterface) =>
        Assert.Equal(Names(dto), PropertiesOf(Model, tsInterface));

    [Fact]
    public void The_client_declares_the_stock_filters_and_groupings_the_server_accepts()
    {
        var text = ReadRepositoryFile(Model);

        Assert.Equal(Enum.GetNames<InventoryStockFilter>().Order(StringComparer.Ordinal), Literals(text, "export type InventoryStockFilter"));
        Assert.Equal(Enum.GetNames<ReportInterval>().Order(StringComparer.Ordinal), Literals(text, "export type SalesGrouping"));
        Assert.Equal(["Month", "Week"], Literals(text, "export type TrendInterval"));
    }

    /// <summary>The stub's row builders return exactly the records' fields.</summary>
    [Theory]
    [InlineData(typeof(SalesReportRow), "function salesRow(")]
    [InlineData(typeof(InventoryReportRow), "function inventoryRow(")]
    [InlineData(typeof(CustomerReportRow), "function customerRow(")]
    [InlineData(typeof(LowStockRow), "function lowStockRow(")]
    [InlineData(typeof(CustomerBuyersPoint), "function buyersPoint(")]
    [InlineData(typeof(CustomerBuyersDto), "function buyersBody(")]
    [InlineData(typeof(ComparedFigure), "function figure(")]
    public void The_stub_s_rows_carry_exactly_the_contract_s_fields(Type dto, string builder)
    {
        var body = Block(ReadRepositoryFile(Stub), builder);
        var returned = body[body.LastIndexOf("return {", StringComparison.Ordinal)..];

        var inner = returned[(returned.IndexOf('{', StringComparison.Ordinal) + 1)..returned.LastIndexOf('}')];

        // One key per line when the object is written over several lines; a shorthand list when it is one.
        var keys = (inner.Contains('\n', StringComparison.Ordinal)
                ? Regex.Matches(inner, @"^\s*(\w+)\s*[:,]", RegexOptions.Multiline).Select(match => match.Groups[1].Value)
                : inner.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(Names(dto), keys);
    }

    private static List<string> Literals(string text, string marker)
    {
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find `{marker}`.");

        return Regex.Matches(text[start..text.IndexOf(';', start)], "'(\\w+)'")
            .Select(match => match.Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> Names(Type dto) =>
        dto.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => char.ToLowerInvariant(property.Name[0]) + property.Name[1..])
            .Order(StringComparer.Ordinal)
            .ToList();

    private static List<string> PropertiesOf(string file, string interfaceName) =>
        Property.Matches(Block(ReadRepositoryFile(file), $"interface {interfaceName} "))
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>The braces-balanced body after <paramref name="marker"/>.</summary>
    private static string Block(string text, string marker)
    {
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find `{marker}`.");

        var open = text.IndexOf('{', start);
        var depth = 0;

        for (var index = open; index < text.Length; index++)
        {
            depth += text[index] switch { '{' => 1, '}' => -1, _ => 0 };

            if (depth == 0)
            {
                return text[(open + 1)..index];
            }
        }

        throw new InvalidOperationException($"`{marker}` has no closing brace.");
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        throw new FileNotFoundException(relativePath);
    }
}
