using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.IntegrationTests.Sales;

namespace SpencerDenim.IntegrationTests.Api.Accounts;

/// <summary>
/// The bag and the order across a sign-in, end to end over HTTP: guest bag → sign in → the same line in
/// <c>/api/cart</c> and <c>/api/checkout</c> → place the order → it is in <c>/api/account/orders</c>.
/// </summary>
/// <remarks>
/// <b>Written because the independent tester failed W3-01 on exactly this, and nothing had tested it.</b>
/// Sign-in merged the guest bag into the account cart and cleared <c>sd_cart</c> — and both the bag and
/// checkout endpoints went on calling <c>CartLookup.ForGuest(cookie)</c> with no cookie. So the bag
/// emptied the moment a shopper signed in, and <c>PlaceOrderCommand.CustomerUserId</c> was never set, so
/// every order placed while signed in was a guest order that <b>never appeared in My account</b> — the
/// feature defeated by its own sign-in. The journal said "the bag merge is built and is covered". The
/// merge <i>store</i> was covered; the round trip was not, and the storefront's "re-reads the bag" spec
/// checked against a mock.
/// <para>
/// <b>Hence one test that walks the whole path through the real endpoints, in one browser</b>, rather than
/// a test of the merge store — which is the blind spot that let this through.
/// </para>
/// </remarks>
public sealed class SignedInBagAndOrderTests(SqlServerDatabaseFixture sqlServer) : CheckoutTestBase(sqlServer)
{
    private const string Password = "Correct-Horse-Battery-1!";

    [Fact]
    public async Task A_guest_bag_survives_sign_in_and_the_order_placed_lands_in_my_account()
    {
        SqlServer.SkipIfUnavailable();

        var variantId = await CheckoutVariantIdAsync();
        var district = await DistrictIdAsync("Colombo");
        var method = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var visitor = NewVisitor();
        var browser = visitor.Http;
        var email = $"bag-round-trip-{Guid.NewGuid():N}@example.com";

        // 1. A guest puts something in the bag.
        var (added, _) = await visitor.AddAsync(variantId, 2, Token);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        // 2. They register and sign in, in the same browser.
        using (var register = await browser.PostAsJsonAsync(
            "/api/account/register",
            new { firstName = "Nimal", lastName = "Perera", email, password = Password },
            Token))
        {
            Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        }

        using (var signIn = await browser.PostAsJsonAsync("/api/account/login", new { email, password = Password }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        }

        var userId = await UserIdAsync(email);

        // 3. The bag still holds the line — read through the bag endpoint...
        var (cartResponse, cart) = await visitor.GetAsync(Token);
        Assert.Equal(HttpStatusCode.OK, cartResponse.StatusCode);
        Assert.NotNull(cart);
        var line = Assert.Single(cart.Lines);
        Assert.Equal(variantId, line.VariantId);
        Assert.Equal(2, line.Quantity);

        // ...and through checkout, which is a view of the same bag.
        var checkout = new CheckoutApiClient(browser);
        var (sessionResponse, session) = await checkout.GetAsync(district, method, Token);
        Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
        Assert.NotNull(session);
        Assert.Single(session.Lines);

        // And it is the *account's* cart that holds it, so another device signing in would see it too.
        Assert.Equal(1, await AccountCartLineCountAsync(userId));

        // 4. Place the order while signed in.
        var (placed, order) = await checkout.PlaceAsync(
            ValidBody(session.CheckoutKey, method, district, QuotedTotal(session)),
            Token);

        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        Assert.NotNull(order);

        // 5. It belongs to the account — in the database and in My account.
        Assert.Equal(userId, await OrderOwnerAsync(order.OrderNumber));

        using var history = await browser.GetAsync(new Uri("/api/account/orders", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);

        var body = await history.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(order.OrderNumber, body.GetProperty("orders")[0].GetProperty("orderNumber").GetString());

        // 6. And the bag the order consumed is empty afterwards, as it is for a guest.
        var (_, afterOrder) = await visitor.GetAsync(Token);
        Assert.NotNull(afterOrder);
        Assert.Empty(afterOrder.Lines);
    }

    /// <summary>
    /// A bag filled while signed in is the account's, so a second browser that signs in sees it — doc 08
    /// §3.5's "server-side for everyone", and doc 11 §1.4's "account bag … across devices".
    /// </summary>
    [Fact]
    public async Task A_bag_filled_while_signed_in_follows_the_customer_to_another_browser()
    {
        SqlServer.SkipIfUnavailable();

        var variantId = await CheckoutVariantIdAsync();
        var email = $"bag-two-devices-{Guid.NewGuid():N}@example.com";

        var laptop = NewVisitor();

        using (var register = await laptop.Http.PostAsJsonAsync(
            "/api/account/register",
            new { firstName = "Nimal", lastName = (string?)null, email, password = Password },
            Token))
        {
            Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        }

        using (var signIn = await laptop.Http.PostAsJsonAsync("/api/account/login", new { email, password = Password }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        }

        var (added, _) = await laptop.AddAsync(variantId, 1, Token);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        // No guest cookie is issued to a signed-in customer: the account is what identifies the bag.
        Assert.False(
            added.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(value => value.StartsWith("sd_cart=", StringComparison.Ordinal)
                && !value.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)),
            "A signed-in customer was handed a guest bag cookie.");

        var phone = NewVisitor();

        using (var signIn = await phone.Http.PostAsJsonAsync("/api/account/login", new { email, password = Password }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        }

        var (_, cart) = await phone.GetAsync(Token);
        Assert.NotNull(cart);
        Assert.Equal(variantId, Assert.Single(cart.Lines).VariantId);
    }

    /// <summary>
    /// Doc 11 P11 step B.7: signing out does not restore the guest bag — and does not leak the account's
    /// bag to whoever uses the browser next.
    /// </summary>
    [Fact]
    public async Task Signing_out_leaves_an_empty_guest_bag_rather_than_the_accounts()
    {
        SqlServer.SkipIfUnavailable();

        var variantId = await CheckoutVariantIdAsync();
        var email = $"bag-sign-out-{Guid.NewGuid():N}@example.com";

        var visitor = NewVisitor();
        await visitor.AddAsync(variantId, 1, Token);

        using (var register = await visitor.Http.PostAsJsonAsync(
            "/api/account/register",
            new { firstName = "Nimal", lastName = (string?)null, email, password = Password },
            Token))
        {
            Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        }

        using (var signIn = await visitor.Http.PostAsJsonAsync("/api/account/login", new { email, password = Password }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        }

        var (_, signedIn) = await visitor.GetAsync(Token);
        Assert.NotNull(signedIn);
        Assert.Single(signedIn.Lines);

        using (var signOut = await visitor.Http.PostAsync(new Uri("/api/account/logout", UriKind.Relative), null, Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
        }

        var (_, signedOut) = await visitor.GetAsync(Token);
        Assert.NotNull(signedOut);
        Assert.Empty(signedOut.Lines);
    }

    private async Task<int> UserIdAsync(string email)
    {
        using var scope = CreateScope();
        var normalised = email.ToUpperInvariant();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users
            .AsNoTracking()
            .Where(user => user.NormalizedEmail == normalised)
            .Select(user => user.Id)
            .SingleAsync(Token);
    }

    private async Task<int> AccountCartLineCountAsync(int userId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.CartItems
            .AsNoTracking()
            .CountAsync(
                item => db.Carts.Any(cart => cart.Id == item.CartId && cart.CustomerUserId == userId),
                Token);
    }

    private async Task<int?> OrderOwnerAsync(string orderNumber)
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
            .AsNoTracking()
            .Where(order => order.OrderNumber == orderNumber)
            .Select(order => order.CustomerUserId)
            .SingleAsync(Token);
    }
}
