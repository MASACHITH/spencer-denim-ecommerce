using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Content;

/// <summary><c>content.HomeSectionItems</c> (doc 08 §3.7) [A].</summary>
internal sealed class HomeSectionItemConfiguration : IEntityTypeConfiguration<HomeSectionItem>
{
    public void Configure(EntityTypeBuilder<HomeSectionItem> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "HomeSectionItems",
            AppDbContext.ContentSchema,
            // Doc 08 §3.7's static-fallback rule: a hero video always has a still to fall back to,
            // for a phone, a data-saver and prefers-reduced-motion (HOME-01, docs/04 §4).
            table => table.HasCheckConstraint(
                "CK_HomeSectionItems_VideoFallback",
                "[VideoAssetId] IS NULL OR [ImageAssetId] IS NOT NULL OR [MobileImageAssetId] IS NOT NULL"));

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Title).HasMaxLength(150);
        builder.Property(item => item.Subtitle).HasMaxLength(300);
        builder.Property(item => item.Body).HasMaxLength(1000);
        builder.Property(item => item.ButtonLabel).HasMaxLength(40);
        builder.Property(item => item.LinkUrl).HasMaxLength(500);
        builder.Property(item => item.IconKey).HasColumnType("varchar(40)");
        builder.Property(item => item.SortOrder).IsRequired();
        builder.Property(item => item.IsEnabled).HasDefaultValue(true).IsRequired();
        builder.Property(item => item.StartsAtUtc);
        builder.Property(item => item.EndsAtUtc);

        builder.ApplyAuditColumns();

        // Doc 08 §1.6: a pure child row, and the only cascade this schema is permitted.
        builder
            .HasOne(item => item.HomeSection)
            .WithMany(section => section.Items)
            .HasForeignKey(item => item.HomeSectionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasIndex(item => new { item.HomeSectionId, item.SortOrder })
            .HasDatabaseName("IX_HomeSectionItems_Section_SortOrder");

        // Every media reference is NO ACTION: the delete guard of doc 08 §3.3 depends on an asset
        // that is still in use being impossible to remove.
        builder
            .HasOne(item => item.ImageAsset)
            .WithMany()
            .HasForeignKey(item => item.ImageAssetId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(item => item.MobileImageAsset)
            .WithMany()
            .HasForeignKey(item => item.MobileImageAssetId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(item => item.VideoAsset)
            .WithMany()
            .HasForeignKey(item => item.VideoAssetId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(item => item.Product)
            .WithMany()
            .HasForeignKey(item => item.ProductId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(item => item.Department)
            .WithMany()
            .HasForeignKey(item => item.DepartmentId)
            .OnDelete(DeleteBehavior.NoAction);

        // Doc 08 §1.6 (DB7): every FK column leads at least one index. EF creates one by convention
        // for each of the five above; these name them the way the rest of the schema names indexes.
        builder.HasIndex(item => item.ImageAssetId).HasDatabaseName("IX_HomeSectionItems_ImageAssetId");
        builder.HasIndex(item => item.MobileImageAssetId).HasDatabaseName("IX_HomeSectionItems_MobileImageAssetId");
        builder.HasIndex(item => item.VideoAssetId).HasDatabaseName("IX_HomeSectionItems_VideoAssetId");
        builder.HasIndex(item => item.ProductId).HasDatabaseName("IX_HomeSectionItems_ProductId");
        builder.HasIndex(item => item.DepartmentId).HasDatabaseName("IX_HomeSectionItems_DepartmentId");
    }
}
