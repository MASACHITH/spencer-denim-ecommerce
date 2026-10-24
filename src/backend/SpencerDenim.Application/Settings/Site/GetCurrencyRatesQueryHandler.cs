using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Application.Settings.Contracts;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Application.Settings.Site;

/// <summary>The currencies the storefront may offer. No parameters: there is one shop.</summary>
public sealed record GetCurrencyRatesQuery;

/// <summary>
/// The display currencies that have a usable rate, in the order the storefront offers them (issue #248).
/// <para>
/// <b>Only a currency whose stored value passes the same rules the save applies is listed.</b> A row can hold
/// something this build would refuse today (an earlier build, a database edit, a rule loosened by somebody who never
/// looked at a product page), and a shopper would then see a wrong price, so such a row is skipped, as is an empty
/// one. An empty list is a valid answer: it means no currency has been set up and the storefront offers rupees only.
/// </para>
/// <para>
/// <b>Nothing here ages a rate out.</b> A stale rate keeps showing (owner decision, 2026-10-24); the admin screen
/// is where staff are told a rate is old, from <see cref="CurrencyRateDto.UpdatedAtUtc"/>.
/// </para>
/// </summary>
public sealed class GetCurrencyRatesQueryHandler(ICurrencyRatesReader reader)
    : IQueryHandler<GetCurrencyRatesQuery, IReadOnlyList<CurrencyRateDto>>
{
    public async Task<IReadOnlyList<CurrencyRateDto>> HandleAsync(
        GetCurrencyRatesQuery query,
        CancellationToken cancellationToken)
    {
        var stored = await reader.ReadAsync(cancellationToken);

        // Case-insensitively, to match the database collation; see SettingCatalog.ByKey for what Ordinal costs.
        var byKey = stored
            .GroupBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var offered = new List<CurrencyRateDto>(DisplayCurrencies.All.Count);

        foreach (var currency in DisplayCurrencies.All)
        {
            if (!byKey.TryGetValue(currency.SettingKey, out var row)
                || CurrencyRateRules.TryReadStored(row.Value) is not { } rate)
            {
                continue;
            }

            offered.Add(new CurrencyRateDto(
                currency.Code,
                currency.Symbol,
                rate,
                row.UpdatedAtUtc is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null));
        }

        return offered;
    }
}

/// <summary>
/// The same read, through doc 08 §3.8's sixty-second cache (<see cref="ICurrencyRatesCache"/>), arranged exactly as
/// <see cref="CachedSiteSettingsQueryHandler"/> is: decorated, so the uncached handler stays testable and the cache
/// stays one concern.
/// <para>
/// <b>Only an answer is cached, and a failure is not one.</b> If the reader throws, nothing is stored and the
/// exception travels to the endpoint, which answers with the empty list and no cache lifetime, so a database blip is
/// never pinned in memory for a minute. A <i>successful</i> read that finds no rate set is cached like any other
/// (it is the true state of a shop that has not set one, and the common one before launch), and the save evicts it,
/// so the first rate an admin saves appears at once.
/// </para>
/// </summary>
public sealed class CachedCurrencyRatesQueryHandler(
    IQueryHandler<GetCurrencyRatesQuery, IReadOnlyList<CurrencyRateDto>> inner,
    ICurrencyRatesCache cache)
    : IQueryHandler<GetCurrencyRatesQuery, IReadOnlyList<CurrencyRateDto>>
{
    public async Task<IReadOnlyList<CurrencyRateDto>> HandleAsync(
        GetCurrencyRatesQuery query,
        CancellationToken cancellationToken)
    {
        if (cache.GetRates() is { } cached)
        {
            return cached;
        }

        var rates = await inner.HandleAsync(query, cancellationToken);
        cache.SetRates(rates);

        return rates;
    }
}

/// <summary>The cache's lifetime, in one place so the test and the cache agree.</summary>
public static class CurrencyRatesCachePolicy
{
    /// <summary>Doc 08 §3.8: sixty seconds.</summary>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromSeconds(60);
}
