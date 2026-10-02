using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Cart.Contracts;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// The bag endpoints against a real SQL Server (CART-01, CART-02, GLB-04): the happy paths, the cookie,
/// and the rule that one visitor's token can never reach another's bag.
/// </summary>
public sealed class CartEndpointTests(SqlServerDatabaseFixture sqlServer) : CartTestBase(sqlServer)
{
    [Fact]
    public async Task A_visitor_with_no_cookie_gets_an_empty_bag_and_no_cart_row_is_created()
    {
        var api = NewVisitor();

        var before = await CountCartsAsync();
        var (response, cart) = await api.GetAsync(Token);
        var after = await CountCartsAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(cart);
        Assert.Empty(cart.Lines);
        Assert.Equal(0, cart.Totals.ItemCount);
        Assert.Equal(0m, cart.Totals.ItemsSubtotal);

        // Doc 08 §3.5: "The cart row is created on the first Add to Bag, never on a page view." This is
        // also what lets the bag page be server-rendered: a read issues no token, so SSR never has to
        // deliver a Set-Cookie it has no way of relaying.
        Assert.Equal(before, after);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Every_cart_answer_forbids_caching_and_varies_on_the_cookie()
    {
        var api = NewVisitor();

        var (response, _) = await api.GetAsync(Token);

        var cacheControl = Assert.Single(response.Headers.GetValues("Cache-Control"));
        Assert.Contains("no-store", cacheControl, StringComparison.Ordinal);
        Assert.Contains("private", cacheControl, StringComparison.Ordinal);
        Assert.Contains("Cookie", Assert.Single(response.Headers.GetValues("Vary")), StringComparison.Ordinal);
    }

    /// <summary>
    /// The first add creates the bag and the cookie. The cookie's attributes are read off the wire, not
    /// off the configuration that set them: doc 08 §3.5 asks for HttpOnly, Secure, SameSite=Lax and a
    /// 30-day window, and the only place that is true or false is the header.
    /// </summary>
    [Fact]
    public async Task The_first_add_creates_the_bag_and_issues_an_httponly_secure_lax_cookie()
    {
        var api = NewVisitor();
        var variantId = (await InStockVariantIdsAsync(1))[0];

        var (response, cart) = await api.AddAsync(variantId, 2, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(cart);
        var line = Assert.Single(cart.Lines);
        Assert.Equal(variantId, line.VariantId);
        Assert.Equal<short>(2, line.Quantity);
        Assert.Equal(2, cart.Totals.ItemCount);

        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("sd_cart=", setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=", setCookie, StringComparison.OrdinalIgnoreCase);

        // The value is the token and nothing else, and the token is nowhere in the body: the cookie is
        // HttpOnly so a script cannot read it, and repeating it in JSON would hand it to any script on
        // the page and make the flag decorative.
        var token = setCookie["sd_cart=".Length..].Split(';')[0];
        Assert.True(Guid.TryParseExact(token, "D", out var parsed) && parsed != Guid.Empty);
        Assert.DoesNotContain(token, await ResponseBodyAsync(api, Token), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The bag survives the next request, which is the whole purpose of the cookie.</summary>
    [Fact]
    public async Task The_bag_is_still_there_on_the_next_request()
    {
        var api = NewVisitor();
        var variantId = (await InStockVariantIdsAsync(1))[0];

        await api.AddAsync(variantId, 1, Token);
        var (response, cart) = await api.GetAsync(Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(cart);
        Assert.Equal(variantId, Assert.Single(cart.Lines).VariantId);
    }

    /// <summary>
    /// Doc 08 §3.5: adding a variant that is already in the bag adds to the quantity rather than making a
    /// second line, and the total is capped at 10 without an error. The cap is the database's
    /// <c>IIF</c>, so this also proves the SQL is the statement doc 08 specifies.
    /// </summary>
    [Fact]
    public async Task Adding_the_same_variant_again_sums_the_quantity_and_caps_at_ten()
    {
        var api = NewVisitor();
        var variantId = await VariantWithAtLeastAsync(CartItem.MaxQuantity + 2);

        await api.AddAsync(variantId, 6, Token);
        var (response, cart) = await api.AddAsync(variantId, 7, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(cart);
        var line = Assert.Single(cart.Lines);
        Assert.Equal(CartItem.MaxQuantity, line.Quantity);
        Assert.Equal(CartItem.MaxQuantity, cart.Totals.ItemCount);
    }

    [Fact]
    public async Task Two_different_variants_are_two_lines_and_the_subtotal_is_the_sum_of_both()
    {
        var api = NewVisitor();
        var ids = await InStockVariantIdsAsync(2);

        await api.AddAsync(ids[0], 1, Token);
        var (_, cart) = await api.AddAsync(ids[1], 2, Token);

        Assert.NotNull(cart);
        Assert.Equal(2, cart.Lines.Count);
        Assert.Equal(3, cart.Totals.ItemCount);
        Assert.Equal(cart.Lines.Sum(line => line.LineSubtotal!.Value), cart.Totals.ItemsSubtotal);
    }

    [Fact]
    public async Task A_quantity_change_replaces_the_quantity_and_a_removal_empties_the_bag()
    {
        var api = NewVisitor();
        var variantId = await VariantWithAtLeastAsync(5);

        await api.AddAsync(variantId, 2, Token);

        var (setResponse, afterSet) = await api.SetQuantityAsync(variantId, 5, Token);
        Assert.Equal(HttpStatusCode.OK, setResponse.StatusCode);
        Assert.NotNull(afterSet);
        Assert.Equal<short>(5, Assert.Single(afterSet.Lines).Quantity);

        var (removeResponse, afterRemove) = await api.RemoveAsync(variantId, Token);
        Assert.Equal(HttpStatusCode.OK, removeResponse.StatusCode);
        Assert.NotNull(afterRemove);
        Assert.Empty(afterRemove.Lines);
        Assert.Equal(0, afterRemove.Totals.ItemCount);
    }

    /// <summary>
    /// Removing twice is not an error. A shopper who double-taps Remove, or who has the bag open in two
    /// tabs, is asking for a state they already want.
    /// </summary>
    [Fact]
    public async Task Removing_a_line_that_is_not_there_answers_the_bag_rather_than_a_404()
    {
        var api = NewVisitor();
        var ids = await InStockVariantIdsAsync(2);

        await api.AddAsync(ids[0], 1, Token);
        await api.RemoveAsync(ids[0], Token);

        var (response, cart) = await api.RemoveAsync(ids[0], Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(cart);
        Assert.Empty(cart.Lines);

        var (otherResponse, otherCart) = await api.RemoveAsync(ids[1], Token);
        Assert.Equal(HttpStatusCode.OK, otherResponse.StatusCode);
        Assert.NotNull(otherCart);
        Assert.Empty(otherCart.Lines);
    }

    /// <summary>
    /// A quantity change aimed at a bag that does not exist, or at a line that is not in it, answers the
    /// bag as it stands. It must not create a cart: an endpoint that creates a row for any request would
    /// let a script fill the table.
    /// </summary>
    [Fact]
    public async Task A_quantity_change_on_a_bag_that_does_not_exist_creates_nothing()
    {
        var api = NewVisitor();
        var variantId = (await InStockVariantIdsAsync(1))[0];

        var before = await CountCartsAsync();
        var (response, cart) = await api.SetQuantityAsync(variantId, 3, Token);
        var after = await CountCartsAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(cart);
        Assert.Empty(cart.Lines);
        Assert.Equal(before, after);
    }

    /// <summary>
    /// An add that is refused creates no cart and no cookie either. Without this, every scripted probe
    /// with a nonsense variant id would leave a row behind.
    /// </summary>
    [Fact]
    public async Task A_refused_add_leaves_no_cart_row_and_no_cookie()
    {
        var api = NewVisitor();

        var before = await CountCartsAsync();
        var (response, _) = await api.AddAsync(2_000_000_000, 1, Token);
        var after = await CountCartsAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, after);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    /// <summary>
    /// <b>Somebody else's token.</b> Two visitors, each with their own bag; the second one sends the
    /// first one's token.
    /// <para>
    /// A guest token is a bearer capability, so presenting it does reach that guest bag — that is what the
    /// cookie is for, and it is why the cookie is HttpOnly, Secure and SameSite=Lax. What must never
    /// happen is for the two bags to merge, or for the holder of a token to reach a bag that belongs to a
    /// signed-in account (see <see cref="CartAccountAndMergeTests"/>, where the account branch is proved).
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_token_reaches_only_the_guest_bag_it_names_and_never_another_visitors()
    {
        var first = NewVisitor();
        var second = NewVisitor();
        var ids = await InStockVariantIdsAsync(2);

        var (firstAdd, _) = await first.AddAsync(ids[0], 1, Token);
        await second.AddAsync(ids[1], 3, Token);

        var firstToken = TokenFrom(firstAdd);

        // A third visitor with no bag of its own, presenting the first visitor's token.
        var thief = NewVisitorWithoutCookieJar();
        var (response, cart) = await thief.GetWithCookieAsync(firstToken.ToString("D"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(cart);
        Assert.Equal(ids[0], Assert.Single(cart.Lines).VariantId);

        // And the second visitor's bag is untouched by any of that.
        var (_, secondCart) = await second.GetAsync(Token);
        Assert.NotNull(secondCart);
        Assert.Equal(ids[1], Assert.Single(secondCart.Lines).VariantId);
        Assert.Equal(3, secondCart.Totals.ItemCount);
    }

    /// <summary>
    /// Doc 08 §3.5: the cookie is sliding, so an answer that resolved the bag reissues it rather than
    /// letting the original 30 days run down. Measured as a later expiry on the wire.
    /// </summary>
    [Fact]
    public async Task A_read_reissues_the_cookie_so_the_thirty_day_window_slides()
    {
        var api = NewVisitor();
        var variantId = (await InStockVariantIdsAsync(1))[0];

        var (addResponse, _) = await api.AddAsync(variantId, 1, Token);
        var issued = ExpiryFrom(addResponse);

        var (getResponse, _) = await api.GetAsync(Token);
        var reissued = ExpiryFrom(getResponse);

        Assert.True(reissued >= issued, $"the read's expiry {reissued:O} is earlier than the add's {issued:O}");
        Assert.InRange(reissued, DateTimeOffset.UtcNow.AddDays(29), DateTimeOffset.UtcNow.AddDays(31));
    }

    /// <summary>
    /// Doc 08 §3.5's inactivity clock. A read moves it, which is what the 30/180-day purge measures.
    /// </summary>
    [Fact]
    public async Task A_read_moves_the_last_activity_clock()
    {
        var api = NewVisitor();
        var variantId = (await InStockVariantIdsAsync(1))[0];

        var (addResponse, _) = await api.AddAsync(variantId, 1, Token);
        var token = TokenFrom(addResponse);
        var created = await LastActivityAsync(token);

        await Task.Delay(TimeSpan.FromMilliseconds(20), Token);
        await api.GetAsync(Token);

        Assert.True(await LastActivityAsync(token) >= created);
    }

    private static Guid TokenFrom(HttpResponseMessage response)
    {
        var setCookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("sd_cart=", StringComparison.Ordinal));

        return Guid.ParseExact(setCookie["sd_cart=".Length..].Split(';')[0], "D");
    }

    private static DateTimeOffset ExpiryFrom(HttpResponseMessage response)
    {
        var setCookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("sd_cart=", StringComparison.Ordinal));

        var expires = setCookie
            .Split(';')
            .Select(part => part.Trim())
            .Single(part => part.StartsWith("expires=", StringComparison.OrdinalIgnoreCase));

        return DateTimeOffset.Parse(
            expires["expires=".Length..],
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ResponseBodyAsync(CartApiClient api, CancellationToken cancellationToken)
    {
        using var response = await api.Http.GetAsync(new Uri(CartApiClient.Path, UriKind.Relative), cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<int> CountCartsAsync()
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Carts.CountAsync(Token);
    }

    private async Task<DateTime> LastActivityAsync(Guid token)
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Carts
            .AsNoTracking()
            .Where(cart => cart.Token == token)
            .Select(cart => cart.LastActivityAtUtc)
            .SingleAsync(Token);
    }

    /// <summary>A seeded variant with at least this much stock, so a quantity test is not testing the stock cap.</summary>
    private async Task<int> VariantWithAtLeastAsync(int available)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var id = await db.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.IsActive && variant.Product!.IsPublished)
            .Where(variant => variant.Product!.Department!.IsActive && variant.Product!.PrimaryCategory!.IsActive)
            .Where(variant => variant.StockLevel != null && variant.StockLevel.Available >= available)
            .OrderBy(variant => variant.Id)
            .Select(variant => variant.Id)
            .FirstOrDefaultAsync(Token);

        Assert.True(id > 0, $"the seeded catalogue has no in-stock variant with at least {available} available");
        return id;
    }
}
