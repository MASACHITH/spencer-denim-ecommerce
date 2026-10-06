using SpencerDenim.Application.Catalog.Contracts;

namespace SpencerDenim.Application.Sales.Wishlist.Abstractions;

/// <summary>
/// Read port for "give me the cards for these slugs" (WISH-01).
/// <para>
/// <b>Its own port rather than a method on <c>IProductCatalogReader</c></b>, for the reason
/// <c>ISitemapCatalogReader</c> gives in the same codebase: the listing contract is about one page of
/// cards chosen by the server, this is about a set of products chosen by the shopper's browser, and a
/// caller that wants one never wants the other (ISP, doc 03 §3). The implementation is the same class —
/// the visibility rules, the card projection and the best-seller thresholds must not be duplicated,
/// because a second copy is a second answer to "is this product still for sale".
/// </para>
/// <para>
/// It is also the honest shape of the storefront's answer to <c>RecentlyViewed</c>'s
/// <c>TODO(W2-07)</c>: "once the API can answer for several slugs at once … keep only the slugs here and
/// re-read the live cards". The wishlist keeps the slugs and re-reads the cards; recently-viewed still
/// stores a snapshot and can move onto this port whenever PDP-07 is revisited.
/// </para>
/// </summary>
public interface IWishlistCatalogReader
{
    /// <summary>
    /// The cards for <paramref name="slugs"/>, in the order given, with <see langword="null"/> for any
    /// slug the shop no longer offers.
    /// <para>
    /// The result has exactly one entry per <i>distinct</i> slug asked for, in the order of first
    /// appearance. A slug that matches nothing is not skipped: the shopper has it in their list and has
    /// to be told it is no longer available (doc 11 §575), and skipping it would read as "the server
    /// lost my item".
    /// </para>
    /// </summary>
    Task<IReadOnlyList<(string Slug, ProductCardDto? Product)>> GetCardsBySlugAsync(
        IReadOnlyList<string> slugs,
        CancellationToken cancellationToken);
}
