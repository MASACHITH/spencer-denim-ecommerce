using SpencerDenim.Domain.Content;

namespace SpencerDenim.Domain.Marketing;

/// <summary>
/// Whether a sale campaign is pricing anything right now (doc 11 P3's "Scheduled → Live → Ended /
/// Inactive / Deleted", which are "derived from dates and flags, not stored statuses").
/// </summary>
/// <remarks>
/// <b>It delegates to <see cref="ContentSchedule.IsLive"/> rather than restating the comparison.</b> The
/// start-inclusive, end-exclusive convention is the same one every scheduled row in this database uses —
/// announcements, home sections, promo codes — and "ends at 23:59" has to mean the same thing on a
/// campaign as on an announcement bar. A second copy of two comparisons is how the two come to disagree
/// by a second at a boundary, which is exactly where a campaign is watched.
/// <para>
/// <b><c>IsDeleted</c> is not a parameter.</b> A soft-deleted campaign is removed by the model's global
/// query filter (<c>ApplySoftDelete</c>, doc 08 §10.2) before any reader sees it, so asking this function
/// about one would mean a reader had already bypassed the filter with <c>IgnoreQueryFilters()</c> — which
/// only the admin list does, and it wants ended and removed campaigns.
/// </para>
/// </remarks>
public static class CampaignWindow
{
    /// <summary>
    /// True when <paramref name="isActive"/> is set and <paramref name="utcNow"/> is in
    /// <c>[startsAtUtc, endsAtUtc)</c>.
    /// </summary>
    public static bool IsLive(bool isActive, DateTime startsAtUtc, DateTime endsAtUtc, DateTime utcNow) =>
        ContentSchedule.IsLive(isActive, startsAtUtc, endsAtUtc, utcNow);
}
