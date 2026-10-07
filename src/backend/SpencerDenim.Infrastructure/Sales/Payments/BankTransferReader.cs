using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Payments;

/// <summary>
/// The bank-transfer flow's reads (doc 11 P7 steps 3 and 4).
/// </summary>
internal sealed class BankTransferReader(AppDbContext dbContext) : IBankTransferReader
{
    /// <summary>
    /// A page of the queue, oldest first, with the time left on every row.
    /// </summary>
    /// <remarks>
    /// <b>The two filter values are written as literals and that is the point of this method (DB28).</b>
    /// <c>IX_Payments_BankQueue</c> is filtered
    /// <c>WHERE ProviderCode = 'BankTransfer' AND Status = 1</c>, and SQL Server matches a filtered index
    /// only when the query's predicate <i>provably implies</i> the filter. A literal implies it; a
    /// parameter does not — <c>ProviderCode = @code</c> cannot be proved to equal <c>'BankTransfer'</c> at
    /// compile time, so the plan falls back to scanning <c>sales.Payments</c>. #103's review flagged this
    /// index as the one most likely to be silently unmatched once the queue was written, so:
    /// <list type="bullet">
    /// <item><description>
    /// the provider code and the status are <b>constants in an EF expression tree</b>, not captured
    /// variables. <c>PaymentProviderCodes.BankTransfer</c> is a <c>const string</c> and
    /// <c>PaymentStatus.Pending</c> a compile-time enum member, so Roslyn bakes them in and EF emits
    /// literals. A field, a local or a parameter in their place would be parameterised and the index would
    /// stop being used, with nothing failing and nothing saying so;
    /// </description></item>
    /// <item><description>
    /// <c>BankQueuePlanTests</c> asserts the plan actually seeks it, because this is not the sort of thing
    /// a correctness test can see.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>The order's own join is not in the filter and does not need to be.</b> The index narrows
    /// <c>sales.Payments</c> to the rows awaiting review and hands their <c>OrderId</c> to a key lookup on
    /// <c>PK_Orders</c>; it is <c>CreatedAtUtc</c> that keys the index, which is also the sort, so "oldest
    /// first" is free.
    /// </para>
    /// </remarks>
    public async Task<BankTransferQueuePage> ReadQueueAsync(
        int page,
        int pageSize,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var awaiting = dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.ProviderCode == PaymentProviderCodes.BankTransfer
                && payment.Status == PaymentStatus.Pending);

        var total = await awaiting.CountAsync(cancellationToken);

        var rows = await awaiting

            // Oldest first: the queue is a work list and the oldest row is the closest to expiring.
            // IX_Payments_BankQueue is keyed on CreatedAtUtc, so this sort is the index's own order.
            .OrderBy(payment => payment.CreatedAtUtc)
            .ThenBy(payment => payment.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(payment => new
            {
                payment.Order!.PublicId,
                payment.Order.OrderNumber,
                payment.Order.FirstName,
                payment.Order.LastName,
                payment.Order.Email,
                payment.Order.PlacedAtUtc,
                payment.Order.PaymentDueAtUtc,
                payment.Amount,
                payment.Currency,
                payment.PayerReference,

                // The slip, through the payment's own FK. A private asset: this token only opens through
                // the authorised endpoint, and media.MediaAssets is soft-deletable, so a replaced slip
                // must not come back as the current one.
                SlipPublicId = payment.ProofAsset != null && !payment.ProofAsset.IsDeleted
                    ? payment.ProofAsset.PublicId
                    : (Guid?)null,
                SlipContentType = payment.ProofAsset != null && !payment.ProofAsset.IsDeleted
                    ? payment.ProofAsset.ContentType
                    : null,
            })
            .ToListAsync(cancellationToken);

        return new BankTransferQueuePage(
            rows
                .Select(row => new BankTransferQueueRow(
                    row.PublicId,
                    row.OrderNumber,
                    string.IsNullOrWhiteSpace(row.LastName) ? row.FirstName : $"{row.FirstName} {row.LastName}",
                    row.Email,
                    row.Amount,
                    row.Currency,
                    row.PayerReference,
                    row.SlipPublicId,
                    row.SlipContentType,
                    row.PlacedAtUtc,
                    row.PaymentDueAtUtc,

                    // Negative once the deadline has passed, deliberately: "overdue by eleven minutes" and
                    // "due now" are different facts to somebody deciding whether to approve. Computed here
                    // rather than in the browser because the server owns "now".
                    (long)Math.Round((row.PaymentDueAtUtc - nowUtc).TotalSeconds)))
                .ToList(),
            page,
            pageSize,
            total);
    }

    public async Task<BankTransferTarget?> FindTargetAsync(
        Guid orderPublicId,
        CancellationToken cancellationToken)
    {
        var order = await dbContext.Orders
            .AsNoTracking()
            .Where(candidate =>
                candidate.PublicId == orderPublicId
                && candidate.PaymentProviderCode == PaymentProviderCodes.BankTransfer)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.OrderNumber,
                candidate.Status,
                candidate.StockState,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (order is null)
        {
            return null;
        }

        // The newest bank row in a state any of the three decisions could act on. Doc 08 §4.3 N7: a
        // rejection closes a row and a resubmission opens another, so an order can hold several and only
        // the last matters. The store resolves it again under the order's lock; this is for the screen and
        // for the API's 404, nothing more.
        var payment = await dbContext.Payments
            .AsNoTracking()
            .Where(candidate =>
                candidate.OrderId == order.Id
                && candidate.ProviderCode == PaymentProviderCodes.BankTransfer
                && (candidate.Status == PaymentStatus.Pending || candidate.Status == PaymentStatus.Expired))
            .OrderByDescending(candidate => candidate.Id)
            .Select(candidate => new { candidate.Id, candidate.Status })
            .FirstOrDefaultAsync(cancellationToken);

        return new BankTransferTarget(
            order.Id,
            order.OrderNumber,
            order.Status,
            order.StockState,
            payment?.Id,
            payment?.Status);
    }

    /// <summary>
    /// The open payment of a <c>PendingPayment</c> bank order, found by its public token.
    /// </summary>
    /// <remarks>
    /// <b>Every condition is in the query, so there is one answer for every way this can fail.</b> No such
    /// token, a PayPal order, an order that has been paid, expired or cancelled, and a bank order whose
    /// payment was rejected and not resubmitted all come back <see langword="null"/> — and the endpoint
    /// turns that into one 404 with no body. The alternative, reporting which of them it was, would make
    /// the order link an oracle: <c>Orders.PublicId</c> never expires (G-41) and the token is the only
    /// credential a guest has, so "that order exists but is already paid" is more than a stranger holding
    /// a guessed GUID should learn. It is the same rule <c>GET /api/orders/{token}</c> already follows.
    /// </remarks>
    public async Task<CustomerBankPayment?> FindOpenPaymentAsync(
        Guid orderPublicId,
        CancellationToken cancellationToken) =>
        await dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.Order!.PublicId == orderPublicId
                && payment.Order.Status == OrderStatus.PendingPayment
                && payment.ProviderCode == PaymentProviderCodes.BankTransfer
                && payment.Status == PaymentStatus.Pending)
            .Select(payment => new CustomerBankPayment(
                payment.OrderId,
                payment.Id,
                payment.ProofAssetId,
                payment.PayerReference,

                // A soft-deleted asset is a slip the customer has already replaced. It stays on disk for
                // the retention window (doc 08 §8.4) but it is not the current file, and showing it back
                // would tell them the wrong one is with the shop.
                payment.ProofAsset != null && !payment.ProofAsset.IsDeleted
                    ? payment.ProofAsset.PublicId
                    : null,
                payment.ProofAsset != null && !payment.ProofAsset.IsDeleted
                    ? payment.ProofAsset.OriginalFileName
                    : null,
                payment.ProofAsset != null && !payment.ProofAsset.IsDeleted
                    ? payment.ProofAsset.ContentType
                    : null,
                payment.ProofAsset != null && !payment.ProofAsset.IsDeleted
                    ? payment.ProofAsset.CreatedAtUtc
                    : null))
            .FirstOrDefaultAsync(cancellationToken);
}
