using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Sales;

/// <summary>
/// <c>sales.OrderLines</c> (doc 08 §3.5): an immutable snapshot line.
/// <para>
/// <b>No cascade from Orders</b>, although EF would default to one. Doc 08 §1.6 lists OrderLines among the
/// rows that are never deleted and does not name the table in the cascade allow-list; a cascade would also
/// be the one path by which a purge could silently take the accounting records with it.
/// </para>
/// </summary>
internal sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("OrderLines", AppDbContext.SalesSchema, ConfigureConstraints);

        builder.HasKey(line => line.Id);

        builder.Property(line => line.LineNumber).IsRequired();
        builder.Property(line => line.ProductName).HasMaxLength(200).IsRequired();
        builder.Property(line => line.ProductCode).HasColumnType("varchar(40)").IsRequired();
        builder.Property(line => line.Sku).HasColumnType("varchar(64)").IsRequired();
        builder.Property(line => line.ColourName).HasMaxLength(60).IsRequired();
        builder.Property(line => line.SizeLabel).HasMaxLength(20).IsRequired();
        builder.Property(line => line.ThumbnailStorageKey).HasColumnType("varchar(400)");
        builder.Property(line => line.Quantity).IsRequired();
        builder.Property(line => line.UnitOriginalPrice).IsRequired();
        builder.Property(line => line.UnitListPrice).IsRequired();
        builder.Property(line => line.UnitPrice).IsRequired();
        builder.Property(line => line.LineSubtotal).IsRequired();
        builder.Property(line => line.PromoDiscountAllocated).IsRequired();
        builder.Property(line => line.LineTotal).IsRequired();
        builder.Property(line => line.RefundedQuantity).HasDefaultValue((short)0).IsRequired();

        ConfigureIndexes(builder);
        ConfigureRelationships(builder);
    }

    private static void ConfigureConstraints(TableBuilder<OrderLine> table)
    {
        table.HasCheckConstraint("CK_OrderLines_Quantity", "[Quantity] > 0");

        // Doc 08 §3.5: UnitOriginalPrice >= UnitListPrice >= UnitPrice > 0. The chain is what makes the two
        // discount totals on the order meaningful — markdown is original minus list, campaign is list minus
        // sale — so a line where list exceeded original would make MarkdownDiscountTotal negative and still
        // satisfy CK_Orders_Totals.
        table.HasCheckConstraint(
            "CK_OrderLines_PriceLadder",
            "[UnitOriginalPrice] >= [UnitListPrice] AND [UnitListPrice] >= [UnitPrice] AND [UnitPrice] > 0");

        table.HasCheckConstraint("CK_OrderLines_LineSubtotal", "[LineSubtotal] = [UnitPrice] * [Quantity]");
        table.HasCheckConstraint("CK_OrderLines_PromoDiscountAllocated", "[PromoDiscountAllocated] >= 0");

        // Doc 08 §3.5 (DB17): the equality *and* the floor. The floor is the part that matters — a promo
        // allocation larger than the line would otherwise make a line the shop pays for.
        table.HasCheckConstraint(
            "CK_OrderLines_LineTotal",
            "[LineTotal] = [LineSubtotal] - [PromoDiscountAllocated] AND [LineTotal] >= 0");

        // Doc 08 §3.5: RefundedQuantity <= Quantity. The lower bound is not in doc 08's note but follows
        // from DF 0: a negative refunded quantity would mean goods were un-refunded.
        table.HasCheckConstraint(
            "CK_OrderLines_RefundedQuantity",
            "[RefundedQuantity] >= 0 AND [RefundedQuantity] <= [Quantity]");
    }

    private static void ConfigureIndexes(EntityTypeBuilder<OrderLine> builder)
    {
        // Doc 08 §3.5. Unique on (OrderId, LineNumber), so the order of an order's lines is a fact and not a
        // sort of whatever came back; the INCLUDE list is everything a confirmation or an invoice reads, so
        // rendering an order touches the lines table once.
        builder
            .HasIndex(line => new { line.OrderId, line.LineNumber })
            .IsUnique()
            .IncludeProperties(line => new
            {
                line.ProductId,
                line.VariantId,
                line.DepartmentId,
                line.CategoryId,
                line.Quantity,
                line.LineTotal,
            })
            .HasDatabaseName("UX_OrderLines_Order_Line");

        // "What has this variant sold" — and the stock deduct/release per line (§5.5, §5.6).
        builder
            .HasIndex(line => line.VariantId)
            .IncludeProperties(line => new { line.OrderId, line.Quantity })
            .HasDatabaseName("IX_OrderLines_Variant");

        builder
            .HasIndex(line => line.ProductId)
            .IncludeProperties(line => new { line.OrderId, line.Quantity, line.LineTotal })
            .HasDatabaseName("IX_OrderLines_Product");

        // Doc 08 §1.6 (DB7): the remaining FK columns, plus SaleCampaignId whose constraint is deferred
        // until marketing.SaleCampaigns exists (ADM-14).
        builder.HasIndex(line => line.DepartmentId).HasDatabaseName("IX_OrderLines_DepartmentId");
        builder.HasIndex(line => line.CategoryId).HasDatabaseName("IX_OrderLines_CategoryId");
        builder.HasIndex(line => line.SaleCampaignId).HasDatabaseName("IX_OrderLines_SaleCampaignId");
    }

    private static void ConfigureRelationships(EntityTypeBuilder<OrderLine> builder)
    {
        builder
            .HasOne(line => line.Order)
            .WithMany(order => order.Lines)
            .HasForeignKey(line => line.OrderId)
            .OnDelete(DeleteBehavior.NoAction);

        // NO ACTION on all four catalogue FKs. A sold product is soft-deleted (doc 08 §1.5), so these
        // constraints only ever bite on a deliberate purge — which is precisely when a sale must not vanish.
        builder
            .HasOne(line => line.Product)
            .WithMany()
            .HasForeignKey(line => line.ProductId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(line => line.Variant)
            .WithMany()
            .HasForeignKey(line => line.VariantId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(line => line.Department)
            .WithMany()
            .HasForeignKey(line => line.DepartmentId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(line => line.Category)
            .WithMany()
            .HasForeignKey(line => line.CategoryId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
