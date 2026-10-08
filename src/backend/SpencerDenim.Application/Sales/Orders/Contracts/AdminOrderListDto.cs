using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Orders.Contracts;

/// <summary>
/// One page of the admin order list (ADM-09, doc 11 P9 step 1).
/// </summary>
/// <param name="Rows">In the order the query asked for.</param>
/// <param name="Page">1-based.</param>
/// <param name="PageSize">How many rows a page holds.</param>
/// <param name="TotalCount">
/// Every order matching the filter, not only this page's. It is what tells a staff member whether the
/// queue is under control, and doc 11 §6.1 has them reading it twice a day.
/// </param>
/// <param name="Sort">
/// Which order the rows are actually in. <b>Echoed rather than assumed</b>, because the server chooses it
/// when the caller does not: doc 11 P9 step 1 says the Paid filter is worked oldest first, and a screen
/// that drew "Newest first" over a list the server sorted the other way would be lying about the one
/// thing this list is read for.
/// </param>
/// <param name="StatusCounts">
/// How many orders sit in each status, across the whole table and <b>ignoring the search term and the
/// status filter</b>.
/// <para>
/// It is the work list's own summary — "eleven paid, nothing started" — so it has to be the same figure
/// whichever tab is open. Scoping it to the current filter would make the Paid tab's own count change
/// when somebody typed in the search box, which is the opposite of what a count beside a tab means.
/// </para>
/// </param>
public sealed record AdminOrderListPage(
    IReadOnlyList<AdminOrderListRow> Rows,
    int Page,
    int PageSize,
    int TotalCount,
    AdminOrderSort Sort,
    IReadOnlyDictionary<string, int> StatusCounts);

/// <summary>
/// One row of the admin order list.
/// </summary>
/// <param name="OrderPublicId">
/// The token every action and link uses. <b>Never the internal id</b> — GLB-11 keeps ids out of URLs, and
/// this is the same token the shopper's own order link carries.
/// </param>
/// <param name="OrderNumber">
/// What the customer quotes on the telephone, which is why it is the first thing the search matches.
/// </param>
/// <param name="Status">
/// The status name, e.g. <c>Shipped</c>. A name and not a number: the number is the database's contract
/// (doc 08 §1.4) and a screen that rendered 3 would be unreadable, while a client that branched on 3
/// would break the day a member was added.
/// </param>
/// <param name="StatusLabel">
/// What a staff member should read, which is <b>not always the status name</b>: a pickup order in
/// <c>Shipped</c> is "Ready for collection" and a pickup order in <c>Delivered</c> is "Collected" (doc 11
/// P9's pickup variant). Rendered on the server so the list, the detail and the shopper's own page cannot
/// disagree about one order's wording.
/// </param>
/// <param name="CustomerName">First and last name as the order snapshot holds them.</param>
/// <param name="CustomerEmail">For matching a caller to their order when they cannot find the number.</param>
/// <param name="GrandTotal">What was charged.</param>
/// <param name="Currency">Sent so a page never assumes.</param>
/// <param name="PaymentProviderCode">Bank transfer or PayPal, for the list's own filter chip.</param>
/// <param name="DeliveryMethodName">The snapshot, so a renamed method does not rewrite an old row.</param>
/// <param name="IsPickup">
/// Whether this is a store-pickup order. It changes the wording, the actions and whether the auto-deliver
/// job may ever touch it (doc 08 §4.1 G-18), so it travels as a fact rather than being re-derived from a
/// code string by every reader.
/// </param>
/// <param name="CourierName">The snapshot taken when it shipped, or <see langword="null"/>.</param>
/// <param name="TrackingNumber">What the courier gave, or <see langword="null"/>.</param>
/// <param name="InvoiceNumber">Issued at payment, or <see langword="null"/> for an unpaid order.</param>
/// <param name="PlacedAtUtc">When it was placed.</param>
/// <param name="PaidAtUtc">When the money was taken, or <see langword="null"/>.</param>
/// <param name="SecondsUntilExpiry">
/// For an order still awaiting payment: time left on the server's clock, negative once it has passed.
/// <see langword="null"/> for every other status, because an order that is paid, expired or cancelled has
/// no deadline left to run out.
/// <para>
/// A number of seconds rather than a rendered sentence, and from the server rather than the browser, for
/// the reason <c>BankTransferQueueRow</c> records: a sentence is stale the moment it is serialised, and a
/// shop computer with a wrong clock would otherwise show a list whose deadlines are all wrong with nothing
/// on the page saying so.
/// </para>
/// </param>
public sealed record AdminOrderListRow(
    Guid OrderPublicId,
    string OrderNumber,
    string Status,
    string StatusLabel,
    string CustomerName,
    string CustomerEmail,
    decimal GrandTotal,
    string Currency,
    string PaymentProviderCode,
    string DeliveryMethodName,
    bool IsPickup,
    string? CourierName,
    string? TrackingNumber,
    string? InvoiceNumber,
    DateTime PlacedAtUtc,
    DateTime? PaidAtUtc,
    long? SecondsUntilExpiry);

/// <summary>Which end of the list comes first.</summary>
/// <remarks>
/// <b>Two values and not a sort-column vocabulary</b>, because the list has exactly one meaningful sort:
/// doc 11 P9 step 1 works the Paid filter oldest first, and everything else is read newest first. A
/// general "sort by any column" would be a surface to test with nothing behind it, and the two indexes
/// that make this list fast (<c>IX_Orders_Status_Placed</c> and <c>IX_Orders_Placed</c>) are both keyed on
/// <c>PlacedAtUtc</c> alone.
/// </remarks>
public enum AdminOrderSort
{
    /// <summary>Newest placed first. The default for every filter but Paid.</summary>
    Newest = 0,

    /// <summary>
    /// Oldest placed first. Doc 11 P9 step 1's own words for the Paid filter: the oldest paid order is the
    /// one a customer has been waiting longest for, and a newest-first list buries it.
    /// </summary>
    Oldest = 1,
}

/// <summary>
/// The wording a staff member and a shopper both read for one order, which is not always the status name.
/// </summary>
/// <remarks>
/// <b>One function, on the server, because more than one screen asks the same question.</b> Doc 11 P9's
/// pickup variant says <c>Shipped</c> "is shown to staff and customer as 'Ready for collection'" — and the
/// admin list and the admin detail both render an order's state. Two copies of the same conditional is two
/// chances for a pickup order to be described as shipped on one of them.
/// </remarks>
public static class OrderStatusLabels
{
    /// <summary>What to show for <paramref name="status"/> on an order whose method is store pickup or not.</summary>
    public static string For(OrderStatus status, bool isPickup) => (status, isPickup) switch
    {
        // Doc 11 P9's pickup variant. "Shipped" on an order nobody is shipping would read as a mistake,
        // and "Delivered" on one the customer walked in and took is the wrong verb for the same event.
        (OrderStatus.Shipped, true) => "Ready for collection",
        (OrderStatus.Delivered, true) => "Collected",

        (OrderStatus.PendingPayment, _) => "Awaiting payment",
        (OrderStatus.Paid, _) => "Paid",
        (OrderStatus.Processing, _) => "Processing",
        (OrderStatus.Shipped, _) => "Shipped",
        (OrderStatus.Delivered, _) => "Delivered",
        (OrderStatus.Cancelled, _) => "Cancelled",
        (OrderStatus.Expired, _) => "Expired",
        (OrderStatus.Refunded, _) => "Refunded",

        // Unreachable while OrderStatus has eight members, and deliberately not a throw: a status added by
        // a later milestone must not make an order list 500. The name is the honest fallback.
        _ => status.ToString(),
    };
}
