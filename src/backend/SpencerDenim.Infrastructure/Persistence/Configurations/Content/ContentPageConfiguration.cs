using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Content;
using SpencerDenim.Infrastructure.Persistence.Configurations.Catalog;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Content;

/// <summary><c>content.ContentPages</c> (doc 08 §3.7, GLB-10) [A] [SD] [RV].</summary>
internal sealed class ContentPageConfiguration : IEntityTypeConfiguration<ContentPage>
{
    public void Configure(EntityTypeBuilder<ContentPage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "ContentPages",
            AppDbContext.ContentSchema,
            table =>
                // The same spelling rule as every other slug an admin can write
                // (CK_Departments_Slug, CK_Categories_Slug, CK_Products_Slug). The domain guard catches a
                // typo on the way in with a named field error; this catches a support script, a data fix
                // or a restore from a spreadsheet, none of which comes through a use case. Without it a
                // row could hold a value the storefront's route cannot match, which is a page nobody can
                // open and nothing to say why.
                table.HasCheckConstraint("CK_ContentPages_Slug", CatalogSlugConstraint.For("Slug")));

        builder.HasKey(page => page.Id);

        builder
            .Property(page => page.SystemKey)
            .HasColumnType($"varchar({ContentPage.SystemKeyMaxLength})");

        builder
            .Property(page => page.Slug)
            .HasColumnType($"varchar({ContentPage.SlugMaxLength})")
            .IsRequired();

        builder.Property(page => page.Title).HasMaxLength(ContentPage.TitleMaxLength).IsRequired();

        // nvarchar(max), as doc 08 §3.7 has it, and NOT NULL: an unwritten page holds the empty string
        // rather than null, so every reader has one shape to handle instead of two.
        builder
            .Property(page => page.BodyHtml)
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.Property(page => page.SeoTitle).HasMaxLength(ContentPage.SeoTitleMaxLength);
        builder.Property(page => page.SeoDescription).HasMaxLength(ContentPage.SeoDescriptionMaxLength);
        builder.Property(page => page.IsPublished).HasDefaultValue(false).IsRequired();
        builder.Property(page => page.PublishedAtUtc);

        builder.ApplyAuditColumns();
        builder.ApplySoftDelete();
        builder.ApplyRowVersion(page => page.RowVersion);

        // Doc 08 §3.7: "Slug … UX WHERE IsDeleted = 0". Filtered, so deleting a custom page releases its
        // slug for reuse.
        builder
            .HasIndex(page => page.Slug)
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_ContentPages_Slug");

        // Doc 08 §3.7: "SystemKey … Filtered UX". Filtered on both conditions, so there is exactly one
        // live `about` row and a NULL SystemKey (every custom page) is outside the index rather than
        // colliding with every other custom page — SQL Server treats NULLs as equal in a unique index,
        // so without the `IS NOT NULL` half the second custom page ever created would be refused.
        builder
            .HasIndex(page => page.SystemKey)
            .IsUnique()
            .HasFilter("[IsDeleted] = 0 AND [SystemKey] IS NOT NULL")
            .HasDatabaseName("UX_ContentPages_SystemKey");

        // The admin list's order, and the storefront footer's: the published pages, by title. Not one of
        // doc 08 §3.7's listed indexes — it lists none for this table beyond the two unique ones — and the
        // footer reads this predicate on every page of the site, which is the same argument
        // AnnouncementMessageConfiguration makes for its own.
        builder
            .HasIndex(page => new { page.IsPublished, page.Title })
            .HasDatabaseName("IX_ContentPages_Published_Title");
    }
}
