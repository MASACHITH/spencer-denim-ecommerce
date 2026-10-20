using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Catalog;

/// <summary>
/// <c>catalog.ProductReviews</c> (doc 08 §3.2; PDP-08, ADM-16; issue #189). [RV], no [A]: the moderation
/// columns are the review's own record of who decided what, and the audit log gets one row per moderation.
/// </summary>
internal sealed class ProductReviewConfiguration : IEntityTypeConfiguration<ProductReview>
{
    public void Configure(EntityTypeBuilder<ProductReview> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "ProductReviews",
            AppDbContext.CatalogSchema,
            table =>
            {
                table.HasCheckConstraint("CK_ProductReviews_Rating", "[Rating] BETWEEN 1 AND 5");
                table.HasCheckConstraint("CK_ProductReviews_Status", "[Status] IN (0, 1, 2)");

                // ISNULL because LEN(NULL) > 0 is UNKNOWN and a CHECK that is UNKNOWN passes (#190, D1).
                table.HasCheckConstraint("CK_ProductReviews_DisplayName", "ISNULL(LEN([DisplayName]), 0) > 0");

                // Doc 08 D4: the badge and the qualifying order go together, in both directions.
                table.HasCheckConstraint(
                    "CK_ProductReviews_Verified",
                    "([IsVerifiedBuyer] = 1 AND [OrderId] IS NOT NULL) OR ([IsVerifiedBuyer] = 0 AND [OrderId] IS NULL)");
            });

        builder.HasKey(review => review.Id);

        builder.Property(review => review.Rating).HasColumnType("tinyint").IsRequired();
        builder.Property(review => review.Title).HasMaxLength(ReviewRules.TitleMaxLength);
        builder.Property(review => review.Body).HasMaxLength(ReviewRules.BodyMaxLength);
        builder.Property(review => review.DisplayName).HasMaxLength(ReviewRules.DisplayNameMaxLength).IsRequired();
        builder.Property(review => review.IsVerifiedBuyer).HasDefaultValue(false).IsRequired();
        builder.Property(review => review.Status).HasColumnType("tinyint").IsRequired();
        builder.Property(review => review.ModerationNote).HasMaxLength(ReviewRules.ModerationNoteMaxLength);

        // No FK on ModeratedByUserId: doc 08 §1.5's convention for user columns, as OrderStatusHistory.ChangedByUserId.
        builder.Property(review => review.ModeratedByUserId);
        builder.Property(review => review.ModeratedAtUtc);
        builder.Property(review => review.CreatedAtUtc).HasDefaultValueSql("sysutcdatetime()").IsRequired();
        builder.Property(review => review.UpdatedAtUtc);
        builder.ApplyRowVersion(review => review.RowVersion);

        // Doc 08 §3.2: one review per customer per product. Also DB7 for the ProductId foreign key, and the
        // index the submission's own "have you reviewed this already" lookup seeks.
        builder
            .HasIndex(review => new { review.ProductId, review.CustomerUserId })
            .IsUnique()
            .HasDatabaseName("UX_ProductReviews_Product_Customer");

        // Doc 08 §3.2: a product's approved reviews, newest first (the product page), and the rating recompute's
        // COUNT/SUM over one product's approved rows, which Rating in the INCLUDE makes index-only.
        builder
            .HasIndex(review => new { review.ProductId, review.Status, review.CreatedAtUtc, review.Id })
            .IsDescending(false, false, true, true)
            .IncludeProperties(review => review.Rating)
            .HasDatabaseName("IX_ProductReviews_Product_Status_Created");

        // Doc 08 §3.2: the moderation queue by status, oldest pending first (doc 11 P12 step 4).
        builder
            .HasIndex(review => new { review.Status, review.CreatedAtUtc, review.Id })
            .HasDatabaseName("IX_ProductReviews_Status_Created");

        // DB7 for the customer foreign key, and the PDPA erasure's seek (doc 08 §3.1 step 2).
        builder
            .HasIndex(review => review.CustomerUserId)
            .HasDatabaseName("IX_ProductReviews_Customer");

        // DB7 for the order foreign key; most reviews have none.
        builder
            .HasIndex(review => review.OrderId)
            .HasFilter("[OrderId] IS NOT NULL")
            .HasDatabaseName("IX_ProductReviews_Order");

        builder
            .HasOne(review => review.Product)
            .WithMany()
            .HasForeignKey(review => review.ProductId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(review => review.CustomerUserId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne<SpencerDenim.Domain.Sales.Order>()
            .WithMany()
            .HasForeignKey(review => review.OrderId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
