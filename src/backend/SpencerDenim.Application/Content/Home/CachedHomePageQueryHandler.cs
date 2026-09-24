using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Contracts;

namespace SpencerDenim.Application.Content.Home;

/// <summary>
/// Doc 08 §3.7's five-minute homepage cache, as a decorator.
/// <para>
/// A decorator rather than a few lines inside <see cref="GetHomePageQueryHandler"/>: assembling
/// the page and deciding how long an assembled page stays good are two jobs with two reasons to
/// change, and this way the assembly can still be tested with no cache in the way at all.
/// </para>
/// <para>
/// Nothing here is per-visitor: the homepage is the same for everyone, because every input to it
/// is admin content and published catalogue rows. The moment that stops being true — a personalised
/// row, a shopper's own recently-viewed — this decorator is the wrong place for it and the cache
/// key stops being "the homepage".
/// </para>
/// </summary>
public sealed class CachedHomePageQueryHandler(
    IQueryHandler<GetHomePageQuery, HomePageDto> inner,
    IHomePageCache cache)
    : IQueryHandler<GetHomePageQuery, HomePageDto>
{
    public async Task<HomePageDto> HandleAsync(GetHomePageQuery query, CancellationToken cancellationToken)
    {
        var cached = cache.GetPage();
        if (cached is not null)
        {
            return cached;
        }

        var page = await inner.HandleAsync(query, cancellationToken);

        // Two requests that miss at the same moment both assemble and both store. That is a
        // duplicated read, not a wrong answer, and the alternative — holding a lock across a
        // database round trip — makes one slow query everybody's slow query.
        cache.SetPage(page);

        return page;
    }
}
