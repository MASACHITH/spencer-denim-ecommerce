using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Payments.Abstractions;

/// <summary>
/// Every read the bank-transfer flow needs: the staff queue (doc 11 P7 step 4), the one order a staff
/// decision is aimed at, and the open payment a customer may attach a reference or a slip to (P7 step 3).
/// <para>
/// <b>Read port separate from <see cref="IBankTransferReviewStore"/></b>, the split
/// <c>IAdminCatalogReader</c> / <c>ICatalogStructureStore</c> established: the reads project to contracts
/// and track nothing, the writes are conditional statements whose row counts are the concurrency control.
/// Keeping them apart is what stops a read accidentally loading a tracked graph that a later
/// <c>SaveChanges</c> would write back.
/// </para>
/// </summary>
public interface IBankTransferReader
{
    /// <summary>
    /// "Orders → Bank transfers awaiting approval", <b>oldest first</b> (doc 11 P7 step 4).
    /// </summary>
    /// <remarks>
    /// Oldest first because the queue is a work list and the oldest row is the one closest to expiring —
    /// doc 11 §6.1 has staff working it twice a day, and a newest-first list would bury exactly the row
    /// that is about to cost a sale.
    /// </remarks>
    Task<BankTransferQueuePage> ReadQueueAsync(
        int page,
        int pageSize,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// The order a staff decision names, with the id of the bank payment that decision would act on, or
    /// <see langword="null"/> when there is no such order.
    /// </summary>
    /// <remarks>
    /// <b>Two round trips rather than one, deliberately.</b> This read is outside the transaction and is
    /// only there to turn a public token into an internal id and to let the API answer 404 before it opens
    /// one. Everything it reports about the order's <i>state</i> is re-read under the order row's lock
    /// inside the transaction, because anything read here can have changed by then — which is the whole
    /// reason the store's refusals carry the current state rather than trusting this.
    /// </remarks>
    Task<BankTransferTarget?> FindTargetAsync(Guid orderPublicId, CancellationToken cancellationToken);

    /// <summary>
    /// The open bank payment of a <c>PendingPayment</c> order reached by its public token, which is the
    /// only thing a customer may attach a reference or a slip to (doc 08 §3.3 N10).
    /// <see langword="null"/> when the order is not in that state, so "no such order" and "that order can
    /// no longer be paid" are the same answer to anybody holding a guessed token.
    /// </summary>
    Task<CustomerBankPayment?> FindOpenPaymentAsync(Guid orderPublicId, CancellationToken cancellationToken);
}

/// <summary>
/// What a staff decision needs to know before a transaction opens: which rows, and whether this is a bank
/// order at all.
/// </summary>
/// <param name="OrderId">Internal id.</param>
/// <param name="OrderNumber">For messages and the audit row.</param>
/// <param name="OrderStatus">As it was a moment ago. Re-read under the lock; never trusted for a guard.</param>
/// <param name="StockState">Likewise.</param>
/// <param name="PaymentId">
/// The bank payment this decision acts on, or <see langword="null"/> when the order has none in a
/// reviewable state.
/// <para>
/// For an approval or a rejection it is the open <c>Pending</c> row. For a late acceptance it is the
/// <b>latest</b> bank row in <c>Expired</c> or <c>Pending</c> (doc 08 §4.3's N7 rule: the Expired row
/// itself settles, or a Pending row created by a resubmission after a rejection).
/// </para>
/// </param>
/// <param name="PaymentStatus">That row's status, for the same "diagnose, do not guard" purpose.</param>
public sealed record BankTransferTarget(
    int OrderId,
    string OrderNumber,
    OrderStatus OrderStatus,
    OrderStockState StockState,
    int? PaymentId,
    PaymentStatus? PaymentStatus);

/// <summary>
/// The open bank payment a customer may add a reference or a slip to (doc 11 P7 step 3).
/// </summary>
/// <param name="OrderId">Internal order id.</param>
/// <param name="PaymentId">The open <c>Pending</c> bank row. A re-upload replaces its proof asset.</param>
/// <param name="ProofAssetId">
/// The slip currently on the payment, so a replacement can soft-delete it (doc 08 §3.3 N10: "at most one
/// current file per payment").
/// </param>
public sealed record CustomerBankPayment(int OrderId, int PaymentId, int? ProofAssetId);
