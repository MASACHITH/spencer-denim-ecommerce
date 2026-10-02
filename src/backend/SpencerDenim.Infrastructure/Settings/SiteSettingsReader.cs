using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Application.Settings.Contracts;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Settings;

/// <summary>
/// The storefront shell's settings, read in one query (GLB-02, GLB-07, GLB-08).
/// <para>
/// <b>Five keys by name, never "every non-secret setting".</b> The query names exactly the keys this
/// payload has fields for, so a secret added to the table tomorrow is not merely filtered out — it is never
/// selected. That is the difference between a guard and a shape: a filter can be got wrong by one
/// <c>IsSecret</c> flag, and this cannot be got wrong at all.
/// </para>
/// <para>
/// <b>Every value is re-validated on the way out.</b> A row can hold something this build would refuse
/// today — written by an earlier build, by a database edit, or by a rule that gets loosened by somebody who
/// never looked at the footer. The browser is what follows these addresses, so the last chance to refuse one
/// is here, and the refusal is the same predicate the write path uses
/// (<see cref="SettingFieldRules.IsAcceptableProfileUrl"/>). A value that fails simply does not appear,
/// which renders as no icon.
/// </para>
/// </summary>
internal sealed class SiteSettingsReader(AppDbContext dbContext, ISettingsStore store) : ISiteSettingsReader
{
    /// <summary>
    /// The keys this payload is made of. Ordered as the footer draws them, because the social list's order
    /// is this list's order.
    /// </summary>
    private static readonly string[] Keys =
    [
        SettingKeys.Store.LogoAssetId,
        SettingKeys.Contact.WhatsApp,
        SettingKeys.Social.FacebookUrl,
        SettingKeys.Social.InstagramUrl,
        SettingKeys.Social.TikTokUrl,
    ];

    public async Task<SiteSettingsDto> ReadAsync(CancellationToken cancellationToken)
    {
        var values = await dbContext.Settings
            .AsNoTracking()
            .Where(setting => Keys.Contains(setting.Key))

            // Projected to key and value only. The secret flag, the permission and the description have no
            // business on an anonymous payload's read path, and a projection that does not select them
            // cannot leak them.
            .Select(setting => new { setting.Key, setting.Value })
            .ToDictionaryAsync(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var social = new List<SiteSocialLinkDto>(3);
        AddSocial(social, SiteSocialNetworks.Facebook, values.GetValueOrDefault(SettingKeys.Social.FacebookUrl));
        AddSocial(social, SiteSocialNetworks.Instagram, values.GetValueOrDefault(SettingKeys.Social.InstagramUrl));
        AddSocial(social, SiteSocialNetworks.TikTok, values.GetValueOrDefault(SettingKeys.Social.TikTokUrl));

        var whatsApp = SettingFieldRules.Clean(values.GetValueOrDefault(SettingKeys.Contact.WhatsApp));

        return new SiteSettingsDto(
            await ReadLogoAsync(values.GetValueOrDefault(SettingKeys.Store.LogoAssetId), cancellationToken),
            SettingFieldRules.IsAcceptableWhatsAppNumber(whatsApp) ? whatsApp : null,
            social);
    }

    /// <summary>
    /// Resolves the logo reference, answering <see langword="null"/> for every way it can fail to be a
    /// drawable image.
    /// <para>
    /// <b>A dangling reference is "no logo", not an error.</b> The value can name an asset a staff user has
    /// since soft-deleted from the library — the usage row makes that hard, not impossible, and a database
    /// restore or a direct edit can get there. The alternative to returning null is a page with a broken
    /// image at the top of it, which is the one outcome worse than the text wordmark.
    /// </para>
    /// </summary>
    private async Task<SiteLogoDto?> ReadLogoAsync(string? value, CancellationToken cancellationToken)
    {
        if (!int.TryParse(SettingFieldRules.Clean(value), NumberStyles.None, CultureInfo.InvariantCulture, out var assetId)
            || assetId <= 0)
        {
            return null;
        }

        var asset = await store.FindUsableImageByIdAsync(assetId, cancellationToken);

        // An asset with no URL cannot be drawn, so it is not a logo. MediaUrlBuilder only returns null for
        // a blank storage key, which should not exist — but "should not exist" is how a broken image gets
        // onto every page of a shop.
        return asset is null || string.IsNullOrWhiteSpace(asset.Url)
            ? null
            : new SiteLogoDto(asset.Url, asset.Width, asset.Height, asset.AltText);
    }

    /// <inheritdoc cref="SiteSettingsReader"/>
    private static void AddSocial(List<SiteSocialLinkDto> into, string network, string? value)
    {
        var cleaned = SettingFieldRules.Clean(value);

        if (SettingFieldRules.IsAcceptableProfileUrl(cleaned))
        {
            into.Add(new SiteSocialLinkDto(network, cleaned!));
        }
    }
}
