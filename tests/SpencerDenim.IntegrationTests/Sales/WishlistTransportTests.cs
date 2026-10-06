using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using SpencerDenim.Api.Endpoints.Sales;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Sales.Wishlist;
using SpencerDenim.Application.Sales.Wishlist.Contracts;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// <b>Whether the wire will carry a full wishlist</b> (W2-08, WISH-01), against a real Kestrel.
/// <para>
/// <b>Why this suite exists.</b> W2-08 shipped <c>GET /api/wishlist?slug=…</c>, whose worst-case request
/// line is several times Kestrel's 8,192-byte default — so a shopper with a full wishlist got
/// <b>414</b> and a Try again button that failed identically every time: their items safe, their page
/// permanently unusable. The test written to guard that boundary,
/// <c>WishlistEndpointTests.A_list_over_the_cap_is_truncated_rather_than_refused</c>, passed throughout,
/// because it used short slugs <i>and</i> ran on <c>WebApplicationFactory</c>'s in-memory
/// <c>TestServer</c>, which has no request line to overflow. That is the W1-12 shape — a fixture that
/// cannot express the defect — and the only cure is a real transport.
/// </para>
/// <para>
/// So these tests measure <b>sizes, not answers</b>: what the endpoint says about a slug is
/// <c>WishlistEndpointTests</c>' business, and these use a stub handler so that a missing or slow
/// database can never be mistaken for a transport failure. Most of them need no SQL Server at all.
/// </para>
/// </summary>
public sealed class WishlistTransportTests : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private KestrelApiHost? _host;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Answers for whatever it is asked, with no product. The transport is what is under test.</summary>
    private sealed class EchoHandler : IQueryHandler<GetWishlistQuery, WishlistDto>
    {
        public Task<WishlistDto> HandleAsync(GetWishlistQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Through the real handler, so the cap and the validation under test are the real ones.
            return new GetWishlistQueryHandler(new NoCatalogue()).HandleAsync(query, cancellationToken);
        }

        private sealed class NoCatalogue : Application.Sales.Wishlist.Abstractions.IWishlistCatalogReader
        {
            public Task<IReadOnlyList<(string Slug, Application.Catalog.Contracts.ProductCardDto? Product)>>
                GetCardsBySlugAsync(IReadOnlyList<string> slugs, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(slugs);

                IReadOnlyList<(string, Application.Catalog.Contracts.ProductCardDto?)> answer =
                    slugs.Select(slug => (slug, (Application.Catalog.Contracts.ProductCardDto?)null)).ToList();

                return Task.FromResult(answer);
            }
        }
    }

    /// <summary>
    /// The worst wishlist the storefront can ask about: <see cref="GetWishlistQueryHandler.MaxSlugs"/>
    /// slugs of <see cref="GetWishlistQueryHandler.MaxSlugLength"/> characters, which is the longest a
    /// <c>catalog.Products.Slug</c> can be. Each one is distinct, so the handler's de-duplication cannot
    /// quietly shrink the list under the test.
    /// </summary>
    private static string[] WorstCaseSlugs(int count = GetWishlistQueryHandler.MaxSlugs) =>
        Enumerable
            .Range(0, count)
            .Select(index => Slug(GetWishlistQueryHandler.MaxSlugLength, index))
            .ToArray();

    private static string Slug(int length, int index)
    {
        var suffix = index.ToString("D5", CultureInfo.InvariantCulture);
        return new string('a', length - suffix.Length) + suffix;
    }

    /// <summary>
    /// What <c>?slug=a&amp;slug=b…</c> would have cost: <c>slug=</c> and a separator per entry, plus the
    /// slug itself, inside <c>GET /api/wishlist?… HTTP/1.1</c>.
    /// </summary>
    private static int QueryStringRequestLine(IReadOnlyCollection<string> slugs) =>
        "GET /api/wishlist?".Length
        + slugs.Sum(slug => "slug=".Length + slug.Length + 1)
        + " HTTP/1.1".Length;

    private async Task<KestrelApiHost> HostAsync() =>
        _host ??= await KestrelApiHost.StartWishlistAsync(new EchoHandler());

    // No `SqlServerDatabaseFixture`: nothing in this suite reads or writes a database, so it needs no
    // SQL Server and cannot disturb the one the rest of the integration suite shares. That is worth
    // having for its own sake and it also keeps this file out of a shared-fixture collection.

    /// <summary>
    /// <b>The defect, as arithmetic.</b> This is the sum the endpoint's old comment got wrong when it
    /// called a full wishlist "well inside any proxy's request-line limit", and the ceiling it is
    /// compared against is read from Kestrel's own type rather than copied into a sentence — so the day
    /// that default changes, this test says so instead of rotting.
    /// </summary>
    [Fact]
    public void A_query_string_could_never_have_held_a_full_wishlist()
    {
        var limit = new KestrelServerLimits().MaxRequestLineSize;
        var requestLine = QueryStringRequestLine(WorstCaseSlugs());

        Assert.Equal(8192, limit);
        Assert.True(
            requestLine > limit * 5,
            $"the old GET shape needed {requestLine} bytes of request line against Kestrel's {limit}; if "
            + "that is no longer several times over, the cap or the slug length has changed and this "
            + "suite's premise with it");
    }

    /// <summary>
    /// <b>And the defect on the wire.</b> A request line the size the old shape produced, sent to a real
    /// Kestrel: 414, before any endpoint is reached. This is the status a shopper got, and the reason
    /// their Try again button could never work.
    /// </summary>
    [Fact]
    public async Task A_request_line_the_old_shape_would_have_sent_is_refused_outright()
    {
        var host = await HostAsync();
        using var client = host.CreateClient();

        var query = string.Join('&', WorstCaseSlugs().Select(slug => $"slug={slug}"));
        var response = await client.GetAsync(new Uri($"/api/wishlist?{query}", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.RequestUriTooLong, response.StatusCode);
    }

    /// <summary>
    /// <b>And it was reachable by an ordinary shopper, not only in theory.</b> The break-even is found by
    /// asking the server rather than by computing it, and it is asserted to be shorter than real slugs
    /// already in the catalogue — <c>womens-high-rise-straight-leg-jeans-indigo</c> is 42 characters.
    /// </summary>
    [Fact]
    public async Task The_old_shape_broke_at_a_slug_length_shoppers_really_have()
    {
        var host = await HostAsync();
        using var client = host.CreateClient();

        var breakEven = 0;

        for (var length = 10; length <= GetWishlistQueryHandler.MaxSlugLength; length++)
        {
            var slugs = Enumerable
                .Range(0, GetWishlistQueryHandler.MaxSlugs)
                .Select(index => Slug(length, index))
                .ToArray();

            var query = string.Join('&', slugs.Select(slug => $"slug={slug}"));
            var response = await client.GetAsync(new Uri($"/api/wishlist?{query}", UriKind.Relative), Token);

            if (response.StatusCode == HttpStatusCode.RequestUriTooLong)
            {
                breakEven = length;
                break;
            }
        }

        Assert.NotEqual(0, breakEven);
        Assert.InRange(breakEven, 20, 45);
    }

    /// <summary>
    /// <b>The fix, on the same server.</b> The same worst-case list in a body: 200 OK and every entry
    /// answered. Reverting the endpoint to a query string makes this a 414.
    /// </summary>
    [Fact]
    public async Task A_full_wishlist_of_maximum_length_slugs_is_answered()
    {
        var host = await HostAsync();
        using var client = host.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/wishlist/products", UriKind.Relative),
            new { slugs = WorstCaseSlugs() },
            Json,
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<WishlistDto>(Json, Token);
        Assert.NotNull(body);
        Assert.Equal(GetWishlistQueryHandler.MaxSlugs, body.Items.Count);
    }

    /// <summary>
    /// The URL does not grow with the wishlist, which is the property the fix rests on. Measured on the
    /// request that is actually sent, not on a constant in the storefront.
    /// </summary>
    [Fact]
    public async Task The_request_line_is_the_same_length_whatever_is_saved()
    {
        var host = await HostAsync();
        using var client = host.CreateClient();

        async Task<int> SendAsync(int count)
        {
            var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri("/api/wishlist/products", UriKind.Relative))
            {
                Content = JsonContent.Create(new { slugs = WorstCaseSlugs(count) }, options: Json),
            };

            var response = await client.SendAsync(request, Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            return $"POST {request.RequestUri} HTTP/1.1".Length;
        }

        var one = await SendAsync(1);
        var full = await SendAsync(GetWishlistQueryHandler.MaxSlugs);

        Assert.Equal(one, full);
        Assert.True(one < 64, $"the request line is {one} bytes, which is no longer a constant handful");
    }

    /// <summary>
    /// A body three times the cap is trimmed by the <i>handler</i>, not refused by the transport — so the
    /// bound that protects the catalogue is the one still doing the work.
    /// </summary>
    [Fact]
    public async Task A_body_over_the_cap_is_truncated_on_a_real_server_too()
    {
        var host = await HostAsync();
        using var client = host.CreateClient();

        // **Short slugs**, so this stays well inside `WishlistEndpoints.MaxBodyBytes` and the thing
        // being measured is the *handler's* cap. With worst-case slugs the body would be refused at 413
        // and this would quietly become a second copy of the body-limit test.
        var slugs = Enumerable
            .Range(0, GetWishlistQueryHandler.MaxSlugs * 3)
            .Select(index => Slug(20, index))
            .ToArray();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/wishlist/products", UriKind.Relative),
            new { slugs },
            Json,
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<WishlistDto>(Json, Token);
        Assert.NotNull(body);
        Assert.Equal(GetWishlistQueryHandler.MaxSlugs, body.Items.Count);
    }

    /// <summary>
    /// <b>The bound the move to a body took away, put back.</b>
    /// <para>
    /// The old <c>GET</c> was refused at 8,192 bytes by the transport before anything was allocated. A
    /// <c>POST</c> is accepted up to Kestrel's 30 MB default and <c>System.Text.Json</c> materialises the
    /// whole array before the handler's cap trims it — measured at 20.68 MB and 2,192 ms for 200,000
    /// slugs. The catalogue was never at risk, because the cap holds the database work at 200 either
    /// way; what had nothing in front of it was the parse-and-allocate cost. <b>A fix for an input that
    /// was too large must not remove the only bound on input size.</b>
    /// </para>
    /// <para>
    /// 413, not 400 and not a trimmed answer: 201 slugs are a wishlist the shop will not draw all of, so
    /// they are trimmed; 200,000 are not a wishlist.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_body_far_larger_than_any_wishlist_is_refused_outright()
    {
        var host = await HostAsync();
        using var client = host.CreateClient();

        // **Just over the limit, not megabytes over it.** A body the server stops reading part way
        // through is answered *and* reset, and whether the client sees the 413 or the reset first is a
        // race — which is exactly how this test failed in the suite and passed on its own. At 400
        // worst-case slugs (about 90 KB) the declared `Content-Length` is over the ceiling, so the
        // refusal happens before the body is read at all and the answer is deterministic.
        var slugs = WorstCaseSlugs(400);
        var response = await client.PostAsJsonAsync(
            new Uri("/api/wishlist/products", UriKind.Relative),
            new { slugs },
            Json,
            Token);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    /// <summary>
    /// And the limit really is above a full wishlist, measured rather than asserted from the constant:
    /// the worst body the storefront can send is well inside it, with room to spare.
    /// </summary>
    [Fact]
    public void The_body_limit_leaves_room_over_a_full_wishlist()
    {
        var body = JsonSerializer.Serialize(new { slugs = WorstCaseSlugs() }, Json);

        Assert.True(
            body.Length < WishlistEndpoints.MaxBodyBytes,
            $"a full wishlist is {body.Length} bytes against a limit of {WishlistEndpoints.MaxBodyBytes}");

        // Headroom, but not so much that the limit stops being one: between 1.2x and 3x a full wishlist.
        Assert.InRange(WishlistEndpoints.MaxBodyBytes / (double)body.Length, 1.2, 3.0);
    }

    /// <summary>
    /// <b>The premise of the whole suite: the deployed application does not raise Kestrel's limits.</b>
    /// <see cref="KestrelApiHost"/> is a bare host, so its defaults are only the deployed ones while
    /// <c>SpencerDenim.Api</c> configures none — and if a later task raises
    /// <c>MaxRequestLineSize</c> to make a long URL work, this suite's conclusions stop holding and
    /// should fail rather than quietly mislead.
    /// </summary>
    [Fact]
    public void The_api_raises_no_kestrel_limit_anywhere()
    {
        var api = Path.Combine(FindRepositoryRoot(), "src", "backend", "SpencerDenim.Api");
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var file in Directory.EnumerateFiles(api, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            scanned++;

            // **Comments are stripped first**, and that is not a nicety: the first version of this rule
            // failed on `WishlistEndpoints`' own docblock, which cites `MaxRequestLineSize` in the course
            // of explaining why the limit matters. A rule that cannot tell code from the prose describing
            // it would make writing that prose down a test failure, which is the wrong incentive exactly.
            var code = string.Join(
                '\n',
                File.ReadLines(file)
                    .Select(line => line.TrimStart())
                    .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
                    .Where(line => !line.StartsWith('*')));

            if (code.Contains("ConfigureKestrel", StringComparison.Ordinal)
                || code.Contains("MaxRequestLineSize", StringComparison.Ordinal)
                || code.Contains("\"Kestrel\"", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(scanned > 10, $"only {scanned} files were scanned, so this rule has stopped sweeping");
        Assert.Empty(offenders);
    }

    /// <summary>
    /// The counter-proof, so the tests above are known to be measuring a transport rather than an
    /// endpoint: the same over-long request line goes through <see cref="ApiFactory"/>'s in-memory server
    /// without complaint. <b>This is the one assertion that names why the original cap test could not
    /// have caught the 414</b>, and it is the reason this suite was written against Kestrel instead.
    /// </summary>
    [Fact]
    public async Task The_in_memory_server_does_not_enforce_a_request_line_limit_at_all()
    {
        // **A deliberately unreachable database**, so this needs no SQL Server and touches no shared one.
        // The request is refused by the *catalogue* rather than answered, which is exactly as good: what
        // is asserted is the status that did **not** come back, and a request line that was never
        // rejected is one the transport never looked at.
        await using var factory = new ApiFactory(ApiFactory.UnreachableConnectionString);
        using var client = factory.CreateClient();

        var padding = new string('a', 12_000);
        var response = await client.GetAsync(
            new Uri($"/api/catalog/products?departments={padding}", UriKind.Relative),
            Token);

        Assert.NotEqual(HttpStatusCode.RequestUriTooLong, response.StatusCode);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SpencerDenim.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate SpencerDenim.sln above the test output directory.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
