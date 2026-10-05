using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Application.Settings.Contracts;

namespace SpencerDenim.Application.Settings.Site;

/// <summary>The storefront shell's settings (GLB-02, GLB-07, GLB-08). No parameters: there is one shop.</summary>
public sealed record GetSiteSettingsQuery;

/// <summary>
/// Reads the shell's settings straight through. Thin on purpose — the projection and the "is this value
/// still safe to publish" re-check live in the reader, next to the query that produced the rows.
/// </summary>
public sealed class GetSiteSettingsQueryHandler(ISiteSettingsReader reader)
    : IQueryHandler<GetSiteSettingsQuery, SiteSettingsDto>
{
    public Task<SiteSettingsDto> HandleAsync(GetSiteSettingsQuery query, CancellationToken cancellationToken) =>
        reader.ReadAsync(cancellationToken);
}

/// <summary>
/// The same read, through doc 08 §3.8's sixty-second cache ("Options classes are cached for 60 s and
/// evicted on save").
/// <para>
/// <b>Why this payload needs a cache more than the homepage does.</b> The homepage's cache saves one render
/// of one page; this one is read during the server render of <i>every</i> page on the site, so without it a
/// crawler walking two thousand product pages is two thousand extra round trips for four rows that change
/// once a year. Decorated rather than folded into the handler, which is the arrangement
/// <c>CachedHomePageQueryHandler</c> established — the uncached handler stays testable and the cache stays
/// one concern.
/// </para>
/// <para>
/// <b>A failure is not cached.</b> If the reader throws, nothing is stored and the exception travels to the
/// endpoint, which answers with the empty payload — so a database blip does not get pinned in memory for a
/// minute, and the storefront's own fallback is what the shopper meets.
/// </para>
/// </summary>
public sealed class CachedSiteSettingsQueryHandler(
    IQueryHandler<GetSiteSettingsQuery, SiteSettingsDto> inner,
    ISiteSettingsCache cache)
    : IQueryHandler<GetSiteSettingsQuery, SiteSettingsDto>
{
    public async Task<SiteSettingsDto> HandleAsync(
        GetSiteSettingsQuery query,
        CancellationToken cancellationToken)
    {
        if (cache.GetSettings() is { } cached)
        {
            return cached;
        }

        var settings = await inner.HandleAsync(query, cancellationToken);
        cache.SetSettings(settings);

        return settings;
    }
}

/// <summary>The cache's lifetime, in one place so the test and the cache agree.</summary>
public static class SiteSettingsCachePolicy
{
    /// <summary>Doc 08 §3.8: sixty seconds.</summary>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromSeconds(60);
}
