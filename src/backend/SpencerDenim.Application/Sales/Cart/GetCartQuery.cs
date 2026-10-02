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

        // Touch, then read — the same order as every write path (see `AddToCartCommandHandler.ViewAsync`),
        // which is the only reason to prefer one: two paths differing for no stated reason is the thing
        // worth removing. It does make one small difference in the right direction: the activity is
        // recorded even if the read then fails, so a visitor whose reads are erroring does not have their
        // bag purged for inactivity.
        //
        // Doc 08 §8.4 purges a cart after 30 days (guest) or 180 (customer) "inactive", and §3.5 keeps
        // `LastActivityAtUtc` for it. Reading "inactive" to include a read, not only a write, is this
        // task's interpretation rather than a quotation — a shopper who looks at their bag every week has
        // not abandoned it.
        await store.TouchAsync(cart.Value.Id, cancellationToken);

        var dto = await reader.ReadAsync(cart.Value.Id, cancellationToken);

        return cart.Value.IsCustomerCart
            ? CartView.ForCustomer(dto, lookup.GuestToken is not null)
            : CartView.ForGuest(dto, cart.Value.Token);
    }
}
