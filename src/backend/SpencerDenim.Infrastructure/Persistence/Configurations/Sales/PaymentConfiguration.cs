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

        // Doc 08 §1.6 (DB7): every FK column leads an index, and "the convention indexes exist on every FK"
        // even where §3.5 does not list them. All three FK columns are declared here rather than left to EF's
        // convention, because the convention suppresses its index whenever some existing index already leads
        // on the column — and it does not look at that index's filter.
        //
        // OrderId leads UX_Payments_OneSettlement and UX_Payments_OneOpen; ProviderCode leads
        // UX_Payments_Provider_Reference and _Txn. All four are filtered, so EF saw both columns as covered
        // and wrote no plain index for either, and until this was added no query could reach a Payments row
        // by order or by provider without reading the whole table. Measured on 100 000 payments over 20 000
        // orders:
        //
        //   - "list this order's payments" (ADM-09's order detail, the screen this schema unblocks) went
        //     Clustered Index Scan(PK_Payments) -> Index Seek(IX_Payments_OrderId), literal and parameterised
        //     alike. A filtered index is only matched when the predicate provably implies the filter (DB28),
        //     and "OrderId = @o" implies neither "Status IN (0,1)" nor "IsOrderSettlement = 1".
        //   - SQL Server's own referential-integrity check behind DELETE FROM sales.PaymentProviders went
        //     Clustered Index Scan(PK_Payments) -> Index Seek(IX_Payments_ProviderCode). The filtered
        //     UX_Payments_Provider_* cannot serve it: that check has to see rows with a NULL reference too.
        //     This is the case DB7 exists for, and it is the FK check, not application SQL, so nothing in
        //     this repository could have been rewritten to avoid it.
        //
        // What is *not* a reason for these two: §4.2 step 3.2's settlement guard,
        //     NOT EXISTS (SELECT 1 FROM sales.Payments s WHERE s.OrderId = @o AND s.IsOrderSettlement = 1)
        // already seeks UX_Payments_OneSettlement and did so before this change. Its parameter is on
        // OrderId, the index *key*; the filter's own column is the literal 1, which implies the filter, so
        // DB28 does not bite. It would bite if that 1 ever became a parameter — "IsOrderSettlement = @flag"
        // falls straight back to a scan, measured — so keep it a constant when step 3 writes the guard.
        //
        // Named overload for OrderId: two indexes above are already over that same single property, and the
        // unnamed overload would reconfigure one of them instead of declaring a third.
        builder
            .HasIndex(payment => payment.OrderId, "IX_Payments_OrderId")
            .HasDatabaseName("IX_Payments_OrderId");

        builder.HasIndex(payment => payment.ProviderCode).HasDatabaseName("IX_Payments_ProviderCode");

        // ProofAssetId leads no index of its own at all, and the media delete guard relies on media FKs
        // being indexed.
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
