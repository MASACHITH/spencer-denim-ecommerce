using SpencerDenim.Application.Content.Contracts;

namespace SpencerDenim.Application.Content.Abstractions;

/// <summary>
/// Read port for the storefront's content pages (GLB-10).
/// <para>
/// <b>Every method here returns published pages and nothing else</b>, and that is the port's whole
/// contract rather than a caller's responsibility. An unpublished page must not appear in navigation, must
/// not be served, and must not be in <c>sitemap.xml</c> — three different callers, so the filter belongs
/// once, in the one place all three go through. An <c>includeDrafts</c> flag here would be the single
/// parameter whose default decides whether a draft privacy policy is on the public internet.
/// </para>
/// <para>
/// The admin screens read the same tables through <see cref="SpencerDenim.Application.Content.Admin.Abstractions.IAdminContentReader"/>,
/// which deliberately does <i>not</i> filter — exactly the split <c>IAdminCatalogReader</c> makes against
/// the storefront's catalogue reader, and for the same reason: a screen that could not see a draft could
/// not publish one.
/// </para>
/// </summary>
public interface IContentPageReader
{
    /// <summary>
    /// One published page by slug, or <see langword="null"/> when no published page has that slug — which
    /// covers a draft, a deleted page and a slug that never existed, deliberately indistinguishably.
    /// </summary>
    Task<ContentPageDto?> GetPublishedAsync(string slug, CancellationToken cancellationToken);

    /// <summary>
    /// Every published page, by title, for the footer, the sitemap and the storefront's route guard.
    /// </summary>
    Task<ContentPageLinksDto> GetPublishedLinksAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The sixty-second cache for the published-page list, which is read during the server render of
/// <b>every</b> page on the site.
/// <para>
/// The same arrangement and the same reason as <c>ISiteSettingsCache</c>: without it a crawler walking two
/// thousand product pages is two thousand extra round trips for six rows that change once a year. The page
/// <i>bodies</i> are not cached here — one page's body is read only when somebody opens that page, and a
/// client who has just corrected a typo in their returns policy should see it corrected.
/// </para>
/// </summary>
public interface IContentPageLinksCache
{
    /// <summary>The cached list, or <see langword="null"/> when there is none or it has expired.</summary>
    ContentPageLinksDto? GetLinks();

    /// <summary>Stores a freshly read list.</summary>
    void SetLinks(ContentPageLinksDto links);

    /// <summary>Drops it, on any admin write that could change what is published or what it is called.</summary>
    void Evict();
}
