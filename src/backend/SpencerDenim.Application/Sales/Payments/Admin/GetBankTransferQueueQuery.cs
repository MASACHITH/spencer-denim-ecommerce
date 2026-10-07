using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.Contracts;

namespace SpencerDenim.Application.Sales.Payments.Admin;

/// <summary>
/// "Orders → Bank transfers awaiting approval" (doc 11 P7 step 4, ADM-09; <c>Orders.View</c>).
/// </summary>
/// <param name="Page">1-based. Out of range is clamped, never refused — see the handler.</param>
/// <param name="PageSize">Clamped to <see cref="GetBankTransferQueueQueryHandler.MaxPageSize"/>.</param>
public sealed record GetBankTransferQueueQuery(int? Page, int? PageSize);

/// <summary>
/// Reads one page of the queue, oldest first.
/// </summary>
/// <remarks>
/// <b>Paging inputs are clamped rather than refused, and that is a decision rather than laxity.</b> This
/// is a work list a staff member reaches by clicking a sidebar link; a 400 for <c>?page=0</c> would turn a
/// hand-edited URL into an error page with no queue on it, and there is nothing a caller can learn from the
/// clamp. The catalogue's admin list screens refuse instead, because a bad page there is a sign a client is
/// building a URL wrongly and the data set is large enough that a silent clamp hides a real paging bug. The
/// difference is worth one sentence here rather than a surprise later.
/// </remarks>
public sealed class GetBankTransferQueueQueryHandler(IBankTransferReader reader, IClock clock)
    : IQueryHandler<GetBankTransferQueueQuery, BankTransferQueuePage>
{
    /// <summary>
    /// The default page size.
    /// <para>
    /// Twenty, because doc 11 §6.1 has staff working this queue twice a day: a shop taking a few dozen
    /// bank-transfer orders a day should see its whole backlog without paging, and anything bigger than
    /// that is a page a person scrolls past rather than reads.
    /// </para>
    /// </summary>
    public const int DefaultPageSize = 20;

    /// <summary>
    /// The ceiling. Each row carries a name, an email and a slip reference, so an unbounded page size is a
    /// payload a caller with <c>Orders.View</c> could make arbitrarily large.
    /// </summary>
    public const int MaxPageSize = 100;

    public async Task<BankTransferQueuePage> HandleAsync(
        GetBankTransferQueueQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = Math.Max(1, query.Page ?? 1);
        var pageSize = Math.Clamp(query.PageSize ?? DefaultPageSize, 1, MaxPageSize);

        return await reader.ReadQueueAsync(
            page,
            pageSize,

            // One "now" for the whole page, so two rows a second apart do not show times left that are
            // inconsistent with each other.
            clock.UtcNow.UtcDateTime,
            cancellationToken);
    }
}
