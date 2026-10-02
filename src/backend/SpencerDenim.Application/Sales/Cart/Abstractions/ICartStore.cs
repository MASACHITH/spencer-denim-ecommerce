namespace SpencerDenim.Application.Sales.Cart.Abstractions;

/// <summary>What one add did, so the caller can tell "already there" from "just created".</summary>
/// <param name="Quantity">What the row holds now, after the clamp at 10.</param>
/// <param name="Inserted">True when the row did not exist before this call.</param>
public readonly record struct CartItemUpsert(short Quantity, bool Inserted);

/// <summary>What a sign-in merge moved.</summary>
/// <param name="LinesMoved">Guest lines that became, or were added to, customer lines.</param>
/// <param name="GuestCartDeleted">True when a guest cart was found and removed.</param>
public readonly record struct CartMergeOutcome(int LinesMoved, bool GuestCartDeleted);

/// <summary>
/// Writes to <c>sales.Carts</c> and <c>sales.CartItems</c> (doc 08 §3.5).
/// <para>
/// <b>Every method here is a set-based statement, never a read-then-write.</b> Doc 08 §1.5 keeps
/// rowversion off carts as hot rows and §3.5 (DB22) replaces it with item-level upserts, so this port
/// deliberately exposes no "load the cart and save it" shape: there is no shape in which a lost update
/// is possible to write.
/// </para>
/// </summary>
public interface ICartStore
{
    /// <summary>
    /// The cart for this visitor, or <see langword="null"/>. Reads only: doc 08 §3.5 says the row is
    /// created on the first Add to Bag and never on a page view, which is also what lets a server-side
    /// render answer without issuing a cookie it cannot deliver.
    /// </summary>
    Task<CartIdentity?> FindAsync(CartLookup lookup, CancellationToken cancellationToken);

    /// <summary>
    /// The cart for this visitor, creating one if there is none. Safe against a concurrent create: for a
    /// customer, <c>UX_Carts_Customer</c> turns the loser of the race into a unique violation, which is
    /// retried as a read.
    /// </summary>
    Task<CartIdentity> EnsureAsync(CartLookup lookup, CancellationToken cancellationToken);

    /// <summary>
    /// Doc 08 §3.5 (DB22), verbatim: <c>UPDATE … SET Quantity = IIF(Quantity + @q &gt; 10, 10, Quantity +
    /// @q) WHERE CartId = @c AND VariantId = @v</c>; if no row was updated, INSERT; on a unique
    /// violation (2627/2601) retry the UPDATE.
    /// </summary>
    Task<CartItemUpsert> AddItemAsync(
        int cartId,
        int variantId,
        short quantity,
        decimal unitPriceAtAdd,
        CancellationToken cancellationToken);

    /// <summary>A single conditional UPDATE. False when the line is not in this cart.</summary>
    Task<bool> SetQuantityAsync(int cartId, int variantId, short quantity, CancellationToken cancellationToken);

    /// <summary>A single DELETE. False when the line was not there, which is not an error.</summary>
    Task<bool> RemoveItemAsync(int cartId, int variantId, CancellationToken cancellationToken);

    /// <summary>
    /// Moves the 30-day / 180-day inactivity clock (doc 08 §3.5). A one-column last-writer-wins UPDATE.
    /// </summary>
    Task TouchAsync(int cartId, CancellationToken cancellationToken);

    /// <summary>
    /// Doc 08 §3.5's merge on login: guest lines are upserted into the customer's cart with the same
    /// clamp, then the guest cart is deleted. One transaction, so a failure leaves the guest cart intact
    /// rather than losing the lines.
    /// </summary>
    Task<CartMergeOutcome> MergeGuestIntoCustomerAsync(
        Guid guestToken,
        int customerUserId,
        CancellationToken cancellationToken);
}
