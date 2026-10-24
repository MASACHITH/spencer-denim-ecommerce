using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Application.Settings.Contracts;
using SpencerDenim.Application.Settings.Site;

namespace SpencerDenim.Infrastructure.Settings;

/// <summary>
/// The currency feed's cache (doc 08 §3.8: "Options classes are cached for 60 s and evicted on save"), in the
/// process's own memory. <b>The same arrangement as <see cref="SiteSettingsCache"/></b>, and it inherits the same
/// honest limit: doc 03 §4 runs one API container, the entry is one small list, and losing it costs one query. A
/// second instance would make this a per-instance cache and <see cref="Evict"/> would stop reaching the other, so
/// a saved rate could take up to a minute to show on half the traffic; that is the day it wants a shared cache with
/// a published eviction, and it is noted here rather than built for a topology that does not exist.
/// <para>
/// <b>The empty list is stored</b>: it is the true answer for a shop that has set no rate, and the save evicts it.
/// What is never stored is a failure, because the decorator only calls <see cref="SetRates"/> with a list the
/// reader actually returned.
/// </para>
/// </summary>
internal sealed class CurrencyRatesCache(TimeProvider timeProvider) : ICurrencyRatesCache
{
    private readonly Lock _gate = new();

    private IReadOnlyList<CurrencyRateDto>? _rates;
    private DateTimeOffset _expiresAt;

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

    public void SetRates(IReadOnlyList<CurrencyRateDto> rates)
    {
        ArgumentNullException.ThrowIfNull(rates);

        lock (_gate)
        {
            _rates = rates;
            _expiresAt = timeProvider.GetUtcNow() + CurrencyRatesCachePolicy.Lifetime;
        }
    }

    public void Evict()
    {
        lock (_gate)
        {
            _rates = null;
        }
    }
}
