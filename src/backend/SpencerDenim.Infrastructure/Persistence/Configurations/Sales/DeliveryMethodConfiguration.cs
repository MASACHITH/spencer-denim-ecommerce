using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Sales;

/// <summary><c>sales.DeliveryMethods</c> (doc 08 §3.5, CHK-02 / PDP-05 / ADM-22) [A] [RV].</summary>
internal sealed class DeliveryMethodConfiguration : IEntityTypeConfiguration<DeliveryMethod>
{
    public void Configure(EntityTypeBuilder<DeliveryMethod> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DeliveryMethods", AppDbContext.SalesSchema);

        builder.HasKey(method => method.Id);

        builder
            .Property(method => method.Code)
            .HasColumnType($"varchar({DeliveryMethod.MaxCodeLength})")
            .IsRequired();

        builder.Property(method => method.Name).HasMaxLength(60).IsRequired();
        builder.Property(method => method.Description).HasMaxLength(300);
        builder.Property(method => method.IsEnabled).HasDefaultValue(false).IsRequired();

        // True by default: doc 08 §3.5 says RequiresAddress is "0 for PICKUP", so needing an address is the
        // normal case and a method added without thinking about it must not quietly skip the address step.
        builder.Property(method => method.RequiresAddress).HasDefaultValue(true).IsRequired();

        builder.Property(method => method.SortOrder).IsRequired();

        builder.ApplyAuditColumns();
        builder.ApplyRowVersion(method => method.RowVersion);

        // Doc 08 §3.5: UX on the code. Orders snapshot this value into Delivery.DeliveryMethodCode and the
        // order check constraints branch on it, so two methods sharing a code would make those checks lie.
        builder
            .HasIndex(method => method.Code)
            .IsUnique()
            .HasDatabaseName("UX_DeliveryMethods_Code");
    }
}
