using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Cart;
using SpencerDenim.Application.Sales.Cart.Abstractions;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Doc 08 §3.5 (DB22) under genuine concurrency.
/// <para>
/// A cart has <b>no rowversion</b> (doc 08 §1.5 excludes hot rows), so nothing stops a lost update except
/// the shape of the statement: the add is one <c>UPDATE … SET Quantity = IIF(Quantity + @q &gt; 10, 10,
/// Quantity + @q)</c>, and when that matches nothing, an <c>INSERT</c> whose unique-index violation is
/// retried as the <c>UPDATE</c>. There are exactly two ways for that to be wrong, and each has its own
/// test here:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Two rows.</b> Several adds race to insert the same <c>(CartId, VariantId)</c> and more than one
/// succeeds — which would mean one variant appearing twice in the bag, and <c>UX_CartItems_Cart_Variant</c>
/// missing from the migration.
/// </description></item>
/// <item><description>
/// <b>A lost update.</b> Several adds read the quantity and write back their own idea of the sum, so the
/// last writer wins and the others are silently dropped. That is the bug a read-then-write produces, and
/// the only reason this code cannot have it is that the arithmetic never leaves the engine.
/// </description></item>
/// </list>
/// <para>
/// <b>Every case is run many times, not once.</b> A race that resolves correctly nine times in ten is not
/// fixed, and a single green run proves almost nothing: the window these tests aim at is a few
/// milliseconds wide. The repeat counts below are deliberately high enough to hit it and low enough to keep
/// the suite in seconds.
/// </para>
/// </summary>
public sealed class CartConcurrencyTests(SqlServerDatabaseFixture sqlServer) : CartTestBase(sqlServer)
{
    /// <summary>How many times each race is replayed. Each round is a fresh cart.</summary>
    private const int Rounds = 25;

    /// <summary>How many requests arrive at once in the many-writers rounds.</summary>
    private const int Writers = 8;

    /// <summary>
    /// <b>The insert race, over HTTP.</b> Eight simultaneous Add to Bag requests for a variant the bag does
    /// not hold yet. Every one of them finds nothing to update and tries to insert; one wins and the other
    /// seven must come back through the UPDATE instead of inserting a second row.
    /// </summary>
    [Fact]
    public async Task Eight_simultaneous_adds_of_a_new_variant_end_at_one_row_holding_all_eight()
    {
        // Two *different* variants with enough stock, and the distinctness is load-bearing: picking the
        // anchor and the contested variant with two separate "first in-stock variant" queries returned the
        // same id, so the round added 1 + 8 to one row and the assertion read 9. The test was wrong, not
        // the SQL — but a bag whose anchor is the variant under test cannot measure an insert race at all,
        // because the row already exists.
        var ids = await VariantsWithAtLeastAsync(CartItem.MaxQuantity + 2, 2);
        var anchorVariantId = ids[0];
        var contestedVariantId = ids[1];

        for (var round = 1; round <= Rounds; round++)
        {
            var (token, cartId) = await NewBagAsync(anchorVariantId);
            var api = NewVisitorWithoutCookieJar();

            var responses = await Task.WhenAll(Enumerable.Range(0, Writers).Select(_ =>
                api.AddWithCookieAsync(token.ToString("D"), contestedVariantId, 1, Token)));

            Assert.All(responses, result => Assert.Equal(HttpStatusCode.OK, result.Response.StatusCode));

            var rows = await RowsForAsync(cartId, contestedVariantId);
            var single = Assert.Single(rows);
            Assert.Equal(
                Writers,
                single.Quantity);
        }
    }

    /// <summary>
    /// <b>The update race, over HTTP.</b> The row already exists, and eight adds of 1 arrive at once. The
    /// quantity must be 8: anything less is a lost update, which is precisely what a read-then-write would
    /// produce here and what no rowversion is present to catch.
    /// </summary>
    [Fact]
    public async Task Eight_simultaneous_adds_to_an_existing_line_lose_none_of_them()
    {
        var variantId = await VariantWithAtLeastAsync(Writers + 2);

        for (var round = 1; round <= Rounds; round++)
        {
            var (token, cartId) = await NewBagAsync(variantId);
            var api = NewVisitorWithoutCookieJar();

            await Task.WhenAll(Enumerable.Range(0, Writers - 1).Select(_ =>
                api.AddWithCookieAsync(token.ToString("D"), variantId, 1, Token)));

            var rows = await RowsForAsync(cartId, variantId);
            var single = Assert.Single(rows);

            // The bag was created with 1, then seven more arrived at once.
            Assert.Equal(Writers, single.Quantity);
        }
    }

    /// <summary>
    /// The clamp survives the race. Two adds of 6 to an empty line must land on 10 — not 12, not 6, and not
    /// an error. The arithmetic and the cap are in the same statement, so there is no instant at which a
    /// row holds 12.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_adds_that_would_exceed_ten_land_exactly_on_ten()
    {
        var ids = await VariantsWithAtLeastAsync(CartItem.MaxQuantity + 2, 2);
        var anchorVariantId = ids[0];
        var contestedVariantId = ids[1];

        for (var round = 1; round <= Rounds; round++)
        {
            var (token, cartId) = await NewBagAsync(anchorVariantId);
            var api = NewVisitorWithoutCookieJar();

            await Task.WhenAll(
                api.AddWithCookieAsync(token.ToString("D"), contestedVariantId, 6, Token),
                api.AddWithCookieAsync(token.ToString("D"), contestedVariantId, 6, Token));

            var single = Assert.Single(await RowsForAsync(cartId, contestedVariantId));
            Assert.Equal(CartItem.MaxQuantity, single.Quantity);
        }
    }

    /// <summary>
    /// The same two races driven straight at <see cref="ICartStore"/>, with no HTTP in the way.
    /// <para>
    /// Worth having as well as the endpoint tests: a request spends most of its time in routing, binding
    /// and the read-back projection, so the eight writers arrive spread out. Calling the store directly
    /// bunches them into the few milliseconds that the UPDATE/INSERT window actually occupies, which is
    /// where the race lives. It is run far more often than the HTTP rounds because it is far cheaper.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_store_itself_never_loses_an_add_and_never_makes_a_second_row()
    {
        const int storeRounds = 60;
        const int storeWriters = 10;

        var variantId = await VariantWithAtLeastAsync(storeWriters);

        for (var round = 1; round <= storeRounds; round++)
        {
            var cartId = await NewBagDirectAsync();

            await Task.WhenAll(Enumerable.Range(0, storeWriters).Select(async _ =>
            {
                // A scope each: AppDbContext is scoped, and sharing one across threads is its own bug
                // rather than a test of the SQL.
                using var scope = CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<ICartStore>();
                await store.AddItemAsync(cartId, variantId, 1, 1000m, Token);
            }));

            var single = Assert.Single(await RowsForAsync(cartId, variantId));
            Assert.Equal(storeWriters, single.Quantity);
        }
    }

    /// <summary>
    /// <b>One customer can never end up with two carts.</b> Ten simultaneous "give me this customer's
    /// cart, creating it if needed" calls, which is what two tabs signing in at the same moment look like.
    /// <para>
    /// This is what <c>UX_Carts_Customer</c> (filtered <c>WHERE CustomerUserId IS NOT NULL</c>) is in the
    /// migration for, and it is only provable here: the losing inserts raise 2601 and
    /// <c>CartStore.EnsureAsync</c> turns that into a second read.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Ten_simultaneous_creates_for_one_customer_make_exactly_one_cart()
    {
        const int rounds = 15;
        const int writers = 10;

        for (var round = 1; round <= rounds; round++)
        {
            var customerUserId = await NewCustomerUserIdAsync();

            var carts = await Task.WhenAll(Enumerable.Range(0, writers).Select(async _ =>
            {
                using var scope = CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<ICartStore>();
                return await store.EnsureAsync(CartLookup.ForCustomer(customerUserId, null), Token);
            }));

            using var scope = CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.Carts
                .AsNoTracking()
                .Where(cart => cart.CustomerUserId == customerUserId)
                .ToListAsync(Token);

            Assert.Single(rows);

            // And every caller came away with the one cart that exists, not with a handle to a row the
            // index rejected.
            Assert.All(carts, cart => Assert.Equal(rows[0].Id, cart.Id));
        }
    }

    /// <summary>A fresh guest bag holding one of <paramref name="variantId"/>, plus its token and id.</summary>
    private async Task<(Guid Token, int CartId)> NewBagAsync(int variantId)
    {
        var api = NewVisitor();
        var (response, _) = await api.AddAsync(variantId, 1, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var setCookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("sd_cart=", StringComparison.Ordinal));
        var token = Guid.ParseExact(setCookie["sd_cart=".Length..].Split(';')[0], "D");

        using var scope = CreateScope();
        var cartId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Carts
            .AsNoTracking()
            .Where(cart => cart.Token == token)
            .Select(cart => cart.Id)
            .SingleAsync(Token);

        return (token, cartId);
    }

    /// <summary>An empty guest bag created through the store, for the rounds that do not need HTTP.</summary>
    private async Task<int> NewBagDirectAsync()
    {
        using var scope = CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ICartStore>();
        var cart = await store.EnsureAsync(CartLookup.ForGuest(null), Token);
        return cart.Id;
    }

    /// <summary>
    /// A customer user row to hang an account cart off. <c>Carts.CustomerUserId</c> has a real foreign key
    /// to <c>identity.Users</c>, so the id has to exist — and using a real row rather than an arbitrary
    /// integer is what makes the test exercise the constraint rather than route around it.
    /// </summary>
    private async Task<int> NewCustomerUserIdAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var suffix = Guid.NewGuid().ToString("N");
        var email = $"cart-concurrency-{suffix}@example.com";
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            UserType = UserType.Customer,
            FirstName = "Cart",
            LastName = "Concurrency",
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

    private async Task<List<CartRowSnapshot>> RowsForAsync(int cartId, int variantId)
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().CartItems
            .AsNoTracking()
            .Where(item => item.CartId == cartId && item.VariantId == variantId)
            .Select(item => new CartRowSnapshot(item.Id, item.Quantity))
            .ToListAsync(Token);
    }

    private async Task<int> VariantWithAtLeastAsync(int available) =>
        (await VariantsWithAtLeastAsync(available, 1))[0];

    /// <summary>
    /// <paramref name="count"/> <b>distinct</b> seeded variants with at least <paramref name="available"/> in
    /// stock. Distinct matters: two calls asking for "a variant with enough stock" return the same id, and a
    /// race test whose two variants are the same variant is not testing what it says it is.
    /// </summary>
    private async Task<IReadOnlyList<int>> VariantsWithAtLeastAsync(int available, int count)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var ids = await db.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.IsActive && variant.Product!.IsPublished)
            .Where(variant => variant.Product!.Department!.IsActive && variant.Product!.PrimaryCategory!.IsActive)
            .Where(variant => variant.StockLevel != null && variant.StockLevel.Available >= available)
            .OrderBy(variant => variant.Id)
            .Select(variant => variant.Id)
            .Take(count)
            .ToListAsync(Token);

        Assert.Equal(count, ids.Count);
        Assert.Equal(count, ids.Distinct().Count());
        return ids;
    }

    private sealed record CartRowSnapshot(int Id, short Quantity);
}
