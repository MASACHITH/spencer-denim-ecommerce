using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Catalog;

/// <summary><c>catalog.ProductStats</c> (doc 08 §3.2): 1:1 derived data, no rowversion.</summary>
internal sealed class ProductStatsConfiguration : IEntityTypeConfiguration<ProductStats>
{
    public void Configure(EntityTypeBuilder<ProductStats> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "ProductStats",
            AppDbContext.CatalogSchema,
            table =>
            {
                table.HasCheckConstraint("CK_ProductStats_PriceRange", "[MinListPrice] <= [MaxListPrice] AND [MinSalePrice] <= [MaxSalePrice]");
                table.HasCheckConstraint("CK_ProductStats_Discount", "[MaxDiscountPercent] BETWEEN 0 AND 100");
                table.HasCheckConstraint("CK_ProductStats_Rating", "[RatingAverage] IS NULL OR [RatingAverage] BETWEEN 0 AND 5");
                table.HasCheckConstraint("CK_ProductStats_Counts", "[RatingCount] >= 0 AND [SoldQty30d] >= 0 AND [SoldQtyTotal] >= 0");
            });

        builder.HasKey(stats => stats.ProductId);

        builder.Property(stats => stats.ProductId).ValueGeneratedNever();
        builder.Property(stats => stats.MinListPrice).HasPrecision(18, 2).IsRequired();
        builder.Property(stats => stats.MaxListPrice).HasPrecision(18, 2).IsRequired();
        builder.Property(stats => stats.MinSalePrice).HasPrecision(18, 2).IsRequired();
        builder.Property(stats => stats.MaxSalePrice).HasPrecision(18, 2).IsRequired();
        builder.Property(stats => stats.MaxOriginalPrice).HasPrecision(18, 2).IsRequired();
        builder.Property(stats => stats.MaxDiscountPercent).HasPrecision(5, 2).IsRequired();
        builder.Property(stats => stats.HasDiscount).HasDefaultValue(false).IsRequired();
        builder.Property(stats => stats.RatingAverage).HasPrecision(3, 2);
        builder.Property(stats => stats.RatingCount).HasDefaultValue(0).IsRequired();
        builder.Property(stats => stats.SoldQty30d).HasDefaultValue(0).IsRequired();
        builder.Property(stats => stats.SoldQtyTotal).HasDefaultValue(0).IsRequired();
        builder.Property(stats => stats.PricingRefreshedAtUtc).HasDefaultValueSql("sysutcdatetime()").IsRequired();
        builder.Property(stats => stats.StatsRefreshedAtUtc).HasDefaultValueSql("sysutcdatetime()").IsRequired();

        // Doc 08 §3.2: covers the price filter and the rating, discount and best-selling sorts.
        builder
            .HasIndex(stats => stats.MinSalePrice)
            .IncludeProperties(stats => new
            {
                stats.HasDiscount,
                stats.MaxDiscountPercent,
                stats.RatingAverage,
                stats.SoldQty30d,
            })
            .HasDatabaseName("IX_ProductStats_MinSalePrice");

        builder
            .HasOne(stats => stats.Product)
            .WithOne(product => product.Stats)
            .HasForeignKey<ProductStats>(stats => stats.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary><c>catalog.ProductSearchIndex</c> (doc 08 §3.2, §7.6): 1:1 search text.</summary>
internal sealed class ProductSearchIndexConfiguration : IEntityTypeConfiguration<ProductSearchIndex>
{
    public void Configure(EntityTypeBuilder<ProductSearchIndex> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ProductSearchIndex", AppDbContext.CatalogSchema);

        builder.HasKey(index => index.ProductId);

        builder.Property(index => index.ProductId).ValueGeneratedNever();
        builder.Property(index => index.NameText).HasMaxLength(200).IsRequired();
        builder.Property(index => index.SearchText).HasMaxLength(2000).IsRequired();
        builder.Property(index => index.UpdatedAtUtc).HasDefaultValueSql("sysutcdatetime()").IsRequired();

        // The search scans SearchText with LIKE '%…%', which no index can seek. NameText carries the
        // prefix relevance bonus and the SRCH-01 suggestions, which are sargable.
        builder.HasIndex(index => index.NameText).HasDatabaseName("IX_ProductSearchIndex_NameText");

        builder
            .HasOne(index => index.Product)
            .WithOne(product => product.SearchIndex)
            .HasForeignKey<ProductSearchIndex>(index => index.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary><c>catalog.ProductCategories</c> (doc 08 §3.2).</summary>
internal sealed class ProductCategoryConfiguration : IEntityTypeConfiguration<ProductCategory>
{
    public void Configure(EntityTypeBuilder<ProductCategory> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ProductCategories", AppDbContext.CatalogSchema);

        // Doc 08 §7.2: (CategoryId, ProductId) is the clustered PK, so the category EXISTS predicate
        // is a seek on the primary key.
        builder.HasKey(link => new { link.CategoryId, link.ProductId });

        builder.HasIndex(link => link.ProductId).HasDatabaseName("IX_ProductCategories_ProductId");

        builder
            .HasOne(link => link.Category)
            .WithMany(category => category.ProductLinks)
            .HasForeignKey(link => link.CategoryId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(link => link.Product)
            .WithMany(product => product.CategoryLinks)
            .HasForeignKey(link => link.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary><c>catalog.ProductColours</c> (doc 08 §3.2).</summary>
internal sealed class ProductColourConfiguration : IEntityTypeConfiguration<ProductColour>
{
    public void Configure(EntityTypeBuilder<ProductColour> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "ProductColours",
            AppDbContext.CatalogSchema,
            table => table.HasCheckConstraint(
                "CK_ProductColours_SwatchHex",
                "[SwatchHex] IS NULL OR [SwatchHex] LIKE '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]'"));

        builder.HasKey(colour => colour.Id);

        builder.Property(colour => colour.Name).HasMaxLength(60).IsRequired();
        builder.Property(colour => colour.SwatchHex).HasColumnType("char(7)");

        builder
            .HasIndex(colour => new { colour.ProductId, colour.Name })
            .IsUnique()
            .HasDatabaseName("UX_ProductColours_Product_Name");

        // Doc 08 §3.2: the target of the composite FK from variants.
        builder
            .HasAlternateKey(colour => new { colour.Id, colour.ProductId })
            .HasName("UX_ProductColours_Id_ProductId");

        // Doc 08 §7.2: the colour filter is an EXISTS on (ColourFamilyId, ProductId).
        builder
            .HasIndex(colour => new { colour.ColourFamilyId, colour.ProductId })
            .HasDatabaseName("IX_ProductColours_ColourFamily");

        builder
            .HasOne(colour => colour.Product)
            .WithMany(product => product.Colours)
            .HasForeignKey(colour => colour.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(colour => colour.ColourFamily)
            .WithMany()
            .HasForeignKey(colour => colour.ColourFamilyId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(colour => colour.SwatchAsset)
            .WithMany()
            .HasForeignKey(colour => colour.SwatchAssetId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(colour => colour.SwatchAssetId).HasDatabaseName("IX_ProductColours_SwatchAssetId");
    }
}

/// <summary><c>catalog.ProductMedia</c> (doc 08 §3.2).</summary>
internal sealed class ProductMediaConfiguration : IEntityTypeConfiguration<ProductMedia>
{
    public void Configure(EntityTypeBuilder<ProductMedia> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "ProductMedia",
            AppDbContext.CatalogSchema,
            table => table.HasCheckConstraint("CK_ProductMedia_MediaRole", "[MediaRole] IN (1, 2, 3)"));

        builder.HasKey(media => media.Id);

        builder.Property(media => media.MediaRole).HasColumnType("tinyint").IsRequired();
        builder.Property(media => media.AltTextOverride).HasMaxLength(250);

        builder
            .HasIndex(media => new { media.ProductId, media.MediaAssetId, media.ProductColourId })
            .IsUnique()
            // HasFilter(null) removes the "IS NOT NULL" filter EF adds by convention for a nullable
            // column in a unique index. SQL Server treats NULLs as equal in a unique index, so the
            // whole-gallery rows (ProductColourId NULL) are covered too, which is what doc 08 asks for.
            .HasFilter(null)
            .HasDatabaseName("UX_ProductMedia_Product_Asset_Colour");

        builder
            .HasIndex(media => new { media.ProductId, media.ProductColourId, media.SortOrder })
            .HasDatabaseName("IX_ProductMedia_Product_Colour_Sort");

        builder
            .HasOne(media => media.Product)
            .WithMany(product => product.Media)
            .HasForeignKey(media => media.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(media => media.MediaAsset)
            .WithMany()
            .HasForeignKey(media => media.MediaAssetId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(media => media.ProductColour)
            .WithMany(colour => colour.Media)
            .HasForeignKey(media => media.ProductColourId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(media => media.MediaAssetId).HasDatabaseName("IX_ProductMedia_MediaAssetId");
        builder.HasIndex(media => media.ProductColourId).HasDatabaseName("IX_ProductMedia_ProductColourId");
    }
}

/// <summary><c>catalog.RelatedProducts</c> (doc 08 §3.2).</summary>
internal sealed class RelatedProductConfiguration : IEntityTypeConfiguration<RelatedProduct>
{
    public void Configure(EntityTypeBuilder<RelatedProduct> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "RelatedProducts",
            AppDbContext.CatalogSchema,
            table => table.HasCheckConstraint("CK_RelatedProducts_NotSelf", "[RelatedProductId] <> [ProductId]"));

        builder.HasKey(related => new { related.ProductId, related.RelatedProductId });

        builder.HasIndex(related => related.RelatedProductId).HasDatabaseName("IX_RelatedProducts_RelatedProductId");

        builder
            .HasOne(related => related.Product)
            .WithMany(product => product.RelatedProducts)
            .HasForeignKey(related => related.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(related => related.Related)
            .WithMany()
            .HasForeignKey(related => related.RelatedProductId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
