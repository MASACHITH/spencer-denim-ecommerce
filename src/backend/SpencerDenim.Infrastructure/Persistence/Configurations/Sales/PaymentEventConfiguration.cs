using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Sales;

/// <summary>
/// <c>sales.PaymentEvents</c> (doc 08 §3.5, DB13): the provider event log.
/// <para>
/// <b><c>UX_PaymentEvents_IdempotencyKey</c> is the exactly-once mechanism</b>, not a tidiness check. Doc 08
/// DB13 verifies a webhook's signature before anything is inserted, then inserts: a duplicate key means the
/// event was already received, and the handler answers 200 without reprocessing. That only works if the
/// uniqueness is in the database, because two redeliveries can arrive on two threads at once.
/// </para>
/// </summary>
internal sealed class PaymentEventConfiguration : IEntityTypeConfiguration<PaymentEvent>
{
    public void Configure(EntityTypeBuilder<PaymentEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "PaymentEvents",
            AppDbContext.SalesSchema,
            table =>
            {
                // Doc 08 §3.5: 1 Webhook (verified), 2 API response, 3 Return redirect.
                table.HasCheckConstraint("CK_PaymentEvents_EventSource", "[EventSource] BETWEEN 1 AND 3");

                // Doc 08 §3.5: 0 Received, 1 Processed, 2 Ignored, 3 Failed.
                table.HasCheckConstraint("CK_PaymentEvents_ProcessingStatus", "[ProcessingStatus] BETWEEN 0 AND 3");

                // Doc 08 §1.4 (DB15). NOT NULL here, unlike the other JSON columns: an event with no payload
                // could never be reprocessed by the retry job or shown to a human in a dispute.
                table.HasCheckConstraint("CK_PaymentEvents_PayloadJson", "ISJSON([PayloadJson]) = 1");

                // Not in doc 08's note: the retry job increments this, and a negative attempt count would make
                // any "give up after N attempts" rule read as if the event were fresh.
                table.HasCheckConstraint("CK_PaymentEvents_AttemptCount", "[AttemptCount] >= 0");
            });

        builder.HasKey(paymentEvent => paymentEvent.Id);

        builder
            .Property(paymentEvent => paymentEvent.ProviderCode)
            .HasColumnType($"varchar({PaymentProviderCodes.MaxCodeLength})")
            .IsRequired();

        builder.Property(paymentEvent => paymentEvent.EventSource).HasColumnType("tinyint").IsRequired();
        builder.Property(paymentEvent => paymentEvent.ProviderEventId).HasColumnType("varchar(100)");
        builder.Property(paymentEvent => paymentEvent.EventType).HasColumnType("varchar(100)").IsRequired();
        builder.Property(paymentEvent => paymentEvent.IdempotencyKey).HasColumnType("varchar(160)").IsRequired();
        builder.Property(paymentEvent => paymentEvent.PayloadJson).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(paymentEvent => paymentEvent.ProcessingStatus).HasColumnType("tinyint").IsRequired();
        builder.Property(paymentEvent => paymentEvent.AttemptCount).HasDefaultValue((short)0).IsRequired();
        builder.Property(paymentEvent => paymentEvent.NextAttemptAtUtc);
        builder.Property(paymentEvent => paymentEvent.LastError).HasMaxLength(2000);
        builder.Property(paymentEvent => paymentEvent.ReceivedAtUtc).HasDefaultValueSql("sysutcdatetime()").IsRequired();
        builder.Property(paymentEvent => paymentEvent.ProcessedAtUtc);

        builder
            .HasIndex(paymentEvent => paymentEvent.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("UX_PaymentEvents_IdempotencyKey");

        // Doc 08 §3.5: the retry job's index. Received (0) is in the filter as well as Failed (3), because an
        // event that was stored and then never processed — the process died between the two — has to be picked
        // up by the same sweep.
        builder
            .HasIndex(paymentEvent => paymentEvent.NextAttemptAtUtc)
            .HasFilter(
                $"[ProcessingStatus] IN ({(byte)PaymentEventProcessingStatus.Received}, "
                    + $"{(byte)PaymentEventProcessingStatus.Failed})")
            .HasDatabaseName("IX_PaymentEvents_Retry");

        // Doc 08 §1.6 (DB7): the three FK columns.
        builder.HasIndex(paymentEvent => paymentEvent.ProviderCode).HasDatabaseName("IX_PaymentEvents_ProviderCode");
        builder.HasIndex(paymentEvent => paymentEvent.PaymentId).HasDatabaseName("IX_PaymentEvents_PaymentId");
        builder.HasIndex(paymentEvent => paymentEvent.OrderId).HasDatabaseName("IX_PaymentEvents_OrderId");

        builder
            .HasOne(paymentEvent => paymentEvent.PaymentProvider)
            .WithMany()
            .HasForeignKey(paymentEvent => paymentEvent.ProviderCode)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(paymentEvent => paymentEvent.Payment)
            .WithMany()
            .HasForeignKey(paymentEvent => paymentEvent.PaymentId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(paymentEvent => paymentEvent.Order)
            .WithMany()
            .HasForeignKey(paymentEvent => paymentEvent.OrderId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
