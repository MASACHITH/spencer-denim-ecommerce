using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Sales.Wishlist.Abstractions;
using SpencerDenim.Application.Sales.Wishlist.Contracts;

namespace SpencerDenim.Application.Sales.Wishlist;

/// <summary>
/// Draw the products a shopper has saved (WISH-01).
/// <para>
/// <b>The slugs come from the request, because the shopper's list lives in their browser.</b> Doc 02's
/// WISH-01 line says "guests keep it in the browser"; doc 08 §1.7 lists the guest wishlist under
/// "Client-side only (no tables)". So this query holds no identity, reads no cookie and creates nothing:
/// it is a catalogue read with the shopper's own keys, and it is the same answer for anyone who asks
/// about the same slugs. See <see cref="GetWishlistQueryHandler"/> for why that still means
/// <c>no-store</c> on the wire.
/// </para>
/// </summary>
/// <param name="Slugs">
/// The product slugs the browser has saved, most recently saved first. Duplicates, blanks and anything
/// too long to be a slug are dropped by the handler rather than refused.
/// </param>
public sealed record GetWishlistQuery(IReadOnlyList<string> Slugs);

/// <summary>
/// Turns a browser's saved slugs into live cards.
/// <para>
/// <b>Live, not a snapshot, and that is the point of the round trip.</b> The browser could have stored
/// whole cards — <c>RecentlyViewed</c> (PDP-07) does, and its own <c>TODO(W2-07)</c> says it should stop
/// — but a wishlist is a list a shopper comes back to in a week, so a stored price is a price that has
/// since changed and a stored "In stock" is a promise the shop may no longer be able to keep. The
/// browser keeps the slugs and the order; everything shown on a card is read from the catalogue at the
/// moment it is drawn.
/// </para>
/// <para>
/// <b>Nothing here is a 400.</b> A junk slug, a duplicate, a blank, a list of 500: none of it is
/// something a shopper can see or fix, because the list is in their own browser storage and may have
/// been written by an older build of the site or another tab (lesson L1). Each is dropped or truncated
/// and the rest is answered. The one thing that would be wrong is to drop a slug <i>silently from the
/// result</i>, so a slug that survives validation and matches no sellable product comes back with a
/// <c>null</c> product rather than not coming back at all.
/// </para>
/// </summary>
public sealed class GetWishlistQueryHandler(IWishlistCatalogReader reader)
    : IQueryHandler<GetWishlistQuery, WishlistDto>
{
    /// <summary>
    /// How many slugs one request may name.
    /// <para>
    /// A bound is needed because the list is sent by the client: without one, anyone could ask the
    /// catalogue to project ten thousand cards in a single query. 200 is far above any real wishlist and
    /// is still one short <c>IN</c> list.
    /// </para>
    /// <para>
    /// <b>It is no longer also a transport limit, and it used to be one by accident.</b> While the
    /// endpoint was a <c>GET</c> with a repeated <c>slug</c> parameter, 200 slugs exceeded Kestrel's
    /// 8,192-byte request line at about 32 characters each and the shopper's own wishlist page became
    /// permanently unloadable — see <c>WishlistEndpoints</c> for the measurements. The slugs now travel
    /// in a <c>POST</c> body, where 200 of them at the maximum length is about 45 KB against Kestrel's
    /// 30 MB default, so this number bounds <i>work</i> and nothing else and is reachable whatever a
    /// slug looks like. <c>WishlistTransportTests</c> holds that against a real Kestrel.
    /// </para>
    /// <para>
    /// <b>Over the cap the extra slugs are not answered, and the shopper is told.</b> Truncating is the
    /// least bad of the three options: a 400 would make a shopper's own list unviewable with nothing they
    /// could do about it, and dropping their stored entries to fit would be destroying saved items to
    /// make a request smaller. Nothing is removed from storage, and the page says it is showing the most
    /// recent 200 — see <c>WishlistStore.truncated</c>.
    /// </para>
    /// </summary>
    public const int MaxSlugs = 200;

    /// <summary>
    /// <c>catalog.Products.Slug</c> is <c>varchar(220)</c>, so nothing longer can match a product and
    /// there is no reason to send it to the database.
    /// </summary>
    public const int MaxSlugLength = 220;

    public async Task<WishlistDto> HandleAsync(GetWishlistQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var slugs = Usable(query.Slugs);
        if (slugs.Count == 0)
        {
            return WishlistDto.Empty;
        }

        var cards = await reader.GetCardsBySlugAsync(slugs, cancellationToken);

        return new WishlistDto(
            cards.Select(card => new WishlistEntryDto(card.Slug, card.Product)).ToList().AsReadOnly());
    }

    /// <summary>
    /// The slugs worth asking the catalogue about: non-blank, short enough to be a slug, de-duplicated,
    /// in the order the browser sent them, capped at <see cref="MaxSlugs"/>.
    /// <para>
    /// <b>Ordinal de-duplication, not case-insensitive</b> — because the slug echoed back has to be the
    /// one the browser sent: it is the key the browser's own storage is held under, and folding two
    /// spellings into one would mean answering about a slug nobody asked about, which is the one thing
    /// <see cref="Contracts.WishlistEntryDto"/> promises not to do.
    /// <para>
    /// <b>It is not because only one spelling can match.</b> An earlier version of this comment said so
    /// and it was wrong: the database's collation is <c>Latin1_General_100_CI_AS_SC</c> (doc 08 §1.1), so
    /// <c>linen-shirt</c> and <c>Linen-Shirt</c> match the <i>same</i> product —
    /// <c>ProductCatalogReader.GetCardsBySlugAsync</c> says as much and keeps a case-insensitive fallback
    /// for exactly that reason. So two such slugs in one list come back as two entries holding the same
    /// product. That is benign and it is the right answer: the browser has two entries, and telling it
    /// about one of them would leave the other looking withdrawn.
    /// </para>
    /// </para>
    /// </summary>
    private static List<string> Usable(IReadOnlyList<string>? slugs)
    {
        if (slugs is null || slugs.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var usable = new List<string>(Math.Min(slugs.Count, MaxSlugs));

        foreach (var slug in slugs)
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Length > MaxSlugLength)
            {
                continue;
            }

            if (seen.Add(slug))
            {
                usable.Add(slug);
            }

            if (usable.Count == MaxSlugs)
            {
                break;
            }
        }

        return usable;
    }
}
