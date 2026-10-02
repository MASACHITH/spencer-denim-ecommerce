namespace SpencerDenim.Domain.Sales;

/// <summary>
/// <c>sales.Carts</c> (doc 08 §3.5, CART-01/02): one shopping bag, server-side for everyone.
/// <para>
/// <b>No <c>RowVersion</c>.</b> Doc 08 §1.5 excludes carts from the rowversion list as a hot row, and
/// §3.5 (DB22) replaces it with item-level upserts: see <c>ICartStore.AddItemAsync</c>. A cart row is
/// therefore never read-then-written as a whole, and nothing here is a concurrency token.
/// </para>
/// <para>
/// <b>Which doc 08 columns are deliberately not here.</b> <c>PromoCodeText</c>, <c>DistrictId</c> and
/// <c>DeliveryMethodId</c> are all in doc 08's table and all serve features that are not built:
/// promo codes are ADM-13 (and <c>marketing.PromoCodes</c> does not exist, so a stored code could not
/// be revalidated on read as §3.5 requires), and the delivery fee needs
/// <c>sales.DeliveryMethodZoneRates</c> (#53) and <c>ref.Districts</c> — <b>neither table exists</b>,
/// so two of the three could not even carry their foreign key. The migration that creates the feature
/// creates its column; this one creates what CART-01/02 reads and writes.
/// </para>
/// </summary>
public sealed class Cart
{
    /// <summary>Doc 08 §1.3: the clustered key is an <c>int IDENTITY</c>, never the token.</summary>
    public int Id { get; set; }

    /// <summary>
    /// The unguessable public token a guest's cookie holds (doc 08 §1.3, §3.5). Under a unique index
    /// and never a clustered key.
    /// <para>
    /// <b>It is never serialised into a response body.</b> The cookie is <c>HttpOnly</c> so that a
    /// script cannot read it; putting the same value in JSON would hand it back to any script on the
    /// page and make the flag decorative.
    /// </para>
    /// </summary>
    public Guid Token { get; set; }

    /// <summary>
    /// The signed-in customer who owns this cart, or <see langword="null"/> for a guest cart.
    /// <c>UX_Carts_Customer</c> (filtered <c>WHERE CustomerUserId IS NOT NULL</c>) means one customer
    /// can never end up with two carts.
    /// </summary>
    public int? CustomerUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// When this cart was last read or written.
    /// <para>
    /// Doc 08 §8.4 purges a guest cart after 30 days "inactive" and a customer cart after 180, and §3.5
    /// keeps this column for it. <b>Counting a read as activity is this task's reading of "inactive",
    /// not a quotation</b> — neither section says "last read or write". It is the reading that matches
    /// the cookie, whose own 30-day window slides on every answer, and it errs towards keeping a bag a
    /// shopper is still looking at. The purge job itself is W4 housekeeping and is not in this task.
    /// </para>
    /// </summary>
    public DateTime LastActivityAtUtc { get; set; }

    public ICollection<CartItem> Items { get; } = [];
}
