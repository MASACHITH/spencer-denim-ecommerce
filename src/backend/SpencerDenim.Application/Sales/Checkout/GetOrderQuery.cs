using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Checkout;

/// <summary>
/// Read one placed order by the unguessable token in its address (CHK-04).
/// </summary>
/// <remarks>
/// <b>Why the success page reads the order back rather than being handed it.</b> A confirmation rendered
/// from whatever the POST returned cannot survive a reload, a shared link, or the shopper pressing back —
/// and doc 11 P6 step 11 says guests reach their order through a link, which is a GET of exactly this.
/// So the POST answers with the order's address and the page fetches it, and the page a shopper bookmarks
/// is the same page they first saw.
/// <para>
/// <b><c>PublicId</c> is the whole credential, which is why it is a GUID.</b> Doc 08 §3.5 keeps it under a
/// unique index and never as a clustered key: a guest has no account, so there is nothing else the link
/// can carry, and a sequential id would let anyone read the next person's order. A wrong token gets 404
/// and nothing else — no "this order exists but is not yours", which would confirm the order exists.
/// </para>
/// <para>
/// <b>Not built here and owed:</b> doc 11 §7 G-41 asks for the slip-upload rate limit to be applied to
/// order-link views as well, and names doc 08 as the place to write it down. Nothing rate-limits this
/// endpoint. It is in the task report.
/// </para>
/// </remarks>
public sealed record GetOrderQuery(Guid PublicId);

/// <summary>
/// Reads the order, and asks its payment provider what the customer has to do next (PAY-01, PAY-03,
/// doc 11 P7 step 1).
/// </summary>
/// <remarks>
/// <b>The order comes from the snapshot and the instructions come from the provider, and the two must not
/// be confused.</b> Everything about the order — its lines, its prices, its totals — is read from
/// <c>sales.Orders</c> and <c>sales.OrderLines</c> and is frozen; the bank details are read live from
/// <c>ops.Settings</c>, because they are the shop's current account and not a fact about this order. Doc 11
/// P7's last exception turns on exactly that distinction: when a Super Admin changes the bank details,
/// "orders already placed keep the instructions they were emailed" — the <i>email</i> is the snapshot, and
/// there are no emails yet (W3-06). So this page shows the live details, which is the right answer for a
/// shopper who has not paid yet and the wrong one for a shopper who paid into the old account. G-16's
/// process rule covers the gap: the previous account stays able to receive funds for 48 hours after a
/// change.
/// <para>
/// <b>Four reads and not one, and only for an unpaid order.</b> A <c>Paid</c>, <c>Expired</c> or
/// <c>Cancelled</c> order has nothing for the customer to do, so neither the provider, the payment attempt
/// nor the proof is asked for and the payload is exactly what it was before this task. That keeps the order
/// page's cost unchanged for every order except the ones that are waiting for money.
/// </para>
/// <para>
/// <b>The provider is told which payment it is describing.</b> The live attempt is read first
/// (<see cref="IPaymentAttemptReader"/>) and its id and the provider's own reference travel in the
/// request, so a provider that redirects can find the page for the attempt that already exists instead of
/// starting another one on every view. Checkout cannot do the same at placement — the row does not exist
/// yet — and passes neither.
/// </para>
/// </remarks>
public sealed class GetOrderQueryHandler(
    ICheckoutReader reader,
    IOrderPaymentProofReader proofs,
    IPaymentProviderRegistry providers,
    IPaymentAttemptReader attempts)
    : IQueryHandler<GetOrderQuery, OrderConfirmationDto?>
{
    public async Task<OrderConfirmationDto?> HandleAsync(
        GetOrderQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Guid.Empty is a parse failure wearing a value's clothes: a client that sends `publicId=`
        // or a non-GUID gets the default, and looking it up would be a pointless query that could in
        // principle match a row written with a default. Refused without asking the database.
        if (query.PublicId == Guid.Empty)
        {
            return null;
        }

        var order = await reader.FindOrderAsync(query.PublicId, cancellationToken);

        if (order is null || order.Status != nameof(OrderStatus.PendingPayment))
        {
            return order;
        }

        // Money has arrived for this order and could not settle it (see OrderConfirmationDto.PaymentNotice),
        // so staff are dealing with it and the shopper is told so. No provider is asked how to pay: whatever
        // it would say - a bank account, a Pay now button - would invite a second payment next to the one
        // already received, and the page must never do that. The order is returned as read, notice included.
        if (order.PaymentNotice is not null)
        {
            return order;
        }

        var provider = providers.Find(order.PaymentMethodCode);

        if (provider is null)
        {
            // An order placed by a provider this build no longer has code for. The order is still readable
            // — it is the shopper's receipt — and the page simply has no instructions to print, which is the
            // same state as bank details that are not set yet.
            return order;
        }

        var attempt = await attempts.FindInFlightAsync(query.PublicId, cancellationToken);

        var instructions = await provider.DescribeAsync(
            new PaymentInstructionsRequest(
                order.OrderNumber,
                order.GrandTotal,
                order.PaymentDueAtUtc,

                // No absolute order link. Doc 11 P7 step 1 lists one because the instructions are an
                // *email*; on this page the link is the page, and the API has no configured storefront
                // origin to build one from. Inventing one from the request's own Host header would put a
                // forgeable value into a payload, which is the hole W1-01 round 3 closed for IP addresses.
                OrderUrl: null,

                // The attempt the customer would pay against, when there is one. Both are null for an
                // order with no live attempt (a bank transfer that was rejected and not resubmitted, say),
                // which every provider must be able to answer.
                PaymentId: attempt?.PaymentId,
                ProviderReference: attempt?.ProviderReference),
            cancellationToken);

        var proof = await proofs.FindAsync(query.PublicId, cancellationToken);

        return order with
        {
            Payment = instructions is null
                ? null
                : new OrderPaymentInstructionsDto(
                    instructions.ProviderCode,
                    instructions.Reference,
                    instructions.AmountLkr,
                    instructions.DueAtUtc,
                    instructions.Fields
                        .Select(field => new OrderPaymentFieldDto(field.Label, field.Value, field.Copyable))
                        .ToList(),
                    instructions.Notes,
                    instructions.RedirectUrl,
                    instructions.AcceptsProof,
                    instructions.RedirectForm is { } form
                        ? new OrderPaymentRedirectFormDto(
                            form.Method,
                            form.Action,
                            form.Fields
                                .Select(field => new OrderPaymentRedirectFieldDto(field.Name, field.Value))
                                .ToList())
                        : null),

            Proof = proof is null
                ? null
                : new OrderPaymentProofDto(
                    proof.Reference,
                    proof.FilePublicId,
                    proof.FileName,
                    proof.ContentType,
                    proof.SubmittedAtUtc),
        };
    }
}
