using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Catalog;

/// <summary><c>catalog.Categories</c> (doc 08 §3.2) [A] [SD] [RV].</summary>
internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "Categories",
            AppDbContext.CatalogSchema,
            table =>
            {
                table.HasCheckConstraint("CK_Categories_Depth", $"[Depth] BETWEEN 1 AND {Category.MaxDepth}");
                table.HasCheckConstraint(
                    "CK_Categories_MegaMenuColumn",
                    "[MegaMenuColumn] IS NULL OR [MegaMenuColumn] BETWEEN 1 AND 4");
                table.HasCheckConstraint("CK_Categories_NotOwnParent", "[ParentCategoryId] IS NULL OR [ParentCategoryId] <> [Id]");
            });

        builder.HasKey(category => category.Id);

        builder.Property(category => category.Path).HasColumnType("varchar(200)").IsRequired();
        builder.Property(category => category.Depth).HasColumnType("tinyint").HasDefaultValue((byte)1).IsRequired();
        builder.Property(category => category.Name).HasMaxLength(80).IsRequired();
        builder.Property(category => category.Slug).HasColumnType("varchar(100)").IsRequired();
        builder.Property(category => category.Description).HasMaxLength(2000);
        builder.Property(category => category.BannerLinkUrl).HasMaxLength(500);
        builder.Property(category => category.SortOrder).IsRequired();
        builder.Property(category => category.ShowInMegaMenu).HasDefaultValue(true).IsRequired();
        builder.Property(category => category.MegaMenuColumn).HasColumnType("tinyint");
        builder.Property(category => category.IsActive).HasDefaultValue(true).IsRequired();
        builder.Property(category => category.SeoTitle).HasMaxLength(160);
        builder.Property(category => category.SeoDescription).HasMaxLength(320);

        builder.ApplyAuditColumns();
        builder.ApplySoftDelete();
        builder.ApplyRowVersion(category => category.RowVersion);

        builder
            .HasIndex(category => new { category.DepartmentId, category.ParentCategoryId, category.Slug })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_Categories_Dept_Parent_Slug");

        builder.HasIndex(category => category.Path).HasDatabaseName("IX_Categories_Path");

        builder
            .HasIndex(category => new { category.DepartmentId, category.ParentCategoryId, category.SortOrder })
            .IncludeProperties(category => new
            {
                category.Name,
                category.Slug,
                category.IsActive,
                category.ShowInMegaMenu,
            })
            .HasDatabaseName("IX_Categories_Dept_Parent_Sort");

        builder
            .HasOne(category => category.Department)
            .WithMany(department => department.Categories)
            .HasForeignKey(category => category.DepartmentId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(category => category.ParentCategory)
            .WithMany(category => category.ChildCategories)
            .HasForeignKey(category => category.ParentCategoryId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(category => category.AgeGroup)
            .WithMany()
            .HasForeignKey(category => category.AgeGroupId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(category => category.SizeGuide)
            .WithMany()
            .HasForeignKey(category => category.SizeGuideId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(category => category.ImageAsset)
            .WithMany()
            .HasForeignKey(category => category.ImageAssetId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(category => category.BannerAsset)
            .WithMany()
            .HasForeignKey(category => category.BannerAssetId)
            .OnDelete(DeleteBehavior.NoAction);

        // Doc 08 §1.6 (DB7): every FK column leads at least one index.
        builder.HasIndex(category => category.ParentCategoryId).HasDatabaseName("IX_Categories_ParentCategoryId");
        builder.HasIndex(category => category.AgeGroupId).HasDatabaseName("IX_Categories_AgeGroupId");
        builder.HasIndex(category => category.SizeGuideId).HasDatabaseName("IX_Categories_SizeGuideId");
        builder.HasIndex(category => category.ImageAssetId).HasDatabaseName("IX_Categories_ImageAssetId");
        builder.HasIndex(category => category.BannerAssetId).HasDatabaseName("IX_Categories_BannerAssetId");
    }
}
