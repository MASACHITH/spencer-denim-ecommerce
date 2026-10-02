using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Sales;

/// <summary><c>sales.CartItems</c> (doc 08 §3.5, CART-01).</summary>
internal sealed class CartItemConfiguration : IEntityTypeConfiguration<CartItem>
{
    public void Configure(EntityTypeBuilder<CartItem> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "CartItems",
            AppDbContext.SalesSchema,
            table => table.HasCheckConstraint(
                "CK_CartItems_Quantity",
                $"[Quantity] BETWEEN {CartItem.MinQuantity} AND {CartItem.MaxQuantity}"));

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Quantity).IsRequired();
        builder.Property(item => item.UnitPriceAtAdd).IsRequired();
        builder.Property(item => item.AddedAtUtc).HasDefaultValueSql("sysutcdatetime()").IsRequired();
        builder.Property(item => item.UpdatedAtUtc);

        // Doc 08 §3.5: one row per (cart, variant). This is not only a correctness rule — it is the
        // constraint the DB22 upsert relies on: two concurrent adds of the same variant race to insert,
        // the loser gets 2627/2601, and retrying its UPDATE is what makes the two adds sum instead of
        // one of them being lost.
        builder
            .HasIndex(item => new { item.CartId, item.VariantId })
            .IsUnique()
            .HasDatabaseName("UX_CartItems_Cart_Variant");

        // Doc 08 §1.6 (DB7): every FK column leads at least one index. VariantId does not lead the
        // unique index above, so it gets its own — which is also the index that answers "which bags
        // hold this variant" when a variant is deactivated.
        builder
            .HasIndex(item => item.VariantId)
            .HasDatabaseName("IX_CartItems_Variant");

        // Doc 08 §1.6 names CartItems in the permitted cascade list: a cart item has no meaning apart
        // from its cart.
        builder
            .HasOne(item => item.Cart)
            .WithMany(cart => cart.Items)
            .HasForeignKey(item => item.CartId)
            .OnDelete(DeleteBehavior.Cascade);

        // NO ACTION, the default: a variant someone has in a bag must not be deletable out from under
        // them, and the catalogue deactivates rather than deletes anyway.
        builder
            .HasOne(item => item.Variant)
            .WithMany()
            .HasForeignKey(item => item.VariantId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
