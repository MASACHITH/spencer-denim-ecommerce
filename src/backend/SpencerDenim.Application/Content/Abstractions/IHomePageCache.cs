using SpencerDenim.Application.Content.Contracts;

namespace SpencerDenim.Application.Content.Abstractions;

/// <summary>
/// The homepage's short-lived cache (doc 08 §3.7: "homepage output cached 5 min and evicted on
/// save").
/// <para>
/// It is here rather than on the read path because of the second half of that sentence: the thing
/// that evicts is the admin write side (ADM-17), which will not know about a cache hidden inside a
/// query handler. <see cref="Evict"/> is the hook it calls.
/// </para>
/// <para>
/// The homepage is the busiest page on the site and the most expensive one to assemble: the
/// sections, their items, and a listing query for each product row — 16 SQL commands for a render
/// of the seeded content, 18 when a row points at a department listing, against 5 for a whole
/// listing page. Five minutes of that is one render per section change instead of one per visitor.
/// </para>
/// </summary>
public interface IHomePageCache
{
    /// <summary>The cached page, or <c>null</c> when there is none or it has expired.</summary>
    HomePageDto? GetPage();

    /// <summary>
    /// Caches a freshly assembled page until the <b>earlier</b> of <see cref="HomePageCachePolicy.Lifetime"/>
    /// from now and <paramref name="validUntilUtc"/> (issue #37).
    /// <para>
    /// <paramref name="validUntilUtc"/> is the next instant at which the page's own content stops being
    /// true — the soonest <c>StartsAtUtc</c> or <c>EndsAtUtc</c> still ahead among the switched-on
    /// sections and items. Without it a section whose window ended inside a warm entry was served for up
    /// to five more minutes: measured at ~250 s after its end. With it the entry goes at the boundary, so a
    /// campaign set to end at midnight is gone at midnight. Null means nothing is scheduled to change.
    /// </para>
    /// </summary>
    void SetPage(HomePageDto page, DateTime? validUntilUtc);

    /// <summary>
    /// Drops the cached page, so the next visitor sees the change immediately. Every ADM-17 content write
    /// calls it after its save (doc 08 §3.7, "evicted on save").
    /// <para>
    /// <b>It reaches this process only.</b> A second API instance keeps its own entry until its own
    /// expiry — the five-minute lifetime or the next schedule boundary, whichever is sooner — which is the
    /// allowance doc 08 §7.4 records.
    /// </para>
    /// </summary>
    void Evict();
}

/// <summary>The one number the cache is built on, so nothing has to guess at it.</summary>
public static class HomePageCachePolicy
{
    /// <summary>Doc 08 §3.7. Five minutes, then a render regardless of whether anyone evicted.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
}
