using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// Low stock on the dashboard (ADM-08) and the inventory report (ADM-19), against SQL Server.
/// <para>
/// <b>Stock is live, so these tests cannot hide in a year of their own.</b> They set the stock of six
/// variants they choose — the lowest ids, which the order suites (who take from the top of the id range)
/// do not touch — and restore every value afterwards. Assertions about the whole shop are made as
/// <b>differences</b>: what the count was with all six well stocked, and what it is after.
/// </para>
/// <para>
/// <b>The threshold is the variant's own <c>LowStockThreshold</c>, or the shop default when it has none.</b>
/// The default is <c>Inventory.DefaultLowStockThreshold</c>, which #113 deferred from the settings seed and
/// which is read as <c>Catalog__DefaultLowStockThreshold</c> (<c>CatalogOptions</c>), default 5 — the value
/// doc 08 §9 seeds. The response carries the default it used, and the tests read it from there rather than
/// assuming 5.
/// </para>
/// </summary>
public sealed class AdminInventoryReportTests(SqlServerDatabaseFixture sqlServer) : ReportingTestBase(sqlServer), IAsyncDisposable
{
    private const string LowStockUrl = "/api/admin/dashboard/low-stock";
    private const string InventoryUrl = "/api/admin/reports/inventory";

    private readonly List<StockSnapshot> _changed = [];

    /// <summary>
    /// Doc 11 P16: "Live: 0 &lt; available ≤ threshold". At the threshold is low; one above is not; nothing
    /// available is out of stock, not low; a variant's own threshold overrides the default in both
    /// directions; an inactive variant is not listed. The dashboard's count and the report's LowStock filter
    /// apply the same rule.
    /// </summary>
    [Fact]
    public async Task Low_stock_is_above_zero_and_at_most_the_variant_s_threshold_or_the_default()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Dashboard.View, Permissions.Reports.View);

        var variants = await ChooseVariantsAsync(6);
        foreach (var variant in variants)
        {
            await SetStockAsync(variant, onHand: 500, threshold: null);
        }

        var baseline = await GetJsonAsync(client, LowStockUrl);
        var threshold = baseline.GetProperty("defaultThreshold").GetInt32();
        Assert.True(threshold >= 1, "The tests need a default of at least 1 to have a boundary to test.");
        var lowBefore = baseline.GetProperty("totalCount").GetInt32();
        var reportBefore = (await GetJsonAsync(client, InventoryUrl)).GetProperty("counts");

        var atDefault = variants[0];
        var aboveDefault = variants[1];
        var empty = variants[2];
        var ownLower = variants[3];
        var ownHigher = variants[4];
        var inactive = variants[5];

        await SetStockAsync(atDefault, onHand: threshold, threshold: null);            // low
        await SetStockAsync(aboveDefault, onHand: threshold + 1, threshold: null);     // in stock
        await SetStockAsync(empty, onHand: 0, threshold: null);                        // out of stock
        await SetStockAsync(ownLower, onHand: threshold, threshold: threshold - 1);    // in stock: its own is lower
        await SetStockAsync(ownHigher, onHand: threshold + 4, threshold: threshold + 4); // low: its own is higher
        await SetStockAsync(inactive, onHand: 1, threshold: null, active: false);      // not listed

        var after = await GetJsonAsync(client, LowStockUrl);
        Assert.Equal(lowBefore + 2, after.GetProperty("totalCount").GetInt32());

        // Every row shown obeys the rule and they are lowest first.
        var rows = after.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(Math.Min(10, lowBefore + 2), rows.Count);
        Assert.All(rows, row =>
        {
            var available = row.GetProperty("available").GetInt32();
            Assert.InRange(available, 1, row.GetProperty("threshold").GetInt32());
        });
        Assert.Equal(
            rows.Select(row => row.GetProperty("available").GetInt32()).Order(),
            rows.Select(row => row.GetProperty("available").GetInt32()));
        Assert.DoesNotContain(rows, row => row.GetProperty("variantId").GetInt32() == inactive.VariantId);

        Assert.Equal("LowStock", await StatusOfAsync(client, atDefault));
        Assert.Equal("InStock", await StatusOfAsync(client, aboveDefault));
        Assert.Equal("OutOfStock", await StatusOfAsync(client, empty));
        Assert.Equal("InStock", await StatusOfAsync(client, ownLower));
        Assert.Equal("LowStock", await StatusOfAsync(client, ownHigher));
        Assert.Null(await StatusOfAsync(client, inactive));

        var reportAfter = (await GetJsonAsync(client, InventoryUrl)).GetProperty("counts");
        Assert.Equal(reportBefore.GetProperty("lowStock").GetInt32() + 2, reportAfter.GetProperty("lowStock").GetInt32());
        Assert.Equal(reportBefore.GetProperty("outOfStock").GetInt32() + 1, reportAfter.GetProperty("outOfStock").GetInt32());
        Assert.Equal(reportBefore.GetProperty("inStock").GetInt32() - 4, reportAfter.GetProperty("inStock").GetInt32());

        // The report's LowStock filter is the dashboard's rule: the same count.
        var lowFiltered = await GetJsonAsync(client, $"{InventoryUrl}?status=LowStock&pageSize=1");
        Assert.Equal(after.GetProperty("totalCount").GetInt32(), lowFiltered.GetProperty("totalCount").GetInt32());

        // The threshold shown says whose it is.
        var own = await RowForAsync(client, ownHigher);
        Assert.Equal(threshold + 4, own!.Value.GetProperty("threshold").GetInt32());
        Assert.False(own.Value.GetProperty("thresholdIsDefault").GetBoolean());
        var inherited = await RowForAsync(client, atDefault);
        Assert.Equal(threshold, inherited!.Value.GetProperty("threshold").GetInt32());
        Assert.True(inherited.Value.GetProperty("thresholdIsDefault").GetBoolean());
    }

    /// <summary>
    /// Reserved stock is not available: a variant with plenty on hand and most of it held by unpaid orders
    /// is low (doc 08 §3.4: <c>Available = OnHand − Reserved</c>).
    /// </summary>
    [Fact]
    public async Task Reserved_stock_is_not_available()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Reports.View);

        var variant = (await ChooseVariantsAsync(1))[0];
        await SetStockAsync(variant, onHand: 100, threshold: 5, reserved: 97);

        var row = await RowForAsync(client, variant);
        Assert.Equal(100, row!.Value.GetProperty("onHand").GetInt32());
        Assert.Equal(97, row.Value.GetProperty("reserved").GetInt32());
        Assert.Equal(3, row.Value.GetProperty("available").GetInt32());
        Assert.Equal("LowStock", row.Value.GetProperty("stockStatus").GetString());
    }

    [Fact]
    public async Task The_inventory_report_pages_and_its_counts_cover_every_active_variant()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Reports.View);

        var all = await GetJsonAsync(client, $"{InventoryUrl}?page=0&pageSize=100000");
        Assert.Equal(1, all.GetProperty("page").GetInt32());
        Assert.Equal(100, all.GetProperty("pageSize").GetInt32());

        var counts = all.GetProperty("counts");
        var total = all.GetProperty("totalCount").GetInt32();
        Assert.Equal(
            total,
            counts.GetProperty("inStock").GetInt32() + counts.GetProperty("lowStock").GetInt32() + counts.GetProperty("outOfStock").GetInt32());

        using var scope = SqlServer.CreateScope();
        var expected = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProductVariants
            .AsNoTracking()
            .CountAsync(variant => variant.IsActive && !variant.Product!.IsDeleted, Token);
        Assert.Equal(expected, total);

        var second = await GetJsonAsync(client, $"{InventoryUrl}?page=2&pageSize=3");
        Assert.Equal(3, second.GetProperty("rows").GetArrayLength());
        var firstSix = await GetJsonAsync(client, $"{InventoryUrl}?page=1&pageSize=6");
        Assert.Equal(
            firstSix.GetProperty("rows")[3].GetProperty("variantId").GetInt32(),
            second.GetProperty("rows")[0].GetProperty("variantId").GetInt32());
    }

    /// <summary>The export is every matching variant, not a page, and is audited with its shape only.</summary>
    [Fact]
    public async Task The_inventory_export_is_every_matching_variant_and_is_audited()
    {
        await using var host = await StartAsync();
        using var viewer = await host.SignInAsync(Permissions.Reports.View);
        using var exporter = await host.SignInAsync(Permissions.Reports.Export);

        var variant = (await ChooseVariantsAsync(1))[0];
        await SetStockAsync(variant, onHand: 0, threshold: null);

        var outOfStock = (await GetJsonAsync(viewer, $"{InventoryUrl}?status=OutOfStock")).GetProperty("totalCount").GetInt32();
        var before = await LastAuditIdAsync();

        using var response = await exporter.PostAsync(new Uri($"{InventoryUrl}/export?status=OutOfStock", UriKind.Relative), null, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var bytes = await response.Content.ReadAsByteArrayAsync(Token);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(outOfStock + 1, lines.Length);
        Assert.StartsWith("\"Product\",\"Product code\",\"Department\",\"SKU\"", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, line => line.Contains($"\"{variant.Sku}\"", StringComparison.Ordinal) && line.EndsWith("\"Out of stock\"", StringComparison.Ordinal));

        var audit = Assert.Single(await AuditRowsAfterAsync(before));
        Assert.Equal(AuditActions.Export, audit.Action);
        Assert.Equal("InventoryReport", audit.EntityType);
        var shape = JsonDocument.Parse(audit.AfterJson!).RootElement;
        Assert.Equal(outOfStock, shape.GetProperty("rows").GetInt32());
        Assert.Equal("OutOfStock", shape.GetProperty("status").GetString());
        Assert.False(shape.GetProperty("searchApplied").GetBoolean());
    }

    public async ValueTask DisposeAsync()
    {
        if (_changed.Count == 0 || !SqlServer.IsAvailable)
        {
            return;
        }

        using var scope = SqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // One snapshot per variant, taken before its first change. OnHand and Reserved move in one UPDATE, so
        // the check constraint (Reserved <= OnHand) is only ever tested against the restored pair.
        foreach (var snapshot in Enumerable.Reverse(_changed))
        {
            await db.StockLevels
                .Where(stock => stock.VariantId == snapshot.VariantId)
                .ExecuteUpdateAsync(
                    stock => stock
                        .SetProperty(row => row.OnHand, snapshot.OnHand)
                        .SetProperty(row => row.Reserved, snapshot.Reserved)
                        .SetProperty(row => row.LowStockThreshold, snapshot.Threshold),
                    CancellationToken.None);

            await db.ProductVariants
                .Where(row => row.Id == snapshot.VariantId)
                .ExecuteUpdateAsync(row => row.SetProperty(column => column.IsActive, snapshot.IsActive), CancellationToken.None);
        }
    }

    private static async Task<string?> StatusOfAsync(HttpClient client, ChosenVariant variant) =>
        (await RowForAsync(client, variant))?.GetProperty("stockStatus").GetString();

    private static async Task<JsonElement?> RowForAsync(HttpClient client, ChosenVariant variant)
    {
        var page = await GetJsonAsync(client, $"{InventoryUrl}?search={Uri.EscapeDataString(variant.Sku)}&pageSize=100");

        foreach (var row in page.GetProperty("rows").EnumerateArray())
        {
            if (row.GetProperty("variantId").GetInt32() == variant.VariantId)
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>The lowest-id active variants of live products with nothing reserved.</summary>
    private async Task<List<ChosenVariant>> ChooseVariantsAsync(int count)
    {
        using var scope = SqlServer.CreateScope();

        var chosen = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProductVariants
            .AsNoTracking()
            .Where(variant => variant.IsActive && !variant.Product!.IsDeleted)
            .Where(variant => variant.StockLevel != null && variant.StockLevel.Reserved == 0)
            .OrderBy(variant => variant.Id)
            .Take(count)
            .Select(variant => new ChosenVariant(variant.Id, variant.Sku))
            .ToListAsync(Token);

        Assert.Equal(count, chosen.Count);
        return chosen;
    }

    private async Task SetStockAsync(ChosenVariant variant, int onHand, int? threshold, int reserved = 0, bool active = true)
    {
        using var scope = SqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!_changed.Exists(snapshot => snapshot.VariantId == variant.VariantId))
        {
            var current = await db.StockLevels
                .AsNoTracking()
                .Where(stock => stock.VariantId == variant.VariantId)
                .Select(stock => new { stock.OnHand, stock.Reserved, stock.LowStockThreshold, stock.Variant!.IsActive })
                .SingleAsync(Token);

            _changed.Add(new StockSnapshot(variant.VariantId, current.OnHand, current.Reserved, current.LowStockThreshold, current.IsActive));
        }

        // Reserved first to 0, so OnHand can drop below the old reservation without breaking the CK.
        await db.StockLevels
            .Where(stock => stock.VariantId == variant.VariantId)
            .ExecuteUpdateAsync(stock => stock.SetProperty(row => row.Reserved, 0), Token);

        await db.StockLevels
            .Where(stock => stock.VariantId == variant.VariantId)
            .ExecuteUpdateAsync(
                stock => stock
                    .SetProperty(row => row.OnHand, onHand)
                    .SetProperty(row => row.Reserved, reserved)
                    .SetProperty(row => row.LowStockThreshold, threshold),
                Token);

        await db.ProductVariants
            .Where(row => row.Id == variant.VariantId)
            .ExecuteUpdateAsync(row => row.SetProperty(column => column.IsActive, active), Token);
    }

    private sealed record ChosenVariant(int VariantId, string Sku);

    private sealed record StockSnapshot(int VariantId, int OnHand, int Reserved, int? Threshold, bool IsActive);
}
