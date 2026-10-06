using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Sales.Wishlist;
using SpencerDenim.Application.Sales.Wishlist.Contracts;

namespace SpencerDenim.Api.Endpoints.Sales;

/// <summary>
/// The wishlist (WISH-01).
/// <para>
/// <b>One endpoint, and it holds no state at all.</b> Doc 02's WISH-01 line says guests keep their
/// wishlist "in the browser", and doc 08 §1.7 lists it under "Client-side only (no tables)" — so the
/// shop does not own a guest's list, the browser does. This endpoint takes the slugs the browser has
/// saved and answers with the products as the catalogue has them today: live prices, live stock, and a
/// <c>null</c> product for anything the shop has since withdrawn (doc 11 §575).
/// </para>
/// <para>
/// <b>No cookie, no identity, no <c>sd_wishlist</c>.</b> That is a deliberate difference from
/// <c>CartEndpoints</c>, which has a token cookie because doc 08 §3.5 makes the bag server-side for
/// everyone and gives four reasons for it. None of the four applies here: there is nothing to price,
/// nothing to validate against stock at save time, no merge that has to be one server operation while
/// there is no account to merge into, and no header count to render on the server (<c>GLB-04</c>'s
/// wishlist badge is taken up after the first render — see <c>StorefrontCounts</c>). Adding a cookie
/// would create a second, server-side home for a list the scope baseline puts in the browser, and would
/// change the shape of the merge W3-01 owns.
/// </para>
/// <para>
/// <b>Why it is a <c>POST</c> with a body, when it reads and changes nothing.</b> It was a <c>GET</c>
/// with a repeated <c>slug</c> parameter, and that was a defect rather than a style choice: <b>the
/// request line does not fit</b>. Kestrel's default <see cref="KestrelServerLimits.MaxRequestLineSize"/>
/// is 8,192 bytes and nothing in this application raises it, while
/// <see cref="GetWishlistQueryHandler.MaxSlugs"/> slugs of
/// <see cref="GetWishlistQueryHandler.MaxSlugLength"/> characters is a <b>45,718-byte</b> request line.
/// Measured against real Kestrel: 30-character slugs reached the handler at 7,718 bytes, 34-character
/// slugs got <b>414</b> at 8,518, and break-even is about 32 characters — so an ordinary slug like
/// <c>womens-high-rise-straight-leg-jeans-indigo</c> (42) put the shopper's own wishlist permanently
/// out of reach, behind a Try again button that failed identically every time. An earlier version of
/// this comment asserted the opposite ("well inside any proxy's request-line limit"), wrong by a factor
/// of 5.6 and never measured; <c>WishlistTransportTests</c> now measures it against a real Kestrel,
/// because <c>WebApplicationFactory</c>'s in-memory server has no request-line limit at all and cannot
/// fail on this.
/// </para>
/// <para>
/// <b>And a body is the better place for this list anyway.</b> A query string carrying a shopper's whole
/// wishlist lands in the access log of every hop it passes, which is a record of what one person has
/// saved. A body does not. That argument was always available and was missed first time round.
/// </para>
/// <para>
/// <b>What is given up, and why it costs nothing here.</b> A <c>POST</c> is neither safe nor cacheable
/// by HTTP's rules, while this call is in fact both — the same slugs get the same answer for anyone who
/// asks, and nothing is written. No cache is lost, because every answer is <c>no-store</c> on purpose
/// (see <see cref="GetWishlistAsync"/>). Nor does server-side rendering need a safe method: the
/// wishlist page deliberately renders no list on the server, because the list is in the browser and the
/// server has nothing to render it from.
/// </para>
/// </summary>
internal static class WishlistEndpoints
{
    public static IEndpointRouteBuilder MapWishlistEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var wishlist = endpoints.MapGroup("/api/wishlist").WithTags("Wishlist");

        // The path says what it answers with — products for a set of slugs — rather than naming the
        // resource being created, because nothing is created. `POST /api/wishlist` would read as "make me
        // a wishlist", which is the one thing this endpoint must never be mistaken for.
        wishlist.MapPost("/products", GetWishlistAsync)
            .WithName("GetWishlistProducts")
            .WithSummary("The saved products for a list of slugs.")
            .WithDescription(
                "WISH-01. The shopper's list lives in their browser (doc 08 §1.7), so the slugs are sent " +
                "in the body and this answers with the live card for each one, in the order asked, with " +
                "a null product for anything the shop no longer offers (doc 11 §575). Reads nothing " +
                "per-visitor and writes nothing at all. A POST because the slugs do not fit in a request " +
                "line and because a wishlist in a URL is a wishlist in every access log on the way.")
            .Produces<WishlistDto>();

        return endpoints;
    }

    private static async Task<Ok<WishlistDto>> GetWishlistAsync(
        [FromBody] WishlistProductsRequest? request,
        IQueryHandler<GetWishlistQuery, WishlistDto> handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new GetWishlistQuery(request?.Slugs ?? []),
            cancellationToken);

        // **`no-store`, even though the answer is not personal.** The *answer* is the same for anyone who
        // asks about the same slugs — but the *request* is a list of what this shopper has saved, and a
        // shared cache keyed on it would be keeping a record of one visitor's wishlist. There is also
        // nothing to gain: the whole point of the round trip is that prices and stock are current (see
        // `GetWishlistQueryHandler`), so a cached copy would defeat it.
        //
        // No `Vary: Cookie` here, unlike the bag: this endpoint reads no cookie, so cookies are not part
        // of what varies the answer and claiming otherwise would be noise.
        httpContext.Response.Headers.CacheControl = "no-store, no-cache, private";

        return TypedResults.Ok(result);
    }
}

/// <summary>
/// The slugs a browser has saved (WISH-01).
/// </summary>
/// <param name="Slugs">
/// Most recently saved first. Duplicates, blanks and anything too long to be a slug are dropped by the
/// handler rather than refused — the list was written by <c>localStorage</c> and a shopper can neither
/// see it nor fix it, so a 400 would wedge their own wishlist shut.
/// </param>
public sealed record WishlistProductsRequest(IReadOnlyList<string>? Slugs);
