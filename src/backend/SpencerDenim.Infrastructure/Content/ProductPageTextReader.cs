using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Content;

/// <summary>
/// Reads <c>Content.PdpDeliveryInfoHtml</c> and <c>Content.PdpReturnsInfoHtml</c> for the product page (issue #33).
/// </summary>
/// <remarks>
/// The same three floors as <c>SiteSettingsReader</c>: a fixed key list, never a secret row, and a projection of
/// key and value only. A row whose type is not <see cref="SettingValueType.TextHtml"/> is not read either — the
/// storefront binds the value as markup, so a row retyped by hand to something else is not one this may publish.
/// </remarks>
internal sealed class ProductPageTextReader(AppDbContext dbContext) : IProductPageTextReader
{
    private static readonly string[] Keys =
    [
        SettingKeys.Content.PdpDeliveryInfoHtml,
        SettingKeys.Content.PdpReturnsInfoHtml,
    ];

    public async Task<ProductPageTexts> ReadAsync(CancellationToken cancellationToken)
    {
        var values = await dbContext.Settings
            .AsNoTracking()
            .Where(setting => Keys.Contains(setting.Key))
            .Where(setting => !setting.IsSecret && setting.ValueType == SettingValueType.TextHtml)
            .Select(setting => new { setting.Key, setting.Value })
            .ToDictionaryAsync(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase, cancellationToken);

        return new ProductPageTexts(
            values.GetValueOrDefault(SettingKeys.Content.PdpDeliveryInfoHtml),
            values.GetValueOrDefault(SettingKeys.Content.PdpReturnsInfoHtml));
    }
}
