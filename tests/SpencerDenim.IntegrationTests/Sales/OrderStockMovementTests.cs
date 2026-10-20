using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Inventory;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// The order lifecycle's stock movements (doc 08 §5.1, doc 11 P2 part C; #108, built with ADM-12 #190):
/// placement writes a Reserve per line, expiry and cancel a Release, approval a Sale that takes from both
/// figures, and a late acceptance a Sale from on hand alone. Each row names its order and carries the
/// figures its update left behind.
/// </summary>
public sealed class OrderStockMovementTests(SqlServerDatabaseFixture sqlServer) : BankTransferTestBase(sqlServer)
{
    [Fact]
    public async Task Placement_reserves_and_approval_sells_each_with_a_movement_naming_the_order()
    {
        var mark = await MarkAsync();
        var (order, variantId) = await PlaceOrderAsync(quantity: 2, onHand: 10);
        var orderId = await OrderIdAsync(order.PublicId);

        var reserve = Assert.Single(await MovementsAfterAsync(mark, variantId));
        Assert.Equal(InventoryMovementType.Reserve, reserve.MovementType);
        Assert.Equal(orderId, reserve.OrderId);
        Assert.Equal((0, 2, 10, 2), (reserve.OnHandDelta, reserve.ReservedDelta, reserve.OnHandAfter, reserve.ReservedAfter));
        Assert.Null(reserve.CreatedByUserId);
        Assert.Null(reserve.Reason);

        Assert.IsType<BankTransferDecisionResult.Applied>(await ApproveAsync(orderId));

        var sale = (await MovementsAfterAsync(mark, variantId))[^1];
        Assert.Equal(InventoryMovementType.Sale, sale.MovementType);
        Assert.Equal(orderId, sale.OrderId);
        Assert.Equal((-2, -2, 8, 0), (sale.OnHandDelta, sale.ReservedDelta, sale.OnHandAfter, sale.ReservedAfter));
        Assert.Equal((8, 0, 8), await StockAsync(variantId));
    }

    [Fact]
    public async Task Expiry_releases_and_a_late_acceptance_sells_from_on_hand_alone()
    {
        var mark = await MarkAsync();
        var (order, variantId) = await PlaceOrderAsync(quantity: 3, onHand: 10);
        var orderId = await OrderIdAsync(order.PublicId);

        await MakeDueAsync(orderId);
        Assert.True(await ExpireAsync(orderId, DateTime.UtcNow));

        var release = (await MovementsAfterAsync(mark, variantId))[^1];
        Assert.Equal(InventoryMovementType.Release, release.MovementType);
        Assert.Equal(orderId, release.OrderId);
        Assert.Equal((0, -3, 10, 0), (release.OnHandDelta, release.ReservedDelta, release.OnHandAfter, release.ReservedAfter));

        Assert.IsType<BankTransferDecisionResult.Applied>(await AcceptLateAsync(orderId));

        var sale = (await MovementsAfterAsync(mark, variantId))[^1];
        Assert.Equal(InventoryMovementType.Sale, sale.MovementType);
        Assert.Equal((-3, 0, 7, 0), (sale.OnHandDelta, sale.ReservedDelta, sale.OnHandAfter, sale.ReservedAfter));

        Assert.Equal(
            [InventoryMovementType.Reserve, InventoryMovementType.Release, InventoryMovementType.Sale],
            (await MovementsAfterAsync(mark, variantId)).Select(movement => movement.MovementType));
    }

    [Fact]
    public async Task A_customer_cancel_releases_with_a_movement()
    {
        var mark = await MarkAsync();
        var (order, variantId) = await PlaceOrderAsync(quantity: 1, onHand: 5);

        using var response = await NewCheckoutVisitor().CancelOrderAsync(order.PublicId.ToString("D"), Token);
        Assert.True(response.IsSuccessStatusCode, $"Cancel answered {(int)response.StatusCode}.");

        var movements = await MovementsAfterAsync(mark, variantId);
        Assert.Equal([InventoryMovementType.Reserve, InventoryMovementType.Release], movements.Select(movement => movement.MovementType));
        Assert.Equal((5, 0), (movements[^1].OnHandAfter, movements[^1].ReservedAfter));
    }

    /// <summary>
    /// A late acceptance that is refused for short stock writes no movement: the refusal rolls the whole
    /// acceptance back, including the Sale rows of any line deducted before the short one.
    /// </summary>
    [Fact]
    public async Task A_late_acceptance_refused_for_short_stock_writes_no_sale()
    {
        var mark = await MarkAsync();
        var (order, variantId) = await PlaceOrderAsync(quantity: 2, onHand: 2);
        var orderId = await OrderIdAsync(order.PublicId);
        await MakeDueAsync(orderId);
        Assert.True(await ExpireAsync(orderId, DateTime.UtcNow));

        // Somebody else buys the shelf empty while the order is closed.
        using (var scope = CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().StockLevels
                .Where(level => level.VariantId == variantId)
                .ExecuteUpdateAsync(level => level.SetProperty(row => row.OnHand, 1), Token);
        }

        Assert.IsType<BankTransferDecisionResult.ShortStock>(await AcceptLateAsync(orderId));

        Assert.DoesNotContain(
            await MovementsAfterAsync(mark, variantId),
            movement => movement.MovementType == InventoryMovementType.Sale);
    }

    /// <summary>
    /// Moves the order's deadline into the past so it expires at the real "now". Moving the clock ahead
    /// instead (the base class's default) stamps the Release movement in the future, and a future-dated
    /// movement then sorts first in that variant's history for every later test (tester #190, D5).
    /// </summary>
    private async Task MakeDueAsync(int orderId)
    {
        using var scope = CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
            .Where(order => order.Id == orderId)
            .ExecuteUpdateAsync(order => order.SetProperty(row => row.PaymentDueAtUtc, DateTime.UtcNow.AddMinutes(-1)), Token);
    }

    private async Task<long> MarkAsync()
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().InventoryMovements
            .AsNoTracking()
            .MaxAsync(movement => (long?)movement.Id, Token) ?? 0;
    }

    private async Task<List<InventoryMovement>> MovementsAfterAsync(long mark, int variantId)
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().InventoryMovements
            .AsNoTracking()
            .Where(movement => movement.Id > mark && movement.VariantId == variantId)
            .OrderBy(movement => movement.Id)
            .ToListAsync(Token);
    }
}
