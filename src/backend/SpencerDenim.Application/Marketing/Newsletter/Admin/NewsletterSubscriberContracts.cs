namespace SpencerDenim.Application.Marketing.Newsletter.Admin;

/// <summary>
/// One page of the newsletter subscriber list (ADM-21, doc 11 P15 step 4; <c>Newsletter.View</c>).
/// </summary>
/// <param name="Rows">Most recent consent first.</param>
/// <param name="Page">1-based.</param>
/// <param name="PageSize">How many rows a page holds.</param>
/// <param name="TotalCount">Every subscriber matching the filters, not only this page's.</param>
/// <param name="SubscribedCount">
/// How many addresses are subscribed <b>across the whole table</b>, ignoring the filters — the figure an
/// export of everything would contain, so it reads the same whichever filter is open.
/// </param>
/// <param name="UnsubscribedCount">The same, for unsubscribed addresses.</param>
public sealed record NewsletterSubscriberPage(
    IReadOnlyList<NewsletterSubscriberRow> Rows,
    int Page,
    int PageSize,
    int TotalCount,
    int SubscribedCount,
    int UnsubscribedCount);

/// <summary>
/// One subscriber on the list.
/// </summary>
/// <remarks>
/// <b>The consent IP address is not on this payload</b>, deliberately. Doc 08 §3.6 records it as proof
/// the address was given willingly, and doc 11 P17 purges it after 90 days; nobody running a mailing list
/// needs to read it, and a list is the payload most likely to be screenshotted or pasted somewhere.
/// </remarks>
/// <param name="SubscriberId"><c>marketing.NewsletterSubscribers.Id</c>.</param>
/// <param name="Email">As the visitor typed it.</param>
/// <param name="Status"><c>Subscribed</c> or <c>Unsubscribed</c>: the name, never the number.</param>
/// <param name="Source">Where the address was given — <c>homepage</c> or <c>footer</c>.</param>
/// <param name="ConsentAtUtc">
/// When the <b>current</b> consent was given. A re-subscription after an unsubscribe is a new consent and
/// replaces it (doc 11 P15 step 2), so this is the date that matters for a mailing list.
/// </param>
/// <param name="UnsubscribedAtUtc">When they unsubscribed, or <see langword="null"/>.</param>
public sealed record NewsletterSubscriberRow(
    int SubscriberId,
    string Email,
    string Status,
    string Source,
    DateTime ConsentAtUtc,
    DateTime? UnsubscribedAtUtc);

/// <summary>
/// One subscriber as the export writes it. Subscribed addresses only, so there is no status column.
/// </summary>
public sealed record NewsletterExportRow(string Email, string Source, DateTime ConsentAtUtc);
