using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Domain.Sales;

/// <summary>
/// <c>sales.WishlistItems</c> (doc 08 §3.5, WISH-01): one product a <b>registered</b> customer has
/// saved, with the colourway they were looking at when they saved it.
/// <para>
/// <b>Why <see cref="CustomerUserId"/> is not nullable, when <c>Carts.CustomerUserId</c> is.</b> The bag
/// is server-side for everyone (doc 08 §3.5 says so at length, and lists the four reasons), so a guest
/// bag needs a row and a row needs an owner that may be NULL plus a cookie token to find it by. The
/// wishlist is the opposite decision, made in the same document: doc 08 §1.7 puts the guest wishlist in
/// <c>localStorage</c> under "Client-side only (no tables)", doc 02's WISH-01 line says "guests keep it
/// in the browser", and doc 11 §D14/§553 say it twice more. So every row in this table has a real owner
/// and <c>UX_WishlistItems_Customer_Product</c> can be a plain unique index rather than a filtered one.
/// </para>
/// <para>
/// <b>Nothing in the application writes this table yet, and that is the honest state of WISH-01.</b>
/// There are no customer accounts (W3-01 / ACC-01), so there is no <see cref="CustomerUserId"/> for a
/// row to carry. W2-08 builds the whole of WISH-01 except the half-sentence "it merges into the account
/// on login", which cannot be built or tested without a login to merge at. The table is created here
/// because doc 08 §3.5 fully specifies it, because two places in the repository say it is W2-08's
/// (<c>20261002035848_SalesCartFoundation</c> and <c>AppDbContext.SalesSchema</c>), and because the merge
/// W3-01 owns needs a target that already has the right shape. <b>W3-01 owns the merge</b>: it signs the
/// customer in, reads the browser list the storefront posts to it, and inserts the rows that are missing
/// — doc 08 §3.5's "the login merge inserts with <c>WHERE NOT EXISTS</c>".
/// </para>
/// <para>
/// <b>No audit columns and no rowversion.</b> Doc 08 §3.5 marks this table with neither <c>[A]</c> nor
/// <c>[RV]</c>: it is a shopper's own list, not an admin-edited aggregate, and §1.5 keeps rowversion off
/// hot rows. A wishlist row is also never updated — it is inserted or deleted — so there is no
/// read-then-write for a concurrency token to protect.
/// </para>
/// </summary>
public sealed class WishlistItem
{
    /// <summary>Doc 08 §1.3: an <c>int IDENTITY</c> clustered key.</summary>
    public int Id { get; set; }

    /// <summary>
    /// The registered customer whose list this is. <c>NO ACTION</c>, doc 08 §1.6's default: a customer is
    /// anonymised rather than hard-deleted, and §3.1's PDPA removal clears the rows itself with a
    /// set-based <c>ExecuteDelete</c> (§1.6 N12 names "wishlist clean-up" among those).
    /// </summary>
    public int CustomerUserId { get; set; }

    /// <summary>
    /// The product saved. One row per (customer, product): doc 11 §553, "one entry per product per
    /// customer, optional colour".
    /// <para>
    /// <c>NO ACTION</c> and <b>not</b> cascade. Doc 08 §1.6's cascade allow-list does not include
    /// <c>WishlistItems</c>, and the catalogue soft-deletes anyway (§1.5), so a product a shopper has
    /// saved is withdrawn from sale rather than deleted. Doc 11 §575 says what the shopper then sees:
    /// the item "is shown as unavailable; can be removed".
    /// </para>
    /// </summary>
    public int ProductId { get; set; }

    public Product? Product { get; set; }

    /// <summary>
    /// The colourway they saved, or <see langword="null"/> when they saved the product without choosing
    /// one — the heart on a listing card, which has no colour selected (CARD-01). Doc 08 §3.5 makes the
    /// column nullable for exactly that.
    /// </summary>
    public int? ProductColourId { get; set; }

    public ProductColour? ProductColour { get; set; }

    public DateTime AddedAtUtc { get; set; }
}
