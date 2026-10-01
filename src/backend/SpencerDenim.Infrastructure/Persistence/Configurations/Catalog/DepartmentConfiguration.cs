using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Catalog;

/// <summary><c>catalog.Departments</c> (doc 08 §3.2) [A] [RV].</summary>
internal sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "Departments",
            AppDbContext.CatalogSchema,

            // W1-12: see CategoryConfiguration and CatalogSlugConstraint. A department slug is the first
            // segment of every listing URL in its department, so an unspellable one takes a third of the
            // shop with it.
            table => table.HasCheckConstraint("CK_Departments_Slug", CatalogSlugConstraint.For("Slug")));

        builder.HasKey(department => department.Id);

        builder.Property(department => department.Name).HasMaxLength(60).IsRequired();
        builder.Property(department => department.Slug).HasColumnType("varchar(60)").IsRequired();
        builder.Property(department => department.Description).HasMaxLength(1000);
        builder.Property(department => department.SortOrder).IsRequired();
        builder.Property(department => department.IsActive).HasDefaultValue(true).IsRequired();
        builder.Property(department => department.SeoTitle).HasMaxLength(160);
        builder.Property(department => department.SeoDescription).HasMaxLength(320);

        builder.ApplyAuditColumns();
        builder.ApplyRowVersion(department => department.RowVersion);

        builder.HasIndex(department => department.Name).IsUnique().HasDatabaseName("UX_Departments_Name");
        builder.HasIndex(department => department.Slug).IsUnique().HasDatabaseName("UX_Departments_Slug");

        builder
            .HasOne(department => department.HeroImageAsset)
            .WithMany()
            .HasForeignKey(department => department.HeroImageAssetId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasIndex(department => department.HeroImageAssetId)
            .HasDatabaseName("IX_Departments_HeroImageAssetId");
    }
}
