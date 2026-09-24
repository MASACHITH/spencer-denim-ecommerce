using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Contracts;

namespace SpencerDenim.Infrastructure.Content;

/// <summary>
/// The homepage cache (doc 08 §3.7), in the process's own memory.
/// <para>
/// In memory and not distributed, deliberately: doc 03 §4 runs one API container, the entry is one
/// small object, and losing it costs one render. When the deployment grows a second instance this
/// becomes a per-instance cache — which is still correct, just less effective — and the eviction
/// hook stops reaching the other instance. That is the point at which it wants a shared cache with
/// a published eviction, and it is noted on ADM-17 rather than built for a topology that does not
/// exist.
/// </para>
/// <para>
/// <b>The lifetime is also the limit of this page's scheduling accuracy.</b> Every entry expires at
/// <c>now + 5 minutes</c> regardless of what is in it, so a section whose <c>EndsAtUtc</c> falls
/// inside a warm entry keeps being served until that entry goes — a campaign set to end at midnight
/// is still on screen at 00:05. It is within doc 08 §3.7's five minutes and it harms no data, but
/// the schedule itself is enforced to the millisecond, and nothing else says that caching rounds it
/// off. Issue <b>#37</b> carries the fix: clamp the expiry to
/// <c>Min(now + Lifetime, nextScheduleBoundaryUtc)</c>.
/// </para>
/// </summary>
internal sealed class HomePageCache(TimeProvider timeProvider) : IHomePageCache
{
    private readonly Lock _gate = new();

    private HomePageDto? _page;
    private DateTimeOffset _expiresAt;

    public HomePageDto? GetPage()
    {
        lock (_gate)
        {
            if (_page is null || timeProvider.GetUtcNow() >= _expiresAt)
            {
                // Dropped on read as well as on expiry, so a page nobody asks for does not sit in
                // memory once it has gone stale.
                _page = null;
                return null;
            }

            return _page;
        }
    }

    public void SetPage(HomePageDto page)
    {
        ArgumentNullException.ThrowIfNull(page);

        lock (_gate)
        {
            _page = page;
            _expiresAt = timeProvider.GetUtcNow() + HomePageCachePolicy.Lifetime;
        }
    }

    public void Evict()
    {
        lock (_gate)
        {
            _page = null;
        }
    }
}
