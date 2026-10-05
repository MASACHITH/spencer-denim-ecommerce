namespace SpencerDenim.Domain.Sales;

/// <summary>
/// <c>sales.Carts</c> (doc 08 §3.5, CART-01/02): one shopping bag, server-side for everyone.
/// <para>
/// <b>No <c>RowVersion</c>.</b> Doc 08 §1.5 excludes carts from the rowversion list as a hot row, and
/// §3.5 (DB22) replaces it with item-level upserts: see <c>ICartStore.AddItemAsync</c>. A cart row is
/// therefore never read-then-written as a whole, and nothing here is a concurrency token.
/// </para>
/// <para>
/// <b>Which doc 08 columns are deliberately not here.</b> <c>PromoCodeText</c> and <c>DistrictId</c> are
/// both in doc 08's table and both serve features that are not built: promo codes are ADM-13 / W3-05
/// (and <c>marketing.PromoCodes</c> does not exist, so a stored code could not be revalidated on read as
/// §3.5 requires), and <c>DistrictId</c>'s <c>ref.Districts</c> has not been created by any migration, so
/// the column could not carry its foreign key. The migration that creates the feature creates its column.
/// </para>
/// <para>
/// <c>DeliveryMethodId</c> <b>was</b> in that list and is now here: W3-03 created
/// <c>sales.DeliveryMethods</c>, so the column and its foreign key could finally be added in one piece,
/// which is what W2-09 said this task should do.
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

    /// <summary>
    /// The delivery method the shopper has chosen in the bag, or <see langword="null"/> while they have not
    /// chosen one (doc 08 §3.5). A "header field", so §3.5 (DB22) writes it with a single-column
    /// last-writer-wins UPDATE — never a read-then-write of the cart row.
    /// <para>
    /// <b>It is a preference, not a price.</b> Nothing in the bag is charged from it: the fee comes from the
    /// <c>DeliveryMethodZoneRates</c> row for (method, zone) and is recomputed on every read (doc 08 §6), and
    /// a method whose rate row is missing or <c>IsAvailable = 0</c> is simply not offered. So a stale value
    /// here can make the bag show the wrong <i>default</i> selection, never the wrong total.
    /// </para>
    /// <para>
    /// Its foreign key is <c>NO ACTION</c> (doc 08 §1.6's default): a method an admin retires must not be
    /// deletable out from under a live bag, and ADM-22 disables rather than deletes.
    /// </para>
    /// </summary>
    public int? DeliveryMethodId { get; set; }

    public DeliveryMethod? DeliveryMethod { get; set; }

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
