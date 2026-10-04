using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Sales;

/// <summary>
/// <c>sales.Payments</c> (doc 08 §3.5, PAY-01..03) [RV].
/// <para>
/// <b>Two filtered unique indexes carry the money invariants</b>, and which statuses they cover is the
/// whole design (N1):
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>UX_Payments_OneSettlement</c> — at most one row per order may be the settling payment. A duplicate or
/// late capture is real money and must still be storable, so it becomes status 7 with the flag at 0,
/// <b>outside</b> this index.
/// </description></item>
/// <item><description>
/// <c>UX_Payments_OneOpen</c> — at most one Initiated or Pending attempt per order.
/// <b>Capturing (2) is deliberately excluded.</b> If the index covered it, the recovery job's re-claim and a
/// customer's retry would both collide with the very row they are trying to resolve, and a capture in flight
/// could become unrecoverable. Double settlement is prevented by the order-row claim, the <c>NOT EXISTS</c>
/// settlement check and the index above, not by this one (§4.2).
/// </description></item>
/// </list>
/// </summary>
internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Payments", AppDbContext.SalesSchema, ConfigureConstraints);

        builder.HasKey(payment => payment.Id);

        builder
            .Property(payment => payment.ProviderCode)
            .HasColumnType($"varchar({PaymentProviderCodes.MaxCodeLength})")
            .IsRequired();

        builder.Property(payment => payment.Status).HasColumnType("tinyint").IsRequired();
        builder.Property(payment => payment.IsOrderSettlement).HasDefaultValue(false).IsRequired();
        builder.Property(payment => payment.Amount).IsRequired();

        builder
            .Property(payment => payment.Currency)
            .HasColumnType($"char({SalesCurrency.CodeLength})")
            .HasDefaultValue(SalesCurrency.Lkr)
            .IsRequired();

        // No CK and no default: this is the one currency column allowed to differ from LKR (D1).
        builder.Property(payment => payment.ProviderCurrency).HasColumnType($"char({SalesCurrency.CodeLength})");

        builder.Property(payment => payment.ProviderAmount);

        // Doc 08 §1.4: an exchange rate is decimal(18,6), not the money default — six places because the
        // rate multiplies every amount, so rounding it to two would move the charged total.
        builder.Property(payment => payment.ExchangeRate).HasPrecision(18, 6);

        // Doc 08 §1.4: a percent is decimal(5,2).
        builder.Property(payment => payment.FxBufferPercent).HasPrecision(5, 2);

        builder.Property(payment => payment.ProviderReference).HasColumnType("varchar(100)");
        builder.Property(payment => payment.ProviderTransactionId).HasColumnType("varchar(100)");
        builder.Property(payment => payment.PayerReference).HasMaxLength(100);
        builder.Property(payment => payment.CaptureStartedAtUtc);
        builder.Property(payment => payment.FailureCode).HasColumnType("varchar(50)");
        builder.Property(payment => payment.FailureMessage).HasMaxLength(500);

        // No FK on ApprovedByUserId: doc 08 §1.5's convention for user columns.
        builder.Property(payment => payment.ApprovedByUserId);

        builder.Property(payment => payment.ApprovedAtUtc);
        builder.Property(payment => payment.ApprovalNote).HasMaxLength(500);
        builder.Property(payment => payment.RefundedAmount).HasDefaultValue(0m).IsRequired();
        builder.Property(payment => payment.CreatedAtUtc).HasDefaultValueSql("sysutcdatetime()").IsRequired();
        builder.Property(payment => payment.UpdatedAtUtc);
        builder.Property(payment => payment.CompletedAtUtc);

        builder.ApplyRowVersion(payment => payment.RowVersion);

        ConfigureIndexes(builder);
        ConfigureRelationships(builder);
    }

    private static void ConfigureConstraints(TableBuilder<Payment> table)
    {
        // Doc 08 §3.5/§4.3: ten statuses.
        table.HasCheckConstraint("CK_Payments_Status", "[Status] BETWEEN 0 AND 9");

        table.HasCheckConstraint("CK_Payments_Currency", $"[Currency] = '{SalesCurrency.Lkr}'");

        // Doc 08 §3.5, verbatim: only money that has actually arrived — Succeeded, PartiallyRefunded or
        // Refunded — may be flagged as the order's settlement. Without it, a Pending bank transfer could be
        // marked as settling the order and the NOT EXISTS guard in §4.2 would then refuse the real capture.
        table.HasCheckConstraint(
            "CK_Payments_IsOrderSettlement",
            $"[IsOrderSettlement] = 0 OR [Status] IN ({(byte)PaymentStatus.Succeeded}, "
                + $"{(byte)PaymentStatus.PartiallyRefunded}, {(byte)PaymentStatus.Refunded})");

        // Not in doc 08's note. DF 0 plus "how much of Amount has gone back" leaves a negative value
        // meaningless, and a negative here would understate Orders.RefundedAmount. The upper bound is
        // deliberately *not* constrained: doc 08 does not state one, and an FX-rounded PayPal refund could
        // land a cent over the LKR Amount, which must not fail the transaction.
        table.HasCheckConstraint("CK_Payments_RefundedAmount", "[RefundedAmount] >= 0");
    }

    private static void ConfigureIndexes(EntityTypeBuilder<Payment> builder)
    {
        // Doc 08 §3.5. Unique on OrderId where the flag is set: one settling payment per order, enforced by
        // the database and not by a read-check-write.
        //
        // Both of the next two index the same single column, so each needs the *named* HasIndex overload.
        // Without the name, EF treats the second call as further configuration of the first index and the
        // migration silently contains only one of them — which would have left the settlement invariant
        // unenforced while every test of the open-payment rule still passed.
        builder
            .HasIndex(payment => payment.OrderId, "UX_Payments_OneSettlement")
            .IsUnique()
            .HasFilter("[IsOrderSettlement] = 1")
            .HasDatabaseName("UX_Payments_OneSettlement");

        // Doc 08 §3.5, including which statuses are in the filter and which are not — see the class remarks.
        builder
            .HasIndex(payment => payment.OrderId, "UX_Payments_OneOpen")
            .IsUnique()
            .HasFilter($"[Status] IN ({(byte)PaymentStatus.Initiated}, {(byte)PaymentStatus.Pending})")
            .HasDatabaseName("UX_Payments_OneOpen");

        // The capture-recovery job's index (every 2 minutes, rows older than 2 minutes).
        builder
            .HasIndex(payment => payment.CaptureStartedAtUtc)
            .HasFilter($"[Status] = {(byte)PaymentStatus.Capturing}")
            .HasDatabaseName("IX_Payments_Capturing");

        // Doc 08 §3.5: "UX_Payments_Provider_Reference / _Txn (filtered NOT NULL)". Read as (ProviderCode,
        // Reference): the provider is part of the identity of a reference, so two gateways can never be made
        // to collide, and the index still answers "find the payment for this PayPal order" with one seek.
        builder
            .HasIndex(payment => new { payment.ProviderCode, payment.ProviderReference })
            .IsUnique()
            .HasFilter("[ProviderReference] IS NOT NULL")
            .HasDatabaseName("UX_Payments_Provider_Reference");

        // The same, for the capture id. This one is a safety net with teeth: the same capture must never be
        // recorded against two payments, which is what would let one lot of money settle two orders.
        builder
            .HasIndex(payment => new { payment.ProviderCode, payment.ProviderTransactionId })
            .IsUnique()
            .HasFilter("[ProviderTransactionId] IS NOT NULL")
            .HasDatabaseName("UX_Payments_Provider_Txn");

        // The dashboard's payment-exception banner (DB2): captured money with nowhere to go. Named, for the
        // same reason as the two OrderId indexes above — IX_Payments_BankQueue indexes the same column.
        builder
            .HasIndex(payment => payment.CreatedAtUtc, "IX_Payments_Exceptions")
            .HasFilter($"[Status] = {(byte)PaymentStatus.CapturedRequiresRefund}")
            .HasDatabaseName("IX_Payments_Exceptions");

        // "Bank transfers awaiting approval", oldest first (doc 11 P7 step 4). The provider code is a literal
        // in the filter, which is what lets the optimiser match it (DB28) — hence the constant.
        builder
            .HasIndex(payment => payment.CreatedAtUtc, "IX_Payments_BankQueue")
            .HasFilter(
                $"[ProviderCode] = '{PaymentProviderCodes.BankTransfer}' AND [Status] = {(byte)PaymentStatus.Pending}")
            .HasDatabaseName("IX_Payments_BankQueue");

        // Doc 08 §1.6 (DB7). OrderId and ProviderCode already lead a unique index above; ProofAssetId does
        // not, and the media delete guard relies on media FKs being indexed.
        builder.HasIndex(payment => payment.ProofAssetId).HasDatabaseName("IX_Payments_ProofAssetId");
    }

    private static void ConfigureRelationships(EntityTypeBuilder<Payment> builder)
    {
        builder
            .HasOne(payment => payment.Order)
            .WithMany(order => order.Payments)
            .HasForeignKey(payment => payment.OrderId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(payment => payment.PaymentProvider)
            .WithMany()
            .HasForeignKey(payment => payment.ProviderCode)
            .OnDelete(DeleteBehavior.NoAction);

        // The bank slip. NO ACTION, so the asset cannot be deleted while a payment points at it — which is
        // the behaviour doc 08 §3.3's delete guard expects of every media FK.
        builder
            .HasOne(payment => payment.ProofAsset)
            .WithMany()
            .HasForeignKey(payment => payment.ProofAssetId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
