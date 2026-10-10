using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Cart.Contracts;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Hosts the real API against the migrated and seeded test database, for the bag endpoints
/// (CART-01/02).
/// <para>
/// <b>Every client addresses the host as <c>https://localhost</c>.</b> The cart cookie is
/// unconditionally <c>Secure</c> (doc 08 §3.5), and .NET's <c>CookieContainer</c> correctly refuses to
/// send a <c>Secure</c> cookie back over <c>http</c> — so a suite that used the default
/// <c>http://localhost</c> base address would silently test a shop in which the bag never persists, and
/// would have "proved" the cookie worked by never exercising it. Using https here means the production
/// configuration is the one under test, with no <c>RequireSecureCookies</c>-style knob to get wrong in a
/// deployment.
/// </para>
/// </summary>
public abstract class CartTestBase(SqlServerDatabaseFixture sqlServer) : IAsyncDisposable
{
    /// <summary>
    /// Extra configuration for the host this suite starts, or <see langword="null"/> for none. A suite whose
    /// subject needs a configured key (PayHere needs the shop's public address) overrides it; every other
    /// suite is unchanged.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string?>? AdditionalHostConfiguration => null;

    /// <summary>What the API is addressed as. See the type remarks: the scheme is load-bearing.</summary>
    protected static readonly Uri BaseAddress = new("https://localhost");

    private readonly List<HttpClient> _clients = [];
    private ApiFactory? _factory;

    /// <summary>
    /// Counts the SQL commands the API runs, so a test can prove a bag read is a bounded number of round
    /// trips rather than one per line. Same interceptor and the same two precedents as the catalogue's
    /// query-plan tests.
    /// </summary>
    protected CommandCountingInterceptor CommandCounter { get; } = new();

    protected SqlServerDatabaseFixture SqlServer { get; } = sqlServer;

    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A fresh browser: its own cookie jar, so one test can act as two different visitors and a stolen
    /// token has to be presented deliberately rather than by accident.
    /// </summary>
    protected CartApiClient NewVisitor()
    {
        SqlServer.SkipIfUnavailable();

        _factory ??= new ApiFactory(SqlServer.ConnectionString, additionalConfiguration: AdditionalHostConfiguration, commandInterceptor: CommandCounter);
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = BaseAddress,
            HandleCookies = true,
        });

        _clients.Add(client);
        return new CartApiClient(client);
    }

    /// <summary>
    /// A client that keeps no cookies, for the cases where the token has to be set by hand — a token
    /// belonging to somebody else, a token for a cart that has been deleted, a cookie value that is not a
    /// GUID at all.
    /// </summary>
    protected CartApiClient NewVisitorWithoutCookieJar()
    {
        SqlServer.SkipIfUnavailable();

        _factory ??= new ApiFactory(SqlServer.ConnectionString, additionalConfiguration: AdditionalHostConfiguration, commandInterceptor: CommandCounter);
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = BaseAddress,
            HandleCookies = false,
        });

        _clients.Add(client);
        return new CartApiClient(client);
    }

    /// <summary>
    /// A DI scope on the seeded database, for arranging catalogue state and reading rows back.
    /// <para>
    /// This is the <b>fixture's</b> container, built from <c>AddInfrastructure()</c> alone, so it resolves
    /// <c>AppDbContext</c> and the cart ports but <b>no application handlers</b>. Use
    /// <see cref="CreateApplicationScope"/> for those.
    /// </para>
    /// </summary>
    protected IServiceScope CreateScope()
    {
        SqlServer.SkipIfUnavailable();
        return SqlServer.CreateScope();
    }

    /// <summary>
    /// The host this suite's clients talk to, created on first use.
    /// </summary>
    /// <remarks>
    /// <b>Exposed so a derived suite can sign a staff user in against the same instance</b> (W3-03: the
    /// bank-transfer queue is an admin screen acting on orders a shopper placed, and both have to be the
    /// same shop). <c>MediaTestHost.SignInAsync(factory, permissions)</c> takes a factory for exactly this
    /// reason. It is a property rather than a second factory because two factories would be two
    /// <c>AddApplication()</c> containers over one database — which works, and which means an admin
    /// request and the order it acts on would be served by different hosts, so anything cached per host
    /// (effective permissions, the settings cache) would silently not be shared.
    /// </remarks>
    protected ApiFactory Factory
    {
        get
        {
            SqlServer.SkipIfUnavailable();
            return _factory ??= new ApiFactory(SqlServer.ConnectionString, additionalConfiguration: AdditionalHostConfiguration, commandInterceptor: CommandCounter);
        }
    }

    /// <summary>
    /// A DI scope on the <b>API host's</b> container, which has <c>AddApplication()</c> as well, so a use-case
    /// handler can be called directly. That is how the account-cart branch is exercised at all: there is no
    /// customer authentication yet, so no HTTP request can carry a customer identity (see
    /// <c>CartAccountAndMergeTests</c>).
    /// </summary>
    protected IServiceScope CreateApplicationScope()
    {
        SqlServer.SkipIfUnavailable();
        _factory ??= new ApiFactory(SqlServer.ConnectionString, additionalConfiguration: AdditionalHostConfiguration, commandInterceptor: CommandCounter);
        return _factory.Services.CreateScope();
    }

    /// <summary>
    /// Ids of variants that are published, active and really in stock, newest first by id so the choice is
    /// deterministic. Seeded data, not fixtures invented here: the point is to exercise the same rows the
    /// storefront reads.
    /// </summary>
    protected async Task<IReadOnlyList<int>> InStockVariantIdsAsync(int count)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var ids = await db.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.IsActive && variant.Product!.IsPublished)
            .Where(variant => variant.Product!.Department!.IsActive && variant.Product!.PrimaryCategory!.IsActive)
            .Where(variant => variant.StockLevel != null && variant.StockLevel.Available > 0)
            .OrderBy(variant => variant.Id)
            .Select(variant => variant.Id)
            .Take(count)
            .ToListAsync(Token);

        Assert.Equal(count, ids.Count);
        return ids;
    }

    /// <summary>
    /// <c>virtual</c> so a derived suite can put shared state back before the host goes away.
    /// <para>
    /// Added by W3-02, which needs it: its tests set a variant's stock to arrange "one unit left" and the
    /// cart suites read the <b>seeded</b> stock of the same low-numbered variants — so without a restore,
    /// four <c>CartStaleItemTests</c> failed in a full run and passed on their own, which is the exact
    /// shape of a test measuring its neighbours. An override must call <c>base.DisposeAsync()</c>.
    /// </para>
    /// </summary>
    public virtual async ValueTask DisposeAsync()
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

/// <summary>
/// A thin wrapper over the bag endpoints, so a test reads as a request and an assertion.
/// <para>
/// It deliberately offers a <b>raw</b> way to send a body (<see cref="PostRawAsync"/>): the failures this
/// suite exists to catch include a body that is not JSON at all, and a typed helper can only ever send
/// well-formed JSON. The same reason <c>CatalogApiClient.GetStatusAsync</c> exists.
/// </para>
/// </summary>
public sealed class CartApiClient(HttpClient client)
{
    public const string Path = "/api/cart";

    public HttpClient Http => client;

    public static JsonSerializerOptions JsonOptions { get; } = CatalogApiClient.JsonOptions;

    public async Task<(HttpResponseMessage Response, CartDto? Cart)> GetAsync(CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(new Uri(Path, UriKind.Relative), cancellationToken);
        return (response, await ReadCartAsync(response, cancellationToken));
    }

    public Task<(HttpResponseMessage Response, CartDto? Cart)> AddAsync(
        int variantId,
        int quantity,
        CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Post, $"{Path}/items", new { variantId, quantity }, cancellationToken);

    public Task<(HttpResponseMessage Response, CartDto? Cart)> SetQuantityAsync(
        int variantId,
        int quantity,
        CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Patch, $"{Path}/items/{variantId}", new { quantity }, cancellationToken);

    public async Task<(HttpResponseMessage Response, CartDto? Cart)> RemoveAsync(
        int variantId,
        CancellationToken cancellationToken)
    {
        var response = await client.DeleteAsync(new Uri($"{Path}/items/{variantId}", UriKind.Relative), cancellationToken);
        return (response, await ReadCartAsync(response, cancellationToken));
    }

    /// <summary>
    /// Sends exactly these bytes with exactly this media type. Nothing is serialised for you.
    /// <para>
    /// <b>The media type is parsed, not passed to <c>StringContent</c>'s three-argument constructor</b>,
    /// which takes a bare media-type *name* and throws on anything carrying a parameter. That made
    /// <c>application/json; charset=utf-8</c> — the type an ordinary browser sends — **structurally
    /// unsendable** from this client, so the CSRF evidence for these endpoints had never actually
    /// exercised it. It passes; it was untested, which is not the same thing.
    /// </para>
    /// </summary>
    public async Task<HttpResponseMessage> PostRawAsync(
        string path,
        string body,
        string? contentType,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Content = new StringContent(body, Encoding.UTF8);

        // Set explicitly so a type with parameters survives, and so "no type at all" is expressible.
        request.Content.Headers.ContentType = contentType is null
            ? null
            : MediaTypeHeaderValue.Parse(contentType);

        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>Sends a request carrying a <c>sd_cart</c> cookie value verbatim, whatever it is.</summary>
    public async Task<(HttpResponseMessage Response, CartDto? Cart)> GetWithCookieAsync(
        string? cookieValue,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Path, UriKind.Relative));
        if (cookieValue is not null)
        {
            request.Headers.Add("Cookie", $"sd_cart={cookieValue}");
        }

        var response = await client.SendAsync(request, cancellationToken);
        return (response, await ReadCartAsync(response, cancellationToken));
    }

    /// <summary>Sends an add carrying a <c>sd_cart</c> cookie value verbatim.</summary>
    public async Task<(HttpResponseMessage Response, CartDto? Cart)> AddWithCookieAsync(
        string? cookieValue,
        int variantId,
        int quantity,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{Path}/items", UriKind.Relative));
        request.Content = JsonContent.Create(new { variantId, quantity });
        if (cookieValue is not null)
        {
            request.Headers.Add("Cookie", $"sd_cart={cookieValue}");
        }

        var response = await client.SendAsync(request, cancellationToken);
        return (response, await ReadCartAsync(response, cancellationToken));
    }

    private async Task<(HttpResponseMessage Response, CartDto? Cart)> SendJsonAsync(
        HttpMethod method,
        string path,
        object body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };

        var response = await client.SendAsync(request, cancellationToken);
        return (response, await ReadCartAsync(response, cancellationToken));
    }

    private static async Task<CartDto?> ReadCartAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<CartDto>(JsonOptions, cancellationToken);
    }
}
