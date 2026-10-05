using SpencerDenim.Application.Settings.Contracts;

namespace SpencerDenim.Application.Settings.Abstractions;

/// <summary>
/// Read port for the storefront shell's settings (GLB-02, GLB-07, GLB-08).
/// <para>
/// Its own port rather than a second use of <see cref="ISettingsStore"/>, for the reason
/// <c>IAdminCatalogReader</c> is separate from <c>ICatalogStructureStore</c>: this one is anonymous,
/// read-only, on every page, and the shape it returns is an allow-list
/// (<see cref="SiteSettingsDto"/>) rather than rows. Keeping them apart is what makes it impossible for
/// the public path to reach a secret, a permission or an unseeded key — it has no method that could.
/// </para>
/// </summary>
public interface ISiteSettingsReader
{
    /// <summary>
    /// The shell's settings. Never throws for missing data: an unseeded database, a null value and an
    /// asset that has been deleted underneath the reference all come back as
    /// <see cref="SiteSettingsDto.Empty"/> or as a payload with that part missing. A broken database
    /// connection does throw, and the caller above decides what the page does about it.
    /// </summary>
    Task<SiteSettingsDto> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The storefront settings cache (doc 08 §3.8: "Options classes are cached for 60 s and evicted on
/// save"), in the process's own memory.
/// <para>
/// Separate from <c>IHomePageCache</c> because the lifetimes differ — five minutes there, sixty seconds
/// here — and because this payload is read by <i>every</i> page rather than by one. The eviction hook is
/// what makes a logo change visible at once instead of within a minute.
/// </para>
/// </summary>
public interface ISiteSettingsCache
{
    /// <summary>
    /// The cached payload, or <see langword="null"/> when there is none or it has expired.
    /// <para>
    /// Named <c>GetSettings</c> rather than <c>Get</c>, matching <c>IHomePageCache.GetPage</c> — bare
    /// <c>Get</c> and <c>Set</c> are reserved words in some CLS languages (CA1716).
    /// </para>
    /// </summary>
    SiteSettingsDto? GetSettings();

    /// <summary>Caches a payload for the lifetime.</summary>
    void SetSettings(SiteSettingsDto settings);

    /// <summary>Drops the entry, so the next read goes to the database. Called by every successful save.</summary>
    void Evict();
}
