using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Marketing;

/// <summary>
/// <c>marketing.SaleCampaigns</c> (doc 08 §3.6; ADM-14, DEPT-03, HOME-07) [A] [SD] [RV].
/// </summary>
internal sealed class SaleCampaignConfiguration : IEntityTypeConfiguration<SaleCampaign>
{
    public void Configure(EntityTypeBuilder<SaleCampaign> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SaleCampaigns", AppDbContext.MarketingSchema, ConfigureConstraints);

        builder.HasKey(campaign => campaign.Id);

        builder.Property(campaign => campaign.Name).HasMaxLength(SaleCampaign.MaxNameLength).IsRequired();
        builder.Property(campaign => campaign.Description).HasMaxLength(SaleCampaign.MaxDescriptionLength);

        builder.Property(campaign => campaign.DiscountType).HasColumnType("tinyint").IsRequired();
        builder.Property(campaign => campaign.DiscountValue).HasPrecision(18, 2).IsRequired();

        builder.Property(campaign => campaign.StartsAtUtc).IsRequired();

        // NOT NULL, unlike a promo code's end: doc 02 line 119 is "for a date range" and doc 08 §3.6
        // writes both bounds on one row with no "Y" in the Null column.
        builder.Property(campaign => campaign.EndsAtUtc).IsRequired();

        // No default: a campaign goes live in whatever state the staff member chose, and a default of 1
        // would make a half-filled insert from a support script discount the shop.
        builder.Property(campaign => campaign.IsActive).IsRequired();

        builder.ApplyAuditColumns();
        builder.ApplySoftDelete();
        builder.ApplyRowVersion(campaign => campaign.RowVersion);

        // Doc 08 §3.3: every media reference is NO ACTION, because the delete guard depends on an asset
        // that is still in use being impossible to remove.
        builder
            .HasOne(campaign => campaign.BannerAsset)
            .WithMany()
            .HasForeignKey(campaign => campaign.BannerAssetId)
            .OnDelete(DeleteBehavior.NoAction);

        // Doc 08 §1.6 (DB7): every FK column leads at least one index.
        builder.HasIndex(campaign => campaign.BannerAssetId).HasDatabaseName("IX_SaleCampaigns_BannerAssetId");

        // Doc 08 §3.6: "IX_SaleCampaigns_Window WHERE IsActive = 1 AND IsDeleted = 0".
        //
        // **The filter is literal and both halves of it are, because the pricing query's predicate has to
        // provably imply it at compile time (doc 08 §10.2 DB28).** `IsDeleted` comes from the model's
        // global soft-delete filter, which EF renders as a literal; `IsActive == true` is written as a
        // constant in `CampaignOfferReader`, not passed in as a variable. The dates stay out of the filter
        // — they move with every clock tick and a filtered index cannot hold a moving predicate — and are
        // the key columns instead, so the seek is a range scan over whatever is live now.
        builder
            .HasIndex(campaign => new { campaign.StartsAtUtc, campaign.EndsAtUtc })
            .HasFilter("[IsActive] = 1 AND [IsDeleted] = 0")
            .HasDatabaseName("IX_SaleCampaigns_Window");
    }

    private static void ConfigureConstraints(TableBuilder<SaleCampaign> table)
    {
        table.HasCheckConstraint(
            "CK_SaleCampaigns_DiscountType",
            $"[DiscountType] IN ({(byte)SaleDiscountType.Percent}, {(byte)SaleDiscountType.FixedAmount})");

        // Doc 08 §3.6: "CK Percent ≤ 90; Fixed > 0". One constraint over both columns, because the legal
        // range of the value *is* a function of the type — split in two it would be possible to store a
        // 90.00 that meant rupees and a 500.00 that meant per cent.
        //
        // The lower bound on the percentage is not in doc 08 and is here for the reason
        // `CK_PromoCodes_DiscountValue` gives: a campaign of 0% is a sale the shop is advertising that
        // discounts nothing, and a negative one is a price increase.
        table.HasCheckConstraint(
            "CK_SaleCampaigns_DiscountValue",
            $"([DiscountType] = {(byte)SaleDiscountType.Percent} "
            + $"AND [DiscountValue] > 0 AND [DiscountValue] <= {SaleCampaign.MaxPercentDiscount:0}) "
            + $"OR ([DiscountType] = {(byte)SaleDiscountType.FixedAmount} AND [DiscountValue] > 0)");

        // Doc 08 §3.6: "CK Ends > Starts". Both bounds are NOT NULL here, so unlike
        // `CK_PromoCodes_Window` there is no null case to spell out.
        table.HasCheckConstraint("CK_SaleCampaigns_Window", "[EndsAtUtc] > [StartsAtUtc]");
    }
}

/// <summary><c>marketing.SaleCampaignTargets</c> (doc 08 §3.6; ADM-14).</summary>
internal sealed class SaleCampaignTargetConfiguration : IEntityTypeConfiguration<SaleCampaignTarget>
{
    public void Configure(EntityTypeBuilder<SaleCampaignTarget> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SaleCampaignTargets", AppDbContext.MarketingSchema, ConfigureConstraints);

        builder.HasKey(target => target.Id);

        builder.Property(target => target.TargetType).HasColumnType("tinyint").IsRequired();

        // Doc 08 §3.6: "DF 1". A category target covers its subtree unless staff say otherwise, which is
        // what an admin who picks "Women › Dresses" for a sale almost always means.
        builder.Property(target => target.IncludeSubcategories).HasDefaultValue(true).IsRequired();

        // Doc 08 §1.6: a pure child row, and one of the cascades the allow-list permits. A campaign is
        // soft-deleted in practice (DB5), so this only runs on a purge.
        builder
            .HasOne(target => target.SaleCampaign)
            .WithMany(campaign => campaign.Targets)
            .HasForeignKey(target => target.SaleCampaignId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(target => target.Department)
            .WithMany()
            .HasForeignKey(target => target.DepartmentId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(target => target.Category)
            .WithMany()
            .HasForeignKey(target => target.CategoryId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(target => target.Product)
            .WithMany()
            .HasForeignKey(target => target.ProductId)
            .OnDelete(DeleteBehavior.NoAction);

        // **Every one of these four is named, and that is not tidiness.** EF's foreign-key index
        // convention creates an unnamed index per FK column and then treats a declared index over the
        // same property list as the same index — so a declared `IX_…_SaleCampaignId` and the convention's
        // own would collapse into one, and a *filtered* declared index would silently take the
        // convention's place and stop serving the FK. Naming all four keeps the pair explicit and the
        // migration diff readable; none of them carries a filter, for the same reason.
        builder.HasIndex(target => target.SaleCampaignId).HasDatabaseName("IX_SaleCampaignTargets_SaleCampaignId");
        builder.HasIndex(target => target.DepartmentId).HasDatabaseName("IX_SaleCampaignTargets_DepartmentId");
        builder.HasIndex(target => target.CategoryId).HasDatabaseName("IX_SaleCampaignTargets_CategoryId");
        builder.HasIndex(target => target.ProductId).HasDatabaseName("IX_SaleCampaignTargets_ProductId");
    }

    private static void ConfigureConstraints(TableBuilder<SaleCampaignTarget> table)
    {
        table.HasCheckConstraint(
            "CK_SaleCampaignTargets_TargetType",
            $"[TargetType] IN ({(byte)SaleCampaignTargetType.Department}, "
            + $"{(byte)SaleCampaignTargetType.Category}, {(byte)SaleCampaignTargetType.Product})");

        // Doc 08 §3.6, verbatim: "CK: exactly the matching one is NOT NULL". Written as one constraint per
        // target type rather than three independent null checks, because the pairing is the rule: a row
        // with TargetType = Category and a ProductId set would otherwise pass "exactly one is not null".
        table.HasCheckConstraint(
            "CK_SaleCampaignTargets_Target",
            $"([TargetType] = {(byte)SaleCampaignTargetType.Department} "
            + "AND [DepartmentId] IS NOT NULL AND [CategoryId] IS NULL AND [ProductId] IS NULL) "
            + $"OR ([TargetType] = {(byte)SaleCampaignTargetType.Category} "
            + "AND [CategoryId] IS NOT NULL AND [DepartmentId] IS NULL AND [ProductId] IS NULL) "
            + $"OR ([TargetType] = {(byte)SaleCampaignTargetType.Product} "
            + "AND [ProductId] IS NOT NULL AND [DepartmentId] IS NULL AND [CategoryId] IS NULL)");
    }
}
