using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Sales;

/// <summary><c>sales.PickupLocations</c> (doc 08 §3.5, CHK-02) [A].</summary>
internal sealed class PickupLocationConfiguration : IEntityTypeConfiguration<PickupLocation>
{
    public void Configure(EntityTypeBuilder<PickupLocation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PickupLocations", AppDbContext.SalesSchema);

        builder.HasKey(location => location.Id);

        builder.Property(location => location.Name).HasMaxLength(100).IsRequired();
        builder.Property(location => location.AddressLine).HasMaxLength(250).IsRequired();
        builder.Property(location => location.City).HasMaxLength(80).IsRequired();
        builder.Property(location => location.DistrictId).IsRequired();
        builder.Property(location => location.Phone).HasColumnType("varchar(20)");
        builder.Property(location => location.OpeningHours).HasMaxLength(200);
        builder.Property(location => location.IsActive).HasDefaultValue(true).IsRequired();
        builder.Property(location => location.SortOrder).IsRequired();

        builder.ApplyAuditColumns();

        // The index for a foreign key that cannot exist yet. Doc 08 §3.5 gives DistrictId an FK to
        // ref.Districts; no migration has created that table (GLB-06), so the constraint is deferred and
        // only the column and its index are built. Building the index now means adding the FK later is a
        // metadata-only change, and it keeps doc 08 §1.6's DB7 rule ("every FK column leads at least one
        // index") true at the moment the constraint arrives rather than one migration afterwards.
        builder
            .HasIndex(location => location.DistrictId)
            .HasDatabaseName("IX_PickupLocations_DistrictId");
    }
}
