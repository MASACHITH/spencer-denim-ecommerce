using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Sales.Cart.Abstractions;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Cart;

/// <summary>Change how many of one line the bag holds (CART-01).</summary>
public sealed record SetCartItemQuantityCommand(CartLookup Lookup, int VariantId, int Quantity);

/// <summary>Take one line out of the bag (CART-01, and the "move to wishlist" half that is not local).</summary>
public sealed record RemoveCartItemCommand(CartLookup Lookup, int VariantId);

/// <summary>
/// Sets a line's quantity to an exact number.
/// <para>
/// <b>Zero is not accepted</b>, and that is a decision rather than an oversight: removing is
/// <c>DELETE /api/cart/items/{variantId}</c>, and an endpoint where 0 silently means "delete" is one
/// where a stray 0 from a number input deletes a line the shopper was editing. A 0 is a 400 naming
/// <c>quantity</c>.
/// </para>
/// <para>
/// It does not create a cart. Setting a quantity on a bag that does not exist, or on a line that is not
/// in it, answers the bag as it is — the row the caller is editing is simply not there, which is the
/// same answer a second tab would get after the line was removed in the first.
/// </para>
/// </summary>
public sealed class SetCartItemQuantityCommandHandler(ICartStore store, ICartReader reader)
    : ICommandHandler<SetCartItemQuantityCommand, CartWriteResult>
{
    public async Task<CartWriteResult> HandleAsync(
        SetCartItemQuantityCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.VariantId <= 0)
        {
            return CartWriteResult.Invalid("variantId", "That line is not in your bag.");
        }

        if (!CartQuantity.IsRequestable(command.Quantity))
        {
            return CartWriteResult.Invalid(
                "quantity",
                $"Choose a quantity between {CartQuantity.Min} and {CartQuantity.Max}. To take the item out, remove it.");
        }

        var cart = await store.FindAsync(command.Lookup, cancellationToken);
        if (cart is null)
        {
            return CartWriteResult.Ok(EmptyViewFor(command.Lookup));
        }

        await store.SetQuantityAsync(cart.Value.Id, command.VariantId, (short)command.Quantity, cancellationToken);

        return CartWriteResult.Ok(
            await AddToCartCommandHandler.ViewAsync(store, reader, cart.Value, command.Lookup, cancellationToken));
    }

    /// <summary>
    /// What a write against a bag that does not exist answers. The guest token is cleared if one was
    /// sent, because it named nothing.
    /// </summary>
    internal static CartView EmptyViewFor(CartLookup lookup) =>
        lookup.GuestToken is not null && !lookup.ByCustomer ? CartView.StaleGuestToken : CartView.Empty;
}

/// <summary>
/// Removes a line.
/// <para>
/// <b>Idempotent on purpose.</b> Removing something that is not there answers 200 with the bag, not 404:
/// a shopper who double-taps Remove, or who has the bag open in two tabs, is not doing anything wrong,
/// and a 404 on the second tap is an error message about a state they already wanted.
/// </para>
/// </summary>
public sealed class RemoveCartItemCommandHandler(ICartStore store, ICartReader reader)
    : ICommandHandler<RemoveCartItemCommand, CartWriteResult>
{
    public async Task<CartWriteResult> HandleAsync(
        RemoveCartItemCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.VariantId <= 0)
        {
            return CartWriteResult.Invalid("variantId", "That line is not in your bag.");
        }

        var cart = await store.FindAsync(command.Lookup, cancellationToken);
        if (cart is null)
        {
            return CartWriteResult.Ok(SetCartItemQuantityCommandHandler.EmptyViewFor(command.Lookup));
        }

        await store.RemoveItemAsync(cart.Value.Id, command.VariantId, cancellationToken);

        return CartWriteResult.Ok(
            await AddToCartCommandHandler.ViewAsync(store, reader, cart.Value, command.Lookup, cancellationToken));
    }
}
