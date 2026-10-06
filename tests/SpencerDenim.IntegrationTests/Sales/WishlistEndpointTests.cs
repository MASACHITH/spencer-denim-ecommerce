using System.Net;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Wishlist;
using SpencerDenim.Application.Sales.Wishlist.Contracts;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// <c>POST /api/wishlist/products</c> against a real SQL Server (W2-08, WISH-01).
/// <para>
/// The endpoint holds no state: a guest's wishlist is in their browser (doc 08 §1.7), so the slugs come
/// in on the query string and the answer is the live card for each. What is worth proving on a real
/// database is that the <i>visibility</i> rule is the catalogue's own — a product that is not for sale
/// comes back as <c>null</c> rather than as a card — and that the order and the spelling the browser sent
/// survive the round trip, because the browser matches its stored entries on them.
/// </para>
/// </summary>
public sealed class WishlistEndpointTests(SqlServerDatabaseFixture sqlServer) : IAsyncDisposable
{
    private static readonly Uri BaseAddress = new("https://localhost");

    /// <summary>
    /// The API writes enums as their member names (<c>"InStock"</c>, <c>"Sale"</c>), which is the contract
    /// the storefront's TypeScript models are written against. Without the string converter here the test
    /// would fail to deserialise a perfectly good answer — and a reader who then "fixed" the API would
    /// have broken every client.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly List<HttpClient> _clients = [];
    private ApiFactory? _factory;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private HttpClient NewVisitor()
    {
        sqlServer.SkipIfUnavailable();

        _factory ??= new ApiFactory(sqlServer.ConnectionString);
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = BaseAddress,
            HandleCookies = true,
        });

        _clients.Add(client);
        return client;
    }

    /// <summary>
    /// The one address, whatever is saved. <b>It takes no slugs</b>, which is the whole of the 414 fix: a
    /// URL that grew with the wishlist is what put a shopper's own page permanently out of reach.
    /// </summary>
    private const string Url = "/api/wishlist/products";

    private async Task<(HttpResponseMessage Response, WishlistDto? Body)> GetAsync(params string[] slugs)
    {
        var response = await NewVisitor().PostAsJsonAsync(
            new Uri(Url, UriKind.Relative),
            new { slugs },
            Json,
            Token);

        var body = response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<WishlistDto>(Json, Token)
            : null;

        return (response, body);
    }

    /// <summary>Two seeded, sellable product slugs — whatever the demo catalogue happens to hold.</summary>
    private async Task<List<string>> SellableSlugsAsync(int count)
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.Products
            .AsNoTracking()
            .Where(product => product.IsPublished)
            .OrderBy(product => product.Id)
            .Select(product => product.Slug)
            .Take(count)
            .ToListAsync(Token);
    }

    /// <summary>
    /// A body with no <c>slugs</c> at all. Not a 400: the list is written by <c>localStorage</c> and a
    /// shopper can neither see nor fix it, so the honest answer is "nothing saved".
    /// </summary>
    [Fact]
    public async Task A_body_with_no_slugs_is_an_empty_list_rather_than_a_400()
    {
        var response = await NewVisitor().PostAsync(
            new Uri(Url, UriKind.Relative),
            new StringContent("{}", Encoding.UTF8, "application/json"),
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<WishlistDto>(Json, Token);
        Assert.NotNull(body);
        Assert.Empty(body.Items);
    }

    [Fact]
    public async Task A_visitor_with_nothing_saved_gets_an_empty_list()
    {
        var (response, body) = await GetAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Empty(body.Items);
    }

    [Fact]
    public async Task Saved_slugs_come_back_as_live_cards_in_the_order_they_were_sent()
    {
        var slugs = await SellableSlugsAsync(2);
        Assert.Equal(2, slugs.Count);

        // Reversed, so the answer's order can only come from the request and not from the database's.
        var asked = new[] { slugs[1], slugs[0] };

        var (response, body) = await GetAsync(asked);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal(asked, body.Items.Select(item => item.Slug));
        Assert.All(body.Items, item => Assert.NotNull(item.Product));
        Assert.Equal(asked, body.Items.Select(item => item.Product!.Slug));
    }

    /// <summary>
    /// Doc 11 §575. A slug that names nothing the shop sells is answered with a null product, not left out
    /// and not a 404 — the shopper has it in their list and the honest answer is "this is no longer
    /// available; here is a Remove button".
    /// </summary>
    [Fact]
    public async Task A_slug_the_catalogue_does_not_have_comes_back_with_no_product()
    {
        var slugs = await SellableSlugsAsync(1);

        var (response, body) = await GetAsync(slugs[0], "a-product-that-was-never-sold-here");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal(2, body.Items.Count);
        Assert.NotNull(body.Items[0].Product);
        Assert.Null(body.Items[1].Product);
    }

    /// <summary>
    /// **An unpublished product is "no longer available", not "missing".** This is the case that matters
    /// commercially — the shopper saved something real and the client has since withdrawn it — and it is
    /// the one that a looser read model would get wrong by answering with a card for something that cannot
    /// be bought. `CatalogVisibility.VisibleProduct` is the one definition of sellable and this read uses
    /// it; the product is put back afterwards so the suite's shared database is left as it was found.
    /// </summary>
    [Fact]
    public async Task An_unpublished_product_comes_back_with_no_product()
    {
        sqlServer.SkipIfUnavailable();

        var slug = (await SellableSlugsAsync(1))[0];

        using (var scope = sqlServer.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await dbContext.Products
                .Where(product => product.Slug == slug)
                .ExecuteUpdateAsync(product => product.SetProperty(row => row.IsPublished, false), Token);
        }

        try
        {
            var (response, body) = await GetAsync(slug);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(body);
            var item = Assert.Single(body.Items);
            Assert.Equal(slug, item.Slug);
            Assert.Null(item.Product);
        }
        finally
        {
            using var scope = sqlServer.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await dbContext.Products
                .IgnoreQueryFilters()
                .Where(product => product.Slug == slug)
                .ExecuteUpdateAsync(product => product.SetProperty(row => row.IsPublished, true), Token);
        }
    }

    /// <summary>
    /// A junk slug is dropped, not refused: the list was written by `localStorage` and a shopper can
    /// neither see it nor fix it, so a 400 would wedge their own wishlist shut (the same reasoning
    /// <c>CartCookie.Read</c> gives for a malformed cookie).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("' OR 1=1--")]
    [InlineData("%2e%2e%2fetc%2fpasswd")]
    public async Task A_slug_that_cannot_name_a_product_is_answered_rather_than_refused(string slug)
    {
        var (response, body) = await GetAsync(slug);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.All(body.Items, item => Assert.Null(item.Product));
    }

    [Fact]
    public async Task A_list_over_the_cap_is_truncated_rather_than_refused()
    {
        var many = Enumerable
            .Range(0, GetWishlistQueryHandler.MaxSlugs + 10)
            .Select(index => $"not-a-real-product-{index}")
            .ToArray();

        var (response, body) = await GetAsync(many);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal(GetWishlistQueryHandler.MaxSlugs, body.Items.Count);
    }

    /// <summary>
    /// Nothing here is personal in the sense the bag is — the same slugs get the same answer for anyone —
    /// but the <i>request</i> is a list of what one shopper has saved, so a shared cache keyed on the URL
    /// would be keeping a record of it. There is also nothing to gain from caching: the whole point of the
    /// round trip is that the prices and the stock are current.
    /// <para>
    /// And deliberately <b>no</b> <c>Vary: Cookie</c>, unlike the bag: this endpoint reads no cookie, so
    /// claiming cookies vary the answer would be noise.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_answer_forbids_caching_and_does_not_claim_to_vary_on_the_cookie()
    {
        var (response, _) = await GetAsync("anything");

        var cacheControl = Assert.Single(response.Headers.GetValues("Cache-Control"));
        Assert.Contains("no-store", cacheControl, StringComparison.Ordinal);
        Assert.Contains("private", cacheControl, StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Vary"));
    }

    /// <summary>
    /// **No cookie is issued, and none is needed.** A wishlist read must not create server-side state or
    /// hand the browser an identifier: the list is the browser's. This is the assertion that would fail if
    /// someone later gave the wishlist a `sd_wishlist` token "for symmetry with the bag" — which would be
    /// a change to the scope baseline, not a tidy-up.
    /// </summary>
    [Fact]
    public async Task No_cookie_is_issued_and_no_row_is_written()
    {
        sqlServer.SkipIfUnavailable();

        var slug = (await SellableSlugsAsync(1))[0];

        int Count()
        {
            using var scope = sqlServer.CreateScope();
            return scope.ServiceProvider.GetRequiredService<AppDbContext>().WishlistItems.Count();
        }

        var before = Count();
        var (response, _) = await GetAsync(slug);
        var after = Count();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(before, after);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
