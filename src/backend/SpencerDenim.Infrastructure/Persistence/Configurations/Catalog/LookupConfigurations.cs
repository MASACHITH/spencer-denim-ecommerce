using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Catalog;

/// <summary><c>catalog.AgeGroups</c> (doc 08 §3.2) [A].</summary>
internal sealed class AgeGroupConfiguration : IEntityTypeConfiguration<AgeGroup>
{
    public void Configure(EntityTypeBuilder<AgeGroup> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "AgeGroups",
            AppDbContext.CatalogSchema,
            table => table.HasCheckConstraint(
                "CK_AgeGroups_Months",
                "[MinMonths] IS NULL OR [MaxMonths] IS NULL OR [MinMonths] <= [MaxMonths]"));

        builder.HasKey(ageGroup => ageGroup.Id);

        builder.Property(ageGroup => ageGroup.Name).HasMaxLength(40).IsRequired();
        builder.Property(ageGroup => ageGroup.Slug).HasColumnType("varchar(40)").IsRequired();
        builder.Property(ageGroup => ageGroup.IsActive).HasDefaultValue(true).IsRequired();

        builder.ApplyAuditColumns();

        builder.HasIndex(ageGroup => ageGroup.Name).IsUnique().HasDatabaseName("UX_AgeGroups_Name");
        builder.HasIndex(ageGroup => ageGroup.Slug).IsUnique().HasDatabaseName("UX_AgeGroups_Slug");
    }
}

/// <summary><c>catalog.AttributeOptions</c> (doc 08 §3.2) [A].</summary>
internal sealed class AttributeOptionConfiguration : IEntityTypeConfiguration<AttributeOption>
{
    public void Configure(EntityTypeBuilder<AttributeOption> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "AttributeOptions",
            AppDbContext.CatalogSchema,
            table => table.HasCheckConstraint("CK_AttributeOptions_AttributeType", "[AttributeType] IN (1, 2, 3)"));

        builder.HasKey(option => option.Id);

        builder.Property(option => option.AttributeType).HasColumnType("tinyint").IsRequired();
        builder.Property(option => option.Name).HasMaxLength(60).IsRequired();
        builder.Property(option => option.Slug).HasColumnType("varchar(60)").IsRequired();
        builder.Property(option => option.IsActive).HasDefaultValue(true).IsRequired();

        builder.ApplyAuditColumns();

        builder
            .HasIndex(option => new { option.AttributeType, option.Slug })
            .IsUnique()
            .HasDatabaseName("UX_AttributeOptions_Type_Slug");
    }
}

/// <summary><c>catalog.ColourFamilies</c> (doc 08 §3.2).</summary>
internal sealed class ColourFamilyConfiguration : IEntityTypeConfiguration<ColourFamily>
{
    public void Configure(EntityTypeBuilder<ColourFamily> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "ColourFamilies",
            AppDbContext.CatalogSchema,
            table => table.HasCheckConstraint("CK_ColourFamilies_HexCode", "[HexCode] LIKE '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]'"));

        builder.HasKey(family => family.Id);

        builder.Property(family => family.Name).HasMaxLength(30).IsRequired();
        builder.Property(family => family.Slug).HasColumnType("varchar(30)").IsRequired();
        builder.Property(family => family.HexCode).HasColumnType("char(7)").IsRequired();

        builder.HasIndex(family => family.Name).IsUnique().HasDatabaseName("UX_ColourFamilies_Name");
        builder.HasIndex(family => family.Slug).IsUnique().HasDatabaseName("UX_ColourFamilies_Slug");
    }
}

/// <summary><c>catalog.Sizes</c> (doc 08 §3.2).</summary>
internal sealed class SizeConfiguration : IEntityTypeConfiguration<Size>
{
    public void Configure(EntityTypeBuilder<Size> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Sizes", AppDbContext.CatalogSchema);

        builder.HasKey(size => size.Id);

        builder.Property(size => size.ScaleName).HasMaxLength(40).IsRequired();
        builder.Property(size => size.Label).HasMaxLength(20).IsRequired();

        builder
            .HasIndex(size => new { size.ScaleName, size.Label })
            .IsUnique()
            .HasDatabaseName("UX_Sizes_Scale_Label");

        builder
            .HasIndex(size => new { size.ScaleName, size.SortOrder })
            .HasDatabaseName("IX_Sizes_Scale_Sort");

        // The size filter (PLP-02) selects by label across scales, so the label leads its own index.
        builder.HasIndex(size => size.Label).HasDatabaseName("IX_Sizes_Label");
    }
}

/// <summary><c>catalog.SizeGuides</c> (doc 08 §3.2) [A] [SD] [RV].</summary>
internal sealed class SizeGuideConfiguration : IEntityTypeConfiguration<SizeGuide>
{
    public void Configure(EntityTypeBuilder<SizeGuide> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "SizeGuides",
            AppDbContext.CatalogSchema,
            // Doc 08 §1.4 (DB15): JSON is nvarchar(max) guarded by ISJSON, never the native json type.
            table => table.HasCheckConstraint("CK_SizeGuides_TableJson", "ISJSON([TableJson]) = 1"));

        builder.HasKey(guide => guide.Id);

        builder.Property(guide => guide.Name).HasMaxLength(100).IsRequired();
        builder.Property(guide => guide.IntroHtml).HasColumnType("nvarchar(max)");
        builder.Property(guide => guide.TableJson).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(guide => guide.MeasuringTipsHtml).HasColumnType("nvarchar(max)");

        builder.ApplyAuditColumns();
        builder.ApplySoftDelete();
        builder.ApplyRowVersion(guide => guide.RowVersion);

        builder
            .HasOne(guide => guide.ImageAsset)
            .WithMany()
            .HasForeignKey(guide => guide.ImageAssetId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(guide => guide.ImageAssetId).HasDatabaseName("IX_SizeGuides_ImageAssetId");
    }
}
