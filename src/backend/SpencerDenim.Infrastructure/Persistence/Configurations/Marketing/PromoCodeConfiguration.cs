using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Marketing;

/// <summary>
/// <c>marketing.PromoCodes</c> (doc 08 §3.6; ADM-13, CART-01) [A] [SD] [RV].
/// <para>
/// <b>The limits are enforced by statements, not by these constraints, and doc 08 §3.6 (DB8) says so in
/// as many words:</b> <c>UsageCount</c> carries <c>CK UsageCount &gt;= 0</c> <i>only</i>, because the
/// total limit is applied by the conditional <c>UPDATE … WHERE UsageCount &lt; TotalUsageLimit</c> at
/// placement. A check constraint of <c>UsageCount &lt;= TotalUsageLimit</c> would make the DB8 row
/// "Expired → Paid: +1 unconditionally, ignoring TotalUsageLimit" impossible to write — a customer who
/// has already paid could not have their order accepted.
/// </para>
/// </summary>
internal sealed class PromoCodeConfiguration : IEntityTypeConfiguration<PromoCode>
{
    public void Configure(EntityTypeBuilder<PromoCode> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PromoCodes", AppDbContext.MarketingSchema, ConfigureConstraints);

        builder.HasKey(code => code.Id);

        // varchar(40), the same width and type as the snapshot the order keeps
        // (`sales.Orders.PromoCodeText`), so a code can never be stored in one and not fit the other.
        builder
            .Property(code => code.Code)
            .HasColumnType($"varchar({PromoCode.MaxCodeLength})")
            .IsRequired();

        builder.Property(code => code.Description).HasMaxLength(PromoCode.MaxDescriptionLength);

        builder.Property(code => code.DiscountType).HasColumnType("tinyint").IsRequired();

        builder.Property(code => code.DiscountValue).HasPrecision(18, 2);
        builder.Property(code => code.MaxDiscountAmount).HasPrecision(18, 2);
        builder.Property(code => code.MinSpendAmount).HasPrecision(18, 2);

        builder.Property(code => code.ExcludeSaleItems).HasDefaultValue(false).IsRequired();

        builder.Property(code => code.StartsAtUtc).IsRequired();
        builder.Property(code => code.EndsAtUtc);

        builder.Property(code => code.TotalUsageLimit);
        builder.Property(code => code.PerCustomerLimit);

        builder.Property(code => code.UsageCount).HasDefaultValue(0).IsRequired();

        // No default: a code is created in whatever state the staff member chose on the form, and a
        // default of 1 would make a half-filled insert from a script live on the shop.
        builder.Property(code => code.IsActive).IsRequired();

        builder.ApplyAuditColumns();
        builder.ApplySoftDelete();
        builder.ApplyRowVersion(code => code.RowVersion);

        // Doc 08 §3.6: "UX_PromoCodes_Code WHERE IsDeleted = 0". Filtered, so removing a code releases
        // the string for reuse — which matters here more than for a slug, because a marketing campaign
        // may well reissue last season's word.
        builder
            .HasIndex(code => code.Code)
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_PromoCodes_Code");

        // Doc 08 §3.6: "IX (IsActive, StartsAtUtc, EndsAtUtc)". Unfiltered, as the document has it: the
        // admin list reads inactive and ended codes too, and a filtered index could not serve it.
        builder
            .HasIndex(code => new { code.IsActive, code.StartsAtUtc, code.EndsAtUtc })
            .HasDatabaseName("IX_PromoCodes_Active");
    }

    private static void ConfigureConstraints(TableBuilder<PromoCode> table)
    {
        table.HasCheckConstraint(
            "CK_PromoCodes_DiscountType",
            $"[DiscountType] IN ({(byte)PromoDiscountType.Percent}, "
            + $"{(byte)PromoDiscountType.FixedAmount}, {(byte)PromoDiscountType.FreeDelivery})");

        // Doc 08 §3.6, verbatim: "Percent 0 < v ≤ 100; Fixed > 0; FreeDelivery NULL". One constraint over
        // two columns, because the legal range of the value *is* a function of the type — split in two it
        // would be possible to store a free-delivery code with a percentage on it.
        table.HasCheckConstraint(
            "CK_PromoCodes_DiscountValue",
            $"([DiscountType] = {(byte)PromoDiscountType.Percent} "
            + "AND [DiscountValue] > 0 AND [DiscountValue] <= 100) "
            + $"OR ([DiscountType] = {(byte)PromoDiscountType.FixedAmount} AND [DiscountValue] > 0) "
            + $"OR ([DiscountType] = {(byte)PromoDiscountType.FreeDelivery} AND [DiscountValue] IS NULL)");

        // Doc 08 §3.6: "CK Ends > Starts". An optional end, so the null case is allowed explicitly rather
        // than left to the three-valued logic of `>` — an unchecked NULL comparison is UNKNOWN, which
        // SQL Server accepts, and relying on that is how a constraint comes to mean something else.
        table.HasCheckConstraint(
            "CK_PromoCodes_Window",
            "[EndsAtUtc] IS NULL OR [EndsAtUtc] > [StartsAtUtc]");

        table.HasCheckConstraint(
            "CK_PromoCodes_TotalUsageLimit",
            "[TotalUsageLimit] IS NULL OR [TotalUsageLimit] >= 1");

        table.HasCheckConstraint(
            "CK_PromoCodes_PerCustomerLimit",
            "[PerCustomerLimit] IS NULL OR [PerCustomerLimit] >= 1");

        // Doc 08 §3.6 (DB8): this and nothing more. See the class remarks.
        table.HasCheckConstraint("CK_PromoCodes_UsageCount", "[UsageCount] >= 0");

        // **These two are not in doc 08 §3.6**, which gives no range for either column, and they are here
        // rather than only in the validator for the reason every other check constraint on this schema
        // exists: a support script, a data fix or a restore does not come through a use case. A cap of
        // 0.00 would silently make every percentage discount worth nothing while the admin list still
        // said "10%", and a minimum spend of 0.00 is a threshold that means "no threshold" spelled a
        // second way — two readings of the same row. Both are strict inequalities so NULL stays the one
        // way to say "not set".
        table.HasCheckConstraint(
            "CK_PromoCodes_MaxDiscountAmount",
            "[MaxDiscountAmount] IS NULL OR [MaxDiscountAmount] > 0");

        table.HasCheckConstraint(
            "CK_PromoCodes_MinSpendAmount",
            "[MinSpendAmount] IS NULL OR [MinSpendAmount] > 0");
    }
}
