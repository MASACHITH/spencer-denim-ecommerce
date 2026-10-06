using SpencerDenim.Application.Catalog.Contracts;

namespace SpencerDenim.Application.Sales.Wishlist.Contracts;

/// <summary>
/// One saved product as the storefront should draw it (WISH-01).
/// </summary>
/// <param name="Slug">
/// The slug the browser asked about, echoed back <b>exactly as it was sent</b>. It is the key the
/// browser's own list is held under, so it is what the page matches its stored entry against — a
/// normalised or re-cased slug would not match and the entry would look like a different product.
/// </param>
/// <param name="Product">
/// The product as the catalogue has it <i>today</i>, or <see langword="null"/> when the shop no longer
/// offers it: unpublished, soft-deleted, or in a category the client has deactivated. Doc 11 §575: a
/// wishlist item removed from sale "is shown as unavailable; can be removed". <b>A null here is not an
/// error and not a 404</b> — the request was answerable and this is the answer.
/// </param>
public sealed record WishlistEntryDto(string Slug, ProductCardDto? Product);

/// <summary>
/// The answer to "draw these saved products for me" (WISH-01).
/// <para>
/// <b>There is no count in this payload, deliberately.</b> The count the header shows (GLB-04) is the
/// number of things in the shopper's own list, including the ones the shop has withdrawn — the browser
/// already knows it, knows it before this request is answered, and must not appear to lose items while a
/// request is in flight. A count here would be a second answer to the same question.
/// </para>
/// </summary>
/// <param name="Items">
/// One entry per slug asked about, <b>in the order they were asked about</b> and with no entry invented
/// and none dropped. The order is the shopper's (most recently saved first), and only the browser knows
/// it, so the server's job is to preserve it rather than to impose one of its own.
/// </param>
public sealed record WishlistDto(IReadOnlyList<WishlistEntryDto> Items)
{
    /// <summary>A shopper with nothing saved, and the answer to a request that named no slugs.</summary>
    public static readonly WishlistDto Empty = new([]);
}
