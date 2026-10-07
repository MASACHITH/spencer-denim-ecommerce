namespace SpencerDenim.Application.Sales.Payments.Abstractions;

/// <summary>
/// Doc 08 §4.1 / §5.6 and doc 11 P10 flow B: the <c>ExpireUnpaidOrders</c> job's one transaction per
/// order.
/// <para>
/// <b>This is the mechanism behind a promise the shop already makes.</b> Checkout writes
/// <c>Orders.PaymentDueAtUtc</c> and the success page tells a shopper their items are held until then; W3-02
/// could not enforce it and said so. Until this ran, every unpaid order held its stock for ever — which is
/// the half of issue #107 that is a real defect rather than a question, and the only reason a script could
/// take the whole catalogue offline with no payment and no account.
/// </para>
/// </summary>
public interface IOrderExpiryStore
{
    /// <summary>
    /// The ids of up to <paramref name="batchSize"/> orders past their payment deadline, oldest deadline
    /// first.
    /// </summary>
    /// <remarks>
    /// Read outside any transaction and treated as a hint, never as a decision: by the time an order is
    /// claimed it may have been paid or cancelled, and <see cref="ExpireAsync"/>'s conditional claim is
    /// what settles that. Doc 08 §5.6 step 1 is explicit about the shape — <c>TOP (50)</c> from
    /// <c>IX_Orders_Pending_Due</c>, then <b>one transaction per order</b>, so a long batch never holds
    /// stock-row locks that a live checkout is waiting on.
    /// </remarks>
    Task<IReadOnlyList<int>> FindDueOrdersAsync(
        int batchSize,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Expires one order in one transaction: order <c>PendingPayment → Expired</c> with
    /// <c>StockState → Released</c> and <c>ClosedAtUtc</c>, the stock given back line by line, every open
    /// payment (<c>Initiated</c> or <c>Pending</c>) → <c>Expired</c>, and a history row with source
    /// <c>SystemJob</c>.
    /// </summary>
    /// <returns>
    /// Whether this call is the one that expired it. <see langword="false"/> means the claim found 0 rows —
    /// somebody paid, cancelled, or a capture lock is in force — and doc 08 §5.6 step 2 says to skip it,
    /// which is not an error.
    /// </returns>
    Task<bool> ExpireAsync(int orderId, DateTime nowUtc, CancellationToken cancellationToken);
}
