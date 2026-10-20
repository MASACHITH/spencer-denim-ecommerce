using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
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
/// <para>
/// <b>An entry lives until the page stops being true, or five minutes, whichever is sooner</b> (issue
/// #37). The schedule is enforced to the millisecond — inclusive start, exclusive end — and a flat
/// five-minute entry used to round that off: a section ending at midnight was still served at 00:04.
/// So after assembling, this asks the content store for the next boundary still ahead and the cache
/// expires the entry there.
/// </para>
/// <para>
/// <b>The boundary is measured from the instant <i>before</i> the page was assembled.</b> The inner handler
/// reads its own clock a moment later, so a boundary falling between the two would be one the page
/// already reflects or one it does not; asking from the earlier instant can only make the entry expire
/// sooner than it needed to, never later.
/// </para>
/// </summary>
public sealed class CachedHomePageQueryHandler(
    IQueryHandler<GetHomePageQuery, HomePageDto> inner,
    IHomePageCache cache,
    IHomeContentReader content,
    IClock clock)
    : IQueryHandler<GetHomePageQuery, HomePageDto>
{
    public async Task<HomePageDto> HandleAsync(GetHomePageQuery query, CancellationToken cancellationToken)
    {
        var cached = cache.GetPage();
        if (cached is not null)
        {
            return cached;
        }

        var assembledFrom = clock.UtcNow.UtcDateTime;
        var page = await inner.HandleAsync(query, cancellationToken);
        var validUntil = await content.GetNextScheduleBoundaryAsync(assembledFrom, cancellationToken);

        // Two requests that miss at the same moment both assemble and both store. That is a
        // duplicated read, not a wrong answer, and the alternative — holding a lock across a
        // database round trip — makes one slow query everybody's slow query.
        cache.SetPage(page, validUntil);

        return page;
    }
}
