using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// How many SQL commands a bag read costs (CART-01), which is the property that regresses silently.
/// <para>
/// Repricing every line on every read (doc 08 §6) is the whole design of <c>CartReader</c>, and the way
/// that goes wrong is not a wrong number on the screen — it is one query per line. A bag of ten would
/// still be correct and would cost ten round trips, and nothing would say so. The catalogue has two
/// precedents for this exact check (<c>CatalogQueryPlanTests</c>), and the interceptor was already in the
/// fixture.
/// </para>
/// <para>
/// The assertion is that the count does not <b>grow with the number of lines</b>, which is the property
/// that matters; the absolute figure is printed so a change in it is visible rather than guessed at.
/// </para>
/// </summary>
public sealed class CartQueryPlanTests(SqlServerDatabaseFixture sqlServer) : CartTestBase(sqlServer)
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(12)]
    public async Task A_bag_read_costs_the_same_number_of_commands_however_many_lines_it_holds(int lines)
    {
        var api = NewVisitor();
        var variantIds = await InStockVariantIdsAsync(lines);

        foreach (var variantId in variantIds)
        {
            await api.AddAsync(variantId, 1, Token);
        }

        CommandCounter.Reset();
        var (response, cart) = await api.GetAsync(Token);
        var commands = CommandCounter.Count;

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(cart);
        Assert.Equal(lines, cart.Lines.Count);

        TestContext.Current.TestOutputHelper!.WriteLine(
            $"A bag of {cart.Lines.Count} lines was read with {commands} SQL commands " +
            "(the cart lookup, the lines projection, and the activity touch).");

        // Three: find the cart, project every line in one query, move the activity clock. A per-line
        // query would make this grow with `lines`, which is the only thing this test is really about —
        // so the bound is deliberately well clear of three and still far below 1-per-line at twelve.
        Assert.True(
            commands <= 5,
            $"A bag of {cart.Lines.Count} lines issued {commands} SQL commands; a per-line query is the "
            + "regression this guards against.");
    }

    /// <summary>
    /// And the projection really is one query over the lines, not a join fan-out that EF then splits.
    /// Read through the reader directly, so the cart lookup and the touch are not in the count.
    /// </summary>
    [Fact]
    public async Task Projecting_the_lines_is_one_query()
    {
        var api = NewVisitor();
        var variantIds = await InStockVariantIdsAsync(8);

        foreach (var variantId in variantIds)
        {
            await api.AddAsync(variantId, 1, Token);
        }

        int cartId;
        using (var scope = CreateScope())
        {
            cartId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Carts
                .AsNoTracking()
                .OrderByDescending(cart => cart.Id)
                .Select(cart => cart.Id)
                .FirstAsync(Token);
        }

        using var readerScope = CreateApplicationScope();
        var reader = readerScope.ServiceProvider
            .GetRequiredService<Application.Sales.Cart.Abstractions.ICartReader>();

        CommandCounter.Reset();
        var cart = await reader.ReadAsync(cartId, Token);
        var commands = CommandCounter.Count;

        TestContext.Current.TestOutputHelper!.WriteLine(
            $"{cart.Lines.Count} lines projected with {commands} SQL command(s).");

        Assert.Equal(8, cart.Lines.Count);
        Assert.Equal(1, commands);
    }
}
