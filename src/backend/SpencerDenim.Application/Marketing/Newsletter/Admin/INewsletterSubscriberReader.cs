namespace SpencerDenim.Application.Marketing.Newsletter.Admin;

/// <summary>
/// The newsletter subscriber screen's reads (ADM-21, doc 11 P15 step 4).
/// </summary>
public interface INewsletterSubscriberReader
{
    /// <summary>One page of the list, most recent consent first.</summary>
    Task<NewsletterSubscriberPage> ReadPageAsync(
        NewsletterSubscriberFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every <b>subscribed</b> address matching the search and the date range, oldest consent first.
    /// </summary>
    /// <remarks>
    /// <b>The filter's status is ignored and <c>Subscribed</c> is applied instead</b>, in the query itself:
    /// an export is "for use in an external email tool" (doc 11 P15 step 4), and an unsubscribed address in
    /// it is a person who said stop being mailed again. Doing it here rather than trusting the caller to
    /// pass the right status means no caller can get it wrong.
    /// </remarks>
    Task<IReadOnlyList<NewsletterExportRow>> ReadSubscribedForExportAsync(
        NewsletterSubscriberFilter filter,
        CancellationToken cancellationToken);
}
