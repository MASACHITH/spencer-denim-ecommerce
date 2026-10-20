using Microsoft.EntityFrameworkCore;
using SpencerDenim.Domain.Inventory;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Inventory;

/// <summary>
/// Doc 08 §5.1's "every stock change writes an <c>InventoryMovements</c> row", for the order lifecycle's
/// stock paths: reserve at placement, release on expiry or cancel, and the sale on payment (#108).
/// <para>
/// <b>Called immediately after the path's own conditional <c>UPDATE</c> has matched its row</b>, in the
/// same transaction. That transaction now holds the row's exclusive lock until it commits, and a
/// transaction always reads its own writes — under RCSI as under locking read committed — so the
/// <c>OnHand</c> and <c>Reserved</c> this copies are exactly the figures that update produced, the same
/// guarantee an <c>OUTPUT</c> clause would give. It is an <c>INSERT … SELECT</c> rather than an
/// <c>OUTPUT</c> on the update so that none of the four existing statements, whose predicates are the
/// whole concurrency argument of doc 08 §5, had to change shape: each gains one line after it and nothing
/// else. (<c>OUTPUT … INTO</c> straight into the ledger is not available either: SQL Server refuses it into
/// a table that has foreign keys or check constraints, and this one has both.)
/// </para>
/// <para>
/// <b>A row count other than 1 throws</b>: the caller's update has just matched this variant's stock row,
/// so not finding it here would mean the statement read something other than what was written.
/// </para>
/// </summary>
internal static class StockLedger
{
    public static async Task AppendAsync(
        AppDbContext dbContext,
        int variantId,
        InventoryMovementType movementType,
        int onHandDelta,
        int reservedDelta,
        int orderId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var written = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO inventory.InventoryMovements
                   (VariantId, MovementType, OnHandDelta, ReservedDelta, OnHandAfter, ReservedAfter, OrderId, CreatedAtUtc)
            SELECT VariantId, {(byte)movementType}, {onHandDelta}, {reservedDelta}, OnHand, Reserved, {orderId}, {nowUtc}
              FROM inventory.StockLevels
             WHERE VariantId = {variantId};
            """,
            cancellationToken);

        if (written != 1)
        {
            throw new InvalidOperationException(
                $"The {movementType} movement for variant {variantId} (order {orderId}) found {written} stock rows; "
                + "it runs straight after the update that matched one.");
        }
    }
}
