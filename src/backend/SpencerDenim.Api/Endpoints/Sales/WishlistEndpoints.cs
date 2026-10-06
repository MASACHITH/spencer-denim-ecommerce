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
/// <b>Why it is a <c>GET</c> with a repeated query parameter</b> rather than a <c>POST</c> with a body.
/// It reads and changes nothing, so it should be safe and idempotent, and the storefront's SSR process
/// needs to be able to make it during a render. The list is bounded at
/// <see cref="GetWishlistQueryHandler.MaxSlugs"/> slugs, which with
/// <see cref="GetWishlistQueryHandler.MaxSlugLength"/>-character slugs is well inside any proxy's
/// request-line limit; Kestrel's default is 8 KB of request line and 32 KB of headers, and in practice a
/// wishlist of 200 real slugs is a few kilobytes. A <c>POST</c> would read as a write and would not
/// cache or log as a read.
/// </para>
/// </summary>
internal static class WishlistEndpoints
{
    public static IEndpointRouteBuilder MapWishlistEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var wishlist = endpoints.MapGroup("/api/wishlist").WithTags("Wishlist");

        wishlist.MapGet(string.Empty, GetWishlistAsync)
            .WithName("GetWishlist")
            .WithSummary("The saved products for a list of slugs.")
            .WithDescription(
                "WISH-01. The shopper's list lives in their browser (doc 08 §1.7), so the slugs are sent " +
                "with the request and this answers with the live card for each one, in the order asked, " +
                "with a null product for anything the shop no longer offers (doc 11 §575). Reads nothing " +
                "per-visitor and writes nothing at all.")
            .Produces<WishlistDto>();

        return endpoints;
    }

    private static async Task<Ok<WishlistDto>> GetWishlistAsync(
        [FromQuery(Name = "slug")] string[]? slug,
        IQueryHandler<GetWishlistQuery, WishlistDto> handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetWishlistQuery(slug ?? []), cancellationToken);

        // **`no-store`, even though the answer is not personal.** The *answer* is the same for anyone who
        // asks about the same slugs — but the *request* is a list of what this shopper has saved, and a
        // shared cache keyed on the URL would be keeping a record of one visitor's wishlist. There is also
        // nothing to gain: the whole point of the round trip is that prices and stock are current (see
        // `GetWishlistQueryHandler`), so a cached copy would defeat it.
        //
        // No `Vary: Cookie` here, unlike the bag: this endpoint reads no cookie, so cookies are not part
        // of what varies the answer and claiming otherwise would be noise.
        httpContext.Response.Headers.CacheControl = "no-store, no-cache, private";

        return TypedResults.Ok(result);
    }
}
