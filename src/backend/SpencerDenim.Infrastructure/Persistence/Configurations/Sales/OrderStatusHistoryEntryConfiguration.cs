using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Sales;

/// <summary><c>sales.OrderStatusHistory</c> (doc 08 §3.5, ADM-09 / ACC-02): append-only.</summary>
internal sealed class OrderStatusHistoryEntryConfiguration : IEntityTypeConfiguration<OrderStatusHistoryEntry>
{
    public void Configure(EntityTypeBuilder<OrderStatusHistoryEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "OrderStatusHistory",
            AppDbContext.SalesSchema,
            table =>
            {
                // The same range as the order's own status, applied to both ends of the transition. FromStatus
                // is nullable (placement has no "from"), so the check has to allow NULL explicitly — a bare
                // BETWEEN would evaluate to UNKNOWN and pass, which is the right outcome here but only by
                // accident, and saying it is cheaper than leaving the next reader to work that out.
                table.HasCheckConstraint(
                    "CK_OrderStatusHistory_FromStatus",
                    "[FromStatus] IS NULL OR [FromStatus] BETWEEN 0 AND 7");

                table.HasCheckConstraint("CK_OrderStatusHistory_ToStatus", "[ToStatus] BETWEEN 0 AND 7");

                // Doc 08 §3.5: 1 Customer, 2 Staff, 3 System job, 4 Payment provider.
                table.HasCheckConstraint("CK_OrderStatusHistory_Source", "[Source] BETWEEN 1 AND 4");
            });

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.FromStatus).HasColumnType("tinyint");
        builder.Property(entry => entry.ToStatus).HasColumnType("tinyint").IsRequired();
        builder.Property(entry => entry.Source).HasColumnType("tinyint").IsRequired();

        // No FK on ChangedByUserId, the convention doc 08 §1.5 sets for user columns: users are anonymised
        // rather than deleted, so a constraint adds a cascade path and buys nothing.
        builder.Property(entry => entry.ChangedByUserId);

        builder.Property(entry => entry.Note).HasMaxLength(1000);
        builder.Property(entry => entry.OccurredAtUtc).HasDefaultValueSql("sysutcdatetime()").IsRequired();

        // Doc 08 §3.5: IX (OrderId, OccurredAtUtc) — one order's timeline in order. It also satisfies DB7 for
        // the OrderId foreign key.
        builder
            .HasIndex(entry => new { entry.OrderId, entry.OccurredAtUtc })
            .HasDatabaseName("IX_OrderStatusHistory_Order_Occurred");

        builder
            .HasOne(entry => entry.Order)
            .WithMany(order => order.StatusHistory)
            .HasForeignKey(entry => entry.OrderId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
