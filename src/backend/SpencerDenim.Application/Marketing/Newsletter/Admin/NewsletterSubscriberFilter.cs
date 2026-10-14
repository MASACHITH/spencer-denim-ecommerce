using SpencerDenim.Domain.Common;
using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.Application.Marketing.Newsletter.Admin;

/// <summary>
/// The newsletter list's filters (doc 11 P15 step 4: "filter by status and date"), shared by the list and
/// the export so the two can never disagree about which rows a filter means.
/// </summary>
/// <param name="Search">Part of an email address, or <see langword="null"/>.</param>
/// <param name="Status">One status, or <see langword="null"/> for both. <b>The export ignores this</b>.</param>
/// <param name="ConsentFrom">
/// The first <b>Colombo</b> calendar day to include, by consent date, or <see langword="null"/>. A date and
/// not an instant, because a staff member who picks "1 October" means the shop's 1 October.
/// </param>
/// <param name="ConsentTo">The last Colombo day to include, inclusive, or <see langword="null"/>.</param>
public sealed record NewsletterSubscriberFilter(
    string? Search,
    NewsletterSubscriberStatus? Status,
    DateOnly? ConsentFrom,
    DateOnly? ConsentTo)
{
    /// <summary>The longest search term honoured; longer input is cut.</summary>
    public const int MaxSearchLength = 100;

    /// <summary>The trimmed, upper-cased search term to compare with <c>EmailNormalized</c>, or null.</summary>
    public string? NormalisedSearch
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Search))
            {
                return null;
            }

            var trimmed = Search.Trim();

            return (trimmed.Length > MaxSearchLength ? trimmed[..MaxSearchLength] : trimmed)
                .ToUpperInvariant();
        }
    }

    /// <summary>
    /// The UTC instant the from-day starts at in Colombo (00:00 +05:30), or null.
    /// </summary>
    public DateTime? FromUtc => ConsentFrom is { } day ? ColomboMidnightUtc(day) : null;

    /// <summary>
    /// The UTC instant the day <b>after</b> the to-day starts at in Colombo — an exclusive upper bound, so
    /// the whole of the last day is included without guessing at its final millisecond.
    /// </summary>
    public DateTime? ToUtcExclusive => ConsentTo is { } day ? ColomboMidnightUtc(day.AddDays(1)) : null;

    private static DateTime ColomboMidnightUtc(DateOnly day) =>
        new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), ColomboTime.Offset).UtcDateTime;
}
