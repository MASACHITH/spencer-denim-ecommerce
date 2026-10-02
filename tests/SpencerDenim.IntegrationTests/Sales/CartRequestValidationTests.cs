using System.Globalization;
using System.Net;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// What the bag endpoints do with input they cannot use (CART-01).
/// <para>
/// This class exists because of <b>#47</b>: a malformed or mistyped JSON body returns <b>500</b> instead
/// of 400 on the admin login endpoints, and that issue is still open against someone else's branch. The
/// bag is the next public write endpoint in the shop, so the same mistake would land on an anonymous
/// surface. Every one of the shapes #47 names is asserted here, plus the ones particular to a bag: a
/// quantity of 0, of 11, of <c>-1</c>, of <c>1e9</c>, a variant id that is not a number, and a cookie
/// that is not a GUID.
/// </para>
/// </summary>
public sealed class CartRequestValidationTests(SqlServerDatabaseFixture sqlServer) : CartTestBase(sqlServer)
{
    /// <summary>
    /// The #47 shapes themselves: a truncated body, a body of the wrong JSON type, an empty body, and a
    /// body that is not JSON at all. None of them may be a 500 — a 500 means an unhandled exception
    /// reached the host, which on a public endpoint is both an error page a shopper can see and a line in
    /// the log for every scripted probe.
    /// </summary>
    [Theory]
    [InlineData("{bad", "application/json")]
    [InlineData("{\"variantId\":\"abc\",\"quantity\":1}", "application/json")]
    [InlineData("{\"variantId\":1,\"quantity\":\"abc\"}", "application/json")]
    [InlineData("", "application/json")]
    [InlineData("null", "application/json")]
    [InlineData("[]", "application/json")]
    [InlineData("\"just a string\"", "application/json")]
    [InlineData("{\"quantity\":1}", "application/json")]
    public async Task A_body_the_endpoint_cannot_read_is_a_400_and_never_a_500(string body, string contentType)
    {
        var api = NewVisitor();

        using var response = await api.PostRawAsync("/api/cart/items", body, contentType, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A body sent as a form, or with no media type at all, is a 415 — which is also the thing that makes a
    /// cross-site HTML form unable to touch anybody's bag. A form can only post
    /// <c>application/x-www-form-urlencoded</c>, <c>multipart/form-data</c> or <c>text/plain</c>; a
    /// cross-origin <c>fetch</c> sending <c>application/json</c> needs a CORS preflight, and this API
    /// configures no CORS policy at all. With <c>SameSite=Lax</c> on top, there is no shape of cross-site
    /// request left that reaches a cart.
    /// </summary>
    [Theory]
    [InlineData("variantId=1&quantity=1", "application/x-www-form-urlencoded")]
    [InlineData("{\"variantId\":1,\"quantity\":1}", "text/plain")]
    [InlineData("{\"variantId\":1,\"quantity\":1}", "multipart/form-data; boundary=x")]
    [InlineData("{\"variantId\":1,\"quantity\":1}", null)]
    public async Task A_body_that_is_not_json_is_refused_on_its_media_type(string body, string? contentType)
    {
        var api = NewVisitor();

        using var response = await api.PostRawAsync("/api/cart/items", body, contentType, Token);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    /// <summary>
    /// <b>The media type an ordinary browser actually sends.</b>
    /// <para>
    /// The 415 above is what stops a cross-site HTML form reaching a bag, so the other half of that
    /// argument — that the *real* content type is accepted — has to hold too. It had never been
    /// exercised: <c>CartApiClient.PostRawAsync</c> used <c>StringContent</c>'s three-argument
    /// constructor, which throws on a media type carrying a parameter, so
    /// <c>application/json; charset=utf-8</c> was structurally unsendable from this suite. It is sendable
    /// now, and it is accepted.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/json;charset=UTF-8")]
    public async Task The_media_type_a_browser_sends_is_accepted(string contentType)
    {
        var api = NewVisitor();
        var variantId = (await InStockVariantIdsAsync(1))[0];
        var body = string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"variantId\":{variantId},\"quantity\":1}}");

        using var response = await api.PostRawAsync("/api/cart/items", body, contentType, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// <b>The API configures no CORS policy, and that is load-bearing rather than incidental.</b>
    /// <para>
    /// The CSRF argument for these endpoints is: a cross-site HTML form can only send urlencoded,
    /// multipart or <c>text/plain</c>, all of which are a 415; and a cross-origin <c>fetch</c> sending
    /// <c>application/json</c> needs a CORS preflight that nothing here answers. The second half is a
    /// fact about the composition root, not about this file — so the day somebody adds a permissive
    /// policy, the whole argument collapses to the 415 alone and nothing says so. This asserts the
    /// premise instead of re-deriving it.
    /// </para>
    /// <para>
    /// Measured on the wire rather than by inspecting the service collection: a preflight that is not
    /// handled gets no <c>Access-Control-Allow-Origin</c> back, which is precisely what makes a browser
    /// refuse the real request.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_api_answers_no_cors_preflight_for_the_bag()
    {
        var api = NewVisitor();

        using var request = new HttpRequestMessage(HttpMethod.Options, new Uri("/api/cart/items", UriKind.Relative));
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");

        using var response = await api.Http.SendAsync(request, Token);

        Assert.False(
            response.Headers.Contains("Access-Control-Allow-Origin"),
            "the API answered a cross-origin preflight, so the CSRF argument for the bag no longer holds");
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    /// <summary>
    /// Doc 08 §3.5: <c>CK Quantity 1–10</c>. A quantity outside it is the caller's mistake and names the
    /// field, so the storefront can attach the message to the control. <c>1e9</c> is in the list because
    /// it is valid JSON for a number that does not fit an <see cref="int"/>, which is a different code
    /// path from <c>11</c>.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(1000)]
    public async Task A_quantity_outside_one_to_ten_is_a_400_naming_quantity(int quantity)
    {
        var api = NewVisitor();
        var variantId = (await InStockVariantIdsAsync(1))[0];

        var (response, _) = await api.AddAsync(variantId, quantity, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("quantity", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1e9")]
    [InlineData("9999999999")]
    [InlineData("1.5")]
    public async Task A_quantity_that_is_not_a_whole_number_in_range_is_a_400(string quantityJson)
    {
        var api = NewVisitor();
        var variantId = (await InStockVariantIdsAsync(1))[0];
        var body = string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"variantId\":{variantId},\"quantity\":{quantityJson}}}");

        using var response = await api.PostRawAsync("/api/cart/items", body, "application/json", Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A variant id the shop does not sell — nonexistent, unpublished, deactivated — is one answer, not
    /// three, so the endpoint cannot be used to find out which ids exist.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(2_000_000_000)]
    public async Task A_variant_the_shop_does_not_sell_is_a_400_naming_variantId(int variantId)
    {
        var api = NewVisitor();

        var (response, _) = await api.AddAsync(variantId, 1, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("variantId", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    /// <summary>
    /// The route constraint catches a variant id that is not an integer before any handler runs. A 404
    /// rather than a 400 — the address itself does not exist — and, again, never a 500.
    /// <para>
    /// A percent-encoded NUL (<c>%00</c>) is <b>not</b> in this list, and it is worth saying why rather
    /// than leaving it looking untested: <c>TestHost</c> refuses to build the request at all
    /// ("The path contains null characters", thrown in <c>PathString.FromUriComponent</c> inside the
    /// client handler), so nothing reaches the server and the assertion would be about the test harness.
    /// Kestrel rejects the same path on the wire before routing. That is a measurement for the browser
    /// suite, not for this one.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("/api/cart/items/abc")]
    [InlineData("/api/cart/items/1.5")]
    [InlineData("/api/cart/items/%20")]
    public async Task A_variant_id_in_the_path_that_is_not_an_integer_does_not_reach_a_handler(string path)
    {
        var api = NewVisitor();

        using var response = await api.Http.DeleteAsync(new Uri(path, UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// A cookie value that is not the canonical GUID this application writes reads as "no bag" — a 200
    /// with an empty bag — and never as a 400 or a 500.
    /// <para>
    /// 400 would be the wrong answer even though the value is wrong: a shopper cannot edit their own
    /// cookie, so a browser that somehow acquired a bad one would be locked out of the shop until they
    /// cleared site data. The injection-shaped values are here because the cookie is the one cart input
    /// that arrives as free text.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("1' OR '1'='1")]
    [InlineData("deadbeef")]
    [InlineData("8e29b4c6c0f94f0e9b7e2d1a5c3f6a7b")]
    public async Task A_cookie_that_is_not_a_canonical_guid_reads_as_an_empty_bag(string cookieValue)
    {
        var api = NewVisitorWithoutCookieJar();

        var (response, cart) = await api.GetWithCookieAsync(cookieValue, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(cart);
        Assert.Empty(cart.Lines);
        Assert.Equal(0, cart.Totals.ItemCount);
    }

    /// <summary>
    /// A well-formed token for a bag that is not there (purged, or never existed) is also an empty bag —
    /// and the stale cookie is <b>cleared</b> rather than left to be re-sent for another 30 days.
    /// </summary>
    [Fact]
    public async Task A_token_for_a_bag_that_does_not_exist_clears_the_cookie()
    {
        var api = NewVisitorWithoutCookieJar();

        var (response, cart) = await api.GetWithCookieAsync(Guid.NewGuid().ToString("D"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(cart);
        Assert.Empty(cart.Lines);

        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("sd_cart=;", setCookie, StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", setCookie, StringComparison.OrdinalIgnoreCase);
    }
}
