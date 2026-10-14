using System.Linq.Expressions;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Sales.Payments;

/// <summary>
/// The order, as the payment flows read it: its number and current state, and the four facts the
/// <c>PaymentConfirmed</c> email is written from. A record class and deliberately not a
/// <c>record struct</c>.
/// </summary>
/// <remarks>
/// <b>This was a struct, and that was a defect an integration test found.</b>
/// <c>FirstOrDefaultAsync</c> over a projection to a value type cannot return null — it returns
/// <c>default</c> — so <c>OrderSnapshot?</c> came back with <c>HasValue = true</c> and every field
/// zeroed whenever the query matched nothing. The <c>is not { }</c> guard therefore never fired,
/// and a decision aimed at an order that was not a bank-transfer order carried on with
/// <c>Status = PendingPayment</c> (0) and <c>StockState = Reserved</c> (0) — the exact two values the
/// claim's own predicate was about to look for. A PayPal order was approved and paid.
/// <para>
/// A reference type makes "no such row" representable, which is the only reliable fix; the provider
/// guard was also moved <i>into</i> the claim statements, so the two defences no longer depend on each
/// other.
/// </para>
/// <para>
/// (Moved out of <c>BankTransferReviewStore</c> unchanged when the Paid effects were extracted: the
/// provider-facing settlement reads the same projection, and the lesson travels with the type.)
/// </para>
/// </remarks>
internal sealed record OrderSnapshot(
    string OrderNumber,
    Guid PublicId,
    OrderStatus Status,
    OrderStockState StockState,
    string Email,
    string FirstName,
    string? LastName,
    decimal GrandTotal)
{
    /// <summary>
    /// The projection, as an expression so each reader composes its own <c>Where</c> — the bank flow's
    /// filters to bank-transfer orders, the provider-facing settlement's to the payment's own order — and
    /// the generated <c>SELECT</c> list is the same in both.
    /// </summary>
    public static readonly Expression<Func<Order, OrderSnapshot>> FromOrder = order => new OrderSnapshot(
        order.OrderNumber,
        order.PublicId,
        order.Status,
        order.StockState,

        // For the email only (W3-06). Four more columns on a projection that already reads this
        // row, rather than a second query inside a transaction that holds the order's lock.
        order.Email,
        order.FirstName,
        order.LastName,
        order.GrandTotal);
}
