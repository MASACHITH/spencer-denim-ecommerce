using SpencerDenim.Application.Settings.Contracts;

namespace SpencerDenim.Application.Settings.Abstractions;

/// <summary>
/// One display-rate row as stored, before anything has been decided about it.
/// </summary>
/// <param name="Key">The <c>Currency.Display.*</c> key.</param>
/// <param name="Value">The stored text, or <see langword="null"/> when the rate has not been set.</param>
/// <param name="UpdatedAtUtc">The row's own <c>UpdatedAtUtc</c>.</param>
public sealed record StoredCurrencyRate(string Key, string? Value, DateTime? UpdatedAtUtc);

/// <summary>
/// Read port for the five display rates (issue #248). Its own port, like <see cref="ISiteSettingsReader"/>, because
/// it is anonymous and read-only: it has no method that could reach a secret, a permission or any other key.
/// <para>
/// It hands back the rows <b>unjudged</b>. Whether a stored value is a usable rate is a rule
/// (<c>CurrencyRateRules</c>) that the save also applies, so it lives in the use case where it can be tested
/// without a database, not in the query.
/// </para>
/// </summary>
public interface ICurrencyRatesReader
{
    /// <summary>
    /// The rows of the <c>Currency.Display</c> group that exist, in no particular order. A row that does not exist
    /// is simply absent. A broken database connection throws, and the endpoint decides what the page does about it.
    /// </summary>
    Task<IReadOnlyList<StoredCurrencyRate>> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The currency feed's cache (doc 08 §3.8: "Options classes are cached for 60 s and evicted on save"), in the
/// process's own memory. <b>The same mechanism as <see cref="ISiteSettingsCache"/>, for the same reasons</b>: the
/// storefront asks for this while it renders pages, so without a cache a crawler walking two thousand product pages
/// is two thousand extra queries for five rows that change once a week. A singleton, because a cache scoped to one
/// request caches nothing.
/// <para>
/// <b>It is evicted by the save</b> (<c>UpdateCurrencyRatesCommandHandler</c>, after a successful write), as the
/// site-settings cache is by the two handlers that change what it shows, so a staff member who has just saved a
/// rate does not see the old one on the shop for the rest of the minute. A direct edit of the table is not seen
/// until the entry expires, which is the same trade the site-settings feed makes.
/// </para>
/// <para>
/// <b>The eviction holds against a read that was already in flight.</b> The save commits and then evicts, so a read
/// that took the old list from the database before the commit can finish after the eviction and try to store what it
/// took, which would re-arm a full minute with the old list. Every eviction therefore moves
/// <see cref="Generation"/>; a reader takes it <i>before</i> it reads and hands it back to <see cref="SetRates"/>,
/// which stores nothing if an eviction has happened since. The request that was in flight still returns the list it
/// read (it began before the save, so that is a fair answer to it); it just cannot put it back in the cache. The
/// site-settings cache has the same gap and is not changed here.
/// </para>
/// <para>
/// <b>A cold or just-evicted cache is read by every request that arrives before the first read finishes</b>, not by one
/// (no single-flight), as the site-settings cache is: for five rows that is a few identical small queries once in a
/// while, and not worth a lock held across a database call.
/// </para>
/// </summary>
public interface ICurrencyRatesCache
{
    /// <summary>
    /// The cached list, or <see langword="null"/> when there is none or it has expired. <b>An empty list is a real
    /// answer</b> (no rate set) and is returned as such, distinct from <see langword="null"/>.
    /// </summary>
    IReadOnlyList<CurrencyRateDto>? GetRates();

    /// <summary>
    /// A number that changes every time <see cref="Evict"/> is called. Take it <b>before</b> reading the database and
    /// give it to <see cref="SetRates"/>, so a save that lands while the read is in flight is not undone by it.
    /// </summary>
    long Generation { get; }

    /// <summary>
    /// Caches a list for the lifetime, <b>only if no eviction has happened since <paramref name="generation"/> was
    /// taken</b>; otherwise it does nothing, because the list may predate the save that evicted it.
    /// </summary>
    void SetRates(IReadOnlyList<CurrencyRateDto> rates, long generation);

    /// <summary>
    /// Drops the entry and moves <see cref="Generation"/>, so the next read goes to the database and no read already in
    /// flight can store its answer. Called by every successful save.
    /// </summary>
    void Evict();
}
