using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Sales;

/// <summary>
/// <c>sales.WishlistItems</c> (doc 08 §3.5, WISH-01).
/// <para>
/// <b>No audit columns and no rowversion</b> — doc 08 §3.5 marks this table with neither <c>[A]</c> nor
/// <c>[RV]</c>, exactly as it marks neither on <c>Carts</c>. See <see cref="WishlistItem"/> for why
/// <c>CustomerUserId</c> is <c>NOT NULL</c> here while the cart's is nullable, and for who owns the
/// login merge.
/// </para>
/// </summary>
internal sealed class WishlistItemConfiguration : IEntityTypeConfiguration<WishlistItem>
{
    public void Configure(EntityTypeBuilder<WishlistItem> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("WishlistItems", AppDbContext.SalesSchema);

        builder.HasKey(item => item.Id);

        builder.Property(item => item.AddedAtUtc).HasDefaultValueSql("sysutcdatetime()").IsRequired();

        // Doc 08 §3.5: `UX_WishlistItems_Customer_Product`, and doc 11 §553 in words — "one entry per
        // product per customer, optional colour". The colour is **not** part of the key: saving the same
        // product in a second colourway is the same entry, which is why the login merge can be a plain
        // "insert where not exists" (§3.5) with nothing to reconcile.
        //
        // **Unfiltered, and that is the whole reason this table needs no DB7 exemption.** `Carts` has to
        // filter its equivalent index (`WHERE CustomerUserId IS NOT NULL`) because a guest cart keeps a
        // NULL owner and a plain unique index would then permit exactly one guest cart in the table;
        // doc 08 §1.6's FK-index rule therefore needs an entry in `SalesSchemaTests`'
        // `FilteredLeadingIndexIsEnough` to excuse it. Every row here has a real owner, so this index is
        // plain, `CustomerUserId` leads an unfiltered index, and DB7 is satisfied outright.
        builder
            .HasIndex(item => new { item.CustomerUserId, item.ProductId })
            .IsUnique()
            .HasDatabaseName("UX_WishlistItems_Customer_Product");

        // Doc 08 §1.6 (DB7): every FK column leads at least one index. `CustomerUserId` leads the unique
        // index above; `ProductId` does not — it is the *second* key there, and a second key is not a
        // leading key — so it gets its own. It is also the index that answers "who has saved this
        // product", which is what a withdrawal from sale needs (doc 11 §575).
        //
        // Named explicitly, like every index in this class: EF treats a repeated `HasIndex` over the same
        // property set as reconfiguring the first one, and `IndexDeclarationConventionTests` fails a
        // declaration that relies on the unnamed overload where a property set is shared. Nothing here
        // shares a set, but naming is what keeps that true when the next column arrives.
        builder
            .HasIndex(item => item.ProductId)
            .HasDatabaseName("IX_WishlistItems_Product");

        // DB7 again, for the nullable FK. EF's convention would have created this one itself (nothing
        // else leads on the column), and it is declared anyway so the name is ours rather than
        // `IX_WishlistItems_ProductColourId`, and so the FK-index rule for this table is readable in one
        // place instead of being half convention and half declaration.
        builder
            .HasIndex(item => item.ProductColourId)
            .HasDatabaseName("IX_WishlistItems_ProductColour");

        // No navigation on AppUser, for the same reason `CartConfiguration` gives: a user does not need a
        // WishlistItems collection, and adding one would put a sales type on an Identity entity.
        // NO ACTION — doc 08 §1.6's default — because a customer is anonymised rather than deleted, and
        // §3.1's PDPA removal clears these rows with a set-based ExecuteDelete of its own.
        builder
            .HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(item => item.CustomerUserId)
            .OnDelete(DeleteBehavior.NoAction);

        // NO ACTION, and deliberately **not** cascade: doc 08 §1.6's cascade allow-list does not name
        // WishlistItems. A saved product is withdrawn from sale (a soft delete, §1.5), not removed from
        // the database, and the shopper is shown it as unavailable with a way to remove it (doc 11 §575).
        // No inverse navigation on Product either — a product does not need to know who has saved it.
        builder
            .HasOne(item => item.Product)
            .WithMany()
            .HasForeignKey(item => item.ProductId)
            .OnDelete(DeleteBehavior.NoAction);

        // NO ACTION on the colourway too. `catalog.ProductColours` **is** on §1.6's cascade list — but as
        // a child of its *product*, not as a parent of anything: a colour dies with its product. A
        // wishlist row pointing at a colour must not be able to delete itself out from under the shopper
        // when a merchandiser retires a colourway, so this side is NO ACTION and the storefront treats a
        // colour it can no longer resolve as "no colour chosen".
        builder
            .HasOne(item => item.ProductColour)
            .WithMany()
            .HasForeignKey(item => item.ProductColourId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
