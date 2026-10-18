using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Issue #107: a script must not be able to hold the shop's stock with unpaid orders at machine speed.
/// <c>POST /api/checkout/orders</c> is limited per visitor address, against a real SQL Server and the real
/// host.
/// <para>
/// <b>Each test's host reports the address the test names.</b> <c>TestServer</c> reports none, so every
/// request would share the "unknown" bucket; a startup filter reads <c>X-Test-Remote-Address</c> and sets
/// <c>Connection.RemoteIpAddress</c> from it, which is the nearest an in-memory host gets to a visitor
/// behind the gateway. Documentation-range addresses (RFC 5737) so none can be mistaken for a real one,
/// and all of them public so the strict per-visitor budget applies rather than the shared one.
/// </para>
/// <para>
/// <b>Every test's limit is a few permits</b> (set through <c>OrderPlacementThrottle:PermitsPerWindow</c>,
/// the same key an operator uses), because the subject is the boundary and not the size of the number:
/// the Nth order is placed, the N+1th is refused. The shipped defaults are pinned in
/// <c>OrderPlacementThrottleTests</c>.
/// </para>
/// <para>
/// <b>Orders are placed over HTTP and never inserted by hand</b>, for the reason
/// <see cref="BankTransferTestBase"/> gives: the stock reservation is half of what is being measured.
/// </para>
/// </summary>
public sealed class CheckoutPlacementThrottleTests(SqlServerDatabaseFixture sqlServer)
    : CheckoutTestBase(sqlServer)
{
    private const string AddressHeader = "X-Test-Remote-Address";

    private const string AddressA = "203.0.113.10";
    private const string AddressB = "203.0.113.20";

    private readonly List<HttpClient> _clients = [];
    private readonly List<ApiFactory> _hosts = [];

    /// <summary>
    /// <b>The Nth order is placed and the N+1th is refused</b> — with a 429, <c>Retry-After</c> and a
    /// problem-details body. The refused request wrote nothing: no order, no reservation, and the shopper's
    /// bag is exactly as it was.
    /// </summary>
    [Fact]
    public async Task The_order_at_the_limit_is_placed_and_the_one_after_it_is_refused_with_a_429()
    {
        const int permits = 3;
        var host = NewHost(permits);
        var shoppers = await NewShoppersAsync(host, permits + 1);
        var ordersBefore = await OrderCountAsync();

        for (var index = 0; index < permits; index++)
        {
            var placed = await shoppers[index].PlaceAsync(AddressA);
            Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        }

        var refused = await shoppers[permits].PlaceAsync(AddressA);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);

        // Retry-After in whole seconds: the default window, ten minutes.
        Assert.Equal(TimeSpan.FromSeconds(600), refused.Headers.RetryAfter?.Delta);
        Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", refused.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);

        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Token));
        Assert.Equal(429, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Too many orders", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("rate_limited", body.RootElement.GetProperty("code").GetString());
        Assert.Contains(
            "Nothing has been charged",
            body.RootElement.GetProperty("detail").GetString(),
            StringComparison.Ordinal);

        // Nothing was written for the refused request, and its bag is untouched.
        Assert.Equal(ordersBefore + permits, await OrderCountAsync());
        Assert.Equal(0, await ReservedAsync(shoppers[permits].VariantId));
        Assert.Equal(1, await CartItemCountAsync(shoppers[permits].CartId));
    }

    /// <summary>
    /// <b>A shopper retrying on a bad connection is not counted.</b> Two orders are placed (two of three
    /// permits); the first is then re-sent five times and every one is answered 200 with that same order;
    /// and the third permit is still there afterwards, so the replays took nothing.
    /// </summary>
    [Fact]
    public async Task A_resubmission_of_a_placed_order_does_not_consume_the_limit()
    {
        var host = NewHost(permits: 3);
        var shoppers = await NewShoppersAsync(host, 4);

        var first = await shoppers[0].PlaceAsync(AddressA);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await ReadOrderAsync(first);

        Assert.Equal(HttpStatusCode.Created, (await shoppers[1].PlaceAsync(AddressA)).StatusCode);

        for (var replay = 0; replay < 5; replay++)
        {
            var again = await shoppers[0].PlaceAsync(AddressA);

            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            var replayed = await ReadOrderAsync(again);
            Assert.Equal(original.OrderNumber, replayed.OrderNumber);
            Assert.True(replayed.IsDuplicateSubmission);
        }

        // Three permits, two placements and five replays: the third placement is still allowed...
        Assert.Equal(HttpStatusCode.Created, (await shoppers[2].PlaceAsync(AddressA)).StatusCode);

        // ...and the fourth is not, so the limit is live and the replays above were not simply unlimited.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await shoppers[3].PlaceAsync(AddressA)).StatusCode);
    }

    /// <summary>
    /// <b>A shopper retrying on a bad connection is not refused either</b>, however spent the address is:
    /// the replay is answered with the order the first submission made, before the limit is consulted.
    /// </summary>
    [Fact]
    public async Task A_resubmission_is_answered_with_its_order_when_the_address_has_no_permits_left()
    {
        var host = NewHost(permits: 2);
        var shoppers = await NewShoppersAsync(host, 3);

        var first = await ReadOrderAsync(await shoppers[0].PlaceAsync(AddressA));
        var second = await ReadOrderAsync(await shoppers[1].PlaceAsync(AddressA));

        // The address is spent: a third, new order is refused.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await shoppers[2].PlaceAsync(AddressA)).StatusCode);

        var reservedBefore = await ReservedAsync(shoppers[0].VariantId);

        foreach (var (shopper, original) in new[] { (shoppers[0], first), (shoppers[1], second) })
        {
            var again = await shopper.PlaceAsync(AddressA);

            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal(original.OrderNumber, (await ReadOrderAsync(again)).OrderNumber);
        }

        // A replay writes nothing: the reservation is what the first submission made it.
        Assert.Equal(reservedBefore, await ReservedAsync(shoppers[0].VariantId));
    }

    /// <summary>
    /// A refusal burns nothing: the bag is intact and the checkout key is unused, so the same order,
    /// resent from an address with permits left, is placed. This is what makes "try again in a few
    /// minutes" true.
    /// </summary>
    [Fact]
    public async Task A_refused_order_can_be_placed_later_with_the_same_bag_and_the_same_key()
    {
        var host = NewHost(permits: 1);
        var shoppers = await NewShoppersAsync(host, 2);

        Assert.Equal(HttpStatusCode.Created, (await shoppers[0].PlaceAsync(AddressA)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await shoppers[1].PlaceAsync(AddressA)).StatusCode);

        // The limit turning over is simulated by arriving from an address with a budget of its own; the
        // request is byte for byte the one that was refused.
        var later = await shoppers[1].PlaceAsync(AddressB);

        Assert.Equal(HttpStatusCode.Created, later.StatusCode);
        Assert.False((await ReadOrderAsync(later)).IsDuplicateSubmission);
    }

    /// <summary>
    /// Each visitor address has a bucket of its own: spending one leaves another's untouched.
    /// </summary>
    [Fact]
    public async Task Two_addresses_do_not_share_a_bucket()
    {
        var host = NewHost(permits: 2);
        var shoppers = await NewShoppersAsync(host, 6);

        Assert.Equal(HttpStatusCode.Created, (await shoppers[0].PlaceAsync(AddressA)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await shoppers[1].PlaceAsync(AddressA)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await shoppers[2].PlaceAsync(AddressA)).StatusCode);

        // B has not placed anything: two permits of its own, then its own refusal.
        Assert.Equal(HttpStatusCode.Created, (await shoppers[3].PlaceAsync(AddressB)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await shoppers[4].PlaceAsync(AddressB)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await shoppers[5].PlaceAsync(AddressB)).StatusCode);
    }

    /// <summary>
    /// Only a request that would reserve stock is counted. A body with a missing field, a total that has
    /// moved and a read of the checkout each leave the budget alone — and an address that has spent its
    /// budget still gets the specific 400 for a bad body, not a 429 that hides what was wrong.
    /// </summary>
    [Fact]
    public async Task Requests_that_would_not_place_an_order_are_not_counted()
    {
        var host = NewHost(permits: 2);
        var shoppers = await NewShoppersAsync(host, 3);
        var shopper = shoppers[0];

        // Three bodies with no first name: 400 each.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var invalid = shopper.Body();
            invalid["firstName"] = null;

            Assert.Equal(HttpStatusCode.BadRequest, (await shopper.PlaceAsync(AddressA, invalid)).StatusCode);
        }

        // A total that has moved: 409, nothing placed.
        var moved = shopper.Body();
        moved["expectedGrandTotal"] = 1m;
        Assert.Equal(HttpStatusCode.Conflict, (await shopper.PlaceAsync(AddressA, moved)).StatusCode);

        // Reading the checkout is not placing anything.
        for (var read = 0; read < 3; read++)
        {
            var response = await shopper.Http.GetAsync(new Uri(CheckoutApiClient.Path, UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // All of that was free: both permits are still there.
        Assert.Equal(HttpStatusCode.Created, (await shopper.PlaceAsync(AddressA)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await shoppers[1].PlaceAsync(AddressA)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await shoppers[2].PlaceAsync(AddressA)).StatusCode);

        // Spent, but a malformed body is still told what is wrong with it.
        var stillInvalid = shoppers[2].Body();
        stillInvalid["firstName"] = null;
        Assert.Equal(HttpStatusCode.BadRequest, (await shoppers[2].PlaceAsync(AddressA, stillInvalid)).StatusCode);
    }

    /// <summary>
    /// <b>Twelve shoppers press Place order at the same instant from one address with five permits.</b>
    /// Exactly five orders exist afterwards, the other seven were told 429 and none was a 5xx — and the
    /// stock is exact: the variant's <c>Reserved</c> is five orders of two units, equal to what the
    /// reserved orders' lines add up to (doc 08 §5.9), with no reservation left behind by a refusal.
    /// </summary>
    [Fact]
    public async Task Twelve_simultaneous_orders_from_one_address_place_exactly_the_permit_count_and_reserve_exactly_that_much()
    {
        const int permits = 5;
        const int contenders = 12;
        const int quantity = 2;

        var host = NewHost(permits);
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 100);

        var shoppers = new List<Shopper>();
        for (var index = 0; index < contenders; index++)
        {
            shoppers.Add(await NewShopperAsync(host, variantId, quantity));
        }

        var ordersBefore = await OrderCountAsync();

        var responses = await Task.WhenAll(
            shoppers.Select(shopper => Task.Run(() => shopper.PlaceAsync(AddressA), Token)));

        var statuses = responses.Select(response => response.StatusCode).ToList();

        Assert.Equal(permits, statuses.Count(status => status == HttpStatusCode.Created));
        Assert.Equal(contenders - permits, statuses.Count(status => status == HttpStatusCode.TooManyRequests));
        Assert.DoesNotContain(statuses, status => (int)status >= 500);

        Assert.Equal(ordersBefore + permits, await OrderCountAsync());

        var (onHand, reserved, available) = await StockAsync(variantId);
        Assert.Equal(permits * quantity, reserved);
        Assert.Equal(100 - (permits * quantity), available);
        Assert.Equal(100, onHand);
        Assert.Equal(reserved, await HeldByReservedOrdersAsync(variantId));

        // Every refused shopper still has their bag.
        foreach (var (shopper, response) in shoppers.Zip(responses).Where(pair => pair.Second.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(1, await CartItemCountAsync(shopper.CartId));
        }
    }

    // --- Arrangement ---------------------------------------------------------------------------------

    public override async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }

        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>The API with the placement limit at <paramref name="permits"/> and a visitor address per request.</summary>
    private ApiFactory NewHost(int permits)
    {
        SqlServer.SkipIfUnavailable();

        var host = new ApiFactory(
            SqlServer.ConnectionString,
            additionalConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["OrderPlacementThrottle:PermitsPerWindow"] = permits.ToString(CultureInfo.InvariantCulture),
                ["OrderPlacementThrottle:SharedAddressPermitsPerWindow"] = "1000",
            },
            configureServices: services => services.AddSingleton<IStartupFilter>(new HeaderAddressStartupFilter()));

        _hosts.Add(host);
        return host;
    }

    /// <summary>Shoppers with a bag and a quote each, every one on a variant of their own.</summary>
    private async Task<List<Shopper>> NewShoppersAsync(ApiFactory host, int count)
    {
        var variantIds = await CheckoutVariantIdsAsync(count);
        var shoppers = new List<Shopper>();

        foreach (var variantId in variantIds)
        {
            await SetStockAsync(variantId, 10);
            shoppers.Add(await NewShopperAsync(host, variantId, quantity: 1));
        }

        return shoppers;
    }

    private async Task<Shopper> NewShopperAsync(ApiFactory host, int variantId, int quantity)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = BaseAddress,
            HandleCookies = true,
        });

        _clients.Add(client);

        var added = await new CartApiClient(client).AddAsync(variantId, quantity, Token);
        Assert.Equal(HttpStatusCode.OK, added.Response.StatusCode);

        var setCookie = added.Response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("sd_cart=", StringComparison.Ordinal));
        var token = Guid.ParseExact(setCookie["sd_cart=".Length..].Split(';')[0], "D");

        int cartId;
        using (var scope = CreateScope())
        {
            cartId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Carts
                .AsNoTracking()
                .Where(candidate => candidate.Token == token)
                .Select(candidate => candidate.Id)
                .SingleAsync(Token);
        }

        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);
        var (_, session) = await new CheckoutApiClient(client).GetAsync(colombo, standard, Token);

        return new Shopper(
            client,
            variantId,
            cartId,
            () => ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session)));
    }

    private static async Task<OrderConfirmationDto> ReadOrderAsync(HttpResponseMessage response)
    {
        var order = await response.Content.ReadFromJsonAsync<OrderConfirmationDto>(
            CheckoutApiClient.JsonOptions,
            Token);

        return Assert.IsType<OrderConfirmationDto>(order);
    }

    /// <summary>
    /// What the orders that hold stock say they hold for this variant: doc 08 §5.9's reconciliation
    /// definition of <c>Reserved</c>, computed independently of the column it is compared with.
    /// </summary>
    private async Task<int> HeldByReservedOrdersAsync(int variantId)
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().OrderLines
            .AsNoTracking()
            .Where(line => line.VariantId == variantId && line.Order!.StockState == OrderStockState.Reserved)
            .SumAsync(line => (int)line.Quantity, Token);
    }

    /// <summary>One shopper: a client with a bag, and the body that places what is in it.</summary>
    private sealed class Shopper(HttpClient http, int variantId, int cartId, Func<Dictionary<string, object?>> body)
    {
        public HttpClient Http => http;

        public int VariantId => variantId;

        public int CartId => cartId;

        /// <summary>A fresh copy of the body, so a test can spoil one field without spoiling the next call.</summary>
        public Dictionary<string, object?> Body() => body();

        /// <summary>Place the order as the visitor at <paramref name="address"/>.</summary>
        public async Task<HttpResponseMessage> PlaceAsync(string address, Dictionary<string, object?>? overrideBody = null)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(CheckoutApiClient.OrdersPath, UriKind.Relative))
            {
                Content = JsonContent.Create(overrideBody ?? body()),
            };

            request.Headers.Add(AddressHeader, address);

            return await http.SendAsync(request, Token);
        }
    }

    /// <summary>
    /// Sets <c>Connection.RemoteIpAddress</c> from a request header, ahead of the whole pipeline, so one
    /// in-memory host can be several visitors.
    /// </summary>
    private sealed class HeaderAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            builder =>
            {
                builder.Use(async (context, continuation) =>
                {
                    if (context.Request.Headers.TryGetValue(AddressHeader, out var value)
                        && IPAddress.TryParse(value.ToString(), out var address))
                    {
                        context.Connection.RemoteIpAddress = address;
                    }

                    await continuation(context);
                });

                next(builder);
            };
    }
}
