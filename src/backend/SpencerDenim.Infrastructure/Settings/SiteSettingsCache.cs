using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Application.Settings.Contracts;
using SpencerDenim.Application.Settings.Site;

namespace SpencerDenim.Infrastructure.Settings;

/// <summary>
/// The storefront settings cache (doc 08 §3.8: "Options classes are cached for 60 s and evicted on save"),
/// in the process's own memory.
/// <para>
/// In memory and not distributed, for the reason <c>HomePageCache</c> gives: doc 03 §4 runs one API
/// container, the entry is one small object, and losing it costs one query. A second instance makes this a
/// per-instance cache — still correct, just less effective — and <see cref="Evict"/> stops reaching the
/// other instance, at which point a logo change takes up to a minute to appear on half the traffic. That is
/// the point at which it wants a shared cache with a published eviction, and it is noted here rather than
/// built for a topology that does not exist.
/// </para>
/// <para>
/// <b>Why the eviction matters more here than on the homepage.</b> A logo is the one change whose whole
/// point is that it is immediate and everywhere; a client who uploads one, reloads the shop and still sees
/// the wordmark concludes the upload failed and uploads it again. The homepage's five minutes are a
/// scheduling tolerance; this minute is a staleness nobody should meet.
/// </para>
/// </summary>
internal sealed class SiteSettingsCache(TimeProvider timeProvider) : ISiteSettingsCache
{
    private readonly Lock _gate = new();

    private SiteSettingsDto? _settings;
    private DateTimeOffset _expiresAt;

    public SiteSettingsDto? GetSettings()
    {
        lock (_gate)
        {
            if (_settings is null || timeProvider.GetUtcNow() >= _expiresAt)
            {
                // Dropped on read as well as on expiry, so a payload nobody asks for does not sit in memory
                // once it has gone stale.
                _settings = null;
                return null;
            }

            return _settings;
        }
    }

    public void SetSettings(SiteSettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            _settings = settings;
            _expiresAt = timeProvider.GetUtcNow() + SiteSettingsCachePolicy.Lifetime;
        }
    }

    public void Evict()
    {
        lock (_gate)
        {
            _settings = null;
        }
    }
}
