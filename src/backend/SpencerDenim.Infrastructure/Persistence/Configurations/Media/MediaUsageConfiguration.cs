using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Media;

/// <summary><c>media.MediaUsages</c> (doc 08 §3.3): the "where used" list and the delete guard.</summary>
internal sealed class MediaUsageConfiguration : IEntityTypeConfiguration<MediaUsage>
{
    public void Configure(EntityTypeBuilder<MediaUsage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MediaUsages", AppDbContext.MediaSchema);

        builder.HasKey(usage => usage.Id);

        builder.Property(usage => usage.OwnerType).HasColumnType("varchar(40)").IsRequired();
        builder.Property(usage => usage.OwnerId).HasColumnType("varchar(100)").IsRequired();
        builder.Property(usage => usage.UsageRole).HasColumnType("varchar(40)").IsRequired();
        builder.Property(usage => usage.CreatedAtUtc).HasDefaultValueSql("sysutcdatetime()").IsRequired();

        builder
            .HasIndex(usage => new { usage.MediaAssetId, usage.OwnerType, usage.OwnerId, usage.UsageRole })
            .IsUnique()
            .HasDatabaseName("UX_MediaUsages_Asset_Owner_Role");

        builder
            .HasIndex(usage => new { usage.OwnerType, usage.OwnerId })
            .HasDatabaseName("IX_MediaUsages_Owner");

        builder
            .HasOne(usage => usage.MediaAsset)
            .WithMany()
            .HasForeignKey(usage => usage.MediaAssetId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
