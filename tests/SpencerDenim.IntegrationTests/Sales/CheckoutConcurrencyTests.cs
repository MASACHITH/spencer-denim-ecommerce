using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Checkout under genuine concurrency (doc 08 §5.2, §5.4, §5.9, §3.5).
/// <para>
/// <b>Three races, and each one is a way for a shop to lose money or a customer.</b>
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Two shoppers, one unit.</b> Both see "1 available"; both must not get it. The only thing standing
/// between them is the shape of one statement — <c>UPDATE … WHERE OnHand - Reserved &gt;= @q</c>, which
/// re-evaluates its predicate after waiting for the other's row lock. A read-then-write would oversell,
/// and <c>CK_StockLevels_ReservedLeOnHand</c> would then make the *second* order fail at commit, after
/// its money had been promised.
/// </description></item>
/// <item><description>
/// <b>One cart, two submissions.</b> A double-clicked button or a retried request must produce one order,
/// not two with two reservations. <c>UX_Orders_CheckoutIdempotencyKey</c> is the mechanism; the use
/// case's pre-check only catches the sequential case.
/// </description></item>
/// <item><description>
/// <b>Two orders, one number series.</b> <c>UPDATE … OUTPUT deleted.NextValue</c> hands each caller a
/// distinct value. A <c>SELECT</c> followed by an <c>UPDATE</c> would hand two checkouts the same number,
/// and <c>UX_Orders_OrderNumber</c> would reject one of them <i>after</i> its stock was reserved.
/// </description></item>
/// </list>
/// <para>
/// <b>Every case is replayed many times.</b> A race that resolves correctly nine times in ten is not
/// fixed, and a single green run proves almost nothing: the windows aimed at here are a few milliseconds
/// wide. The repeat counts are deliberately high enough to hit them and low enough to keep the suite in
/// minutes rather than hours.
/// </para>
/// </summary>
public sealed class CheckoutConcurrencyTests(SqlServerDatabaseFixture sqlServer)
    : CheckoutTestBase(sqlServer)
{
    /// <summary>How many times the last-unit race is replayed. Each round is a fresh pair of shoppers.</summary>
    private const int LastUnitRounds = 20;

    /// <summary>How many times the double-submit race is replayed.</summary>
    private const int DoubleSubmitRounds = 12;

    /// <summary>How many shoppers fight over the last unit in each round.</summary>
    private const int Contenders = 4;

    /// <summary>
    /// How many submissions of <b>one</b> checkout key race each other, and why it is not four.
    /// <para>
    /// <b>Four could not see the defect this number exists to catch.</b> The window between the handler's
    /// replay pre-check and its repricing read is a few milliseconds wide, and a loser only falls into it
    /// if the winner commits while it is in there. At four contenders that happened in approximately 0% of
    /// rounds on a developer machine — the suite passed 6/6 on three consecutive runs over a handler that
    /// returned 409 "your bag is empty" to shoppers whose orders were placed, and the only machine that
    /// ever caught it was CI, whose slower SQL Server and narrower thread pool widen the window. At
    /// twenty-four it appears in roughly a quarter of rounds on a developer machine, which is what makes
    /// the twelve rounds below a test rather than a hope.
    /// </para>
    /// <para>
    /// It is deliberately not shared with <see cref="Contenders"/>: the last-unit race needs a fresh bag
    /// and a fresh stock row per contender per round, so twenty-four of those would cost minutes to prove
    /// something four already prove.
    /// </para>
    /// </summary>
    private const int DoubleSubmitContenders = 24;

    /// <summary>
    /// <b>The last unit, over HTTP.</b> Four shoppers, each with their own bag holding the one remaining
    /// unit, all pressing Place order at the same instant. Exactly one order exists afterwards, and
    /// <c>Reserved</c> is exactly 1.
    /// </summary>
    [Fact]
    public async Task Four_simultaneous_checkouts_of_the_last_unit_place_exactly_one_order()
    {
        var variantId = await CheckoutVariantIdAsync();
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        for (var round = 1; round <= LastUnitRounds; round++)
        {
            // One unit, nothing reserved. Arranged rather than hoped for.
            await SetStockAsync(variantId, 1);

            // Four bags, each holding the same single unit. Doc 08 §3.5 reserves nothing in a cart, so all
            // four are legitimately in this position — which is precisely the situation checkout has to
            // resolve.
            var shoppers = new List<(CheckoutApiClient Api, object Body)>();

            for (var i = 0; i < Contenders; i++)
            {
                var (_, _, api) = await NewBagAsync(variantId);
                var (_, session) = await api.GetAsync(colombo, standard, Token);

                shoppers.Add((
                    api,
                    ValidBody(session!.CheckoutKey, standard, colombo, session.Totals.GrandTotal!.Value)));
            }

            var placedBefore = await OrderNumbersSinceAsync();

            var results = await Task.WhenAll(
                shoppers.Select(shopper => shopper.Api.PlaceAsync(shopper.Body, Token)));

            var created = results.Count(result => result.Response.StatusCode == HttpStatusCode.Created);
            var conflicts = results.Count(result => result.Response.StatusCode == HttpStatusCode.Conflict);

            Assert.Equal(1, created);
            Assert.Equal(Contenders - 1, conflicts);

            // **Every loser must be told it is a stock problem, and there are two honest ways to say so.**
            //
            // This assertion was first written to demand `shortLines` from every loser, and it failed on
            // round 2 — correctly, because the premise was wrong. Which refusal a loser gets depends on
            // where the winner's commit lands relative to the loser's own work, and both answers are
            // right:
            //
            //  * the loser's use case re-reads the bag *before* reserving anything (the step that
            //    implements G-08), so if the winner has already committed, that read sees the line as
            //    OutOfStock and the whole checkout is refused as `LineNotPurchasable` — "go and adjust
            //    your bag";
            //  * if the winner commits in the window between that read and the reserving UPDATE, the
            //    reserve matches no row and doc 08 §5.4 step 3's `OUT_OF_STOCK` fires instead, with the
            //    per-line shortfall.
            //
            // Demanding only the second would have made this test fail intermittently for a correct
            // implementation — the worst kind of red. What actually matters is the pair of invariants
            // below: one order, and one reservation.
            foreach (var loser in results.Where(result => result.Response.StatusCode == HttpStatusCode.Conflict))
            {
                using var problem = await CheckoutApiClient.ProblemAsync(loser.Response, Token);
                var root = problem.RootElement;

                var namedShortLines =
                    root.TryGetProperty("shortLines", out var shortLines)
                    && shortLines.EnumerateObject().Any();

                var namedTheLine =
                    root.TryGetProperty("blockers", out var blockers)
                    && blockers.EnumerateArray().Any(value =>
                        value.GetString() == nameof(CheckoutBlocker.LineNotPurchasable));

                Assert.True(
                    namedShortLines || namedTheLine,
                    $"Round {round}: a loser was refused without being told the stock had gone. "
                    + $"Body: {root.GetRawText()}");
            }

            // **The invariant.** One unit was sold once.
            var stock = await StockAsync(variantId);
            Assert.Equal(1, stock.OnHand);
            Assert.Equal(1, stock.Reserved);
            Assert.Equal(0, stock.Available);

            var placedAfter = await OrderNumbersSinceAsync();
            Assert.Equal(placedBefore.Count + 1, placedAfter.Count);
        }
    }

    /// <summary>
    /// <b>The double submit, over HTTP.</b> The same cart and the same checkout key, submitted
    /// twenty-four times at once. One order, one reservation — and the twenty-three that lost must be
    /// told their order is already placed rather than that something went wrong.
    /// </summary>
    /// <remarks>
    /// <b>This is the regression test for the CI failure, and the contention is the test.</b> A loser that
    /// passes the handler's replay pre-check and then meets the winner's committed work — an emptied bag,
    /// or a reserving UPDATE that matches no row — used to be refused with 409 and a reason drawn from the
    /// database rather than from the question it asked. See <see cref="DoubleSubmitContenders"/> for why
    /// four contenders demonstrably could not see it.
    /// </remarks>
    [Fact]
    public async Task Many_simultaneous_submissions_of_one_cart_place_exactly_one_order()
    {
        var variantId = await CheckoutVariantIdAsync();
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        for (var round = 1; round <= DoubleSubmitRounds; round++)
        {
            // **The stock is exactly what the bag holds on the odd rounds, and plentiful on the even
            // ones**, because the two refusals a loser can hit are reached through different code. With
            // spare stock a loser reserves successfully and is caught by
            // `UX_Orders_CheckoutIdempotencyKey` on the INSERT; with none spare its reserving UPDATE
            // matches no row and `OrderPlacementStore` returns OUT_OF_STOCK before the index is ever
            // consulted. Both have to answer with the order.
            await SetStockAsync(variantId, round % 2 == 1 ? 2 : 10);

            var (token, cartId, api) = await NewBagAsync(variantId, quantity: 2);
            var (_, session) = await api.GetAsync(colombo, standard, Token);
            var body = ValidBody(session!.CheckoutKey, standard, colombo, session.Totals.GrandTotal!.Value);

            // One cookie jar would serialise nothing, but each request is its own client so that the
            // submissions are genuinely simultaneous — which is what a double-clicked button on a flaky
            // connection produces.
            var clients = Enumerable.Range(0, DoubleSubmitContenders)
                .Select(_ => NewCheckoutVisitorWithoutCookieJar())
                .ToList();

            var results = await Task.WhenAll(
                clients.Select(client => client.PlaceWithCookieAsync(token.ToString("D"), body, Token)));

            var created = results.Count(result => result.Response.StatusCode == HttpStatusCode.Created);
            var replayed = results.Count(result => result.Response.StatusCode == HttpStatusCode.OK);

            // **Asserted before the counts, because the counts cannot say what went wrong.** A refused
            // submission of a key that placed an order is the defect, and the message has to name the
            // reason it was given — "EmptyBag" is the one CI saw.
            foreach (var refused in results.Where(result => !result.Response.IsSuccessStatusCode))
            {
                using var problem = await CheckoutApiClient.ProblemAsync(refused.Response, Token);

                Assert.Fail(
                    $"Round {round}: one of {DoubleSubmitContenders} submissions of a single checkout key "
                    + $"was refused with {(int)refused.Response.StatusCode} instead of being replayed the "
                    + $"order its own key placed. Body: {problem.RootElement.GetRawText()}");
            }

            Assert.Equal(1, created);
            Assert.Equal(DoubleSubmitContenders - 1, replayed);

            // Every answer names the same order, and only the replays say so.
            var numbers = results
                .Select(result => result.Order!.OrderNumber)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Assert.Single(numbers);
            Assert.All(
                results.Where(result => result.Response.StatusCode == HttpStatusCode.OK),
                result => Assert.True(result.Order!.IsDuplicateSubmission));
            Assert.All(
                results.Where(result => result.Response.StatusCode == HttpStatusCode.Created),
                result => Assert.False(result.Order!.IsDuplicateSubmission));

            // **One order row, one reservation.** Two reservations would be the shop holding four units of
            // stock for an order of two.
            using (var scope = CreateScope())
            {
                Assert.Equal(
                    1,
                    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
                        .CountAsync(order => order.CheckoutIdempotencyKey == session.CheckoutKey, Token));
            }

            Assert.Equal(2, (await StockAsync(variantId)).Reserved);
            Assert.Equal(0, await CartItemCountAsync(cartId));
        }
    }

    /// <summary>
    /// <b>The number series, under its own contention.</b> Thirty-two allocations at once from one series:
    /// every value distinct, every value used, no gaps within the batch, and the row advanced exactly
    /// thirty-two times.
    /// </summary>
    /// <remarks>
    /// Driven through the allocator rather than through HTTP, deliberately. The thing under test is one
    /// SQL statement, and putting thirty-two whole checkouts in front of it would measure the stock rows'
    /// contention instead — the two races are separable and are separated. The series key is this test's
    /// own, so it cannot be disturbed by, or disturb, an order placed by another test in the same database.
    /// </remarks>
    [Fact]
    public async Task Thirty_two_simultaneous_allocations_hand_out_thirty_two_distinct_numbers()
    {
        SqlServer.SkipIfUnavailable();

        const int allocations = 32;
        const int rounds = 10;

        for (var round = 1; round <= rounds; round++)
        {
            var seriesKey = $"TEST-{Guid.NewGuid():N}"[..30];

            // A scope per caller: the allocator holds a DbContext, and sharing one across thirty-two
            // concurrent calls would serialise them inside EF rather than in the database — the test would
            // pass while proving nothing about the statement.
            var scopes = Enumerable.Range(0, allocations).Select(_ => CreateScope()).ToList();

            try
            {
                var values = await Task.WhenAll(scopes.Select(scope =>
                    scope.ServiceProvider.GetRequiredService<INumberSeriesAllocator>()
                        .AllocateAsync(seriesKey, DocumentNumber.OrderNumberPrefix, Token)));

                // **No two callers got the same number.** This is the assertion a SELECT-then-UPDATE fails.
                Assert.Equal(allocations, values.Distinct().Count());

                // And the values are exactly 1..32, so nothing was skipped inside the batch either: the
                // series advanced once per caller.
                Assert.Equal(Enumerable.Range(1, allocations), values.OrderBy(value => value));

                using var scope = CreateScope();
                var series = await scope.ServiceProvider.GetRequiredService<AppDbContext>().NumberSeries
                    .AsNoTracking()
                    .SingleAsync(row => row.SeriesKey == seriesKey, Token);

                Assert.Equal(allocations + 1, series.NextValue);
            }
            finally
            {
                foreach (var scope in scopes)
                {
                    scope.Dispose();
                }

                // Doc 14: leave no database behind, and that extends to rows. The series this round created
                // is this round's alone, so it is removed.
                using var cleanup = CreateScope();
                await cleanup.ServiceProvider.GetRequiredService<AppDbContext>().NumberSeries
                    .Where(row => row.SeriesKey == seriesKey)
                    .ExecuteDeleteAsync(Token);
            }
        }
    }

    /// <summary>
    /// The lazy insert, raced. Thirty-two callers find no series row and all try to create it; one wins
    /// the insert, the rest take the primary-key violation and allocate from the winner's row. Doc 08 §3.5
    /// names this path ("the row is inserted lazily, retry on a PK violation") and it is the one that fires
    /// on the first order of a new Colombo year — once a year, which is exactly when nobody is watching.
    /// </summary>
    [Fact]
    public async Task The_lazy_series_insert_survives_thirty_two_callers_arriving_at_once()
    {
        SqlServer.SkipIfUnavailable();

        const int allocations = 32;
        const int rounds = 10;

        for (var round = 1; round <= rounds; round++)
        {
            var seriesKey = $"TEST-{Guid.NewGuid():N}"[..30];
            var scopes = Enumerable.Range(0, allocations).Select(_ => CreateScope()).ToList();

            try
            {
                using (var before = CreateScope())
                {
                    Assert.Equal(
                        0,
                        await before.ServiceProvider.GetRequiredService<AppDbContext>().NumberSeries
                            .CountAsync(row => row.SeriesKey == seriesKey, Token));
                }

                // Released together, so the create race is real rather than a sequence of warm calls.
                using var gate = new SemaphoreSlim(0, allocations);

                var waiting = scopes.Select(async scope =>
                {
                    await gate.WaitAsync(Token);
                    return await scope.ServiceProvider.GetRequiredService<INumberSeriesAllocator>()
                        .AllocateAsync(seriesKey, DocumentNumber.OrderNumberPrefix, Token);
                }).ToList();

                gate.Release(allocations);

                var values = await Task.WhenAll(waiting);

                Assert.Equal(allocations, values.Distinct().Count());
                Assert.Equal(Enumerable.Range(1, allocations), values.OrderBy(value => value));

                // Exactly one row: a second insert would have meant two series with one key, which the
                // primary key forbids — and the assertion is that the losers *recovered* rather than threw.
                using var after = CreateScope();
                Assert.Equal(
                    1,
                    await after.ServiceProvider.GetRequiredService<AppDbContext>().NumberSeries
                        .CountAsync(row => row.SeriesKey == seriesKey, Token));
            }
            finally
            {
                foreach (var scope in scopes)
                {
                    scope.Dispose();
                }

                using var cleanup = CreateScope();
                await cleanup.ServiceProvider.GetRequiredService<AppDbContext>().NumberSeries
                    .Where(row => row.SeriesKey == seriesKey)
                    .ExecuteDeleteAsync(Token);
            }
        }
    }

    /// <summary>
    /// <b>Two different carts, one unit each, both arriving at once over the whole checkout path.</b> The
    /// previous tests separate the races; this one runs them together, which is the shape a real shop
    /// produces — and the assertion is that the order numbers are distinct as well as the stock being
    /// right. A shared number would have been a 500 for one shopper after their stock was taken.
    /// </summary>
    [Fact]
    public async Task Simultaneous_checkouts_of_different_variants_all_succeed_with_distinct_numbers()
    {
        const int shoppers = 6;
        const int rounds = 8;

        var variantIds = await CheckoutVariantIdsAsync(shoppers);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        for (var round = 1; round <= rounds; round++)
        {
            foreach (var variantId in variantIds)
            {
                await SetStockAsync(variantId, 1);
            }

            var prepared = new List<(CheckoutApiClient Api, object Body)>();

            foreach (var variantId in variantIds)
            {
                var (_, _, api) = await NewBagAsync(variantId);
                var (_, session) = await api.GetAsync(colombo, standard, Token);

                prepared.Add((
                    api,
                    ValidBody(session!.CheckoutKey, standard, colombo, session.Totals.GrandTotal!.Value)));
            }

            var results = await Task.WhenAll(prepared.Select(entry => entry.Api.PlaceAsync(entry.Body, Token)));

            // Nothing is contended, so everything must succeed. A failure here is a deadlock the retry
            // strategy did not absorb, or a number collision.
            Assert.All(results, result => Assert.Equal(HttpStatusCode.Created, result.Response.StatusCode));

            var numbers = results.Select(result => result.Order!.OrderNumber).ToList();
            Assert.Equal(shoppers, numbers.Distinct(StringComparer.Ordinal).Count());

            foreach (var variantId in variantIds)
            {
                var stock = await StockAsync(variantId);
                Assert.Equal(1, stock.Reserved);
                Assert.Equal(0, stock.Available);
            }
        }
    }

    /// <summary>
    /// Doc 08 §5.9's reconciliation aggregate, asserted after the rounds above: per variant,
    /// <c>StockLevels.Reserved</c> equals <c>Σ OrderLines.Quantity</c> over orders in
    /// <c>StockState = 0</c>.
    /// </summary>
    /// <remarks>
    /// This is the check that would catch a reservation that was taken and then orphaned — the drift the
    /// nightly <c>ReconcileStock</c> job exists to alert on. It is worth asserting here because
    /// <c>inventory.InventoryMovements</c> does not exist yet (ADM-12/W1-05, see
    /// <c>OrderPlacementStore</c>), so this aggregate is currently the <b>only</b> way the reservation
    /// ledger can be checked at all.
    /// </remarks>
    [Fact]
    public async Task Reserved_stock_equals_the_lines_of_the_orders_that_hold_it()
    {
        var variantIds = await CheckoutVariantIdsAsync(3);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        foreach (var variantId in variantIds)
        {
            await SetStockAsync(variantId, 5);
        }

        // **The high-water mark of the orders that already exist.** `SetStockAsync` has just zeroed
        // `Reserved`, and so detached whatever earlier orders were holding on these variants — its own
        // remarks say as much. Summing every order in `StockState = 0` would therefore compare this
        // test's reservations against this test's *plus* several earlier tests', which is how the first
        // version of this assertion went red in a full run and green on its own.
        int placedBefore;
        using (var highWater = CreateScope())
        {
            // `MaxAsync(selector)` over a nullable projection, not `DefaultIfEmpty(0).MaxAsync()`: EF
            // cannot translate the latter, and an empty table has to answer 0 rather than throw.
            placedBefore = await highWater.ServiceProvider.GetRequiredService<AppDbContext>().Orders
                .AsNoTracking()
                .MaxAsync(order => (int?)order.Id, Token) ?? 0;
        }

        // A handful of orders across the three variants, placed concurrently.
        var prepared = new List<(CheckoutApiClient Api, object Body)>();

        foreach (var variantId in variantIds)
        {
            for (var i = 0; i < 2; i++)
            {
                var (_, _, api) = await NewBagAsync(variantId, quantity: 2);
                var (_, session) = await api.GetAsync(colombo, standard, Token);

                prepared.Add((
                    api,
                    ValidBody(session!.CheckoutKey, standard, colombo, session.Totals.GrandTotal!.Value)));
            }
        }

        await Task.WhenAll(prepared.Select(entry => entry.Api.PlaceAsync(entry.Body, Token)));

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var variantId in variantIds)
        {
            var reserved = await db.StockLevels
                .AsNoTracking()
                .Where(level => level.VariantId == variantId)
                .Select(level => level.Reserved)
                .SingleAsync(Token);

            var held = await db.OrderLines
                .AsNoTracking()
                .Where(line =>
                    line.VariantId == variantId
                    && line.OrderId > placedBefore
                    && line.Order!.StockState == OrderStockState.Reserved)
                .SumAsync(line => (int)line.Quantity, Token);

            Assert.Equal(held, reserved);
        }
    }

    /// <summary>Every order number currently in the database, as a set.</summary>
    private async Task<HashSet<string>> OrderNumbersSinceAsync()
    {
        using var scope = CreateScope();
        var numbers = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
            .AsNoTracking()
            .Select(order => order.OrderNumber)
            .ToListAsync(Token);

        return numbers.ToHashSet(StringComparer.Ordinal);
    }
}
