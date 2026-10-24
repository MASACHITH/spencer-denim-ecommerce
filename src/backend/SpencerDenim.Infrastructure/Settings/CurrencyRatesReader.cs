using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Settings;

/// <summary>
/// The five display rates as stored, in one query (issue #248).
/// <para>
/// <b>Five keys by name, never "every setting in the group".</b> The query names exactly the keys
/// <see cref="DisplayCurrencies"/> lists, so a secret added to the table tomorrow is not merely filtered out, it is
/// never selected; a row flagged <c>IsSecret</c> is excluded as well, as <see cref="SiteSettingsReader"/> does.
/// The projection is the key, the value and the age and nothing else: the permission and the description have no
/// business on an anonymous payload's read path.
/// </para>
/// </summary>
internal sealed class CurrencyRatesReader(AppDbContext dbContext) : ICurrencyRatesReader
{
    private static readonly string[] Keys = [.. DisplayCurrencies.All.Select(currency => currency.SettingKey)];

    public async Task<IReadOnlyList<StoredCurrencyRate>> ReadAsync(CancellationToken cancellationToken) =>
        await dbContext.Settings
            .AsNoTracking()
            .Where(setting => Keys.Contains(setting.Key) && !setting.IsSecret)
            .Select(setting => new StoredCurrencyRate(setting.Key, setting.Value, setting.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
}
