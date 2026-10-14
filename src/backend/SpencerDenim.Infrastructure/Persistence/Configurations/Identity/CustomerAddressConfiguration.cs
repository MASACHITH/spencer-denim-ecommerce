using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Identity;

/// <summary>
/// <c>identity.CustomerAddresses</c> (doc 08 §3.1, CHK-01 / ACC-02) [A].
/// </summary>
/// <remarks>
/// <b>The one index doc 08 names is the filtered unique one</b>, and it is the whole of "one default per
/// customer": without it, two tabs pressing "make this my default" would both succeed and the shopper
/// would have two defaults with nothing to choose between them. The other two indexes are doc 08 §1.6's
/// DB7 rule ("every FK column leads at least one index") for the two foreign keys — <c>UserId</c> does
/// <i>not</i> satisfy DB7 through the filtered index, because a filtered index cannot answer "all of this
/// customer's addresses", which is the only query the screen makes.
/// </remarks>
internal sealed class CustomerAddressConfiguration : IEntityTypeConfiguration<CustomerAddress>
{
    public void Configure(EntityTypeBuilder<CustomerAddress> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CustomerAddresses", "identity");

        builder.HasKey(address => address.Id);

        builder.Property(address => address.Label).HasMaxLength(CustomerAddress.LabelMaxLength);

        builder
            .Property(address => address.RecipientName)
            .HasMaxLength(CustomerAddress.RecipientNameMaxLength)
            .IsRequired();

        builder
            .Property(address => address.Phone)
            .HasColumnType($"varchar({CustomerAddress.PhoneMaxLength})")
            .IsRequired();

        builder.Property(address => address.Line1).HasMaxLength(CustomerAddress.LineMaxLength).IsRequired();
        builder.Property(address => address.Line2).HasMaxLength(CustomerAddress.LineMaxLength);
        builder.Property(address => address.City).HasMaxLength(CustomerAddress.CityMaxLength).IsRequired();

        builder
            .Property(address => address.PostalCode)
            .HasColumnType($"varchar({CustomerAddress.PostalCodeMaxLength})");

        builder.Property(address => address.Landmark).HasMaxLength(CustomerAddress.LandmarkMaxLength);

        builder.Property(address => address.IsDefault).HasDefaultValue(false).IsRequired();

        // Doc 08 §3.1 marks the table [A]; the SaveChanges interceptor stamps the four columns.
        builder.ApplyAuditColumns();

        // Doc 08 §3.1, verbatim: `UX_CustomerAddresses_UserId_Default (UserId) WHERE IsDefault = 1`.
        builder
            .HasIndex(address => address.UserId)
            .IsUnique()
            .HasFilter("[IsDefault] = 1")
            .HasDatabaseName("UX_CustomerAddresses_UserId_Default");

        // DB7, and the index the address book is read by. A *filtered* unique index on the same column
        // cannot serve "give me this customer's addresses" — the optimiser may only use it for rows that
        // satisfy the filter — so this is a second, unfiltered index rather than a duplicate. Named,
        // because EF treats a repeated HasIndex over the same property set as reconfiguring the first:
        // see IndexDeclarationConventionTests, which exists because that silently deleted two indexes in
        // W3-03. The property sets differ here only by the filter, so the explicit names are load-bearing.
        builder
            .HasIndex(address => new { address.UserId, address.Id })
            .HasDatabaseName("IX_CustomerAddresses_User");

        // DB7 for the district FK.
        builder
            .HasIndex(address => address.DistrictId)
            .HasDatabaseName("IX_CustomerAddresses_District");

        // No navigation on AppUser, for the reason CartConfiguration and WishlistItemConfiguration both
        // give: a user does not need an addresses collection, and the Identity entity stays free of it.
        // NO ACTION (doc 08 §1.6's default) because a customer is anonymised rather than deleted, and
        // §3.1 step 2's set-based ExecuteDelete clears these rows itself.
        builder
            .HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(address => address.UserId)
            .OnDelete(DeleteBehavior.NoAction);

        // NO ACTION: a district is reference data that is deactivated, never deleted (doc 08 §3.1
        // `IsActive`). A saved address keeps pointing at a district the shop no longer delivers to, and
        // the use case refuses to *quote* it rather than losing the shopper's address.
        builder
            .HasOne(address => address.District)
            .WithMany()
            .HasForeignKey(address => address.DistrictId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
