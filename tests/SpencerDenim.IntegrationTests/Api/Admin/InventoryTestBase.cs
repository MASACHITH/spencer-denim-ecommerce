using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Inventory;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.IntegrationTests.Sales;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// What the inventory suites (ADM-12, #190) share.
/// <para>
/// <b>Built on <see cref="CheckoutTestBase"/> on purpose.</b> Two of the races this screen has to win are
/// against a real order placement, so the suite needs real bags and real checkouts on the same API; and it
/// needs that base's stock bookkeeping, which takes variants from the <i>high</i> end of the catalogue
/// (away from the cart and report suites) and restores <c>OnHand</c> and a reconciled <c>Reserved</c>
/// afterwards. The threshold is this class's own to restore.
/// </para>
/// <para>
/// <b>Movements are never deleted</b> (doc 08 §1.6), so every assertion about them is scoped to rows after
/// a high-water mark taken at the start of the test.
/// </para>
/// </summary>
public abstract class InventoryTestBase(SqlServerDatabaseFixture sqlServer) : CheckoutTestBase(sqlServer)
{
    protected const string ListUrl = "/api/admin/inventory";
    protected const string AdjustUrl = "/api/admin/inventory/adjustments";

    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<int, int?> _originalThreshold = [];
    private readonly List<HttpClient> _clients = [];

    /// <summary>A staff client holding exactly <paramref name="permissions"/>, with the antiforgery header set.</summary>
    protected async Task<HttpClient> StaffAsync(IEnumerable<string> permissions)
    {
        var client = await MediaTestHost.SignInAsync(Factory, [.. permissions]);
        _clients.Add(client);
        return client;
    }

    protected Task<HttpClient> StaffAsync(params string[] permissions) => StaffAsync((IEnumerable<string>)permissions);

    /// <summary>Exactly these figures on one variant: on hand, reserved and its own threshold.</summary>
    protected async Task ArrangeAsync(int variantId, int onHand, int reserved = 0, int? threshold = null)
    {
        await SetStockAsync(variantId, onHand);

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!_originalThreshold.ContainsKey(variantId))
        {
            _originalThreshold[variantId] = await db.StockLevels
                .AsNoTracking()
                .Where(level => level.VariantId == variantId)
                .Select(level => level.LowStockThreshold)
                .SingleAsync(Token);
        }

        await db.StockLevels
            .Where(level => level.VariantId == variantId)
            .ExecuteUpdateAsync(
                level => level
                    .SetProperty(row => row.Reserved, reserved)
                    .SetProperty(row => row.LowStockThreshold, threshold),
                Token);
    }

    protected static object Row(int variantId, string kind, int quantity, int? expectedOnHand = null) =>
        new { variantId, kind, quantity, expectedOnHand };

    protected static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(HttpClient client, string url, object body)
    {
        using var response = await client.PostAsJsonAsync(new Uri(url, UriKind.Relative), body, Token);
        var text = await response.Content.ReadAsStringAsync(Token);

        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    protected static async Task<(HttpStatusCode Status, JsonElement Body)> PutAsync(HttpClient client, string url, object body)
    {
        using var response = await client.PutAsJsonAsync(new Uri(url, UriKind.Relative), body, Token);
        var text = await response.Content.ReadAsStringAsync(Token);

        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    protected static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative), Token);
        var text = await response.Content.ReadAsStringAsync(Token);

        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    /// <summary>A stock change that must be accepted (200), with its body.</summary>
    protected static async Task<JsonElement> AdjustAsync(HttpClient client, string reason, params object[] rows)
    {
        var (status, body) = await PostAsync(client, AdjustUrl, new { reason, rows });
        Assert.True(status == HttpStatusCode.OK, $"The stock change answered {(int)status}: {body}");
        return body;
    }

    protected async Task<long> MovementMarkAsync()
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().InventoryMovements
            .AsNoTracking()
            .MaxAsync(movement => (long?)movement.Id, Token) ?? 0;
    }

    protected async Task<List<InventoryMovement>> MovementsAfterAsync(long mark, int? variantId = null)
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().InventoryMovements
            .AsNoTracking()
            .Where(movement => movement.Id > mark && (variantId == null || movement.VariantId == variantId))
            .OrderBy(movement => movement.Id)
            .ToListAsync(Token);
    }

    protected async Task<long> AuditMarkAsync()
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .AsNoTracking()
            .MaxAsync(row => (long?)row.Id, Token) ?? 0;
    }

    protected async Task<List<AuditLog>> StockAuditAfterAsync(long mark)
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .AsNoTracking()
            .Where(row => row.Id > mark && row.EntityType == "StockLevel")
            .OrderBy(row => row.Id)
            .ToListAsync(Token);
    }

    protected async Task<int?> OwnThresholdAsync(int variantId)
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().StockLevels
            .AsNoTracking()
            .Where(level => level.VariantId == variantId)
            .Select(level => level.LowStockThreshold)
            .SingleAsync(Token);
    }

    protected async Task<string> SkuAsync(int variantId)
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProductVariants
            .AsNoTracking()
            .Where(variant => variant.Id == variantId)
            .Select(variant => variant.Sku)
            .SingleAsync(Token);
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        if (_originalThreshold.Count > 0 && SqlServer.IsAvailable)
        {
            using var scope = SqlServer.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            foreach (var (variantId, threshold) in _originalThreshold)
            {
                await db.StockLevels
                    .Where(level => level.VariantId == variantId)
                    .ExecuteUpdateAsync(level => level.SetProperty(row => row.LowStockThreshold, threshold), CancellationToken.None);
            }
        }

        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
