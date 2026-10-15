using SpencerDenim.Domain.Analytics;

namespace SpencerDenim.Application.Analytics;

/// <summary>
/// What the recording endpoint was told about one page view, before anything is decided about it.
/// <para>
/// <b>There is no IP address field, and there must never be one.</b> The endpoint does not read
/// <c>RemoteIpAddress</c> or <c>X-Forwarded-For</c>, so nothing downstream can store it by accident.
/// </para>
/// </summary>
/// <param name="Path">The address the shopper was on: path, query and fragment, as the browser had it.</param>
/// <param name="Referrer">The referring URL, or null. Only its host survives.</param>
/// <param name="UserAgent">The browser's user agent, or null.</param>
/// <param name="Automated">The browser reported <c>navigator.webdriver</c>.</param>
/// <param name="SessionId">The visit-session cookie's value.</param>
/// <param name="OccurredAtUtc">When the API received the view.</param>
public sealed record PageViewSubmission(
    string? Path,
    string? Referrer,
    string? UserAgent,
    bool Automated,
    Guid SessionId,
    DateTime OccurredAtUtc);

/// <summary>
/// Turns a <see cref="PageViewSubmission"/> into the <see cref="PageView"/> row doc 08 §3.8 describes, or
/// into nothing. Pure: it is the whole of the privacy rule in one place that a unit test can hold.
/// </summary>
public static class PageViewClassifier
{
    /// <summary>
    /// The row to queue, or <see langword="null"/> when the address is not a storefront path
    /// (<see cref="PagePath.Normalise"/>). The page type is provisional for single-segment and listing
    /// paths and <see cref="PageView.ProductId"/>/<see cref="PageView.CategoryId"/> are unset; the writer
    /// resolves both against the catalogue in one query per batch.
    /// </summary>
    /// <param name="submission">What the endpoint was told.</param>
    /// <param name="internalHosts">The shop's own hosts: a referrer on one of them is a click inside the shop.</param>
    public static PageView? Classify(PageViewSubmission submission, IReadOnlySet<string> internalHosts)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(internalHosts);

        if (submission.SessionId == Guid.Empty || PagePath.Normalise(submission.Path) is not { } page)
        {
            return null;
        }

        var referrerHost = TrafficSourceClassifier.ReferrerHost(submission.Referrer, internalHosts);

        return new PageView
        {
            // datetime2(0): the second is all doc 08 §3.8 keeps, and truncating here rather than letting SQL
            // Server round means 23:59:59.9 Colombo stays on its own day.
            OccurredAtUtc = new DateTime(
                submission.OccurredAtUtc.Ticks - (submission.OccurredAtUtc.Ticks % TimeSpan.TicksPerSecond),
                DateTimeKind.Utc),
            SessionId = submission.SessionId,
            Path = page.Path,
            PageType = page.PageType,
            ReferrerHost = referrerHost,
            UtmSource = page.Campaign.Source,
            UtmMedium = page.Campaign.Medium,
            UtmCampaign = page.Campaign.Campaign,
            Channel = TrafficSourceClassifier.Classify(page.Campaign, referrerHost),
            DeviceType = UserAgentClassifier.Device(submission.UserAgent),
            IsBot = UserAgentClassifier.IsBot(submission.UserAgent, submission.Automated),
        };
    }
}
