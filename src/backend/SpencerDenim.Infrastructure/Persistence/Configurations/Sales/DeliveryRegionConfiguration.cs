using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Sales;

/// <summary><c>sales.DeliveryRegions</c> [A] [RV] (owner decision 2026-10-21). See <see cref="DeliveryRegion"/>.</summary>
internal sealed class DeliveryRegionConfiguration : IEntityTypeConfiguration<DeliveryRegion>
{
    public void Configure(EntityTypeBuilder<DeliveryRegion> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DeliveryRegions", AppDbContext.SalesSchema, table =>
        {
            table.HasCheckConstraint("CK_DeliveryRegions_Fee", "[Fee] IS NULL OR [Fee] >= 0");
            table.HasCheckConstraint("CK_DeliveryRegions_EstimatedDays", "[EstimatedDaysMin] <= [EstimatedDaysMax]");
        });

        builder.HasKey(region => region.Code).HasName("PK_DeliveryRegions");

        builder.Property(region => region.Code)
            .HasColumnType($"varchar({DeliveryRegion.MaxCodeLength})")
            .ValueGeneratedNever();

        builder.Property(region => region.Name).HasMaxLength(DeliveryRegion.MaxNameLength).IsRequired();
        builder.Property(region => region.Fee);
        builder.Property(region => region.EstimatedDaysMin).HasColumnType("tinyint").IsRequired();
        builder.Property(region => region.EstimatedDaysMax).HasColumnType("tinyint").IsRequired();
        builder.Property(region => region.IsEnabled).HasDefaultValue(true).IsRequired();
        builder.Property(region => region.SortOrder).HasDefaultValue((short)0).IsRequired();

        builder.ApplyAuditColumns();
        builder.ApplyRowVersion(region => region.RowVersion);

        builder.HasIndex(region => region.Name).IsUnique().HasDatabaseName("UX_DeliveryRegions_Name");
    }
}
