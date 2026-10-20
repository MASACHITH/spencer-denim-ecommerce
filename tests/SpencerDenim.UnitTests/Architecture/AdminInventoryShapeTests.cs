using System.Reflection;
using System.Text.RegularExpressions;
using SpencerDenim.Application.Inventory.Contracts;
using SpencerDenim.Domain.Inventory;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// Pins the inventory screen's payloads (ADM-12, #190) against their hand-written TypeScript copies and the
/// Playwright stub, in both directions — <c>AdminCustomerShapeTests</c>' arrangement. A stub or a client
/// model that drifts from the contract is how a screen goes green against a server it would fail on (#126).
/// </summary>
public sealed class AdminInventoryShapeTests
{
    private const string Model = "src/frontend/projects/admin/src/app/core/inventory/inventory.model.ts";
    private const string Stub = "src/frontend/e2e/support/admin-inventory-stub.mjs";

    private static readonly Regex Property = new(@"readonly\s+(?<name>\w+)\??:", RegexOptions.CultureInvariant);

    [Theory]
    [InlineData(typeof(InventoryListPage), "InventoryListPage")]
    [InlineData(typeof(InventoryRow), "InventoryRow")]
    [InlineData(typeof(SpencerDenim.Application.Admin.Reporting.Contracts.InventoryStatusCounts), "InventoryStatusCounts")]
    [InlineData(typeof(InventoryFilterOptions), "InventoryFilterOptions")]
    [InlineData(typeof(InventoryDepartmentOption), "InventoryDepartmentOption")]
    [InlineData(typeof(InventoryCategoryOption), "InventoryCategoryOption")]
    [InlineData(typeof(InventoryHistoryPage), "InventoryHistoryPage")]
    [InlineData(typeof(InventoryMovementRow), "InventoryMovementRow")]
    [InlineData(typeof(StockChangeResultDto), "StockChangeResult")]
    [InlineData(typeof(StockChangeRowResult), "StockChangeRowResult")]
    public void A_payload_has_exactly_the_properties_the_screen_reads(Type dto, string tsInterface) =>
        Assert.Equal(Names(dto), PropertiesOf(tsInterface));

    [Fact]
    public void The_stub_s_builders_carry_exactly_the_contract_s_fields()
    {
        var stub = ReadRepositoryFile(Stub);

        Assert.Equal(Names(typeof(InventoryRow)), ObjectKeys(Block(stub, "function inventoryRow(stock)")));
        Assert.Equal(Names(typeof(InventoryMovementRow)), ObjectKeys(Block(stub, "function movementRow(movement)")));
        Assert.Equal(Names(typeof(StockChangeRowResult)), ObjectKeys(Block(stub, "function changeResult(variantId, outcome, message, onHandDelta, stock)")));
    }

    [Fact]
    public void The_client_declares_the_names_the_enums_have()
    {
        var text = ReadRepositoryFile(Model);

        Assert.Equal(Enum.GetNames<InventoryMovementType>().Order(StringComparer.Ordinal), Union(text, "InventoryMovementType"));
        Assert.Equal(Enum.GetNames<StockChangeKind>().Order(StringComparer.Ordinal), Union(text, "StockChangeKind"));
        Assert.Equal(Enum.GetNames<StockChangeOutcome>().Order(StringComparer.Ordinal), Union(text, "StockChangeOutcome"));
        Assert.Equal(Enum.GetNames<InventorySort>().Order(StringComparer.Ordinal), Union(text, "InventorySort"));
        Assert.Equal(Enum.GetNames<InventorySortDirection>().Order(StringComparer.Ordinal), Union(text, "InventorySortDirection"));
    }

    /// <summary>The client's limits are the server's, so the form refuses exactly what the API would.</summary>
    [Fact]
    public void The_client_s_limits_are_the_server_s()
    {
        var text = ReadRepositoryFile(Model);
        var block = Block(text, "export const STOCK_LIMITS = ");

        Assert.Contains($"maxQuantity: {Underscored(StockChangeRules.MaxQuantity)}", Normalise(block), StringComparison.Ordinal);
        Assert.Contains($"maxOnHand: {Underscored(StockChangeRules.MaxOnHand)}", Normalise(block), StringComparison.Ordinal);
        Assert.Contains($"maxThreshold: {Underscored(StockChangeRules.MaxThreshold)}", Normalise(block), StringComparison.Ordinal);
        Assert.Contains($"maxBulkRows: {StockChangeRules.MaxBulkRows}", Normalise(block), StringComparison.Ordinal);
        Assert.Contains($"reasonMaxLength: {InventoryMovement.ReasonMaxLength}", Normalise(block), StringComparison.Ordinal);
    }

    private static string Normalise(string block) => Regex.Replace(block, @"\s+", " ");

    private static string Underscored(int value) =>
        value.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', '_');

    private static List<string> Union(string text, string type)
    {
        var start = text.IndexOf($"export type {type} =", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find `export type {type}`.");

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

    private static List<string> PropertiesOf(string interfaceName) =>
        Property.Matches(Block(ReadRepositoryFile(Model), $"interface {interfaceName} "))
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static List<string> ObjectKeys(string body)
    {
        // The builder's returned object literal: the keys at the first level of indentation inside `return {`.
        var start = body.IndexOf("return {", StringComparison.Ordinal);
        Assert.True(start >= 0, "The builder has no `return {`.");

        return Regex.Matches(body[start..], @"^\s{4}(\w+)[:,]", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

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
