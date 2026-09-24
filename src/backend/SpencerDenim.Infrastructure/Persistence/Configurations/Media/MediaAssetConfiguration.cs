using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Media;

/// <summary><c>media.MediaAssets</c> (doc 08 §3.3).</summary>
internal sealed class MediaAssetConfiguration : IEntityTypeConfiguration<MediaAsset>
{
    public void Configure(EntityTypeBuilder<MediaAsset> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "MediaAssets",
            AppDbContext.MediaSchema,
            table =>
            {
                table.HasCheckConstraint("CK_MediaAssets_MediaType", "[MediaType] IN (1, 2, 3)");
                table.HasCheckConstraint("CK_MediaAssets_Visibility", "[Visibility] IN (1, 2)");
                table.HasCheckConstraint("CK_MediaAssets_Status", "[Status] IN (0, 1, 2)");

                // Doc 08 §3.3: video ≤ 50 MB; a private file (bank slip) ≤ 5 MB.
                table.HasCheckConstraint(
                    "CK_MediaAssets_SizeBytes",
                    "[SizeBytes] > 0 AND ([MediaType] <> 2 OR [SizeBytes] <= 52428800) AND ([Visibility] <> 2 OR [SizeBytes] <= 5242880)");

                // Doc 08 §3.3: a video always carries a poster image.
                table.HasCheckConstraint(
                    "CK_MediaAssets_VideoHasPoster",
                    "[MediaType] <> 2 OR [PosterAssetId] IS NOT NULL");
            });

        builder.HasKey(asset => asset.Id);

        builder.Property(asset => asset.PublicId).IsRequired();
        builder.Property(asset => asset.MediaType).HasColumnType("tinyint").IsRequired();
        builder.Property(asset => asset.Visibility).HasColumnType("tinyint").IsRequired();
        builder.Property(asset => asset.Status).HasColumnType("tinyint").IsRequired();
        builder.Property(asset => asset.OriginalFileName).HasMaxLength(255).IsRequired();
        builder.Property(asset => asset.StorageKey).HasColumnType("varchar(400)").IsRequired();
        builder.Property(asset => asset.ContentType).HasColumnType("varchar(100)").IsRequired();
        builder.Property(asset => asset.SizeBytes).IsRequired();
        builder.Property(asset => asset.DurationSeconds).HasPrecision(9, 2);
        builder.Property(asset => asset.RenditionSetVersion).HasColumnType("tinyint");
        builder.Property(asset => asset.Checksum).HasColumnType("binary(32)").IsRequired();
        builder.Property(asset => asset.AltText).HasMaxLength(250);
        builder.Property(asset => asset.Title).HasMaxLength(150);

        builder.ApplyAuditColumns();
        builder.ApplySoftDelete();

        builder.HasIndex(asset => asset.PublicId).IsUnique().HasDatabaseName("UX_MediaAssets_PublicId");
        builder.HasIndex(asset => asset.StorageKey).IsUnique().HasDatabaseName("UX_MediaAssets_StorageKey");
        builder.HasIndex(asset => asset.Checksum).HasDatabaseName("IX_MediaAssets_Checksum");

        builder
            .HasIndex(asset => new { asset.Visibility, asset.MediaType, asset.CreatedAtUtc })
            .IsDescending(false, false, true)
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("IX_MediaAssets_Library");

        builder
            .HasOne(asset => asset.PosterAsset)
            .WithMany()
            .HasForeignKey(asset => asset.PosterAssetId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(asset => asset.PosterAssetId).HasDatabaseName("IX_MediaAssets_PosterAssetId");
    }
}
