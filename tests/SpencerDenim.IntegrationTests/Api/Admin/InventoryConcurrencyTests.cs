using System.Net;
using System.Text.Json;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The inventory screen's writes under genuine concurrency (doc 08 §5.2, §5.3, §5.8; RCSI is on, #183).
/// <para>
/// <b>What each race would look like if the store read and then wrote.</b> Two staff members adjusting one
/// variant would each read the same figure and the second write would erase the first (a lost update); an
/// adjustment racing a checkout would read "10 available", the order would reserve 6, and the adjustment
/// would then take 6 away and leave on hand below what the order holds. The conditional <c>UPDATE</c>
/// evaluates its predicate under the row lock, so neither can happen; these tests replay each race enough
/// times to hit the window rather than trusting one green run.
/// </para>
/// </summary>
public sealed class InventoryConcurrencyTests(SqlServerDatabaseFixture sqlServer) : InventoryTestBase(sqlServer)
{
    private const int Pairs = 20;
    private const int PlacementRounds = 8;

    /// <summary>
    /// Forty single-row adjustments from two staff members at once — twenty +3s and twenty −1s — land as
    /// forty movements and a final figure of exactly start + 40. A read-then-write would lose some.
    /// </summary>
    [Fact]
    public async Task Parallel_adjustments_are_never_lost()
    {
        var variantId = await CheckoutVariantIdAsync();
        await ArrangeAsync(variantId, onHand: 100);
        using var first = await StaffAsync(Permissions.Inventory.Edit);
        using var second = await StaffAsync(Permissions.Inventory.Edit);
        var mark = await MovementMarkAsync();

        var requests = Enumerable.Range(0, Pairs)
            .SelectMany(_ => new[]
            {
                PostAsync(first, AdjustUrl, new { reason = "Found", rows = new[] { Row(variantId, "Adjust", 3) } }),
                PostAsync(second, AdjustUrl, new { reason = "Damaged", rows = new[] { Row(variantId, "Adjust", -1) } }),
            })
            .ToList();

        var results = await Task.WhenAll(requests);

        Assert.All(results, result =>
        {
            Assert.Equal(HttpStatusCode.OK, result.Status);
            Assert.Equal("Applied", result.Body.GetProperty("rows")[0].GetProperty("outcome").GetString());
        });

        Assert.Equal(100 + (Pairs * 2), (await StockAsync(variantId)).OnHand);

        // The ledger is an unbroken chain: each movement is inserted while its transaction still holds the
        // row's lock, so id order is the order the changes happened in, and each "after" is the previous
        // "after" plus its own delta. A lost update would show as a link that does not follow.
        var movements = await MovementsAfterAsync(mark, variantId);
        Assert.Equal(Pairs * 2, movements.Count);

        var expected = 100;
        foreach (var movement in movements)
        {
            expected += movement.OnHandDelta;
            Assert.Equal(expected, movement.OnHandAfter);
        }

        Assert.Equal(100 + (Pairs * 2), expected);
    }

    /// <summary>
    /// Two stock-takes typed against the same figure: exactly one applies and the other is told the count
    /// moved. An absolute write must never silently replace another.
    /// </summary>
    [Fact]
    public async Task Of_two_set_counts_against_one_figure_exactly_one_applies()
    {
        var variantId = await CheckoutVariantIdAsync();
        using var first = await StaffAsync(Permissions.Inventory.Edit);
        using var second = await StaffAsync(Permissions.Inventory.Edit);

        for (var round = 0; round < 10; round++)
        {
            await ArrangeAsync(variantId, onHand: 50);
            var mark = await MovementMarkAsync();

            var results = await Task.WhenAll(
                PostAsync(first, AdjustUrl, new { reason = "Count A", rows = new[] { Row(variantId, "SetCount", 40, expectedOnHand: 50) } }),
                PostAsync(second, AdjustUrl, new { reason = "Count B", rows = new[] { Row(variantId, "SetCount", 45, expectedOnHand: 50) } }));

            var outcomes = results.Select(result => result.Body.GetProperty("rows")[0].GetProperty("outcome").GetString()).Order().ToList();
            Assert.Equal(["Applied", "Changed"], outcomes);

            var movement = Assert.Single(await MovementsAfterAsync(mark, variantId));
            Assert.Equal(movement.OnHandAfter, (await StockAsync(variantId)).OnHand);
            Assert.True(movement.OnHandAfter is 40 or 45, $"The count left {movement.OnHandAfter}.");
        }
    }

    /// <summary>
    /// An adjustment racing a checkout for the same units: with 6 available, a shopper placing an order for
    /// 6 and a staff member removing 6 at the same instant — exactly one of them succeeds, every round, and
    /// on hand never falls below reserved.
    /// </summary>
    [Fact]
    public async Task An_adjustment_racing_an_order_placement_never_leaves_on_hand_below_reserved()
    {
        var variantId = await CheckoutVariantIdAsync();
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);
        using var manager = await StaffAsync(Permissions.Inventory.Edit);
        var orderWins = 0;
        var adjustmentWins = 0;

        for (var round = 0; round < PlacementRounds; round++)
        {
            await ArrangeAsync(variantId, onHand: 6);

            var (_, _, shopper) = await NewBagAsync(variantId, quantity: 6);
            var (_, session) = await shopper.GetAsync(colombo, standard, Token);
            var body = ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session));

            var placing = shopper.PlaceAsync(body, Token);
            var adjusting = PostAsync(manager, AdjustUrl, new { reason = "Damaged", rows = new[] { Row(variantId, "Adjust", -6) } });
            await Task.WhenAll(placing, adjusting);

            var placed = (await placing).Response.StatusCode == HttpStatusCode.Created;
            var adjusted = (await adjusting).Body.GetProperty("rows")[0].GetProperty("outcome").GetString() == "Applied";

            Assert.True(placed ^ adjusted, $"Round {round}: placed {placed}, adjusted {adjusted} — exactly one may win.");

            var (onHand, reserved, available) = await StockAsync(variantId);
            Assert.True(onHand >= reserved, $"Round {round}: on hand {onHand} below reserved {reserved}.");
            Assert.Equal(placed ? (6, 6, 0) : (0, 0, 0), (onHand, reserved, available));

            if (placed)
            {
                orderWins++;
                Assert.Contains("reserved for unpaid orders", (await adjusting).Body.GetProperty("rows")[0].GetProperty("message").GetString(), StringComparison.Ordinal);
            }
            else
            {
                adjustmentWins++;
            }
        }

        // Not asserted to be balanced — which side wins is the scheduler's business — but recorded, so a run
        // where one side always won is visible in the output rather than mistaken for a race exercised.
        TestContext.Current.SendDiagnosticMessage($"order won {orderWins}, adjustment won {adjustmentWins}");
    }

    /// <summary>
    /// A bulk update and a single adjustment racing over overlapping variants, in opposite request orders.
    /// The store applies rows in VariantId order (doc 08 §5.3), so the two cannot deadlock each other into a
    /// failure, and both apply in full.
    /// </summary>
    [Fact]
    public async Task Overlapping_bulk_updates_in_opposite_orders_both_apply()
    {
        var ids = await CheckoutVariantIdsAsync(4);
        foreach (var id in ids)
        {
            await ArrangeAsync(id, onHand: 10);
        }

        using var first = await StaffAsync(Permissions.Inventory.Edit);
        using var second = await StaffAsync(Permissions.Inventory.Edit);

        for (var round = 0; round < 10; round++)
        {
            var forward = ids.Select(id => Row(id, "Adjust", 1)).ToArray();
            var backward = ids.Reverse().Select(id => Row(id, "Adjust", 1)).ToArray();

            var results = await Task.WhenAll(
                PostAsync(first, AdjustUrl, new { reason = "Forward", rows = forward }),
                PostAsync(second, AdjustUrl, new { reason = "Backward", rows = backward }));

            Assert.All(results, result =>
            {
                Assert.Equal(HttpStatusCode.OK, result.Status);
                Assert.Equal(ids.Count, result.Body.GetProperty("appliedCount").GetInt32());
            });
        }

        foreach (var id in ids)
        {
            Assert.Equal(10 + 20, (await StockAsync(id)).OnHand);
        }
    }
}
