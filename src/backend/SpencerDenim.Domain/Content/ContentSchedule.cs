namespace SpencerDenim.Domain.Content;

/// <summary>
/// The one rule that decides whether a piece of scheduled content is on the storefront right now.
/// <para>
/// <c>content.HomeSections</c>, <c>content.HomeSectionItems</c> and
/// <c>content.AnnouncementMessages</c> each carry an on/off flag and an optional
/// <c>StartsAtUtc</c>/<c>EndsAtUtc</c> window (doc 08 §3.7), and every reader has to apply them the
/// same way. It lives in the domain so the answer is identical whether it is asked in SQL, in a
/// projection or in a test.
/// </para>
/// </summary>
public static class ContentSchedule
{
    /// <summary>
    /// Whether content with this flag and window is live at <paramref name="utcNow"/>.
    /// <para>
    /// The start is inclusive and the end is exclusive: a window that ends at 18:00 is over at
    /// 18:00, which is what an admin who types an end time means, and it makes two consecutive
    /// windows join without a second of overlap. A missing bound means "no bound on that side"; a
    /// window whose start is after its end is never live, because the start test already fails.
    /// </para>
    /// </summary>
    public static bool IsLive(bool isEnabled, DateTime? startsAtUtc, DateTime? endsAtUtc, DateTime utcNow) =>
        isEnabled
        && (startsAtUtc is null || startsAtUtc.Value <= utcNow)
        && (endsAtUtc is null || endsAtUtc.Value > utcNow);
}
