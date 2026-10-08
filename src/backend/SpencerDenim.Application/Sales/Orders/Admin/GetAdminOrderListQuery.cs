using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Application.Sales.Orders.Contracts;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Orders.Admin;

/// <summary>
/// The admin order list (ADM-09, doc 11 P9 step 1; <c>Orders.View</c>).
/// </summary>
/// <param name="Search">
/// What the staff member typed. One box, classified by <see cref="AdminOrderSearch"/>: an order number, an
/// invoice number, an email address, a telephone number or a name.
/// </param>
/// <param name="Status">The status to show, or <see langword="null"/> for every status.</param>
/// <param name="Sort">
/// Which end first, or <see langword="null"/> to let the server choose — which is how doc 11 P9 step 1's
/// "Paid (oldest first)" is honoured. See the handler.
/// </param>
/// <param name="Page">1-based. Out of range is clamped, never refused — see the handler.</param>
/// <param name="PageSize">Clamped to <see cref="GetAdminOrderListQueryHandler.MaxPageSize"/>.</param>
public sealed record GetAdminOrderListQuery(
    string? Search,
    OrderStatus? Status,
    AdminOrderSort? Sort,
    int? Page,
    int? PageSize);

/// <summary>
/// Reads one page of the order list.
/// </summary>
/// <remarks>
/// <b>Paging inputs are clamped rather than refused</b>, the same decision
/// <c>GetBankTransferQueueQueryHandler</c> took and for the same reason: this is a work list a staff
/// member reaches by clicking a sidebar link, and a 400 for <c>?page=0</c> would turn a hand-edited URL
/// into an error page with no orders on it. The catalogue's admin lists refuse instead, because a bad page
/// there is a sign a client is building URLs wrongly.
/// </remarks>
public sealed class GetAdminOrderListQueryHandler(IAdminOrderReader reader, IClock clock)
    : IQueryHandler<GetAdminOrderListQuery, AdminOrderListPage>
{
    /// <summary>
    /// The default page size.
    /// <para>
    /// Twenty-five, matching the enquiry inbox rather than the bank queue's twenty: this list is the shop's
    /// main order screen rather than a backlog that should fit on one page, and the figure a staff member
    /// cares about is <c>TotalCount</c> and the status counts beside the tabs.
    /// </para>
    /// </summary>
    public const int DefaultPageSize = 25;

    /// <summary>
    /// The ceiling. Each row carries a name, an email address and a money figure, so an unbounded page
    /// size is a payload of customer data a caller with <c>Orders.View</c> could make arbitrarily large.
    /// </summary>
    public const int MaxPageSize = 100;

    public async Task<AdminOrderListPage> HandleAsync(
        GetAdminOrderListQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = Math.Max(1, query.Page ?? 1);
        var pageSize = Math.Clamp(query.PageSize ?? DefaultPageSize, 1, MaxPageSize);

        return await reader.ReadListAsync(
            query.Search,
            query.Status,
            DefaultSortFor(query.Status, query.Sort),
            page,
            pageSize,

            // One "now" for the whole page, so two rows a second apart do not show times left that are
            // inconsistent with each other.
            clock.UtcNow.UtcDateTime,
            cancellationToken);
    }

    /// <summary>
    /// Which way to sort when the caller did not say.
    /// </summary>
    /// <remarks>
    /// <b>Doc 11 P9 step 1 is a business rule and lives here rather than on the screen.</b> "Opens
    /// <b>Orders</b>, filter <b>Paid</b> (oldest first)" is what the shop is supposed to do: the oldest
    /// paid order is the one a customer has been waiting longest for, and a newest-first list buries
    /// exactly the row that is about to cost a complaint. Putting the rule in the client would mean every
    /// future client — a second screen, a report, a mobile view — had to remember it, and one of them
    /// would not.
    /// <para>
    /// An explicit <c>sort</c> from the caller always wins, so a staff member who wants the newest paid
    /// orders can have them. The answer echoes what was used (<see cref="AdminOrderListPage.Sort"/>), so
    /// the screen labels the list correctly either way rather than asserting a default it did not choose.
    /// </para>
    /// </remarks>
    public static AdminOrderSort DefaultSortFor(OrderStatus? status, AdminOrderSort? requested) =>
        requested ?? (status == OrderStatus.Paid ? AdminOrderSort.Oldest : AdminOrderSort.Newest);
}
