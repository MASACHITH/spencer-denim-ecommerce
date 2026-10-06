using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Reference;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Reference;

/// <summary>
/// <c>ref.Provinces</c> (doc 08 §3.1). Nine rows, never edited, no audit columns and no rowversion —
/// §3.1 marks the table neither <c>[A]</c> nor <c>[RV]</c>.
/// </summary>
internal sealed class ProvinceConfiguration : IEntityTypeConfiguration<Province>
{
    public void Configure(EntityTypeBuilder<Province> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Provinces", AppDbContext.ReferenceSchema);

        builder.HasKey(province => province.Id);

        // Doc 08 §3.1: "seeded 1–9, no identity". The ids are part of the seed data, because
        // `Districts.ProvinceId` is seeded as a literal and a generated key would make that impossible.
        builder.Property(province => province.Id).ValueGeneratedNever();

        builder
            .Property(province => province.Code)
            .HasColumnType($"varchar({Province.MaxCodeLength})")
            .IsRequired();

        builder.Property(province => province.Name).HasMaxLength(Province.MaxNameLength).IsRequired();

        builder.HasIndex(province => province.Code).IsUnique().HasDatabaseName("UX_Provinces_Code");
    }
}
