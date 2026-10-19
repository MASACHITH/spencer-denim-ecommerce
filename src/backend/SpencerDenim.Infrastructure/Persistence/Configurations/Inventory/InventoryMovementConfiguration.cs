using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Inventory;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Inventory;

/// <summary>
/// <c>inventory.InventoryMovements</c> (doc 08 §3.4): the append-only stock ledger, <c>bigint IDENTITY</c>
/// (doc 08 §1.2) because it grows with every stock change and is never deleted.
/// </summary>
internal sealed class InventoryMovementConfiguration : IEntityTypeConfiguration<InventoryMovement>
{
    public void Configure(EntityTypeBuilder<InventoryMovement> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "InventoryMovements",
            AppDbContext.InventorySchema,
            table =>
            {
                // Doc 08 §3.4: 1 Initial … 9 CountCorrection.
                table.HasCheckConstraint("CK_InventoryMovements_MovementType", "[MovementType] BETWEEN 1 AND 9");

                // Doc 08 §3.4: "Required for Adjust and CountCorrection". LEN ignores trailing spaces, so a
                // reason of blanks is refused as well as a NULL one.
                table.HasCheckConstraint(
                    "CK_InventoryMovements_Reason",
                    "[MovementType] NOT IN (3, 4, 9) OR LEN([Reason]) > 0");

                // The figures a change left behind are a StockLevels row's, which cannot be negative.
                table.HasCheckConstraint(
                    "CK_InventoryMovements_After",
                    "[OnHandAfter] >= 0 AND [ReservedAfter] >= 0 AND [ReservedAfter] <= [OnHandAfter]");
            });

        builder.HasKey(movement => movement.Id);

        builder.Property(movement => movement.MovementType).HasColumnType("tinyint").IsRequired();
        builder.Property(movement => movement.Reason).HasMaxLength(InventoryMovement.ReasonMaxLength);

        // No FK on CreatedByUserId: doc 08 §1.5's convention for user columns (users are anonymised, not
        // deleted), the same as OrderStatusHistory.ChangedByUserId.
        builder.Property(movement => movement.CreatedByUserId);

        builder.Property(movement => movement.CreatedAtUtc)
            .HasDefaultValueSql("sysutcdatetime()")
            .IsRequired();

        // Doc 08 §3.4: one variant's history, newest first. Id DESC is the tie-break for two movements in
        // the same millisecond (a bulk update writes many), so the history reads in index order with no sort.
        // It also satisfies DB7 for the VariantId foreign key.
        builder
            .HasIndex(movement => new { movement.VariantId, movement.CreatedAtUtc, movement.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("IX_InventoryMovements_Variant_Created");

        // Doc 08 §3.4's two filtered indexes. The order one is DB7 for the OrderId foreign key; the batch one
        // finds every row of one bulk update.
        builder
            .HasIndex(movement => movement.OrderId)
            .HasFilter("[OrderId] IS NOT NULL")
            .HasDatabaseName("IX_InventoryMovements_Order");

        builder
            .HasIndex(movement => movement.BatchId)
            .HasFilter("[BatchId] IS NOT NULL")
            .HasDatabaseName("IX_InventoryMovements_Batch");

        builder
            .HasOne(movement => movement.Variant)
            .WithMany()
            .HasForeignKey(movement => movement.VariantId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(movement => movement.Order)
            .WithMany()
            .HasForeignKey(movement => movement.OrderId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
