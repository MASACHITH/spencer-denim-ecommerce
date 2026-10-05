using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Reference;
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

        // Doc 08 §3.5 gives DistrictId an FK to ref.Districts. W3-03 built the column and this index with
        // no constraint because the table did not exist; W3-02 created it (doc 08 §3.1), so the constraint
        // lands here. The index stays named explicitly rather than left to EF's convention, which is what
        // doc 08 §1.6's DB7 rule asks for.
        //
        // NO ACTION, and it is load-bearing for R7: a PICKUP order's district is the *location's*
        // district, so a district that still has an active pickup location must not be removable.
        builder
            .HasIndex(location => location.DistrictId)
            .HasDatabaseName("IX_PickupLocations_DistrictId");

        builder
            .HasOne<District>()
            .WithMany()
            .HasForeignKey(location => location.DistrictId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
