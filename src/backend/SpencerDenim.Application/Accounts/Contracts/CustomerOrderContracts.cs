namespace SpencerDenim.Application.Accounts.Contracts;

/// <summary>
/// How to follow a shipped order (doc 11 P11 step 12: "courier tracking link"; doc 08 §3.5
/// <c>Couriers</c>).
/// </summary>
/// <param name="CourierName">
/// The <b>snapshot</b> taken when the order shipped, as <c>AdminOrderDispatchDto</c> explains: doc 11 P9
/// copies the courier's name into the order, so renaming or deleting a carrier does not rewrite a past
/// dispatch note.
/// </param>
/// <param name="TrackingNumber">What the courier gave. Always present when this block is.</param>
/// <param name="TrackingUrl">
/// The link, built from the courier's <b>current</b> template, or <see langword="null"/> when the courier
/// has no online tracking or has since been deleted — in which case the number is shown as text and the
/// shopper can quote it.
/// <para>
/// Doc 08 §3.5 is explicit that the order keeps the name and the number and never a built URL, so editing
/// a template changes the link on older orders too. That is the documented behaviour, not a side effect.
/// </para>
/// </param>
/// <param name="ShippedAtUtc">When it went.</param>
public sealed record CustomerOrderTrackingDto(
    string CourierName,
    string TrackingNumber,
    string? TrackingUrl,
    DateTime ShippedAtUtc);

/// <summary>
/// One row of My account's order list (ACC-02, doc 11 P11 step 12: "list (newest first) with number,
/// date, total, status").
/// </summary>
/// <remarks>
/// <b>It carries the tracking block as well as the four fields doc 11 names, and that is the whole point
/// of this task.</b> Before W3-01 a customer who placed an order had no way to look it up again: the
/// tracking number existed only in the admin panel. Putting it on the <i>list</i> rather than only on the
/// detail means the answer to "where is my parcel" is one page, not two.
/// </remarks>
/// <param name="Token">
/// The order's <c>PublicId</c>, which is what the detail page is addressed by — the same route a guest's
/// emailed link uses (<c>/order/{token}</c>).
/// <para>
/// <b>It is a secret, and sending it to its own owner does not weaken it.</b> Doc 11 P11 step 15 keeps
/// the link unguessable and never expiring because it is a guest's only credential; this payload is
/// behind a session cookie and only ever lists orders whose <c>CustomerUserId</c> is the caller's, so the
/// only person who learns a token here is the person the order belongs to. The alternative — a second
/// address for the same order, reachable only when signed in — would mean two code paths to the one page
/// and two chances to get the ownership filter wrong.
/// </para>
/// </param>
/// <param name="StatusLabel">
/// What the shopper reads, which is <b>not</b> the status name lower-cased: doc 11 P9's pickup variant
/// shows <c>Shipped</c> as "Ready for collection" and <c>Delivered</c> as "Collected". Rendered by the
/// same function the two admin screens and the guest order page use, so the four cannot disagree.
/// </param>
/// <param name="ItemCount">
/// How many items, counting quantities, so the row reads as "3 items" rather than as a number of lines.
/// </param>
/// <param name="FirstLineThumbnailUrl">
/// One picture, so the list is scannable. Null when the order's first line had no thumbnail — the stored
/// key is a snapshot (doc 08 §3.5), so a withdrawn photograph leaves a gap rather than quietly becoming a
/// different picture.
/// </param>
public sealed record CustomerOrderSummaryDto(
    string OrderNumber,
    Guid Token,
    string Status,
    string StatusLabel,
    DateTime PlacedAtUtc,
    decimal GrandTotal,
    string Currency,
    int ItemCount,
    string? FirstLineThumbnailUrl,
    CustomerOrderTrackingDto? Tracking);

/// <summary>
/// A page of My account's order list.
/// </summary>
/// <remarks>
/// <b>Paged, because a customer of a shop that has been trading for a year may have a hundred orders</b>
/// and the list carries a thumbnail per row. Doc 11 P11 step 12 asks only for "newest first"; the page
/// size is a decision, named in the task report.
/// </remarks>
public sealed record CustomerOrderPageDto(
    IReadOnlyList<CustomerOrderSummaryDto> Orders,
    int Page,
    int PageSize,
    int TotalCount)
{
    /// <summary>Whether there is another page after this one.</summary>
    public bool HasMore => Page * PageSize < TotalCount;
}
