using SpencerDenim.Application.Sales.Cart.Contracts;

namespace SpencerDenim.Application.Sales.Cart.Abstractions;

/// <summary>
/// A variant as the bag needs to see it at the moment of an add: is it still for sale, and what does it
/// cost now.
/// </summary>
/// <param name="CurrentPrice">Doc 08 §6 steps 1–3, computed from the catalogue, not from the cart.</param>
/// <param name="AvailableQuantity"><c>inventory.StockLevels.Available</c>, floored at 0.</param>
public readonly record struct CartVariantSnapshot(int VariantId, decimal CurrentPrice, int AvailableQuantity);

/// <summary>Reads for the bag. Separate from <see cref="ICartStore"/>: one projects, the other writes.</summary>
public interface ICartReader
{
    /// <summary>
    /// The whole bag, priced from today's catalogue (doc 08 §6: "prices are computed on every cart
    /// read"). A cart id of <see langword="null"/> is a visitor with no cart and answers
    /// <see cref="CartDto.Empty"/> without touching the database.
    /// </summary>
    Task<CartDto> ReadAsync(int? cartId, CancellationToken cancellationToken);

    /// <summary>
    /// The variant, if it is something this shop currently sells: published product, active department,
    /// active primary category, not soft-deleted, active variant. <see langword="null"/> otherwise —
    /// which covers both "no such variant" and "not for sale", because a public endpoint must not let a
    /// caller tell those apart by probing ids.
    /// </summary>
    Task<CartVariantSnapshot?> FindSellableVariantAsync(int variantId, CancellationToken cancellationToken);
}
