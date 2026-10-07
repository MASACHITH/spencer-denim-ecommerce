using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Contracts;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Application.Content.Pages;

/// <summary>One published content page, by slug (GLB-10).</summary>
/// <param name="Slug">The address segment, as it came off the URL.</param>
public sealed record GetContentPageQuery(string? Slug);

/// <summary>
/// Reads one published page.
/// <para>
/// <b>A slug that is not a slug is refused here rather than queried.</b> <c>CK_ContentPages_Slug</c> means
/// no row can hold one, so a lookup would be a guaranteed miss against a public endpoint — and the same
/// rule the catalogue applies to a product slug ("the API refuses to look one up"). It also keeps a
/// scanner's path traversal off the database entirely.
/// </para>
/// </summary>
public sealed class GetContentPageQueryHandler(IContentPageReader reader)
    : IQueryHandler<GetContentPageQuery, ContentPageDto?>
{
    public Task<ContentPageDto?> HandleAsync(GetContentPageQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var slug = query.Slug?.Trim().ToLowerInvariant();

        return CatalogSlug.IsValid(slug)
            ? reader.GetPublishedAsync(slug!, cancellationToken)
            : Task.FromResult<ContentPageDto?>(null);
    }
}

/// <summary>Every published page, for the footer, the sitemap and the route guard (GLB-10, GLB-11).</summary>
public sealed record GetContentPageLinksQuery;

/// <summary>Reads the published-page list straight through; the projection lives in the reader.</summary>
public sealed class GetContentPageLinksQueryHandler(IContentPageReader reader)
    : IQueryHandler<GetContentPageLinksQuery, ContentPageLinksDto>
{
    public Task<ContentPageLinksDto> HandleAsync(
        GetContentPageLinksQuery query,
        CancellationToken cancellationToken) =>
        reader.GetPublishedLinksAsync(cancellationToken);
}

/// <summary>
/// The same read, through the sixty-second cache — the arrangement
/// <c>CachedSiteSettingsQueryHandler</c> established, decorated rather than folded in so the uncached
/// handler stays testable and the cache stays one concern.
/// <para>
/// <b>A failure is not cached.</b> If the reader throws, nothing is stored and the exception travels to the
/// endpoint. That matters more here than it does for the site settings: the storefront turns "the list
/// could not be read" into 503 rather than 404, and caching the failure would pin that answer across a
/// database blip for a minute.
/// </para>
/// </summary>
public sealed class CachedContentPageLinksQueryHandler(
    IQueryHandler<GetContentPageLinksQuery, ContentPageLinksDto> inner,
    IContentPageLinksCache cache)
    : IQueryHandler<GetContentPageLinksQuery, ContentPageLinksDto>
{
    public async Task<ContentPageLinksDto> HandleAsync(
        GetContentPageLinksQuery query,
        CancellationToken cancellationToken)
    {
        if (cache.GetLinks() is { } cached)
        {
            return cached;
        }

        var links = await inner.HandleAsync(query, cancellationToken);
        cache.SetLinks(links);

        return links;
    }
}

/// <summary>The cache's lifetime, in one place so the test and the cache agree.</summary>
public static class ContentPageLinksCachePolicy
{
    /// <summary>Sixty seconds, as doc 08 §3.8 gives the other shell-wide read.</summary>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromSeconds(60);
}
