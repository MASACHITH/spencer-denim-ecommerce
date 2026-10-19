using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Inventory;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.IntegrationTests.Sales;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The independent tester's attacks on ADM-12 (#190) and the order-path movements (#108).
/// <para>
/// <b>Deterministic interleavings rather than hoped-for races.</b> A second connection takes the stock
/// row's lock with the exact statement the competing path would run, the API request is started, the test
/// waits until SQL Server reports that request blocked by that connection, and only then commits. The
/// request's conditional <c>UPDATE</c> therefore has to re-evaluate its predicate against the committed
/// figure — which is what has to hold under RCSI (#183), where a plain read would see the old version.
/// </para>
/// <para>
/// <b>The floor tests switch <c>CK_StockLevels_ReservedLeOnHand</c> off</b> for their duration, so the
/// only thing between the request and on hand below reserved is the <c>UPDATE</c>'s own predicate (the
/// developer's caveat: with the constraint on, a broken predicate and a working one both stop the change,
/// one as a 500). The constraint is re-enabled <c>WITH CHECK</c> in a <c>finally</c>, and a test asserts
/// it is enabled and trusted.
/// </para>
/// </summary>
public sealed class InventoryTesterAttackTests(SqlServerDatabaseFixture sqlServer) : BankTransferTestBase(sqlServer)
{
    private const string AdjustUrl = "/api/admin/inventory/adjustments";
    private const string FloorConstraint = "CK_StockLevels_ReservedLeOnHand";
    private const string FailingTrigger = "TR_Tester_FailMovement";

    private readonly List<HttpClient> _clients = [];

    // ---- the predicate alone holds the floor, under RCSI, after waiting -----------------------------------

    [Fact]
    public async Task A_removal_blocked_behind_a_reservation_is_refused_by_its_own_predicate_with_the_constraint_off()
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 6);
        using var manager = await ManagerAsync();
        var mark = await MovementMarkAsync();

        await WithFloorConstraintOffAsync(variantId, async () =>
        {
            var (status, body) = await RaceBehindAsync(
                "UPDATE inventory.StockLevels SET Reserved = Reserved + 6 WHERE VariantId = @v;",
                variantId,
                () => PostAsync(manager, new { reason = "Damaged", rows = new[] { Row(variantId, "Adjust", -6) } }));

            Assert.Equal(HttpStatusCode.OK, status);
            var row = body.GetProperty("rows")[0];
            Assert.Equal("Refused", row.GetProperty("outcome").GetString());
            Assert.Contains("6 units are reserved", row.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Equal((6, 6, 0), await StockAsync(variantId));
            Assert.Empty(await MovementsAfterAsync(mark, variantId));
        });
    }

    [Fact]
    public async Task A_set_count_blocked_behind_a_reservation_is_refused_not_applied_with_the_constraint_off()
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 6);
        using var manager = await ManagerAsync();
        var mark = await MovementMarkAsync();

        await WithFloorConstraintOffAsync(variantId, async () =>
        {
            var (status, body) = await RaceBehindAsync(
                "UPDATE inventory.StockLevels SET Reserved = Reserved + 4 WHERE VariantId = @v;",
                variantId,
                () => PostAsync(manager, new { reason = "Stock-take", rows = new[] { Row(variantId, "SetCount", 2, expectedOnHand: 6) } }));

            Assert.Equal(HttpStatusCode.OK, status);
            var row = body.GetProperty("rows")[0];

            // On hand still equals the screen's figure, so this is the floor, not Changed.
            Assert.Equal("Refused", row.GetProperty("outcome").GetString());
            Assert.Contains("4 units are reserved", row.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Equal((6, 4, 2), await StockAsync(variantId));
            Assert.Empty(await MovementsAfterAsync(mark, variantId));
        });
    }

    /// <summary>
    /// The other direction: the request must see a release that committed while it waited, not the
    /// reservation it read before. A stale read would refuse a change that is now allowed.
    /// </summary>
    [Fact]
    public async Task A_removal_blocked_behind_a_release_sees_the_release_and_applies()
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 6);
        await ExecuteAsync("UPDATE inventory.StockLevels SET Reserved = 6 WHERE VariantId = @v;", variantId);
        using var manager = await ManagerAsync();
        var mark = await MovementMarkAsync();

        try
        {
            var (status, body) = await RaceBehindAsync(
                "UPDATE inventory.StockLevels SET Reserved = Reserved - 6 WHERE VariantId = @v;",
                variantId,
                () => PostAsync(manager, new { reason = "Damaged", rows = new[] { Row(variantId, "Adjust", -6) } }));

            Assert.Equal(HttpStatusCode.OK, status);
            var row = body.GetProperty("rows")[0];
            Assert.Equal("Applied", row.GetProperty("outcome").GetString());
            Assert.Equal((0, 0, 0), (row.GetProperty("onHand").GetInt32(), row.GetProperty("reserved").GetInt32(), row.GetProperty("available").GetInt32()));
            Assert.Equal((0, 0, 0), await StockAsync(variantId));

            var movement = Assert.Single(await MovementsAfterAsync(mark, variantId));
            Assert.Equal((InventoryMovementType.AdjustOut, -6, 0, 0, 0), (movement.MovementType, movement.OnHandDelta, movement.ReservedDelta, movement.OnHandAfter, movement.ReservedAfter));
        }
        finally
        {
            await ExecuteAsync("UPDATE inventory.StockLevels SET Reserved = 0 WHERE VariantId = @v;", variantId);
        }
    }

    [Fact]
    public async Task A_set_count_blocked_behind_a_receipt_is_Changed_and_quotes_the_committed_figure()
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 6);
        using var manager = await ManagerAsync();
        var mark = await MovementMarkAsync();

        var (status, body) = await RaceBehindAsync(
            "UPDATE inventory.StockLevels SET OnHand = OnHand + 5 WHERE VariantId = @v;",
            variantId,
            () => PostAsync(manager, new { reason = "Stock-take", rows = new[] { Row(variantId, "SetCount", 3, expectedOnHand: 6) } }));

        Assert.Equal(HttpStatusCode.OK, status);
        var row = body.GetProperty("rows")[0];
        Assert.Equal("Changed", row.GetProperty("outcome").GetString());
        Assert.Equal(11, row.GetProperty("onHand").GetInt32());
        Assert.Contains("On hand is now 11", row.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, body.GetProperty("appliedCount").GetInt32());
        Assert.Equal((11, 0, 11), await StockAsync(variantId));
        Assert.Empty(await MovementsAfterAsync(mark, variantId));
    }

    [Fact]
    public async Task The_floor_constraint_is_enabled_and_trusted()
    {
        SqlServer.SkipIfUnavailable();

        await using var connection = new SqlConnection(SqlServer.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_disabled, is_not_trusted FROM sys.check_constraints WHERE name = @name;";
        command.Parameters.AddWithValue("@name", FloorConstraint);
        await using var reader = await command.ExecuteReaderAsync(Token);

        Assert.True(await reader.ReadAsync(Token));
        Assert.False(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
    }

    // ---- real order paths racing a manual change: the ledger stays a chain --------------------------------

    /// <summary>
    /// Approval (−q on hand, −q reserved) and a removal that is legal before and after it race; both must
    /// apply, whichever lands first, and the movements must chain on both figures.
    /// </summary>
    [Fact]
    public async Task An_approval_racing_a_removal_both_apply_and_the_ledger_chains()
    {
        using var manager = await ManagerAsync();

        for (var round = 0; round < 4; round++)
        {
            var mark = await MovementMarkAsync();
            var (order, variantId) = await PlaceOrderAsync(quantity: 6, onHand: 10);
            var orderId = await OrderIdAsync(order.PublicId);

            var approving = ApproveAsync(orderId);
            var removing = PostAsync(manager, new { reason = "Damaged", rows = new[] { Row(variantId, "Adjust", -4) } });
            await Task.WhenAll(approving, removing);

            Assert.IsType<BankTransferDecisionResult.Applied>(await approving);
            Assert.Equal("Applied", (await removing).Body.GetProperty("rows")[0].GetProperty("outcome").GetString());
            Assert.Equal((0, 0, 0), await StockAsync(variantId));

            var movements = await MovementsAfterAsync(mark, variantId);
            Assert.Equal(3, movements.Count);
            AssertChain(movements, startOnHand: 10, startReserved: 0);
        }
    }

    /// <summary>
    /// Expiry and a removal of everything race: either the expiry lands first and the removal applies, or
    /// the removal is refused by the reservation. Never a 500, never below reserved, always a chain.
    /// </summary>
    [Fact]
    public async Task An_expiry_racing_a_removal_never_breaks_the_floor_or_the_chain()
    {
        using var manager = await ManagerAsync();

        for (var round = 0; round < 4; round++)
        {
            var mark = await MovementMarkAsync();
            var (order, variantId) = await PlaceOrderAsync(quantity: 6, onHand: 6);
            var orderId = await OrderIdAsync(order.PublicId);

            await MakeDueAsync(orderId);
            var expiring = ExpireAsync(orderId, DateTime.UtcNow);
            var removing = PostAsync(manager, new { reason = "Damaged", rows = new[] { Row(variantId, "Adjust", -6) } });
            await Task.WhenAll(expiring, removing);

            Assert.True(await expiring);
            Assert.Equal(HttpStatusCode.OK, (await removing).Status);
            var outcome = (await removing).Body.GetProperty("rows")[0].GetProperty("outcome").GetString();
            Assert.True(outcome is "Applied" or "Refused", $"Round {round}: {outcome}");

            Assert.Equal(outcome == "Applied" ? (0, 0, 0) : (6, 0, 6), await StockAsync(variantId));

            var movements = await MovementsAfterAsync(mark, variantId);
            Assert.Equal(outcome == "Applied" ? 3 : 2, movements.Count);
            AssertChain(movements, startOnHand: 6, startReserved: 0);
        }
    }

    // ---- the movement and the stock change are one transaction --------------------------------------------

    /// <summary>
    /// A movement insert that fails takes the stock changes of the whole batch with it — including the rows
    /// whose UPDATE had already run — and leaves no audit row.
    /// </summary>
    [Fact]
    public async Task A_failing_movement_insert_rolls_back_every_stock_change_in_the_batch()
    {
        var ids = await CheckoutVariantIdsAsync(2);
        await SetStockAsync(ids[0], 10);
        await SetStockAsync(ids[1], 10);
        using var manager = await ManagerAsync();
        var mark = await MovementMarkAsync();
        var auditMark = await AuditMarkAsync();

        await WithFailingTriggerAsync($"VariantId = {ids.Max().ToString(CultureInfo.InvariantCulture)}", async () =>
        {
            var (status, _) = await PostAsync(manager, new
            {
                reason = "Delivery",
                rows = new[] { Row(ids[0], "Receive", 5), Row(ids[1], "Receive", 5) },
            });

            Assert.Equal(HttpStatusCode.InternalServerError, status);
        });

        Assert.Equal(10, (await StockAsync(ids[0])).OnHand);
        Assert.Equal(10, (await StockAsync(ids[1])).OnHand);
        Assert.Empty(await MovementsAfterAsync(mark));
        Assert.Equal(0, await StockAuditCountAfterAsync(auditMark));
    }

    [Fact]
    public async Task A_failing_Sale_movement_rolls_the_approval_back_whole()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 2, onHand: 10);
        var orderId = await OrderIdAsync(order.PublicId);
        var mark = await MovementMarkAsync();
        var invoiceBefore = await InvoiceSeriesNextValueAsync();

        await WithFailingTriggerAsync(
            $"MovementType = {(byte)InventoryMovementType.Sale} AND VariantId = {variantId.ToString(CultureInfo.InvariantCulture)}",
            async () => await Assert.ThrowsAnyAsync<Exception>(() => ApproveAsync(orderId)));

        Assert.Equal((10, 2, 8), await StockAsync(variantId));
        Assert.Empty(await MovementsAfterAsync(mark, variantId));
        Assert.Equal(OrderStatus.PendingPayment, (await ReadOrderAsync(order.PublicId)).Status);
        Assert.Equal(invoiceBefore, await InvoiceSeriesNextValueAsync());

        // And with the trigger gone the same approval goes through, so nothing was left half-claimed.
        Assert.IsType<BankTransferDecisionResult.Applied>(await ApproveAsync(orderId));
        Assert.Equal((8, 0, 8), await StockAsync(variantId));
    }

    [Fact]
    public async Task A_failing_Release_movement_rolls_the_expiry_back_whole()
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 3, onHand: 10);
        var orderId = await OrderIdAsync(order.PublicId);
        var mark = await MovementMarkAsync();

        await WithFailingTriggerAsync(
            $"MovementType = {(byte)InventoryMovementType.Release} AND VariantId = {variantId.ToString(CultureInfo.InvariantCulture)}",
            async () =>
            {
                await MakeDueAsync(orderId);
                await Assert.ThrowsAnyAsync<Exception>(() => ExpireAsync(orderId, DateTime.UtcNow));
            });

        Assert.Equal((10, 3, 7), await StockAsync(variantId));
        Assert.Empty(await MovementsAfterAsync(mark, variantId));
        Assert.Equal(OrderStatus.PendingPayment, (await ReadOrderAsync(order.PublicId)).Status);
    }

    [Fact]
    public async Task A_failing_Reserve_movement_places_no_order_and_reserves_nothing()
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);
        var mark = await MovementMarkAsync();
        var ordersBefore = await OrderCountAsync();

        var (_, cartId, shopper) = await NewBagAsync(variantId, quantity: 2);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);
        var (_, session) = await shopper.GetAsync(colombo, standard, Token);

        await WithFailingTriggerAsync(
            $"MovementType = {(byte)InventoryMovementType.Reserve} AND VariantId = {variantId.ToString(CultureInfo.InvariantCulture)}",
            async () =>
            {
                var (response, _) = await shopper.PlaceAsync(ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session)), Token);
                Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            });

        Assert.Equal((10, 0, 10), await StockAsync(variantId));
        Assert.Equal(ordersBefore, await OrderCountAsync());
        Assert.Empty(await MovementsAfterAsync(mark, variantId));
        Assert.Equal(1, await CartItemCountAsync(cartId));
    }

    // ---- multi-line orders: one movement per line, each with its own figures -------------------------------

    [Fact]
    public async Task A_two_line_order_writes_a_movement_per_line_with_each_line_s_own_figures()
    {
        var ids = await CheckoutVariantIdsAsync(2);
        await SetStockAsync(ids[0], 10);
        await SetStockAsync(ids[1], 20);
        var mark = await MovementMarkAsync();

        var cart = NewVisitor();
        Assert.Equal(HttpStatusCode.OK, (await cart.AddAsync(ids[0], 2, Token)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cart.AddAsync(ids[1], 3, Token)).Response.StatusCode);
        var shopper = new CheckoutApiClient(cart.Http);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);
        var (_, session) = await shopper.GetAsync(colombo, standard, Token);
        var (response, order) = await shopper.PlaceAsync(ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session)), Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var orderId = await OrderIdAsync(order!.PublicId);

        var first = Assert.Single(await MovementsAfterAsync(mark, ids[0]));
        var second = Assert.Single(await MovementsAfterAsync(mark, ids[1]));
        Assert.Equal((InventoryMovementType.Reserve, 0, 2, 10, 2, orderId), (first.MovementType, first.OnHandDelta, first.ReservedDelta, first.OnHandAfter, first.ReservedAfter, first.OrderId));
        Assert.Equal((InventoryMovementType.Reserve, 0, 3, 20, 3, orderId), (second.MovementType, second.OnHandDelta, second.ReservedDelta, second.OnHandAfter, second.ReservedAfter, second.OrderId));

        Assert.IsType<BankTransferDecisionResult.Applied>(await ApproveAsync(orderId));

        var firstSale = (await MovementsAfterAsync(mark, ids[0]))[^1];
        var secondSale = (await MovementsAfterAsync(mark, ids[1]))[^1];
        Assert.Equal((InventoryMovementType.Sale, -2, -2, 8, 0), (firstSale.MovementType, firstSale.OnHandDelta, firstSale.ReservedDelta, firstSale.OnHandAfter, firstSale.ReservedAfter));
        Assert.Equal((InventoryMovementType.Sale, -3, -3, 17, 0), (secondSale.MovementType, secondSale.OnHandDelta, secondSale.ReservedDelta, secondSale.OnHandAfter, secondSale.ReservedAfter));
        Assert.Equal(orderId, firstSale.OrderId);
        Assert.Equal(orderId, secondSale.OrderId);
    }

    [Fact]
    public async Task A_two_line_expiry_and_late_acceptance_write_a_movement_per_line()
    {
        var ids = await CheckoutVariantIdsAsync(2);
        await SetStockAsync(ids[0], 10);
        await SetStockAsync(ids[1], 20);
        var mark = await MovementMarkAsync();

        var cart = NewVisitor();
        Assert.Equal(HttpStatusCode.OK, (await cart.AddAsync(ids[0], 2, Token)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cart.AddAsync(ids[1], 3, Token)).Response.StatusCode);
        var shopper = new CheckoutApiClient(cart.Http);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);
        var (_, session) = await shopper.GetAsync(colombo, standard, Token);
        var (_, order) = await shopper.PlaceAsync(ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session)), Token);
        var orderId = await OrderIdAsync(order!.PublicId);

        await MakeDueAsync(orderId);
        Assert.True(await ExpireAsync(orderId, DateTime.UtcNow));
        Assert.IsType<BankTransferDecisionResult.Applied>(await AcceptLateAsync(orderId));

        var first = await MovementsAfterAsync(mark, ids[0]);
        var second = await MovementsAfterAsync(mark, ids[1]);
        Assert.Equal([InventoryMovementType.Reserve, InventoryMovementType.Release, InventoryMovementType.Sale], first.Select(movement => movement.MovementType));
        Assert.Equal([InventoryMovementType.Reserve, InventoryMovementType.Release, InventoryMovementType.Sale], second.Select(movement => movement.MovementType));
        Assert.Equal((0, -2, 10, 0), (first[1].OnHandDelta, first[1].ReservedDelta, first[1].OnHandAfter, first[1].ReservedAfter));
        Assert.Equal((0, -3, 20, 0), (second[1].OnHandDelta, second[1].ReservedDelta, second[1].OnHandAfter, second[1].ReservedAfter));
        Assert.Equal((-2, 0, 8, 0), (first[2].OnHandDelta, first[2].ReservedDelta, first[2].OnHandAfter, first[2].ReservedAfter));
        Assert.Equal((-3, 0, 17, 0), (second[2].OnHandDelta, second[2].ReservedDelta, second[2].OnHandAfter, second[2].ReservedAfter));
        Assert.All(first.Concat(second), movement => Assert.Equal(orderId, movement.OrderId));
        Assert.All(first.Concat(second), movement => Assert.Null(movement.BatchId));
    }

    // ---- bulk result mapping and the batch id -------------------------------------------------------------

    /// <summary>
    /// Rows sent out of VariantId order, mixing every outcome: each answer comes back at its own index with
    /// its own variant id, applied rows share one batch id with their movements, and the counts add up.
    /// </summary>
    [Fact]
    public async Task A_mixed_bulk_answers_every_row_in_the_order_sent_and_shares_one_batch_id()
    {
        var ids = (await CheckoutVariantIdsAsync(3)).Order().ToList();
        await SetStockAsync(ids[0], 10);
        await SetStockAsync(ids[1], 10);
        await SetStockAsync(ids[2], 10);
        await ExecuteAsync("UPDATE inventory.StockLevels SET Reserved = 8 WHERE VariantId = @v;", ids[1]);
        using var manager = await ManagerAsync();
        var mark = await MovementMarkAsync();
        var auditMark = await AuditMarkAsync();
        const int missing = int.MaxValue - 7;

        try
        {
            // Highest first, so request order and lock order differ.
            var (status, body) = await PostAsync(manager, new
            {
                reason = "Mixed",
                rows = new[]
                {
                    Row(ids[2], "SetCount", 4, expectedOnHand: 9),
                    Row(missing, "Receive", 1),
                    Row(ids[1], "Adjust", -5),
                    Row(ids[0], "Receive", 7),
                },
            });

            Assert.Equal(HttpStatusCode.OK, status);
            var rows = body.GetProperty("rows");
            Assert.Equal(4, rows.GetArrayLength());
            Assert.Equal((ids[2], "Changed"), (rows[0].GetProperty("variantId").GetInt32(), rows[0].GetProperty("outcome").GetString()));
            Assert.Equal((missing, "NotFound"), (rows[1].GetProperty("variantId").GetInt32(), rows[1].GetProperty("outcome").GetString()));
            Assert.Equal((ids[1], "Refused"), (rows[2].GetProperty("variantId").GetInt32(), rows[2].GetProperty("outcome").GetString()));
            Assert.Equal((ids[0], "Applied"), (rows[3].GetProperty("variantId").GetInt32(), rows[3].GetProperty("outcome").GetString()));
            Assert.Equal((7, 17, 0, 17), (rows[3].GetProperty("onHandDelta").GetInt32(), rows[3].GetProperty("onHand").GetInt32(), rows[3].GetProperty("reserved").GetInt32(), rows[3].GetProperty("available").GetInt32()));
            Assert.Equal(10, rows[0].GetProperty("onHand").GetInt32());
            Assert.Equal((10, 8, 2), (rows[2].GetProperty("onHand").GetInt32(), rows[2].GetProperty("reserved").GetInt32(), rows[2].GetProperty("available").GetInt32()));
            Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("onHand").ValueKind);
            Assert.Equal((1, 3), (body.GetProperty("appliedCount").GetInt32(), body.GetProperty("notAppliedCount").GetInt32()));

            var batchId = body.GetProperty("batchId").GetGuid();
            var movement = Assert.Single(await MovementsAfterAsync(mark));
            Assert.Equal((ids[0], batchId, "Mixed", 17), (movement.VariantId, movement.BatchId, movement.Reason, movement.OnHandAfter));
            Assert.Equal(1, await StockAuditCountAfterAsync(auditMark));
            Assert.Equal((10, 8, 2), await StockAsync(ids[1]));
            Assert.Equal(10, (await StockAsync(ids[2])).OnHand);
        }
        finally
        {
            await ExecuteAsync("UPDATE inventory.StockLevels SET Reserved = 0 WHERE VariantId = @v;", ids[1]);
        }
    }

    [Fact]
    public async Task A_single_change_has_no_batch_id_and_a_two_row_change_shares_one()
    {
        var ids = await CheckoutVariantIdsAsync(2);
        await SetStockAsync(ids[0], 10);
        await SetStockAsync(ids[1], 10);
        using var manager = await ManagerAsync();

        var mark = await MovementMarkAsync();
        var (_, single) = await PostAsync(manager, new { reason = "One", rows = new[] { Row(ids[0], "Receive", 1) } });
        Assert.Equal(JsonValueKind.Null, single.GetProperty("batchId").ValueKind);
        Assert.Null(Assert.Single(await MovementsAfterAsync(mark)).BatchId);

        mark = await MovementMarkAsync();
        var (_, pair) = await PostAsync(manager, new { reason = "Two", rows = new[] { Row(ids[0], "Receive", 1), Row(ids[1], "Receive", 1) } });
        var batchId = pair.GetProperty("batchId").GetGuid();
        Assert.NotEqual(Guid.Empty, batchId);
        Assert.All(await MovementsAfterAsync(mark), movement => Assert.Equal(batchId, movement.BatchId));
        Assert.Equal(2, (await MovementsAfterAsync(mark)).Count);
    }

    [Fact]
    public async Task Five_hundred_well_formed_rows_are_accepted_and_answered_row_by_row()
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);
        using var manager = await ManagerAsync();
        var mark = await MovementMarkAsync();

        var rows = Enumerable.Range(0, 499).Select(index => Row(int.MaxValue - index, "Receive", 1)).Append(Row(variantId, "Receive", 1)).ToArray();
        var (status, body) = await PostAsync(manager, new { reason = "Big", rows });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(500, body.GetProperty("rows").GetArrayLength());
        Assert.Equal((1, 499), (body.GetProperty("appliedCount").GetInt32(), body.GetProperty("notAppliedCount").GetInt32()));
        Assert.Equal(variantId, body.GetProperty("rows")[499].GetProperty("variantId").GetInt32());
        Assert.Single(await MovementsAfterAsync(mark));
    }

    // ---- validation edges -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("Receive", 100_000, null, true)]
    [InlineData("Receive", 100_001, null, false)]
    [InlineData("Receive", 0, null, false)]
    [InlineData("Adjust", -100_000, null, true)]
    [InlineData("Adjust", 100_000, null, true)]
    [InlineData("SetCount", 1_000_000, 200_000, true)]
    [InlineData("SetCount", 1_000_001, 200_000, false)]
    [InlineData("SetCount", 0, 200_000, true)]
    [InlineData("SetCount", 5, -1, false)]
    public async Task The_quantity_limits_are_inclusive_at_their_edges(string kind, int quantity, int? expected, bool accepted)
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 200_000);
        using var manager = await ManagerAsync();

        var (status, body) = await PostAsync(manager, new { reason = "Edge", rows = new[] { Row(variantId, kind, quantity, expected) } });

        Assert.True(status == (accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest), $"{kind} {quantity}: {(int)status} {body}");

        if (accepted)
        {
            Assert.Equal("Applied", body.GetProperty("rows")[0].GetProperty("outcome").GetString());
        }
        else
        {
            Assert.Equal(200_000, (await StockAsync(variantId)).OnHand);
        }
    }

    [Fact]
    public async Task The_ceiling_is_exactly_one_million()
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 999_999);
        using var manager = await ManagerAsync();

        var (_, over) = await PostAsync(manager, new { reason = "Over", rows = new[] { Row(variantId, "Receive", 2) } });
        Assert.Equal("Refused", over.GetProperty("rows")[0].GetProperty("outcome").GetString());
        Assert.Contains("1,000,000", over.GetProperty("rows")[0].GetProperty("message").GetString(), StringComparison.Ordinal);

        var (_, exact) = await PostAsync(manager, new { reason = "Exact", rows = new[] { Row(variantId, "Receive", 1) } });
        Assert.Equal("Applied", exact.GetProperty("rows")[0].GetProperty("outcome").GetString());
        Assert.Equal(1_000_000, (await StockAsync(variantId)).OnHand);
    }

    [Theory]
    [InlineData("""{"reason":"x","rows":[{"variantId":"{v}","kind":"Adjust","quantity":1,"expectedOnHand":null}]}""", "rows[0].variantId")]
    [InlineData("""{"reason":"x","rows":[{"variantId":{v},"kind":"Adjust","quantity":"1","expectedOnHand":null}]}""", "rows[0].quantity")]
    [InlineData("""{"reason":"x","rows":[{"variantId":{v},"kind":"Adjust","quantity":true,"expectedOnHand":null}]}""", "rows[0].quantity")]
    [InlineData("""{"reason":"x","rows":[{"variantId":{v},"kind":"Adjust","quantity":1.0,"expectedOnHand":null}]}""", "rows[0].quantity")]
    [InlineData("""{"reason":"x","rows":[{"variantId":{v},"kind":"Adjust","quantity":[1],"expectedOnHand":null}]}""", "rows[0].quantity")]
    [InlineData("""{"reason":"x","rows":[{"variantId":{v},"kind":2,"quantity":1,"expectedOnHand":null}]}""", "rows[0].kind")]
    [InlineData("""{"reason":"x","rows":[{"variantId":{v},"kind":"Receive, Adjust","quantity":1,"expectedOnHand":5}]}""", "rows[0].kind", Skip = "Tester #190 defect D2: ReadRow's Enum.TryParse accepts \"+2\" and comma lists (\"Receive, Adjust\" = SetCount). Unskip when fixed.")]
    [InlineData("""{"reason":"x","rows":[{"variantId":{v},"kind":"0x2","quantity":1,"expectedOnHand":null}]}""", "rows[0].kind")]
    [InlineData("""{"reason":"x","rows":[{"variantId":{v},"kind":"+2","quantity":1,"expectedOnHand":null}]}""", "rows[0].kind", Skip = "Tester #190 defect D2: ReadRow's Enum.TryParse accepts \"+2\" and comma lists (\"Receive, Adjust\" = SetCount). Unskip when fixed.")]
    [InlineData("""{"reason":"x","rows":[{"variantId":{v},"kind":"Adjust,Adjust","quantity":1,"expectedOnHand":null}]}""", "rows[0].kind", Skip = "Tester #190 defect D2: ReadRow's Enum.TryParse accepts \"+2\" and comma lists (\"Receive, Adjust\" = SetCount). Unskip when fixed.")]
    [InlineData("""{"reason":5,"rows":[{"variantId":{v},"kind":"Adjust","quantity":1,"expectedOnHand":null}]}""", "reason")]
    [InlineData("""{"reason":"x","rows":{"variantId":{v}}}""", "rows")]
    [InlineData("""{"reason":"x","rows":[5]}""", "rows[0]")]
    [InlineData("""{"reason":"x","rows":[{"variantId":2147483648,"kind":"Adjust","quantity":1,"expectedOnHand":null}]}""", "rows[0].variantId")]
    [InlineData("""{"reason":"x","rows":[{"variantId":0,"kind":"Adjust","quantity":1,"expectedOnHand":null}]}""", "rows[0].variantId")]
    [InlineData("""{"reason":"x","rows":[{"variantId":-{v},"kind":"Adjust","quantity":1,"expectedOnHand":null}]}""", "rows[0].variantId")]
    [InlineData("""{"reason":" \t\n","rows":[{"variantId":{v},"kind":"Adjust","quantity":1,"expectedOnHand":null}]}""", "reason")]
    [InlineData("""{"reason":"x","rows":[{"variantId":{v},"kind":"Adjust","quantity":1,"expectedOnHand":null},{"variantId":{v},"kind":"Receive","quantity":1,"expectedOnHand":null}]}""", "rows[1].variantId")]
    public async Task Type_confusion_is_a_400_naming_the_field_and_changes_nothing(string template, string field)
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);
        using var manager = await ManagerAsync();
        var mark = await MovementMarkAsync();

        var json = template.Replace("{v}", variantId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await manager.PostAsync(new Uri(AdjustUrl, UriKind.Relative), content, Token);
        var text = await response.Content.ReadAsStringAsync(Token);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{json} → {(int)response.StatusCode} {text}");
        Assert.True(JsonDocument.Parse(text).RootElement.GetProperty("errors").TryGetProperty(field, out _), $"{json} → {text}");
        Assert.Equal(10, (await StockAsync(variantId)).OnHand);
        Assert.Empty(await MovementsAfterAsync(mark, variantId));
    }

    [Fact]
    public async Task A_reason_padded_past_250_is_trimmed_to_fit_and_251_real_characters_are_refused()
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);
        using var manager = await ManagerAsync();
        var mark = await MovementMarkAsync();

        var padded = "   " + new string('p', 250) + "   ";
        var (ok, _) = await PostAsync(manager, new { reason = padded, rows = new[] { Row(variantId, "Receive", 1) } });
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal(new string('p', 250), Assert.Single(await MovementsAfterAsync(mark, variantId)).Reason);

        var (refused, _) = await PostAsync(manager, new { reason = new string('q', 251), rows = new[] { Row(variantId, "Receive", 1) } });
        Assert.Equal(HttpStatusCode.BadRequest, refused);
        Assert.Equal(11, (await StockAsync(variantId)).OnHand);
    }

    // ---- threshold ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Setting_the_same_threshold_twice_audits_once_and_writes_no_movement()
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);
        await ExecuteAsync("UPDATE inventory.StockLevels SET LowStockThreshold = NULL WHERE VariantId = @v;", variantId);
        using var manager = await ManagerAsync();
        var mark = await MovementMarkAsync();
        var auditMark = await AuditMarkAsync();

        try
        {
            var url = $"/api/admin/inventory/{variantId.ToString(CultureInfo.InvariantCulture)}/threshold";
            using (var first = await manager.PutAsJsonAsync(new Uri(url, UriKind.Relative), new { lowStockThreshold = 10_000 }, Token))
            {
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            }

            using (var again = await manager.PutAsJsonAsync(new Uri(url, UriKind.Relative), new { lowStockThreshold = 10_000 }, Token))
            {
                Assert.Equal(HttpStatusCode.OK, again.StatusCode);
                var row = await again.Content.ReadFromJsonAsync<JsonElement>(Token);
                Assert.Equal(10_000, row.GetProperty("ownThreshold").GetInt32());
            }

            using (var over = await manager.PutAsJsonAsync(new Uri(url, UriKind.Relative), new { lowStockThreshold = 10_001 }, Token))
            {
                Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
            }

            using (var asText = await manager.PutAsJsonAsync(new Uri(url, UriKind.Relative), new { lowStockThreshold = "5" }, Token))
            {
                Assert.Equal(HttpStatusCode.BadRequest, asText.StatusCode);
            }

            Assert.Equal(1, await StockAuditCountAfterAsync(auditMark));
            Assert.Empty(await MovementsAfterAsync(mark, variantId));
            Assert.Equal(10_000, await ThresholdAsync(variantId));
        }
        finally
        {
            await ExecuteAsync("UPDATE inventory.StockLevels SET LowStockThreshold = NULL WHERE VariantId = @v;", variantId);
        }
    }

    /// <summary>
    /// The audit row carries the actor and the figures, and nothing a secret could hide in: no token, no
    /// password, no cookie.
    /// </summary>
    [Fact]
    public async Task The_audit_row_names_the_change_and_carries_no_secret()
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);
        using var manager = await ManagerAsync();
        var auditMark = await AuditMarkAsync();

        await PostAsync(manager, new { reason = "Delivery", rows = new[] { Row(variantId, "Receive", 5) } });

        using var scope = CreateScope();
        var audit = Assert.Single(await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .AsNoTracking()
            .Where(row => row.Id > auditMark && row.EntityType == "StockLevel")
            .ToListAsync(Token));

        var everything = JsonSerializer.Serialize(audit);
        Assert.Equal(variantId.ToString(CultureInfo.InvariantCulture), audit.EntityId);
        Assert.Equal(10, JsonDocument.Parse(audit.BeforeJson!).RootElement.GetProperty("onHand").GetInt32());
        var after = JsonDocument.Parse(audit.AfterJson!).RootElement;
        Assert.Equal(15, after.GetProperty("onHand").GetInt32());
        Assert.Equal("Receive", after.GetProperty("movementType").GetString());
        Assert.Equal("Delivery", after.GetProperty("reason").GetString());
        Assert.NotNull(audit.UserId);

        foreach (var secret in new[] { "password", "token", "cookie", "antiforgery", "Correct-Horse" })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// IDOR-style probe by id: a variant of a soft-deleted product. A 404 must mean nothing was written.
    /// </summary>
    [Fact(Skip = "Tester #190 defect D3: SetThresholdAsync commits and audits, then FindAsync filters the deleted product and the API answers 404. Unskip when fixed.")]
    public async Task A_404_on_a_deleted_product_s_variant_means_nothing_was_written()
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);
        await ExecuteAsync("UPDATE inventory.StockLevels SET LowStockThreshold = NULL WHERE VariantId = @v;", variantId);
        await ExecuteAsync("UPDATE catalog.Products SET IsDeleted = 1 WHERE Id = (SELECT ProductId FROM catalog.ProductVariants WHERE Id = @v);", variantId);
        using var manager = await ManagerAsync();
        var auditMark = await AuditMarkAsync();

        try
        {
            var url = $"/api/admin/inventory/{variantId.ToString(CultureInfo.InvariantCulture)}/threshold";
            using var response = await manager.PutAsJsonAsync(new Uri(url, UriKind.Relative), new { lowStockThreshold = 7 }, Token);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Assert.Null(await ThresholdAsync(variantId));
                Assert.Equal(0, await StockAuditCountAfterAsync(auditMark));
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }
        finally
        {
            await ExecuteAsync("UPDATE catalog.Products SET IsDeleted = 0 WHERE Id = (SELECT ProductId FROM catalog.ProductVariants WHERE Id = @v);", variantId);
            await ExecuteAsync("UPDATE inventory.StockLevels SET LowStockThreshold = NULL WHERE VariantId = @v;", variantId);
        }
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private static void AssertChain(IReadOnlyList<InventoryMovement> movements, int startOnHand, int startReserved)
    {
        var (onHand, reserved) = (startOnHand, startReserved);

        foreach (var movement in movements)
        {
            onHand += movement.OnHandDelta;
            reserved += movement.ReservedDelta;
            Assert.True(
                (onHand, reserved) == (movement.OnHandAfter, movement.ReservedAfter),
                $"Movement {movement.Id} ({movement.MovementType}) says {movement.OnHandAfter}/{movement.ReservedAfter}, the chain says {onHand}/{reserved}.");
            Assert.True(movement.OnHandAfter >= movement.ReservedAfter, $"Movement {movement.Id} left on hand below reserved.");
        }
    }

    private async Task<HttpClient> ManagerAsync()
    {
        var client = await MediaTestHost.SignInAsync(Factory, Permissions.Inventory.View, Permissions.Inventory.Edit);
        _clients.Add(client);
        return client;
    }

    private static object Row(int variantId, string kind, int quantity, int? expectedOnHand = null) =>
        new { variantId, kind, quantity, expectedOnHand };

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync(new Uri(AdjustUrl, UriKind.Relative), body, Token);
        var text = await response.Content.ReadAsStringAsync(Token);

        return (response.StatusCode, text.Length == 0 || response.StatusCode == HttpStatusCode.InternalServerError
            ? default
            : JsonDocument.Parse(text).RootElement.Clone());
    }

    /// <summary>
    /// Holds the variant's stock row with <paramref name="blockerSql"/> in an open transaction, starts
    /// <paramref name="request"/>, waits until SQL Server reports it blocked by that transaction, then
    /// commits and returns the request's answer.
    /// </summary>
    private async Task<T> RaceBehindAsync<T>(string blockerSql, int variantId, Func<Task<T>> request)
    {
        await using var blocker = new SqlConnection(SqlServer.ConnectionString);
        await blocker.OpenAsync(Token);
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync(Token);

        await using (var hold = blocker.CreateCommand())
        {
            hold.Transaction = transaction;
            hold.CommandText = blockerSql;
            hold.Parameters.AddWithValue("@v", variantId);
            Assert.Equal(1, await hold.ExecuteNonQueryAsync(Token));
        }

        int spid;
        await using (var who = blocker.CreateCommand())
        {
            who.Transaction = transaction;
            who.CommandText = "SELECT @@SPID;";
            spid = Convert.ToInt32(await who.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture);
        }

        var pending = request();

        try
        {
            await WaitUntilBlockedAsync(spid, pending);
        }
        finally
        {
            await transaction.CommitAsync(Token);
        }

        return await pending;
    }

    private async Task WaitUntilBlockedAsync(int blockerSpid, Task pending)
    {
        await using var monitor = new SqlConnection(SqlServer.ConnectionString);
        await monitor.OpenAsync(Token);

        for (var attempt = 0; attempt < 300; attempt++)
        {
            if (pending.IsCompleted)
            {
                Assert.Fail("The request finished without waiting on the held stock row: it did not read the row under a lock.");
            }

            await using var command = monitor.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = @spid;";
            command.Parameters.AddWithValue("@spid", blockerSpid);

            if (Convert.ToInt32(await command.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture) > 0)
            {
                return;
            }

            await Task.Delay(50, Token);
        }

        Assert.Fail("The request never blocked on the held stock row.");
    }

    private async Task WithFloorConstraintOffAsync(int variantId, Func<Task> act)
    {
        await ExecuteAsync($"ALTER TABLE inventory.StockLevels NOCHECK CONSTRAINT {FloorConstraint};", variantId);

        try
        {
            await act();
        }
        finally
        {
            // Whatever the act left, put the row back inside the rule before the rule is trusted again.
            await ExecuteAsync("UPDATE inventory.StockLevels SET Reserved = 0 WHERE VariantId = @v;", variantId);
            await ExecuteAsync($"ALTER TABLE inventory.StockLevels WITH CHECK CHECK CONSTRAINT {FloorConstraint};", variantId);
        }
    }

    private async Task WithFailingTriggerAsync(string condition, Func<Task> act)
    {
        await ExecuteAsync(
            $"""
            CREATE TRIGGER inventory.{FailingTrigger} ON inventory.InventoryMovements AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM inserted WHERE {condition})
                    THROW 50190, 'Tester: movement insert refused.', 1;
            END;
            """,
            0);

        try
        {
            await act();
        }
        finally
        {
            await ExecuteAsync($"DROP TRIGGER IF EXISTS inventory.{FailingTrigger};", 0);
        }
    }

    /// <summary>
    /// Moves the order's deadline into the past so it can be expired at the real "now". Moving the clock
    /// 49 hours ahead instead (the base class's default) stamps the Release movement in the future, and a
    /// future-dated movement then sorts as the newest in that variant's history for every later test —
    /// which is exactly how this class first broke <c>Every_applied_change_writes_one_movement_and_one_audit_row</c>.
    /// </summary>
    private Task MakeDueAsync(int orderId) =>
        ExecuteAsync("UPDATE sales.Orders SET PaymentDueAtUtc = DATEADD(MINUTE, -1, SYSUTCDATETIME()) WHERE Id = @v;", orderId);

    private async Task ExecuteAsync(string sql, int variantId)
    {
        await using var connection = new SqlConnection(SqlServer.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        // DDL (CREATE TRIGGER) must be the first statement of its batch, which a parameterised
        // sp_executesql call is not, so only statements that use @v get it.
        if (sql.Contains("@v", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("@v", variantId);
        }

        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<long> MovementMarkAsync()
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().InventoryMovements
            .AsNoTracking()
            .MaxAsync(movement => (long?)movement.Id, Token) ?? 0;
    }

    private async Task<List<InventoryMovement>> MovementsAfterAsync(long mark, int? variantId = null)
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().InventoryMovements
            .AsNoTracking()
            .Where(movement => movement.Id > mark && (variantId == null || movement.VariantId == variantId))
            .OrderBy(movement => movement.Id)
            .ToListAsync(Token);
    }

    private async Task<long> AuditMarkAsync()
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .AsNoTracking()
            .MaxAsync(row => (long?)row.Id, Token) ?? 0;
    }

    private async Task<int> StockAuditCountAfterAsync(long mark)
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .AsNoTracking()
            .CountAsync(row => row.Id > mark && row.EntityType == "StockLevel", Token);
    }

    private async Task<int?> ThresholdAsync(int variantId)
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().StockLevels
            .AsNoTracking()
            .Where(level => level.VariantId == variantId)
            .Select(level => level.LowStockThreshold)
            .SingleAsync(Token);
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
