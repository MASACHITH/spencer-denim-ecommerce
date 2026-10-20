namespace SpencerDenim.Application.Sales.Checkout.Abstractions;

/// <summary>
/// How many orders one visitor address may place in a window (issue #107).
/// <para>
/// <b>Why it exists.</b> <c>POST /api/checkout/orders</c> needs no account (guest checkout, CHK-03) and
/// reserves stock before any money moves, so a script that loops over it can take the catalogue's
/// <c>Available</c> to zero with orders nobody means to pay for. The expiry job
/// (<c>ExpireUnpaidOrders</c>, doc 08 §5.6) is what gives that stock back; this is what stops the stock
/// leaving at machine speed in the meantime. It is a speed limit, not a ceiling on the total: an address
/// can still place its quota every window, and a script with many addresses is not slowed at all.
/// </para>
/// <para>
/// A port rather than ASP.NET Core's rate-limiting middleware, for the reason
/// <see cref="Marketing.Newsletter.INewsletterSignupThrottle"/> gives: <b>when</b> the permit is taken
/// matters, and only the use case knows. The middleware takes it before the endpoint runs, which would
/// charge the visitor for a form with a missing field, for a total that moved, and — the one that
/// matters most — <b>for a resubmission of an order that already exists</b>. A shopper retrying on a bad
/// connection is replaying a checkout key that has placed an order, and must be answered with that order,
/// not counted and not refused. So the handler asks for a permit only once it knows the request is a new
/// placement: after the replay check, after validation, after the total has been agreed.
/// </para>
/// </summary>
public interface IOrderPlacementThrottle
{
    /// <summary>
    /// Takes a permit for <paramref name="partitionKey"/>, or answers <see langword="false"/> when that
    /// partition has none left. Called only for a request that is about to reserve stock.
    /// </summary>
    /// <param name="partitionKey">The visitor's address, or null when there is none.</param>
    bool TryAcquire(string? partitionKey);

    /// <summary>How long a refused caller should wait, for the <c>Retry-After</c> header.</summary>
    TimeSpan Window { get; }
}
