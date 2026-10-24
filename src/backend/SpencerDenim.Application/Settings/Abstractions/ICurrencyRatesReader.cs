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
/// <b>It is evicted by the save</b> (<c>UpdateCurrencyRatesCommandHandler</c>, after a successful write), exactly as
/// the site-settings cache is by the two handlers that change what it shows, so a staff member who has just saved a
/// rate does not see the old one on the shop for the rest of the minute. A direct edit of the table is not seen
/// until the entry expires, which is the same trade the site-settings feed makes.
/// </para>
/// </summary>
public interface ICurrencyRatesCache
{
    /// <summary>
    /// The cached list, or <see langword="null"/> when there is none or it has expired. <b>An empty list is a real
    /// answer</b> (no rate set) and is returned as such, distinct from <see langword="null"/>.
    /// </summary>
    IReadOnlyList<CurrencyRateDto>? GetRates();

    /// <summary>Caches a list for the lifetime.</summary>
    void SetRates(IReadOnlyList<CurrencyRateDto> rates);

    /// <summary>Drops the entry, so the next read goes to the database. Called by every successful save.</summary>
    void Evict();
}
