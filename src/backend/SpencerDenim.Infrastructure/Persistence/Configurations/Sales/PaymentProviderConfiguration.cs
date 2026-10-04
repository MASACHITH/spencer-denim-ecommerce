using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Sales;

/// <summary>
/// <c>sales.PaymentProviders</c> (doc 08 §3.5) [A] [RV].
/// <para>
/// The key is the <c>varchar(30)</c> code, which doc 08 §3.5 chooses deliberately against §1.3's general
/// "natural keys are never PKs" rule. Every FK that references it therefore carries the code, not an int —
/// which is also what makes <c>IX_Payments_BankQueue</c>'s literal filter possible.
/// </para>
/// </summary>
internal sealed class PaymentProviderConfiguration : IEntityTypeConfiguration<PaymentProvider>
{
    public void Configure(EntityTypeBuilder<PaymentProvider> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PaymentProviders", AppDbContext.SalesSchema);

        builder.HasKey(provider => provider.Code);

        // varchar, not nvarchar: doc 08 §1.4 says codes are varchar. ValueGeneratedNever so EF does not
        // treat a string key as store-generated and refuse the insert.
        builder
            .Property(provider => provider.Code)
            .HasColumnType($"varchar({PaymentProviderCodes.MaxCodeLength})")
            .ValueGeneratedNever()
            .IsRequired();

        builder.Property(provider => provider.DisplayName).HasMaxLength(60).IsRequired();

        // Doc 08 §1.4: a bit column always has a default. False, because doc 08 §3.5 requires a provider's
        // settings to be valid before it is enabled, and a row inserted by a seeder or a script must not
        // start accepting money before anyone has checked them.
        builder.Property(provider => provider.IsEnabled).HasDefaultValue(false).IsRequired();

        builder.Property(provider => provider.DisplayOrder).IsRequired();
        builder.Property(provider => provider.CheckoutDescription).HasMaxLength(300);

        builder.ApplyAuditColumns();
        builder.ApplyRowVersion(provider => provider.RowVersion);
    }
}
