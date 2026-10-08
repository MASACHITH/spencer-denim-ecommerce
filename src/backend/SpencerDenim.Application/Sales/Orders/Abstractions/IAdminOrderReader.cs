using SpencerDenim.Application.Sales.Orders.Contracts;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Orders.Abstractions;

/// <summary>
/// Every read the admin orders screen needs (ADM-09): the list, one order, its invoice and the courier
/// list a dispatch chooses from.
/// <para>
/// <b>Read port separate from <see cref="IOrderFulfilmentStore"/></b>, the split
/// <c>IBankTransferReader</c> / <c>IBankTransferReviewStore</c> established: the reads project to
/// contracts and track nothing, the writes are conditional statements whose row counts are the concurrency
/// control. Keeping them apart is what stops a read accidentally loading a tracked graph that a later
/// <c>SaveChanges</c> would write back.
/// </para>
/// </summary>
public interface IAdminOrderReader
{
    /// <summary>
    /// One page of the order list (doc 11 P9 step 1).
    /// </summary>
    /// <param name="search">
    /// A trimmed search term, or <see langword="null"/>. <b>The order number comes first</b>, because that
    /// is what a customer quotes on the telephone — see <see cref="AdminOrderSearch"/> for how a term is
    /// classified and which index each classification can use.
    /// </param>
    /// <param name="status">The status to show, or <see langword="null"/> for every status.</param>
    /// <param name="sort">Which end of the list comes first.</param>
    /// <param name="page">1-based.</param>
    /// <param name="pageSize">How many rows.</param>
    /// <param name="nowUtc">
    /// One "now" for the whole page, so two rows a second apart do not show times left that are
    /// inconsistent with each other.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<AdminOrderListPage> ReadListAsync(
        string? search,
        OrderStatus? status,
        AdminOrderSort sort,
        int page,
        int pageSize,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// One order in full, or <see langword="null"/> when there is no such order.
    /// </summary>
    /// <remarks>
    /// Found by its public token, which is what the list, every link and the shopper's own order page all
    /// carry (GLB-11). The internal id never leaves the server.
    /// </remarks>
    Task<AdminOrderDetailDto?> ReadDetailAsync(
        Guid orderPublicId,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// One order's invoice, or <see langword="null"/> when the order does not exist or has no invoice
    /// number yet.
    /// </summary>
    /// <remarks>
    /// <b>The two null cases are deliberately one answer.</b> An invoice exists from the moment the order
    /// is paid (doc 11 P9: "invoice printing is available once the order is Paid") and not before, so
    /// "that order has not been paid" and "there is no such order" are the same 404 — the same rule
    /// <c>GET /api/orders/{token}</c> follows, and the reason is the same: the token never expires, so the
    /// response must not become an oracle about which orders exist and which are paid.
    /// </remarks>
    Task<OrderInvoiceDto?> ReadInvoiceAsync(Guid orderPublicId, CancellationToken cancellationToken);

    /// <summary>Every courier, active first then by sort order, with how many orders each has carried.</summary>
    Task<IReadOnlyList<AdminCourierDto>> ReadCouriersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The order's internal id and the state a transition needs to know before a transaction opens, or
    /// <see langword="null"/> when there is no such order.
    /// </summary>
    /// <remarks>
    /// <b>Two round trips rather than one, deliberately</b>, exactly as <c>IBankTransferReader</c> does it:
    /// this read is outside the transaction and exists only to turn a public token into an internal id and
    /// let the API answer 404 before opening one. Every state it reports is re-read under the order row's
    /// own lock inside the store, which is why the store's refusals carry the current state rather than
    /// trusting this.
    /// </remarks>
    Task<AdminOrderTarget?> FindTargetAsync(Guid orderPublicId, CancellationToken cancellationToken);
}

/// <summary>
/// What a transition needs to know before a transaction opens.
/// </summary>
/// <param name="OrderId">Internal id.</param>
/// <param name="OrderNumber">For messages and the audit row.</param>
/// <param name="Status">As it was a moment ago. Re-read under the lock; never trusted for a guard.</param>
/// <param name="IsPickup">
/// Whether the delivery method is store pickup. Unlike the status this <b>cannot change</b> — doc 11 P9
/// makes the delivery method immutable after placement — so the use case may safely decide from it which
/// transition a request even means.
/// </param>
public sealed record AdminOrderTarget(
    int OrderId,
    string OrderNumber,
    OrderStatus Status,
    bool IsPickup);
