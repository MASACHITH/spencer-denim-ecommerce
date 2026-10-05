using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Application.Sales.Checkout.Contracts;

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

/// <summary>Reads the order. One call, because the whole payload is a projection of two tables.</summary>
public sealed class GetOrderQueryHandler(ICheckoutReader reader)
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
        return query.PublicId == Guid.Empty
            ? null
            : await reader.FindOrderAsync(query.PublicId, cancellationToken);
    }
}
