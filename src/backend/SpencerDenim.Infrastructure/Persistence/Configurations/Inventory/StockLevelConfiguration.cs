using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Inventory;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Inventory;

/// <summary><c>inventory.StockLevels</c> (doc 08 §3.4): 1:1 with a variant, no rowversion.</summary>
internal sealed class StockLevelConfiguration : IEntityTypeConfiguration<StockLevel>
{
    public void Configure(EntityTypeBuilder<StockLevel> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "StockLevels",
            AppDbContext.InventorySchema,
            table =>
            {
                table.HasCheckConstraint("CK_StockLevels_OnHand", "[OnHand] >= 0");
                table.HasCheckConstraint("CK_StockLevels_Reserved", "[Reserved] >= 0");
                table.HasCheckConstraint("CK_StockLevels_ReservedLeOnHand", "[Reserved] <= [OnHand]");
                table.HasCheckConstraint(
                    "CK_StockLevels_LowStockThreshold",
                    "[LowStockThreshold] IS NULL OR [LowStockThreshold] >= 0");
            });

        builder.HasKey(stock => stock.VariantId);

        builder.Property(stock => stock.VariantId).ValueGeneratedNever();
        builder.Property(stock => stock.OnHand).HasDefaultValue(0).IsRequired();
        builder.Property(stock => stock.Reserved).HasDefaultValue(0).IsRequired();

        // Doc 08 §3.4: computed and persisted, so IX_StockLevels_Available can index it.
        builder
            .Property(stock => stock.Available)
            .HasComputedColumnSql("[OnHand] - [Reserved]", stored: true);

        builder.Property(stock => stock.UpdatedAtUtc).HasDefaultValueSql("sysutcdatetime()").IsRequired();

        builder
            .HasIndex(stock => stock.Available)
            .IncludeProperties(stock => new { stock.LowStockThreshold, stock.OnHand })
            .HasDatabaseName("IX_StockLevels_Available");

        builder
            .HasOne(stock => stock.Variant)
            .WithOne(variant => variant.StockLevel)
            .HasForeignKey<StockLevel>(stock => stock.VariantId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
