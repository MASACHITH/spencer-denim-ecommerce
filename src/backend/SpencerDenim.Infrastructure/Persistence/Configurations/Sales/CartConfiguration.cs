using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Sales;

/// <summary>
/// <c>sales.Carts</c> (doc 08 §3.5, CART-01/02).
/// <para>
/// <b>No rowversion</b> (doc 08 §1.5: not on hot rows) and no audit columns — doc 08 §3.5 marks the
/// cart with neither <c>[A]</c> nor <c>[RV]</c>, and §3.8 lists <c>Carts</c> and <c>CartItems</c>
/// among the tables that are never audited.
/// </para>
/// </summary>
internal sealed class CartConfiguration : IEntityTypeConfiguration<Cart>
{
    public void Configure(EntityTypeBuilder<Cart> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Carts", AppDbContext.SalesSchema);

        builder.HasKey(cart => cart.Id);

        // Doc 08 §1.3: `uniqueidentifier` is never a clustered key. It is the public token, under a
        // unique index. NEWID() as the default so a row inserted by hand still gets one that is not
        // guessable from its neighbours; the application supplies its own value on every insert.
        builder.Property(cart => cart.Token).HasDefaultValueSql("newid()").IsRequired();

        builder.Property(cart => cart.CreatedAtUtc).HasDefaultValueSql("sysutcdatetime()").IsRequired();
        builder.Property(cart => cart.LastActivityAtUtc).HasDefaultValueSql("sysutcdatetime()").IsRequired();

        builder
            .HasIndex(cart => cart.Token)
            .IsUnique()
            .HasDatabaseName("UX_Carts_Token");

        // Doc 08 §3.5: filtered, so the "one cart per customer" rule is enforced by the database while
        // any number of guest carts keep a NULL. The filter is what makes the constraint possible at
        // all — a plain unique index would allow exactly one guest cart in the whole table.
        builder
            .HasIndex(cart => cart.CustomerUserId)
            .IsUnique()
            .HasFilter("[CustomerUserId] IS NOT NULL")
            .HasDatabaseName("UX_Carts_Customer");

        // Doc 08 §3.5: the retention purge reads this (guest 30 days, customer 180). CustomerUserId is
        // included because the purge's cut-off depends on it, so the scan needs no lookup.
        builder
            .HasIndex(cart => cart.LastActivityAtUtc)
            .IncludeProperties(cart => cart.CustomerUserId)
            .HasDatabaseName("IX_Carts_LastActivity");

        // No navigation on AppUser: a user does not need a Carts collection, and adding one would put
        // a sales type on an Identity entity. NO ACTION, which is doc 08 §1.6's default — a customer is
        // anonymised, never hard-deleted, so there is no cascade to want.
        builder
            .HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(cart => cart.CustomerUserId)
            .OnDelete(DeleteBehavior.NoAction);

        // W3-03, doc 08 §3.5. W2-09 left this column out because sales.DeliveryMethods did not exist and the
        // column could not have carried its foreign key; W3-03 creates the table, so the column and the
        // constraint arrive together — which is the whole point of deferring it rather than adding a bare int
        // that nothing validated. NO ACTION: a method an admin retires must not be deletable out from under a
        // live bag, and ADM-22 disables rather than deletes.
        builder
            .HasOne(cart => cart.DeliveryMethod)
            .WithMany()
            .HasForeignKey(cart => cart.DeliveryMethodId)
            .OnDelete(DeleteBehavior.NoAction);

        // Doc 08 §1.6 (DB7): the new FK column leads an index of its own.
        builder
            .HasIndex(cart => cart.DeliveryMethodId)
            .HasDatabaseName("IX_Carts_DeliveryMethodId");
    }
}
