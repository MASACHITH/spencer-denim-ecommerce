using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Application.Settings.Contracts;
using SpencerDenim.Application.Settings.Site;

namespace SpencerDenim.Infrastructure.Settings;

/// <summary>
/// The currency feed's cache (doc 08 §3.8: "Options classes are cached for 60 s and evicted on save"), in the
/// process's own memory. <b>The same arrangement as <see cref="SiteSettingsCache"/></b>, and it inherits the same
/// honest limits: doc 03 §4 runs one API container, the entry is one small list, and losing it costs one query. A
/// second instance would make this a per-instance cache and <see cref="Evict"/> would stop reaching the other, so
/// a saved rate could take up to a minute to show on half the traffic; that is the day it wants a shared cache with
/// a published eviction, and it is noted here rather than built for a topology that does not exist.
/// <para>
/// <b>The empty list is stored</b>: it is the true answer for a shop that has set no rate, and the save evicts it.
/// What is never stored is a failure, because the decorator only calls <see cref="SetRates"/> with a list the
/// reader actually returned.
/// </para>
/// <para>
/// <b>An eviction cannot be undone by a read that was already in flight</b> (found by the independent tester, and not
/// true of <see cref="SiteSettingsCache"/>, whose follow-up is noted in the pull request). The save commits and then
/// evicts, so a read that took the old list before the commit could finish after the eviction and store it, re-arming
/// a minute of the old list. <see cref="Evict"/> therefore moves a generation number, a reader takes it before it
/// reads, and <see cref="SetRates"/> stores only if it is unchanged. The check and the store, the eviction and its
/// increment all happen under one lock, so there is no gap between "is it still current" and "store it".
/// </para>
/// <para>
/// <b>A cold cache is read by every request that arrives before the first read finishes, not by one</b> (no
/// single-flight), exactly as the site-settings cache is: right after start-up, after an expiry or after a save,
/// requests that arrive together each run the same small query and the last to finish stores its answer. For five
/// rows that is a handful of identical statements once in a while, which is cheaper and simpler than a lock held across
/// a database call, so it is left as it is and written down here.
/// </para>
/// </summary>
internal sealed class CurrencyRatesCache(TimeProvider timeProvider) : ICurrencyRatesCache
{
    private readonly Lock _gate = new();

    private IReadOnlyList<CurrencyRateDto>? _rates;
    private DateTimeOffset _expiresAt;
    private long _generation;

    public long Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    public IReadOnlyList<CurrencyRateDto>? GetRates()
    {
        lock (_gate)
        {
            if (_rates is null || timeProvider.GetUtcNow() >= _expiresAt)
            {
                // Dropped on read as well as on expiry, so a list nobody asks for does not sit in memory once stale.
                _rates = null;
                return null;
            }

            return _rates;
        }
    }

    public void SetRates(IReadOnlyList<CurrencyRateDto> rates, long generation)
    {
        ArgumentNullException.ThrowIfNull(rates);

        lock (_gate)
        {
            // An eviction since the reader took its generation means a save has landed since the read began, so the
            // list may predate it. Storing it would put the old rate back for a minute.
            if (generation != _generation)
            {
                return;
            }

            _rates = rates;
            _expiresAt = timeProvider.GetUtcNow() + CurrencyRatesCachePolicy.Lifetime;
        }
    }

    public void Evict()
    {
        lock (_gate)
        {
            _rates = null;
            _generation++;
        }
    }
}
