using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Catalog;

/// <summary><c>catalog.ProductVariants</c> (doc 08 §3.2) [A] [RV].</summary>
internal sealed class ProductVariantConfiguration : IEntityTypeConfiguration<ProductVariant>
{
    public void Configure(EntityTypeBuilder<ProductVariant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "ProductVariants",
            AppDbContext.CatalogSchema,
            table =>
            {
                table.HasCheckConstraint("CK_ProductVariants_PriceOverride", "[PriceOverride] IS NULL OR [PriceOverride] > 0");
                table.HasCheckConstraint(
                    "CK_ProductVariants_CompareAtOverride",
                    "[CompareAtPriceOverride] IS NULL OR [CompareAtPriceOverride] > 0");

                // DB17. The cross-table case (an override against the product's own prices) cannot be a
                // check constraint; it is validated on save, and pricing uses max(original, list).
                table.HasCheckConstraint(
                    "CK_ProductVariants_CompareAtGtPrice",
                    "[CompareAtPriceOverride] IS NULL OR [PriceOverride] IS NULL OR [CompareAtPriceOverride] > [PriceOverride]");
            });

        builder.HasKey(variant => variant.Id);

        builder.Property(variant => variant.Sku).HasColumnType("varchar(64)").IsRequired();
        builder.Property(variant => variant.Barcode).HasColumnType("varchar(32)");
        builder.Property(variant => variant.PriceOverride).HasPrecision(18, 2);
        builder.Property(variant => variant.CompareAtPriceOverride).HasPrecision(18, 2);
        builder.Property(variant => variant.IsActive).HasDefaultValue(true).IsRequired();

        builder.ApplyAuditColumns();
        builder.ApplyRowVersion(variant => variant.RowVersion);

        builder.HasIndex(variant => variant.Sku).IsUnique().HasDatabaseName("UX_ProductVariants_Sku");

        builder
            .HasIndex(variant => variant.Barcode)
            .IsUnique()
            .HasFilter("[Barcode] IS NOT NULL")
            .HasDatabaseName("UX_ProductVariants_Barcode");

        builder
            .HasIndex(variant => new { variant.ProductColourId, variant.SizeId })
            .IsUnique()
            .HasDatabaseName("UX_ProductVariants_Colour_Size");

        // Doc 08 §7.2: the size filter is an EXISTS on (SizeId, ProductId).
        builder
            .HasIndex(variant => new { variant.SizeId, variant.ProductId })
            .IncludeProperties(variant => variant.IsActive)
            .HasDatabaseName("IX_ProductVariants_Size");

        // Doc 08 §7.2: availability joins StockLevels from the product's active variants.
        builder
            .HasIndex(variant => variant.ProductId)
            .IncludeProperties(variant => new { variant.IsActive, variant.SizeId })
            .HasDatabaseName("IX_ProductVariants_Product");

        builder
            .HasOne(variant => variant.Product)
            .WithMany(product => product.Variants)
            .HasForeignKey(variant => variant.ProductId)
            .OnDelete(DeleteBehavior.NoAction);

        // Doc 08 §3.2 and §10.1: a composite FK against the (Id, ProductId) alternate key of
        // ProductColours, so a variant can never reference another product's colourway.
        builder
            .HasOne(variant => variant.ProductColour)
            .WithMany(colour => colour.Variants)
            .HasForeignKey(variant => new { variant.ProductColourId, variant.ProductId })
            .HasPrincipalKey(colour => new { colour.Id, colour.ProductId })
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(variant => variant.Size)
            .WithMany()
            .HasForeignKey(variant => variant.SizeId)
            .OnDelete(DeleteBehavior.NoAction);

        // No separate index on ProductColourId: EF creates IX_ProductVariants_ProductColourId_ProductId
        // for the composite FK above, and ProductColourId leads it (doc 08 §1.6, DB7).
    }
}
