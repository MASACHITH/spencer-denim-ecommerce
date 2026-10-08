using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Orders.Abstractions;

namespace SpencerDenim.Application.Sales.Orders.Customer;

/// <summary>
/// A shopper cancelling their own unpaid order (doc 11 P10 flow A, step 1: "Opens the order (account or
/// order link) and clicks <b>Cancel order</b>").
/// </summary>
/// <param name="OrderPublicId">
/// The token in their order's address, and <b>the only credential there is</b>. There is no customer
/// sign-in anywhere in this build — ACC-01 is W3-01 — and P10 step 1 says "account <i>or</i> order link",
/// so the guest order page is enough and this does not wait for accounts.
/// </param>
public sealed record CancelOwnOrderCommand(Guid OrderPublicId);

/// <summary>
/// Hands a shopper's cancellation to the one transaction that applies it.
/// </summary>
/// <remarks>
/// <b>No reason is collected, and that is a decision rather than an omission.</b> Doc 11 P10 step 1 asks
/// the <i>staff</i> member for a reason and asks the customer only to press the button. So
/// <c>Orders.CancelReason</c> stays null on a customer cancel: the order's history row carries source
/// <c>Customer</c>, which is the record P10 step 3 asks for and what the admin timeline shows ("by the
/// customer"). Writing a sentence of our own into a column the admin screen prints under "Why it was
/// cancelled" would be putting words in a shopper's mouth, and offering them a free-text box would be a
/// public write into a staff screen that this task has no reason to open.
/// <para>
/// <b>It owns no guard of its own.</b> Every condition — the order is still awaiting payment, its stock is
/// still reserved, no capture is in flight, nothing has settled it — is evaluated inside the store's
/// conditional <c>UPDATE</c>, under the order row's own lock (doc 08 §5.2). A check out here would be a
/// second answer to the same question, read a moment earlier, and the moment is the whole problem: the
/// expiry sweep can take the order between the check and the write.
/// </para>
/// <para>
/// <b>Nothing is emailed and nothing says one was.</b> <c>ops.EmailOutbox</c> is W3-06's, and doc 11 P10's
/// notification list says there is no email on cancellation in any case (§7 G-14).
/// </para>
/// </remarks>
public sealed class CancelOwnOrderCommandHandler(IOrderCancellationStore store, IClock clock)
    : ICommandHandler<CancelOwnOrderCommand, CancelOwnOrderResult>
{
    public async Task<CancelOwnOrderResult> HandleAsync(
        CancelOwnOrderCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var outcome = await store.CancelAsync(
            new OrderCancellation(
                command.OrderPublicId,

                // **The source genuinely differs by caller**, and this is the line that makes it so. The
                // history row is the only record of who closed the order, and "the customer changed their
                // mind" and "a colleague closed it after a telephone call" are different facts about the
                // same order.
                OrderCancelledBy.Customer,

                // No staff actor: nothing in ops.AuditLogs, which records what staff did (doc 08 §3.8),
                // and no user id on the history row, because there is no customer sign-in to have one.
                Actor: null,
                Reason: null,
                clock.UtcNow.UtcDateTime),
            cancellationToken);

        return outcome switch
        {
            CancelOrderOutcome.Applied applied => new CancelOwnOrderResult.Cancelled(applied.OrderNumber),

            // **One answer for "no such order" and "that order cannot be cancelled", and it is the
            // narrower one of the two.** The token never expires (doc 11 §7 G-41) and is a guest's only
            // credential, so a stranger holding a guessed GUID must not learn that it names a real order
            // — which is exactly the rule `GET /api/orders/{token}` and the payment-reference route
            // already follow, and the reason their refusals are bare 404s with no body.
            //
            // The cost is real and is worth stating: a shopper whose press lost the race to the expiry
            // sweep is told "this order can no longer be cancelled" rather than "it expired a minute
            // ago". The page's sentence covers both and tells them to reload, and the page then shows the
            // order's actual state from `GET /api/orders/{token}` — which is where a shopper holding the
            // link is entitled to the detail, because holding the link is the credential.
            //
            // Doc 11 P10's edge case gives the PayPal case a distinguishable sentence ("payment is being
            // processed"). Saying that here would be a deliberate oracle, and it is W4-01's decision to
            // make when there is a capture that can be in progress.
            _ => new CancelOwnOrderResult.NotCancellable(),
        };
    }
}

/// <summary>What the API should answer.</summary>
public abstract record CancelOwnOrderResult
{
    private CancelOwnOrderResult()
    {
    }

    /// <summary>Cancelled. 200, and the page re-reads the order to redraw from the server.</summary>
    public sealed record Cancelled(string OrderNumber) : CancelOwnOrderResult;

    /// <summary>
    /// No such order, or it can no longer be cancelled. A bare 404 — see the handler's remarks for why
    /// those two must be one answer.
    /// </summary>
    public sealed record NotCancellable : CancelOwnOrderResult;
}
