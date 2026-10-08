using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Hosts the real API against the migrated and seeded test database, for the checkout endpoints
/// (CHK-01..04).
/// <para>
/// It extends <see cref="CartTestBase"/> rather than repeating its setup: checkout <b>is</b> a view of the
/// bag, every request is identified by the same <c>sd_cart</c> cookie, and the base already handles the one
/// detail that is easy to get wrong — the host is addressed as <c>https://localhost</c>, because the cart
/// cookie is unconditionally <c>Secure</c> and a suite over <c>http</c> would silently be testing a shop in
/// which the bag never persists.
/// </para>
/// </summary>
public abstract class CheckoutTestBase(SqlServerDatabaseFixture sqlServer) : CartTestBase(sqlServer)
{
    /// <summary>
    /// Every variant whose stock this test arranged, with the <c>OnHand</c> it had beforehand, so that
    /// <see cref="DisposeAsync"/> can put the catalogue back.
    /// </summary>
    private readonly Dictionary<int, int> _originalOnHand = [];

    /// <summary>A fresh browser for the checkout endpoints, with its own cookie jar.</summary>
    protected CheckoutApiClient NewCheckoutVisitor() => new(NewVisitor().Http);

    /// <summary>A client that keeps no cookies, for sending a bag token by hand.</summary>
    protected CheckoutApiClient NewCheckoutVisitorWithoutCookieJar() =>
        new(NewVisitorWithoutCookieJar().Http);

    /// <summary>
    /// A fresh guest bag holding <paramref name="quantity"/> of <paramref name="variantId"/>, with the
    /// token the cookie carries and the cart's id.
    /// </summary>
    protected async Task<(Guid Token, int CartId, CheckoutApiClient Api)> NewBagAsync(
        int variantId,
        int quantity = 1)
    {
        var cart = NewVisitor();
        var (response, _) = await cart.AddAsync(variantId, quantity, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var setCookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("sd_cart=", StringComparison.Ordinal));
        var token = Guid.ParseExact(setCookie["sd_cart=".Length..].Split(';')[0], "D");

        using var scope = CreateScope();
        var cartId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Carts
            .AsNoTracking()
            .Where(candidate => candidate.Token == token)
            .Select(candidate => candidate.Id)
            .SingleAsync(Token);

        // The same HttpClient, so the cookie jar that now holds the bag token is the one that checks out.
        return (token, cartId, new CheckoutApiClient(cart.Http));
    }

    /// <summary>
    /// <paramref name="count"/> distinct in-stock variants for a checkout test, taken from the
    /// <b>high</b> end of the catalogue.
    /// </summary>
    /// <remarks>
    /// <b>Checkout is the one suite that takes stock and never gives it back</b>, because an order is
    /// never deleted (doc 08 §1.6) and a placed order holds its reservation for ever. Every other suite
    /// asks <c>InStockVariantIdsAsync</c>, which hands out the <i>lowest</i> ids, so a checkout test that
    /// used those left a permanent <c>Reserved</c> on the exact rows the cart suites arrange — and
    /// <c>CartStaleItemTests.WithStockAsync</c> sets <c>OnHand</c> alone, to figures as low as 0, which
    /// then either violates <c>CK_StockLevels_ReservedLeOnHand</c> outright or quietly changes the
    /// <c>Available</c> the test is measuring. Four of its tests went red in a full run and green on
    /// their own, twice, before this was understood: the fix is not a cleverer restore, it is not sharing
    /// the fixture at all.
    /// <para>
    /// Descending by id, so the two ends of the catalogue cannot meet until a suite asks for sixty of
    /// them. <see cref="SetStockAsync"/> still records and restores what it touches, which keeps the
    /// shop's shelves as it found them.
    /// </para>
    /// </remarks>
    protected async Task<IReadOnlyList<int>> CheckoutVariantIdsAsync(int count)
    {
        SqlServer.SkipIfUnavailable();

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var ids = await db.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.IsActive && variant.Product!.IsPublished)
            .Where(variant =>
                variant.Product!.Department!.IsActive && variant.Product!.PrimaryCategory!.IsActive)
            .Where(variant => variant.StockLevel != null && variant.StockLevel.Available > 0)
            .OrderByDescending(variant => variant.Id)
            .Select(variant => variant.Id)
            .Take(count)
            .ToListAsync(Token);

        Assert.Equal(count, ids.Count);
        return ids;
    }

    /// <summary>One in-stock variant for a checkout test. See <see cref="CheckoutVariantIdsAsync"/>.</summary>
    protected async Task<int> CheckoutVariantIdAsync() => (await CheckoutVariantIdsAsync(1))[0];

    /// <summary>The id of the seeded district with this name (<c>Colombo</c>, <c>Jaffna</c>, …).</summary>
    protected async Task<int> DistrictIdAsync(string name)
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Districts
            .AsNoTracking()
            .Where(district => district.Name == name)
            .Select(district => district.Id)
            .SingleAsync(Token);
    }

    /// <summary>The id of the seeded delivery method with this code.</summary>
    protected async Task<int> DeliveryMethodIdAsync(string code)
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().DeliveryMethods
            .AsNoTracking()
            .Where(method => method.Code == code)
            .Select(method => method.Id)
            .SingleAsync(Token);
    }

    /// <summary>
    /// Sets a variant's stock to exactly <paramref name="onHand"/> with nothing reserved, so a test can
    /// arrange "one unit left" rather than hoping the seed happens to hold one.
    /// </summary>
    /// <remarks>
    /// <b>It zeroes <c>Reserved</c>, which detaches any reservation an earlier test's order still holds.</b>
    /// That is deliberate — a round of a concurrency test needs a known starting point and orders are never
    /// deleted (doc 08 §1.6) — but it means doc 08 §5.9's reconciliation invariant is **false** across a
    /// call to this method. Any test that asserts that invariant must arrange its own variants with this
    /// method first and then place every order it is going to count, which is what
    /// <c>Reserved_stock_equals_the_lines_of_the_orders_that_hold_it</c> does. A test that asserted it over
    /// variants another test had touched would be measuring this helper.
    /// </remarks>
    protected async Task SetStockAsync(int variantId, int onHand)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Recorded the first time only, so the value kept is the one the seeder wrote rather than
        // whatever a previous call in the same test set.
        if (!_originalOnHand.ContainsKey(variantId))
        {
            _originalOnHand[variantId] = await db.StockLevels
                .AsNoTracking()
                .Where(level => level.VariantId == variantId)
                .Select(level => level.OnHand)
                .SingleAsync(Token);
        }

        var updated = await db.StockLevels
            .Where(level => level.VariantId == variantId)
            .ExecuteUpdateAsync(
                level => level
                    .SetProperty(row => row.OnHand, onHand)
                    .SetProperty(row => row.Reserved, 0)
                    .SetProperty(row => row.UpdatedAtUtc, DateTime.UtcNow),
                Token);

        Assert.Equal(1, updated);
    }

    /// <summary>
    /// Puts back every variant's stock that this test arranged, and leaves <c>Reserved</c> consistent
    /// with the orders that still hold it.
    /// </summary>
    /// <remarks>
    /// <b>This exists because four <c>CartStaleItemTests</c> failed in a full run and passed on their
    /// own.</b> The assembly shares one database, <c>InStockVariantIdsAsync</c> hands every suite the
    /// same low-numbered variants, and the cart suites read the <i>seeded</i> stock of exactly those —
    /// so a checkout test that set one to 1 and walked away had changed what a later test measured. The
    /// classic shape of a test measuring its neighbours, and the classic fix: own what you changed.
    /// <para>
    /// <c>OnHand</c> goes back to the recorded value. <c>Reserved</c> is <b>recomputed</b> rather than
    /// restored, from <c>Σ OrderLines.Quantity</c> over the orders in <c>StockState = 0</c> — which is
    /// doc 08 §5.9's reconciliation definition, so the row is left consistent by construction. Restoring
    /// the old figure instead would under-report: the orders this test placed are real, they are never
    /// deleted (doc 08 §1.6), and they still hold their stock.
    /// </para>
    /// </remarks>
    public override async ValueTask DisposeAsync()
    {
        if (_originalOnHand.Count > 0 && SqlServer.IsAvailable)
        {
            using var scope = SqlServer.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            foreach (var (variantId, onHand) in _originalOnHand)
            {
                var held = await db.OrderLines
                    .AsNoTracking()
                    .Where(line =>
                        line.VariantId == variantId
                        && line.Order!.StockState == OrderStockState.Reserved)
                    .SumAsync(line => (int)line.Quantity, CancellationToken.None);

                // **`OnHand = original + held`, so that `Available` comes back to the value the seeder
                // wrote.** `Available` is the computed column every other suite reads, and restoring
                // `OnHand` alone leaves it short by whatever this test's orders are holding — which is
                // the first version of this restore, and it left `CartStaleItemTests` failing exactly as
                // before. The state it leaves is a real one: the shop received enough stock to cover the
                // orders that were placed, so the shelf is as full as it was.
                await db.StockLevels
                    .Where(level => level.VariantId == variantId)
                    .ExecuteUpdateAsync(
                        level => level
                            // Both in one SET, so `CK_StockLevels_ReservedLeOnHand` sees them together.
                            .SetProperty(row => row.OnHand, onHand + held)
                            .SetProperty(row => row.Reserved, held)
                            .SetProperty(row => row.UpdatedAtUtc, DateTime.UtcNow),
                        CancellationToken.None);
            }

            _originalOnHand.Clear();
        }

        // The base call disposes the clients and the host, and suppresses finalization. Repeated here
        // because CA1816 asks every DisposeAsync to do it and cannot see through the base call.
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// <c>Reserved</c> for one variant, for the many assertions that are about a <b>change</b> in it.
    /// <para>
    /// Almost nothing in this suite can assert <c>Reserved == 0</c>: orders are never deleted (doc 08
    /// §1.6), so an order an earlier test placed is still holding its stock on the same low-numbered
    /// variants <c>InStockVariantIdsAsync</c> hands everybody. Taking the figure before and after is the
    /// only assertion that means what it says — and getting that wrong is what made thirteen validation
    /// tests red in a full run and green on their own.
    /// </para>
    /// </summary>
    protected async Task<int> ReservedAsync(int variantId) => (await StockAsync(variantId)).Reserved;

    /// <summary><c>OnHand</c>, <c>Reserved</c> and <c>Available</c> for one variant.</summary>
    protected async Task<(int OnHand, int Reserved, int Available)> StockAsync(int variantId)
    {
        using var scope = CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().StockLevels
            .AsNoTracking()
            .Where(level => level.VariantId == variantId)
            .Select(level => new { level.OnHand, level.Reserved, level.Available })
            .SingleAsync(Token);

        return (row.OnHand, row.Reserved, row.Available);
    }

    /// <summary>Every order whose number is in this list, with its lines, payments and history.</summary>
    protected async Task<List<Order>> OrdersByNumberAsync(IEnumerable<string> orderNumbers)
    {
        var numbers = orderNumbers.ToList();

        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
            .AsNoTracking()
            .Include(order => order.Lines)
            .Include(order => order.Payments)
            .Include(order => order.StatusHistory)
            .Where(order => numbers.Contains(order.OrderNumber))
            .ToListAsync(Token);
    }

    /// <summary>One order by its number, with its children.</summary>
    protected async Task<Order> OrderByNumberAsync(string orderNumber) =>
        Assert.Single(await OrdersByNumberAsync([orderNumber]));

    /// <summary>
    /// How many orders exist in the whole database.
    /// <para>
    /// <b>Compared before and after, never against a time window.</b> The suite shares one database and
    /// doc 08 §1.6 forbids deleting orders, so "no order placed in the last minute" counts the previous
    /// test's order and fails. That mistake was made twice in this task before it was caught, which is why
    /// the helper exists at all rather than each test writing its own count.
    /// </para>
    /// </summary>
    protected async Task<int> OrderCountAsync()
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders.CountAsync(Token);
    }

    /// <summary>How many lines the bag still holds.</summary>
    protected async Task<int> CartItemCountAsync(int cartId)
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().CartItems
            .AsNoTracking()
            .CountAsync(item => item.CartId == cartId, Token);
    }

    /// <summary>
    /// The grand total the server quoted, with a readable failure when there is not one.
    /// </summary>
    /// <remarks>
    /// <b><c>session.Totals.GrandTotal!.Value</c> threw instead of failing</b>, and twenty-eight call
    /// sites were written that way. A quote that unexpectedly carries no total — the delivery option
    /// vanished, the zone rate went unavailable — turned "expected 400, got 201" into a
    /// <c>NullReferenceException</c> inside the arrange step, which is a confusing crash in the test's
    /// setup rather than a statement about the behaviour under test. These two assertions name the real
    /// problem instead.
    /// </remarks>
    protected static decimal QuotedTotal(CheckoutSessionDto? session)
    {
        Assert.NotNull(session);
        Assert.NotNull(session.Totals.GrandTotal);

        return session.Totals.GrandTotal.Value;
    }

    /// <summary>
    /// A complete, valid body for a Standard delivery to a district, for the tests whose subject is
    /// something other than the fields.
    /// </summary>
    protected static Dictionary<string, object?> ValidBody(
        Guid checkoutKey,
        int deliveryMethodId,
        int districtId,
        decimal expectedGrandTotal) =>
        new(StringComparer.Ordinal)
        {
            ["checkoutKey"] = checkoutKey,
            ["firstName"] = "Nimali",
            ["lastName"] = "Perera",
            ["email"] = "nimali@example.com",
            ["phone"] = "0771234567",
            ["deliveryMethodId"] = deliveryMethodId,
            ["districtId"] = districtId,
            ["paymentMethodCode"] = PaymentProviderCodes.BankTransfer,
            ["expectedGrandTotal"] = expectedGrandTotal,
            ["address"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["recipientName"] = "Nimali Perera",
                ["phone"] = "0771234567",
                ["line1"] = "14 Galle Road",
                ["line2"] = "Flat 3",
                ["city"] = "Kollupitiya",
                ["postalCode"] = "00300",
                ["landmark"] = "Opposite the temple",
            },
        };
}

/// <summary>
/// A thin wrapper over the checkout endpoints, so a test reads as a request and an assertion.
/// <para>
/// Like <c>CartApiClient</c> it keeps a <b>raw</b> way to send a body, because the failures this suite
/// exists to catch include a body that is not what a typed helper could express — issue #47's <c>{}</c>
/// and <c>{"email":"a@b.c"}</c> are the whole point, and a typed request object cannot send them.
/// </para>
/// </summary>
public sealed class CheckoutApiClient(HttpClient client)
{
    public const string Path = "/api/checkout";
    public const string OrdersPath = "/api/checkout/orders";
    public const string OrderPath = "/api/orders";

    public HttpClient Http => client;

    public static JsonSerializerOptions JsonOptions { get; } = CatalogApiClient.JsonOptions;

    /// <summary>GET the checkout, optionally quoted for a district and a method.</summary>
    public async Task<(HttpResponseMessage Response, CheckoutSessionDto? Session)> GetAsync(
        int? districtId,
        int? deliveryMethodId,
        CancellationToken cancellationToken)
    {
        var query = new List<string>();

        if (districtId is { } district)
        {
            query.Add($"districtId={district}");
        }

        if (deliveryMethodId is { } method)
        {
            query.Add($"deliveryMethodId={method}");
        }

        var path = query.Count == 0 ? Path : $"{Path}?{string.Join('&', query)}";
        var response = await client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);

        return (response, await ReadAsync<CheckoutSessionDto>(response, cancellationToken));
    }

    /// <summary>POST a body that is serialised from an object.</summary>
    public async Task<(HttpResponseMessage Response, OrderConfirmationDto? Order)> PlaceAsync(
        object body,
        CancellationToken cancellationToken)
    {
        var response = await client.PostAsJsonAsync(
            new Uri(OrdersPath, UriKind.Relative),
            body,
            cancellationToken);

        return (response, await ReadAsync<OrderConfirmationDto>(response, cancellationToken));
    }

    /// <summary>
    /// POST exactly these bytes with exactly this media type. Nothing is serialised for you — this is how
    /// #47's shapes, and a body that is not JSON at all, are sent.
    /// </summary>
    public async Task<HttpResponseMessage> PlaceRawAsync(
        string? body,
        string? contentType,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(OrdersPath, UriKind.Relative));

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);

            // Set explicitly, so a type carrying a parameter survives and "no type at all" is expressible.
            request.Content.Headers.ContentType = contentType is null
                ? null
                : System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>POST carrying an <c>sd_cart</c> cookie value verbatim, for the concurrency rounds.</summary>
    public async Task<(HttpResponseMessage Response, OrderConfirmationDto? Order)> PlaceWithCookieAsync(
        string? cookieValue,
        object body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(OrdersPath, UriKind.Relative));
        request.Content = JsonContent.Create(body);

        if (cookieValue is not null)
        {
            request.Headers.Add("Cookie", $"sd_cart={cookieValue}");
        }

        var response = await client.SendAsync(request, cancellationToken);
        return (response, await ReadAsync<OrderConfirmationDto>(response, cancellationToken));
    }

    /// <summary>GET the checkout carrying an <c>sd_cart</c> cookie value verbatim.</summary>
    public async Task<(HttpResponseMessage Response, CheckoutSessionDto? Session)> GetWithCookieAsync(
        string? cookieValue,
        int? districtId,
        int? deliveryMethodId,
        CancellationToken cancellationToken)
    {
        var query = new List<string>();

        if (districtId is { } district)
        {
            query.Add($"districtId={district}");
        }

        if (deliveryMethodId is { } method)
        {
            query.Add($"deliveryMethodId={method}");
        }

        var path = query.Count == 0 ? Path : $"{Path}?{string.Join('&', query)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));

        if (cookieValue is not null)
        {
            request.Headers.Add("Cookie", $"sd_cart={cookieValue}");
        }

        var response = await client.SendAsync(request, cancellationToken);
        return (response, await ReadAsync<CheckoutSessionDto>(response, cancellationToken));
    }

    /// <summary>GET one placed order by its public token.</summary>
    public async Task<(HttpResponseMessage Response, OrderConfirmationDto? Order)> GetOrderAsync(
        string publicId,
        CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(
            new Uri($"{OrderPath}/{publicId}", UriKind.Relative),
            cancellationToken);

        return (response, await ReadAsync<OrderConfirmationDto>(response, cancellationToken));
    }

    /// <summary>
    /// POST the shopper's own Cancel order (doc 11 P10 flow A).
    /// </summary>
    /// <remarks>
    /// <b>No body, no antiforgery header and no cookie</b>, which is the route's own contract: the order's
    /// token is the only credential there is, and sending anything else here would be testing a request
    /// the storefront never makes.
    /// </remarks>
    public async Task<HttpResponseMessage> CancelOrderAsync(
        string publicId,
        CancellationToken cancellationToken) =>
        await client.PostAsync(
            new Uri($"{OrderPath}/{publicId}/cancel", UriKind.Relative),
            content: null,
            cancellationToken);

    /// <summary>The <c>ProblemDetails</c> body of a refused request, as a parsed document.</summary>
    public static async Task<JsonDocument> ProblemAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(body);
    }

    private static async Task<T?> ReadAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }
}
