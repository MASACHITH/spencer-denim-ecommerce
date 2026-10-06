using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Catalog;

/// <summary><c>catalog.Products</c> (doc 08 §3.2) [A] [SD] [RV].</summary>
internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "Products",
            AppDbContext.CatalogSchema,
            table =>
            {
                table.HasCheckConstraint("CK_Products_BasePrice", "[BasePrice] > 0");

                // DB17: the "was" price is either absent or genuinely higher than the list price.
                table.HasCheckConstraint(
                    "CK_Products_CompareAtGtBase",
                    "[CompareAtPrice] IS NULL OR [CompareAtPrice] > [BasePrice]");

                // Doc 08 §3.2: only the four admin-assignable badges. Low Stock is computed, never stored.
                table.HasCheckConstraint(
                    "CK_Products_BadgeOverride",
                    "[BadgeOverride] IS NULL OR [BadgeOverride] IN (1, 2, 3, 4)");

                // The W1-02 follow-up, carried by W1-03 because W1-03 is what gives this column a write
                // path that is not the seeder. CatalogSlug guards the property setter, which protects every
                // write that goes through the application and nothing else — a support script, a data fix
                // or a restore from a spreadsheet can all put a value here that makes /p/{slug} answer 400,
                // and a product nobody can open is not a defect any screen shows. The expression is
                // CatalogSlug's own regular expression in SQL; see CatalogSlugConstraint for why the
                // collation matters.
                table.HasCheckConstraint("CK_Products_Slug", CatalogSlugConstraint.For("Slug"));
            });

        builder.HasKey(product => product.Id);

        builder.Property(product => product.ProductCode).HasColumnType("varchar(40)").IsRequired();
        builder.Property(product => product.Name).HasMaxLength(200).IsRequired();
        builder.Property(product => product.Slug).HasColumnType("varchar(220)").IsRequired();
        builder.Property(product => product.ShortDescription).HasMaxLength(500);
        builder.Property(product => product.Description).HasColumnType("nvarchar(max)");
        builder.Property(product => product.CareInstructions).HasMaxLength(1000);
        builder.Property(product => product.BasePrice).HasPrecision(18, 2).IsRequired();
        builder.Property(product => product.CompareAtPrice).HasPrecision(18, 2);
        builder.Property(product => product.BadgeOverride).HasColumnType("tinyint");
        builder.Property(product => product.IsPublished).HasDefaultValue(false).IsRequired();
        builder.Property(product => product.SeoTitle).HasMaxLength(160);
        builder.Property(product => product.SeoDescription).HasMaxLength(320);

        builder.ApplyAuditColumns();
        builder.ApplySoftDelete();
        builder.ApplyRowVersion(product => product.RowVersion);

        // Doc 08 §3.2: both natural keys are unique among live rows, so a soft-deleted product does
        // not keep its slug or code reserved for ever.
        builder
            .HasIndex(product => product.Slug)
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_Products_Slug");

        builder
            .HasIndex(product => product.ProductCode)
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_Products_ProductCode");

        // Doc 08 §7.2: the covering index behind every listing. The filter is a literal, so the
        // constant-only predicates in the read store can match it (DB28).
        builder
            .HasIndex(product => new { product.DepartmentId, product.PrimaryCategoryId })
            .IncludeProperties(product => new
            {
                product.AgeGroupId,
                product.MaterialId,
                product.FitId,
                product.OccasionId,
                product.FirstPublishedAtUtc,
                product.RecommendedRank,
                product.NewUntilUtc,
                product.BadgeOverride,
                product.Name,
                product.Slug,
                product.CardImageAssetId,
                product.HoverImageAssetId,
            })
            .HasFilter("[IsPublished] = 1 AND [IsDeleted] = 0")
            .HasDatabaseName("IX_Products_Listing");

        builder
            .HasIndex(product => product.NewUntilUtc)
            .HasFilter("[IsPublished] = 1 AND [IsDeleted] = 0")
            .HasDatabaseName("IX_Products_NewUntilUtc");

        builder
            .HasOne(product => product.Department)
            .WithMany(department => department.Products)
            .HasForeignKey(product => product.DepartmentId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(product => product.PrimaryCategory)
            .WithMany()
            .HasForeignKey(product => product.PrimaryCategoryId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(product => product.AgeGroup)
            .WithMany()
            .HasForeignKey(product => product.AgeGroupId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(product => product.Material)
            .WithMany()
            .HasForeignKey(product => product.MaterialId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(product => product.Fit)
            .WithMany()
            .HasForeignKey(product => product.FitId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(product => product.Occasion)
            .WithMany()
            .HasForeignKey(product => product.OccasionId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(product => product.SizeGuide)
            .WithMany()
            .HasForeignKey(product => product.SizeGuideId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(product => product.CardImageAsset)
            .WithMany()
            .HasForeignKey(product => product.CardImageAssetId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(product => product.HoverImageAsset)
            .WithMany()
            .HasForeignKey(product => product.HoverImageAssetId)
            .OnDelete(DeleteBehavior.NoAction);

        // Doc 08 §1.6 (DB7): every FK column leads at least one index. DepartmentId and
        // PrimaryCategoryId already lead IX_Products_Listing, but that index is filtered, so the
        // unfiltered convention indexes below still carry admin queries over unpublished products.
        builder.HasIndex(product => product.DepartmentId).HasDatabaseName("IX_Products_DepartmentId");
        builder.HasIndex(product => product.PrimaryCategoryId).HasDatabaseName("IX_Products_PrimaryCategoryId");
        builder.HasIndex(product => product.AgeGroupId).HasDatabaseName("IX_Products_AgeGroupId");
        builder.HasIndex(product => product.MaterialId).HasDatabaseName("IX_Products_MaterialId");
        builder.HasIndex(product => product.FitId).HasDatabaseName("IX_Products_FitId");
        builder.HasIndex(product => product.OccasionId).HasDatabaseName("IX_Products_OccasionId");
        builder.HasIndex(product => product.SizeGuideId).HasDatabaseName("IX_Products_SizeGuideId");
        builder.HasIndex(product => product.CardImageAssetId).HasDatabaseName("IX_Products_CardImageAssetId");
        builder.HasIndex(product => product.HoverImageAssetId).HasDatabaseName("IX_Products_HoverImageAssetId");
    }
}
