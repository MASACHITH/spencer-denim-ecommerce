using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Sales.Cart.Abstractions;

namespace SpencerDenim.Application.Sales.Cart;

/// <summary>
/// Fold a guest bag into a customer's account bag at sign-in (doc 08 §3.5, "Merge on login").
/// </summary>
/// <param name="GuestToken">
/// The token from the cookie the browser arrived with, or <see langword="null"/> when there was none.
/// </param>
public sealed record MergeGuestCartCommand(Guid? GuestToken, int CustomerUserId);

/// <summary>
/// <b>Merge, not replace and not refuse</b> — and the reasoning, because all three are defensible and
/// only one of them is right here.
/// <list type="bullet">
/// <item><description>
/// <b>Replace</b> (the account cart wins, the guest cart is dropped) throws away what the shopper was
/// doing in the last five minutes in favour of something they may have added weeks ago on another
/// device. That is the shape of the bug this project has already shipped twice: work silently lost on an
/// ordinary action.
/// </description></item>
/// <item><description>
/// <b>Refuse</b> (keep both, make them choose) needs a screen, a decision and a way to describe two
/// bags. It is more honest than replacing and much worse than merging for a shop this size, and nothing
/// in the scope baseline asks for it.
/// </description></item>
/// <item><description>
/// <b>Merge</b> keeps everything, and the only case it has to get right is the same variant in both
/// bags — where the quantities sum and clamp at 10, exactly as a second Add to Bag would. Doc 08 §3.5
/// specifies it ("guest items are upserted into the customer cart, capped at 10 … then the guest cart is
/// deleted"), so this is not a new decision so much as the one already taken.
/// </description></item>
/// </list>
/// <para>
/// <b>Nothing calls this yet, and that is reported rather than hidden.</b> There is no customer
/// authentication in the repository at all (ACC-01): the only cookie scheme is the admin one, so no
/// request can arrive with a customer principal and <c>Carts.CustomerUserId</c> is never written over
/// HTTP today. The logic and its tests live here because the merge is where a bag is silently lost if it
/// is written in a hurry at the end of ACC-01, and because <c>UX_Carts_Customer</c> is in the migration
/// to stop exactly the double-cart this produces when it is wrong. ACC-01 calls it after
/// <c>SignInAsync</c>; see the task report.
/// </para>
/// </summary>
public sealed class MergeGuestCartCommandHandler(ICartStore store, ICartReader reader)
    : ICommandHandler<MergeGuestCartCommand, CartView>
{
    public async Task<CartView> HandleAsync(MergeGuestCartCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.CustomerUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                command.CustomerUserId,
                "A merge needs the signed-in customer's user id.");
        }

        if (command.GuestToken is { } token)
        {
            await store.MergeGuestIntoCustomerAsync(token, command.CustomerUserId, cancellationToken);
        }

        // Read back whatever the customer now has — which may be nothing at all, if they had no account
        // cart and arrived with no guest cart either. A sign-in never creates an empty bag.
        var lookup = CartLookup.ForCustomer(command.CustomerUserId, command.GuestToken);
        var cart = await store.FindAsync(lookup, cancellationToken);
        if (cart is null)
        {
            return CartView.ForCustomer(Contracts.CartDto.Empty, command.GuestToken is not null);
        }

        var dto = await reader.ReadAsync(cart.Value.Id, cancellationToken);
        return CartView.ForCustomer(dto, command.GuestToken is not null);
    }
}
