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

    /// <summary>Caches a freshly assembled page for <see cref="HomePageCachePolicy.Lifetime"/>.</summary>
    void SetPage(HomePageDto page);

    /// <summary>
    /// Drops the cached page, so the next visitor sees the change immediately.
    /// <para>
    /// TODO(ADM-17): the content admin's save must call this — every write to
    /// <c>content.HomeSections</c> and <c>content.HomeSectionItems</c>, and the publish of a
    /// product that a row's listing would now include. Until those screens exist nothing calls it,
    /// which is why the entry expires on its own as well.
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
