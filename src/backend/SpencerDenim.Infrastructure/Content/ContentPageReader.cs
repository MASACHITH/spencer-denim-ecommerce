using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Contracts;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Content;

/// <summary>
/// The storefront's content pages, read in one query each (GLB-10).
/// <para>
/// <b>Every query here carries <c>IsPublished</c> in its <c>WHERE</c> clause, and there is no code path
/// through this class that does not.</b> That is the one thing this file has to get right: an unpublished
/// privacy policy must not be servable, must not be in the footer and must not be in
/// <c>sitemap.xml</c>, and all three go through here. The soft-delete filter is applied by the context's
/// own global query filter, so a removed page is excluded without this class mentioning it — which is also
/// why nothing here calls <c>IgnoreQueryFilters</c>.
/// </para>
/// </summary>
internal sealed class ContentPageReader(AppDbContext dbContext) : IContentPageReader
{
    public async Task<ContentPageDto?> GetPublishedAsync(string slug, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(slug);

        // UX_ContentPages_Slug is a unique index on exactly this column, so this is a seek. The equality is
        // the database's own collation (Latin1_General_100_CI_AS_SC), which is case-insensitive — so
        // /Returns finds the `returns` page rather than 404ing on a capital letter a shopper typed, and the
        // storefront's canonical link then points at the stored spelling.
        return await dbContext.ContentPages
            .AsNoTracking()
            .Where(page => page.IsPublished && page.Slug == slug)
            .Select(page => new ContentPageDto(
                page.Slug,
                page.Title,
                page.BodyHtml,
                page.SeoTitle,
                page.SeoDescription,
                page.SystemKey,
                page.PublishedAtUtc,
                page.UpdatedAtUtc))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<ContentPageLinksDto> GetPublishedLinksAsync(CancellationToken cancellationToken)
    {
        // IX_ContentPages_Published_Title leads on IsPublished and then Title, so the filter and the order
        // are both the index's. No body is selected: this list is rendered into the footer of every page on
        // the site, and selecting BodyHtml here would put the whole of the shop's terms into the markup of
        // every product page.
        var pages = await dbContext.ContentPages
            .AsNoTracking()
            .Where(page => page.IsPublished)
            .OrderBy(page => page.Title)
            .Select(page => new ContentPageLinkDto(
                page.Slug,
                page.Title,
                page.SystemKey,
                page.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

        return new ContentPageLinksDto(pages);
    }
}

/// <summary>
/// The published-page list's sixty-second cache (GLB-10), in the process's own memory.
/// <para>
/// The same arrangement and the same trade-off as <c>SiteSettingsCache</c>, whose remarks cover the
/// topology question in full: one API container (doc 03 §4), one small object, one query to lose. A second
/// instance makes it per-instance and <see cref="Evict"/> stops reaching the other one, at which point
/// publishing a page takes up to a minute to appear in half the traffic's footer.
/// </para>
/// <para>
/// <b>Why the eviction matters here specifically.</b> The list decides three things at once: what is in the
/// footer, what <c>sitemap.xml</c> offers a crawler, and — through the storefront's route guard — whether a
/// slug answers 200 or 404. A client who publishes their returns policy and then finds <c>/returns</c>
/// still answering 404 concludes the publish failed. One minute of that is already more than it should be;
/// not evicting at all would be a minute that never ends, because the next read refreshes the entry.
/// </para>
/// </summary>
internal sealed class ContentPageLinksCache(TimeProvider timeProvider) : IContentPageLinksCache
{
    private readonly Lock _gate = new();

    private ContentPageLinksDto? _links;
    private DateTimeOffset _expiresAt;

    public ContentPageLinksDto? GetLinks()
    {
        lock (_gate)
        {
            if (_links is null || timeProvider.GetUtcNow() >= _expiresAt)
            {
                // Dropped on read as well as on expiry, so a list nobody asks for does not sit in memory
                // once it has gone stale.
                _links = null;
                return null;
            }

            return _links;
        }
    }

    public void SetLinks(ContentPageLinksDto links)
    {
        ArgumentNullException.ThrowIfNull(links);

        lock (_gate)
        {
            _links = links;
            _expiresAt = timeProvider.GetUtcNow()
                + Application.Content.Pages.ContentPageLinksCachePolicy.Lifetime;
        }
    }

    public void Evict()
    {
        lock (_gate)
        {
            _links = null;
        }
    }
}
