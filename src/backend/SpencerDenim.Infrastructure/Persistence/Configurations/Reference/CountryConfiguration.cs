using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Reference;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Reference;

/// <summary>
/// <c>ref.Countries</c> [A] [RV] (owner decision 2026-10-21). See <see cref="Country"/>.
/// <para>
/// <b>Explicit names for every index and constraint</b>, as the rest of the schema has them, so the migration
/// does not depend on EF's naming. The string columns take the database collation
/// (<c>Latin1_General_100_CI_AS_SC</c>), set when the database is created and never with <c>UseCollation</c>
/// (see <see cref="AppDbContext"/>), so <c>UX_Countries_Name</c> is case-insensitive like every other name.
/// </para>
/// </summary>
internal sealed class CountryConfiguration : IEntityTypeConfiguration<Country>
{
    public void Configure(EntityTypeBuilder<Country> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Countries", AppDbContext.ReferenceSchema, table =>
        {
            // Upper-case ASCII letters only. The binary collation makes the range test exact: under the
            // case-insensitive database collation 'gb' would pass [A-Z].
            table.HasCheckConstraint(
                "CK_Countries_Code",
                "[Code] COLLATE Latin1_General_100_BIN2 LIKE '[A-Z][A-Z]'");

            // Sri Lanka is the domestic country and has no overseas region; every other country has one.
            table.HasCheckConstraint(
                "CK_Countries_Region",
                $"([Code] = '{Country.SriLankaCode}' AND [RegionCode] IS NULL) "
                + $"OR ([Code] <> '{Country.SriLankaCode}' AND [RegionCode] IS NOT NULL)");

            // The domestic country cannot be switched off from the overseas screen.
            table.HasCheckConstraint(
                "CK_Countries_SriLankaEnabled",
                $"[Code] <> '{Country.SriLankaCode}' OR [IsShippingEnabled] = 1");

            table.HasCheckConstraint(
                "CK_Countries_DeliveryFeeOverride",
                "[DeliveryFeeOverride] IS NULL OR [DeliveryFeeOverride] >= 0");

            // Both or neither, and in order: a country never mixes its own minimum with the region's maximum.
            table.HasCheckConstraint(
                "CK_Countries_DaysOverride",
                "([EstimatedDaysMinOverride] IS NULL AND [EstimatedDaysMaxOverride] IS NULL) "
                + "OR ([EstimatedDaysMinOverride] IS NOT NULL AND [EstimatedDaysMaxOverride] IS NOT NULL "
                + "AND [EstimatedDaysMinOverride] <= [EstimatedDaysMaxOverride])");
        });

        builder.HasKey(country => country.Code).HasName("PK_Countries");

        builder.Property(country => country.Code)
            .HasColumnType($"char({Country.CodeLength})")
            .ValueGeneratedNever();

        builder.Property(country => country.Name).HasMaxLength(Country.MaxNameLength).IsRequired();

        builder.Property(country => country.RegionCode)
            .HasColumnType($"varchar({Domain.Sales.DeliveryRegion.MaxCodeLength})");

        builder.Property(country => country.PostalCodeRequired).HasDefaultValue(false).IsRequired();
        builder.Property(country => country.RegionRequired).HasDefaultValue(false).IsRequired();
        builder.Property(country => country.IsShippingEnabled).HasDefaultValue(true).IsRequired();
        builder.Property(country => country.DeliveryFeeOverride);
        builder.Property(country => country.EstimatedDaysMinOverride).HasColumnType("tinyint");
        builder.Property(country => country.EstimatedDaysMaxOverride).HasColumnType("tinyint");
        builder.Property(country => country.SortOrder).HasDefaultValue((short)0).IsRequired();

        builder.ApplyAuditColumns();
        builder.ApplyRowVersion(country => country.RowVersion);

        builder.HasIndex(country => country.Name).IsUnique().HasDatabaseName("UX_Countries_Name");
        builder.HasIndex(country => country.RegionCode).HasDatabaseName("IX_Countries_RegionCode");

        builder
            .HasOne(country => country.Region)
            .WithMany()
            .HasForeignKey(country => country.RegionCode)
            .HasConstraintName("FK_Countries_DeliveryRegions_RegionCode")
            .OnDelete(DeleteBehavior.NoAction);
    }
}
