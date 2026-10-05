using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Sales;

/// <summary><c>sales.Refunds</c> (doc 08 §3.5, ADM-09; permission <c>Orders.Refund</c>).</summary>
internal sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "Refunds",
            AppDbContext.SalesSchema,
            table =>
            {
                // Doc 08 §3.5: CK > 0. Zero is not a refund.
                table.HasCheckConstraint("CK_Refunds_Amount", "[Amount] > 0");

                table.HasCheckConstraint("CK_Refunds_Currency", $"[Currency] = '{SalesCurrency.Lkr}'");

                // Doc 08 §3.5: 1 Provider API, 2 Manual bank transfer, 3 Manual other.
                table.HasCheckConstraint("CK_Refunds_Method", "[Method] BETWEEN 1 AND 3");

                // Doc 08 §3.5: 0 Pending, 1 Completed, 2 Failed.
                table.HasCheckConstraint("CK_Refunds_Status", "[Status] BETWEEN 0 AND 2");

                // Not in doc 08's note; the same reasoning as CK_Refunds_Amount applied to the USD leg.
                table.HasCheckConstraint(
                    "CK_Refunds_ProviderAmount",
                    "[ProviderAmount] IS NULL OR [ProviderAmount] > 0");
            });

        builder.HasKey(refund => refund.Id);

        builder.Property(refund => refund.Amount).IsRequired();

        builder
            .Property(refund => refund.Currency)
            .HasColumnType($"char({SalesCurrency.CodeLength})")
            .HasDefaultValue(SalesCurrency.Lkr)
            .IsRequired();

        builder.Property(refund => refund.ProviderAmount);
        builder.Property(refund => refund.Method).HasColumnType("tinyint").IsRequired();
        builder.Property(refund => refund.ProviderRefundId).HasColumnType("varchar(100)");
        builder.Property(refund => refund.Status).HasColumnType("tinyint").IsRequired();

        // NOT NULL: doc 08 §4.1 (G-13) makes a reason mandatory, and for a manual bank transfer it also
        // carries the outgoing transfer's bank reference. A nullable column would make "mandatory" a rule
        // only the UI remembered.
        builder.Property(refund => refund.Reason).HasMaxLength(500).IsRequired();

        // False by default: doc 08 (R6, N8) treats restocking as a decision someone has to make, and the
        // G-23 dashboard-refund path explicitly runs with RestockItems = 0 because nobody in the system knows
        // whether the goods came back.
        builder.Property(refund => refund.RestockItems).HasDefaultValue(false).IsRequired();

        // Nullable and no FK (G-23, doc 08 §1.5): NULL means the provider's webhook did it, with no staff
        // user behind it.
        builder.Property(refund => refund.CreatedByUserId);

        builder.Property(refund => refund.CreatedAtUtc).HasDefaultValueSql("sysutcdatetime()").IsRequired();
        builder.Property(refund => refund.CompletedAtUtc);

        // Doc 08 §3.5: IX (CompletedAtUtc) WHERE Status = 1 — the completed-refunds report.
        builder
            .HasIndex(refund => refund.CompletedAtUtc)
            .HasFilter($"[Status] = {(byte)RefundStatus.Completed}")
            .HasDatabaseName("IX_Refunds_Completed");

        // Doc 08 §3.5: "filtered UX" on the provider refund id. It is what keeps the
        // PAYMENT.CAPTURE.REFUNDED webhook idempotent — a refund we already recorded cannot be written twice.
        builder
            .HasIndex(refund => refund.ProviderRefundId)
            .IsUnique()
            .HasFilter("[ProviderRefundId] IS NOT NULL")
            .HasDatabaseName("UX_Refunds_ProviderRefundId");

        // Doc 08 §1.6 (DB7): both FK columns.
        builder.HasIndex(refund => refund.OrderId).HasDatabaseName("IX_Refunds_OrderId");
        builder.HasIndex(refund => refund.PaymentId).HasDatabaseName("IX_Refunds_PaymentId");

        builder
            .HasOne(refund => refund.Order)
            .WithMany(order => order.Refunds)
            .HasForeignKey(refund => refund.OrderId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(refund => refund.Payment)
            .WithMany(payment => payment.Refunds)
            .HasForeignKey(refund => refund.PaymentId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
