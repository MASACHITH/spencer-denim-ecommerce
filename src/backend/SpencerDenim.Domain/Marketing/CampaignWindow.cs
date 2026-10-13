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

    /// <summary>
    /// Doc 11 P3's derived status, which the document gives to a campaign and a promo code in one
    /// sentence: "Scheduled (start in future) → Live → Ended (by date) / Inactive (by staff) / Deleted
    /// (soft). These are derived from dates and flags, not stored statuses."
    /// </summary>
    /// <remarks>
    /// <b>It delegates to <see cref="PromoCodeSchedule.StatusOf"/> rather than restating the four
    /// branches</b>, and the shared <see cref="PromoCodeStatus"/> is the document's own shared concept
    /// rather than a type borrowed by accident. The name reads oddly on a campaign and the alternative —
    /// a second copy, or renaming the promo-code type and every test that mentions it two days before the
    /// deadline — is worse: a status a staff member reads has to mean the same thing on both screens, and
    /// the only way to guarantee that is for it to be the same function.
    /// </remarks>
    public static PromoCodeStatus StatusOf(
        bool isActive,
        DateTime startsAtUtc,
        DateTime endsAtUtc,
        DateTime utcNow) =>
        PromoCodeSchedule.StatusOf(isActive, startsAtUtc, endsAtUtc, utcNow);

    /// <summary>
    /// The instant a staff member meant by a wall-clock date and time in the shop, for a campaign's
    /// window.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="PromoCodeSchedule.ToUtc"/> so that Asia/Colombo appears in exactly one
    /// place in this codebase, which is the whole point of that type existing. "Ends at 23:59" has to mean
    /// the same thing on a campaign as on a code.
    /// </remarks>
    public static DateTime ToUtc(DateTime colomboLocal) => PromoCodeSchedule.ToUtc(colomboLocal);

    /// <summary>The wall-clock date and time in the shop for a stored instant, for the form to show back.</summary>
    public static DateTime ToColombo(DateTime utc) => PromoCodeSchedule.ToColombo(utc);
}
