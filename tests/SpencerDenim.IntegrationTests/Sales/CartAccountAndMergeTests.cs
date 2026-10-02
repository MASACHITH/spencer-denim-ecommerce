using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Sales.Cart;
using SpencerDenim.Application.Sales.Cart.Abstractions;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// The account cart and the sign-in merge (doc 08 §3.5).
/// <para>
/// <b>These run against the application layer, not over HTTP, and the reason is a gap rather than a
/// preference.</b> There is no customer authentication in this repository: the only cookie scheme is the
/// admin one (ACC-01 is not built), so no request can arrive carrying a customer principal and the account
/// branch of the bag is unreachable from outside. The logic is still here, and still tested, because the
/// merge is where a shopper's bag is silently lost if it is written in a hurry at the end of ACC-01 — and
/// because <c>UX_Carts_Customer</c> is in this task's migration specifically to stop the double cart these
/// tests would otherwise find. ACC-01 calls <see cref="MergeGuestCartCommandHandler"/> after its
/// <c>SignInAsync</c> and reads the customer id in <c>CartEndpoints.ReadLookup</c>; nothing else changes.
/// </para>
/// </summary>
public sealed class CartAccountAndMergeTests(SqlServerDatabaseFixture sqlServer) : CartTestBase(sqlServer)
{
    /// <summary>
    /// <b>Merge, not replace.</b> A guest bag and an account bag holding different things end as one bag
    /// holding both — nothing is dropped in favour of the other.
    /// </summary>
    [Fact]
    public async Task Signing_in_keeps_both_bags_and_deletes_the_guest_cart()
    {
        var ids = await DistinctVariantIdsAsync(2);
        var customerUserId = await NewCustomerUserIdAsync();

        var guestToken = await NewGuestCartAsync((ids[0], 2));
        await AddToCustomerCartAsync(customerUserId, (ids[1], 1));

        var view = await MergeAsync(guestToken, customerUserId);

        Assert.Equal(2, view.Cart.Lines.Count);
        Assert.Equal(3, view.Cart.Totals.ItemCount);
        Assert.Equal<short>(2, view.Cart.Lines.Single(line => line.VariantId == ids[0]).Quantity);
        Assert.Equal<short>(1, view.Cart.Lines.Single(line => line.VariantId == ids[1]).Quantity);

        // The guest cart is gone, and the browser is told to stop sending its token.
        Assert.Null(await FindCartByTokenAsync(guestToken));
        Assert.Equal(CartCookieAction.Clear, view.Cookie);
        Assert.Null(view.GuestToken);
    }

    /// <summary>
    /// The same variant in both bags sums and clamps at 10, exactly as a second Add to Bag would. Two lines
    /// for one variant would break <c>UX_CartItems_Cart_Variant</c>; silently keeping one of the two
    /// quantities would be the lost update in a different coat.
    /// </summary>
    [Fact]
    public async Task A_variant_in_both_bags_sums_once_and_clamps_at_ten()
    {
        var variantId = (await DistinctVariantIdsAsync(1))[0];
        var customerUserId = await NewCustomerUserIdAsync();

        var guestToken = await NewGuestCartAsync((variantId, 7));
        await AddToCustomerCartAsync(customerUserId, (variantId, 6));

        var view = await MergeAsync(guestToken, customerUserId);

        var line = Assert.Single(view.Cart.Lines);
        Assert.Equal(CartItem.MaxQuantity, line.Quantity);

        // One row, not two. Read from the table rather than from the projection, because a projection that
        // grouped would hide a second row.
        Assert.Single(await CartItemRowsAsync(customerUserId, variantId));
    }

    /// <summary>A sign-in with no guest bag leaves the account bag exactly as it was.</summary>
    [Fact]
    public async Task Signing_in_without_a_guest_bag_changes_nothing()
    {
        var variantId = (await DistinctVariantIdsAsync(1))[0];
        var customerUserId = await NewCustomerUserIdAsync();

        await AddToCustomerCartAsync(customerUserId, (variantId, 2));

        var view = await MergeAsync(guestToken: null, customerUserId);

        Assert.Equal<short>(2, Assert.Single(view.Cart.Lines).Quantity);
        Assert.Equal(CartCookieAction.Leave, view.Cookie);
    }

    /// <summary>
    /// A sign-in with nothing in either bag must not leave an empty cart row behind. Doc 08 §3.5 creates the
    /// row on the first Add to Bag, and a sign-in is not one.
    /// </summary>
    [Fact]
    public async Task Signing_in_with_nothing_in_either_bag_creates_no_cart()
    {
        var customerUserId = await NewCustomerUserIdAsync();

        var before = await CountCartsAsync();
        var view = await MergeAsync(guestToken: null, customerUserId);
        var after = await CountCartsAsync();

        Assert.Empty(view.Cart.Lines);
        Assert.Equal(before, after);
    }

    /// <summary>A guest token that names nothing — expired, purged, invented — is not an error at sign-in.</summary>
    [Fact]
    public async Task Signing_in_with_a_token_that_names_nothing_is_not_an_error()
    {
        var variantId = (await DistinctVariantIdsAsync(1))[0];
        var customerUserId = await NewCustomerUserIdAsync();
        await AddToCustomerCartAsync(customerUserId, (variantId, 1));

        var view = await MergeAsync(Guid.NewGuid(), customerUserId);

        Assert.Equal<short>(1, Assert.Single(view.Cart.Lines).Quantity);
    }

    /// <summary>
    /// <b>A guest token can never reach an account cart.</b> This is the "someone sends another person's
    /// token" case that actually matters: a customer's bag is addressed by their user id, so even the exact
    /// token stored on their cart row resolves to nothing when it arrives as a guest cookie.
    /// </summary>
    [Fact]
    public async Task A_customer_carts_own_token_presented_as_a_guest_cookie_resolves_to_nothing()
    {
        var variantId = (await DistinctVariantIdsAsync(1))[0];
        var customerUserId = await NewCustomerUserIdAsync();
        await AddToCustomerCartAsync(customerUserId, (variantId, 1));

        var accountToken = await ReadAsync(db => db.Carts
            .AsNoTracking()
            .Where(cart => cart.CustomerUserId == customerUserId)
            .Select(cart => cart.Token)
            .SingleAsync(Token));

        using var scope = CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ICartStore>();

        Assert.Null(await store.FindAsync(CartLookup.ForGuest(accountToken), Token));

        // And over HTTP, where every request is a guest today, it is an empty bag rather than someone
        // else's.
        var api = NewVisitorWithoutCookieJar();
        var (response, cart) = await api.GetWithCookieAsync(accountToken.ToString("D"), Token);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(cart);
        Assert.Empty(cart.Lines);
    }

    /// <summary>
    /// A signed-in customer reading their bag while still carrying a guest cookie gets the account bag, and
    /// the guest token is cleared rather than left to be sent for another 30 days.
    /// </summary>
    [Fact]
    public async Task A_customer_read_answers_the_account_bag_and_clears_any_guest_token()
    {
        var ids = await DistinctVariantIdsAsync(2);
        var customerUserId = await NewCustomerUserIdAsync();

        var guestToken = await NewGuestCartAsync((ids[0], 1));
        await AddToCustomerCartAsync(customerUserId, (ids[1], 4));

        using var scope = CreateApplicationScope();
        var handler = scope.ServiceProvider.GetRequiredService<IQueryHandler<GetCartQuery, CartView>>();
        var view = await handler.HandleAsync(
            new GetCartQuery(CartLookup.ForCustomer(customerUserId, guestToken)),
            Token);

        Assert.Equal(ids[1], Assert.Single(view.Cart.Lines).VariantId);
        Assert.Equal(CartCookieAction.Clear, view.Cookie);

        // The guest cart still exists — a read is not a merge, and silently consuming it on a read would
        // mean the merge happened at an unpredictable moment.
        Assert.NotNull(await FindCartByTokenAsync(guestToken));
    }

    private async Task<CartView> MergeAsync(Guid? guestToken, int customerUserId)
    {
        using var scope = CreateApplicationScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<MergeGuestCartCommand, CartView>>();
        return await handler.HandleAsync(new MergeGuestCartCommand(guestToken, customerUserId), Token);
    }

    private async Task<Guid> NewGuestCartAsync(params (int VariantId, short Quantity)[] lines)
    {
        using var scope = CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ICartStore>();
        var cart = await store.EnsureAsync(CartLookup.ForGuest(null), Token);

        foreach (var (variantId, quantity) in lines)
        {
            await store.AddItemAsync(cart.Id, variantId, quantity, 1000m, Token);
        }

        return cart.Token;
    }

    private async Task AddToCustomerCartAsync(int customerUserId, params (int VariantId, short Quantity)[] lines)
    {
        using var scope = CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ICartStore>();
        var cart = await store.EnsureAsync(CartLookup.ForCustomer(customerUserId, null), Token);

        foreach (var (variantId, quantity) in lines)
        {
            await store.AddItemAsync(cart.Id, variantId, quantity, 1000m, Token);
        }
    }

    /// <summary>
    /// <c>async</c>, and it has to be: the first version returned the <see cref="Task"/> from inside a
    /// <c>using var scope</c>, so the scope — and the <c>AppDbContext</c> in it — was disposed before the
    /// query ran, and the two tests that called it failed with "Invalid operation. The connection is
    /// closed." Awaiting inside the scope is the whole fix.
    /// </summary>
    private async Task<CartIdentity?> FindCartByTokenAsync(Guid token)
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ICartStore>()
            .FindAsync(CartLookup.ForGuest(token), Token);
    }

    private Task<List<int>> CartItemRowsAsync(int customerUserId, int variantId) => ReadAsync(db => db.CartItems
        .AsNoTracking()
        .Where(item => item.VariantId == variantId && item.Cart!.CustomerUserId == customerUserId)
        .Select(item => item.Id)
        .ToListAsync(Token));

    private Task<int> CountCartsAsync() => ReadAsync(db => db.Carts.CountAsync(Token));

    private async Task<IReadOnlyList<int>> DistinctVariantIdsAsync(int count)
    {
        var ids = await InStockVariantIdsAsync(count);
        Assert.Equal(count, ids.Distinct().Count());
        return ids;
    }

    private async Task<int> NewCustomerUserIdAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var suffix = Guid.NewGuid().ToString("N");
        var email = $"cart-merge-{suffix}@example.com";
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            UserType = UserType.Customer,
            FirstName = "Cart",
            LastName = "Merge",
            CreatedAtUtc = DateTime.UtcNow,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            NormalizedUserName = email.ToUpperInvariant(),
            NormalizedEmail = email.ToUpperInvariant(),
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(Token);
        return user.Id;
    }

    private async Task<T> ReadAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        using var scope = CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
