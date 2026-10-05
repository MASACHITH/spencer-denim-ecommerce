using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Sales;

/// <summary><c>sales.Couriers</c> (doc 08 §3.5, ADM-09 / ADM-22 / ACC-02) [A].</summary>
internal sealed class CourierConfiguration : IEntityTypeConfiguration<Courier>
{
    public void Configure(EntityTypeBuilder<Courier> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "Couriers",
            AppDbContext.SalesSchema,

            // Doc 08 §3.5: "CK contains {trackingNumber} when not NULL". CHARINDEX rather than LIKE because
            // the braces are literal characters and LIKE would need them escaped; CHARINDEX also reads as
            // what it is. A template with no placeholder would send every customer to the courier's home
            // page, since doc 08 renders the link from the template plus Orders.TrackingNumber.
            table => table.HasCheckConstraint(
                "CK_Couriers_TrackingUrlTemplate",
                $"[TrackingUrlTemplate] IS NULL OR CHARINDEX('{Courier.TrackingNumberPlaceholder}', [TrackingUrlTemplate]) > 0"));

        builder.HasKey(courier => courier.Id);

        builder.Property(courier => courier.Name).HasMaxLength(80).IsRequired();
        builder.Property(courier => courier.TrackingUrlTemplate).HasMaxLength(500);
        builder.Property(courier => courier.Phone).HasColumnType("varchar(20)");
        builder.Property(courier => courier.IsActive).HasDefaultValue(true).IsRequired();
        builder.Property(courier => courier.SortOrder).IsRequired();

        builder.ApplyAuditColumns();

        builder
            .HasIndex(courier => courier.Name)
            .IsUnique()
            .HasDatabaseName("UX_Couriers_Name");
    }
}
