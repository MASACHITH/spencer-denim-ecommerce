using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Sales;

/// <summary>
/// <c>sales.DeliveryMethodZoneRates</c> (doc 08 §3.5, N4) [A] [RV].
/// <para>
/// Doc 08 §3.5 explains why this is a table and not JSON on the method: rates get CK validation,
/// row-level admin editing and audit, and a direct join from checkout. All three of those show up here as
/// the check constraints, the <c>[A]</c> columns and the composite key checkout looks the row up by.
/// </para>
/// </summary>
internal sealed class DeliveryMethodZoneRateConfiguration : IEntityTypeConfiguration<DeliveryMethodZoneRate>
{
    public void Configure(EntityTypeBuilder<DeliveryMethodZoneRate> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "DeliveryMethodZoneRates",
            AppDbContext.SalesSchema,
            table =>
            {
                // Doc 08 §3.5: the same CK as ref.Districts.DeliveryZone, which does not exist yet. Adding a
                // fourth zone is a code change, which is exactly what a CK makes it.
                table.HasCheckConstraint(
                    "CK_DeliveryMethodZoneRates_DeliveryZone",
                    "[DeliveryZone] BETWEEN 1 AND 3");

                // Doc 08 §3.5: Fee >= 0. Zero is allowed (a zone with free delivery); negative is not,
                // because a negative fee would subtract from the grand total and CK_Orders_Totals would
                // then be satisfied by an order the shop paid the customer to receive.
                table.HasCheckConstraint("CK_DeliveryMethodZoneRates_Fee", "[Fee] >= 0");

                // Not in doc 08's note, but the same reasoning as the fee: a negative free-delivery
                // threshold would make every order qualify.
                table.HasCheckConstraint(
                    "CK_DeliveryMethodZoneRates_FreeAboveAmount",
                    "[FreeAboveAmount] IS NULL OR [FreeAboveAmount] >= 0");

                // Doc 08 §3.5: CK Min <= Max.
                table.HasCheckConstraint(
                    "CK_DeliveryMethodZoneRates_EstimatedDays",
                    "[EstimatedDaysMin] <= [EstimatedDaysMax]");
            });

        // The pair is the key, not a surrogate: doc 08 §3.5 gives "no row" the meaning "unavailable", so
        // two rows for one pair would be two answers to a question that must have one.
        builder.HasKey(rate => new { rate.DeliveryMethodId, rate.DeliveryZone });

        builder.Property(rate => rate.DeliveryZone).HasColumnType("tinyint").IsRequired();
        builder.Property(rate => rate.IsAvailable).HasDefaultValue(true).IsRequired();
        builder.Property(rate => rate.Fee).IsRequired();
        builder.Property(rate => rate.FreeAboveAmount);
        builder.Property(rate => rate.EstimatedDaysMin).HasColumnType("tinyint").IsRequired();
        builder.Property(rate => rate.EstimatedDaysMax).HasColumnType("tinyint").IsRequired();

        builder.ApplyAuditColumns();
        builder.ApplyRowVersion(rate => rate.RowVersion);

        // Doc 08 §1.6 names DeliveryMethodZoneRates in the cascade allow-list: a rate has no meaning apart
        // from its method, so it dies with it. This is the only cascade this task adds.
        builder
            .HasOne(rate => rate.DeliveryMethod)
            .WithMany(method => method.ZoneRates)
            .HasForeignKey(rate => rate.DeliveryMethodId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
