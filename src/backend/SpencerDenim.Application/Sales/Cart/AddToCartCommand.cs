using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Sales.Cart.Abstractions;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Cart;

/// <summary>Add to Bag (CART-01, PDP-03).</summary>
/// <param name="VariantId">The colour × size the shopper chose.</param>
/// <param name="Quantity">1–10. Added to whatever the line already holds, clamped at 10.</param>
public sealed record AddToCartCommand(CartLookup Lookup, int VariantId, int Quantity);

/// <summary>
/// Adds one variant to the bag, creating the bag if this is the first thing in it.
/// <para>
/// The order of the checks is the point. The variant is validated <b>before</b> a cart is created, so a
/// request naming a variant that does not exist cannot leave an empty cart row (and a cookie) behind for
/// every scripted probe.
/// </para>
/// </summary>
public sealed class AddToCartCommandHandler(ICartStore store, ICartReader reader)
    : ICommandHandler<AddToCartCommand, CartWriteResult>
{
    public async Task<CartWriteResult> HandleAsync(AddToCartCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.VariantId <= 0)
        {
            return CartWriteResult.Invalid("variantId", "Choose a size and colour before adding to the bag.");
        }

        if (!CartQuantity.IsRequestable(command.Quantity))
        {
            return CartWriteResult.Invalid(
                "quantity",
                $"Choose a quantity between {CartQuantity.Min} and {CartQuantity.Max}.");
        }

        var variant = await reader.FindSellableVariantAsync(command.VariantId, cancellationToken);
        if (variant is null)
        {
            return CartWriteResult.NotSellable();
        }

        if (variant.Value.AvailableQuantity <= 0)
        {
            return CartWriteResult.OutOfStock();
        }

        var cart = await store.EnsureAsync(command.Lookup, cancellationToken);

        // The clamp at 10 is the database's (doc 08 §3.5's IIF), not this method's: two adds arriving at
        // once must sum and cap atomically, which a check here could not promise.
        await store.AddItemAsync(
            cart.Id,
            command.VariantId,
            (short)command.Quantity,
            variant.Value.CurrentPrice,
            cancellationToken);

        return CartWriteResult.Ok(await ViewAsync(store, reader, cart, command.Lookup, cancellationToken));
    }

    /// <summary>
    /// Re-reads the bag after a write, so the answer is the bag as it now is rather than what the caller
    /// asked for. Shared by all three writes.
    /// </summary>
    internal static async Task<CartView> ViewAsync(
        ICartStore store,
        ICartReader reader,
        CartIdentity cart,
        CartLookup lookup,
        CancellationToken cancellationToken)
    {
        await store.TouchAsync(cart.Id, cancellationToken);
        var dto = await reader.ReadAsync(cart.Id, cancellationToken);

        return cart.IsCustomerCart
            ? CartView.ForCustomer(dto, lookup.GuestToken is not null)
            : CartView.ForGuest(dto, cart.Token);
    }
}
