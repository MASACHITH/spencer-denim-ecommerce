using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Content;

/// <summary><c>content.MegaMenuPromoCards</c> (doc 08 §3.7, GLB-05; #215) [A] [RV].</summary>
internal sealed class MegaMenuPromoCardConfiguration : IEntityTypeConfiguration<MegaMenuPromoCard>
{
    public void Configure(EntityTypeBuilder<MegaMenuPromoCard> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "MegaMenuPromoCards",
            AppDbContext.ContentSchema,
            table =>
            {
                // A card on the shop always has its picture: the storefront draws nothing in its place.
                // Switched off it may wait for one, which is how the seeder offers today's words.
                table.HasCheckConstraint(
                    "CK_MegaMenuPromoCards_ActiveImage",
                    "[IsActive] = 0 OR [ImageAssetId] IS NOT NULL");

                // The same rule ContentFieldRules.Schedule applies, so a script cannot store a window that
                // is never open.
                table.HasCheckConstraint(
                    "CK_MegaMenuPromoCards_Schedule",
                    "[StartsAtUtc] IS NULL OR [EndsAtUtc] IS NULL OR [EndsAtUtc] > [StartsAtUtc]");

                // A blank heading or link is a card that says nothing or goes nowhere.
                table.HasCheckConstraint("CK_MegaMenuPromoCards_Title", "LEN([Title]) > 0");
                table.HasCheckConstraint("CK_MegaMenuPromoCards_ButtonLabel", "LEN([ButtonLabel]) > 0");
                table.HasCheckConstraint("CK_MegaMenuPromoCards_LinkUrl", "LEN([LinkUrl]) > 0");
            });

        builder.HasKey(card => card.Id);

        builder.Property(card => card.Title).HasMaxLength(MegaMenuPromoCardLimits.TitleMaxLength).IsRequired();
        builder.Property(card => card.Subtitle).HasMaxLength(MegaMenuPromoCardLimits.SubtitleMaxLength);
        builder.Property(card => card.ButtonLabel).HasMaxLength(ContentFieldLimits.ButtonLabelMaxLength).IsRequired();
        builder.Property(card => card.LinkUrl).HasMaxLength(ContentFieldLimits.LinkUrlMaxLength).IsRequired();
        builder.Property(card => card.SortOrder).IsRequired();
        builder.Property(card => card.IsActive).IsRequired();
        builder.Property(card => card.StartsAtUtc);
        builder.Property(card => card.EndsAtUtc);

        builder.ApplyAuditColumns();
        builder.ApplyRowVersion(card => card.RowVersion);

        // A department is never hard-deleted (doc 08 §1.6), so NO ACTION costs nothing and keeps a card from
        // disappearing as a side effect.
        builder
            .HasOne(card => card.Department)
            .WithMany()
            .HasForeignKey(card => card.DepartmentId)
            .OnDelete(DeleteBehavior.NoAction);

        // NO ACTION, as every media reference: the delete guard of doc 08 §3.3 depends on it.
        builder
            .HasOne(card => card.ImageAsset)
            .WithMany()
            .HasForeignKey(card => card.ImageAssetId)
            .OnDelete(DeleteBehavior.NoAction);

        // The storefront's read and the editor's list: a department's cards in menu order. It also leads with
        // the foreign key, which doc 08 §1.6 (DB7) asks of every FK column.
        builder
            .HasIndex(card => new { card.DepartmentId, card.SortOrder })
            .HasDatabaseName("IX_MegaMenuPromoCards_Department_SortOrder");

        builder.HasIndex(card => card.ImageAssetId).HasDatabaseName("IX_MegaMenuPromoCards_ImageAssetId");
    }
}
