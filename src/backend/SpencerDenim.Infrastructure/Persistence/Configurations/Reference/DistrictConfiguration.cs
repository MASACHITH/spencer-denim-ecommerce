using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Reference;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Reference;

/// <summary>
/// <c>ref.Districts</c> (doc 08 §3.1). Twenty-five rows with one admin-editable column
/// (<c>DeliveryZone</c>), so there is a CK on the zone and no audit pair — §3.1 marks the table neither
/// <c>[A]</c> nor <c>[RV]</c>.
/// </summary>
internal sealed class DistrictConfiguration : IEntityTypeConfiguration<District>
{
    public void Configure(EntityTypeBuilder<District> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "Districts",
            AppDbContext.ReferenceSchema,
            table => table.HasCheckConstraint(
                // Doc 08 §3.1: "1 Colombo, 2 Western, 3 IslandWide. CK. … Adding a new zone value is a
                // code change" — which is what a CK makes it. The same three values are constrained on
                // `sales.DeliveryMethodZoneRates.DeliveryZone` and `sales.Orders.DeliveryZone`, so a
                // district can never carry a zone that no rate table column could answer for.
                "CK_Districts_DeliveryZone",
                "[DeliveryZone] BETWEEN 1 AND 3"));

        builder.HasKey(district => district.Id);

        // Doc 08 §3.1: "seeded 1–25, no identity".
        builder.Property(district => district.Id).ValueGeneratedNever();

        builder.Property(district => district.ProvinceId).IsRequired();

        builder
            .Property(district => district.Code)
            .HasColumnType($"varchar({District.MaxCodeLength})")
            .IsRequired();

        builder.Property(district => district.Name).HasMaxLength(District.MaxNameLength).IsRequired();
        builder.Property(district => district.DeliveryZone).HasColumnType("tinyint").IsRequired();
        builder.Property(district => district.SortOrder).IsRequired();
        builder.Property(district => district.IsActive).HasDefaultValue(true).IsRequired();

        builder.HasIndex(district => district.Code).IsUnique().HasDatabaseName("UX_Districts_Code");

        // Doc 08 §3.1 marks Name UX as well. It is not decoration: `Orders.DeliveryDistrictName` is a
        // snapshot, and two districts sharing a name would make an old order ambiguous about where it went.
        builder.HasIndex(district => district.Name).IsUnique().HasDatabaseName("UX_Districts_Name");

        builder
            .HasOne(district => district.Province)
            .WithMany(province => province.Districts)
            .HasForeignKey(district => district.ProvinceId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
