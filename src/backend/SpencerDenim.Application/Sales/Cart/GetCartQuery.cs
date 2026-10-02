using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Sales.Cart.Abstractions;

namespace SpencerDenim.Application.Sales.Cart;

/// <summary>Read the bag for one visitor (CART-01, CART-02, GLB-04).</summary>
public sealed record GetCartQuery(CartLookup Lookup);

/// <summary>
/// Reads the bag and decides what the guest cookie should now say.
/// <para>
/// <b>It never creates a cart.</b> Doc 08 §3.5: the row is created on the first Add to Bag, never on a
/// page view. That is also what makes a server-side render of the bag page possible at all — SSR cannot
/// deliver a <c>Set-Cookie</c> to the browser, and because a read issues no new token it never needs to.
/// </para>
/// </summary>
public sealed class GetCartQueryHandler(ICartStore store, ICartReader reader)
    : IQueryHandler<GetCartQuery, CartView>
{
    public async Task<CartView> HandleAsync(GetCartQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var lookup = query.Lookup;
        var cart = await store.FindAsync(lookup, cancellationToken);

        if (cart is null)
        {
            // A guest whose cookie named a cart that is gone: clear the token rather than leave the
            // browser sending a dead one for another 30 days. A guest with no token at all has nothing
            // to clear, and a signed-in customer with no cart yet has no guest token to touch.
            return lookup.GuestToken is not null && !lookup.ByCustomer
                ? CartView.StaleGuestToken
                : CartView.Empty;
        }

        var dto = await reader.ReadAsync(cart.Value.Id, cancellationToken);

        // Doc 08 §3.5: the inactivity clock is "last read or write", so a read moves it. It is a
        // single-column update and the row is not locked for the read.
        await store.TouchAsync(cart.Value.Id, cancellationToken);

        return cart.Value.IsCustomerCart
            ? CartView.ForCustomer(dto, lookup.GuestToken is not null)
            : CartView.ForGuest(dto, cart.Value.Token);
    }
}
