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
/// <b>Every value is re-validated on the way out, and re-normalised.</b> A row can hold something this build
/// would refuse today — written by an earlier build, by a database edit, or by a rule that gets loosened by
/// somebody who never looked at the footer. The browser is what follows these addresses, so the last chance
/// to refuse one is here, and the refusal is the same function the write path uses
/// (<see cref="SettingFieldRules.TryNormaliseProfileUrl"/>). A value that fails simply does not appear, which
/// renders as no icon; one that passes is published in its normalised form, so a legacy row holding a
/// homograph host is published as the ASCII host it actually resolves to rather than as the text it imitates.
/// </para>
/// <para>
/// <b>And a row flagged <c>IsSecret</c> is not selected at all</b> — see the query.
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

        // W2-11 (GLB-10): the Contact page's hours-and-address panel and the footer's contact column.
        SettingKeys.Contact.Phone,
        SettingKeys.Contact.Email,
        SettingKeys.Contact.Address,
        SettingKeys.Contact.OpeningHours,
        SettingKeys.Contact.MapEmbedUrl,

        SettingKeys.Social.FacebookUrl,
        SettingKeys.Social.InstagramUrl,
        SettingKeys.Social.TikTokUrl,
        SettingKeys.Social.YouTubeUrl,
    ];

    public async Task<SiteSettingsDto> ReadAsync(CancellationToken cancellationToken)
    {
        var values = await dbContext.Settings
            .AsNoTracking()
            .Where(setting => Keys.Contains(setting.Key))

            // **The third floor, and it was missing until round 1.** The other two are the key allow-list
            // above and the hand-written contract this returns — both real, and both about keys and shape
            // rather than about the row. If one of these five keys were ever flipped to Secret, its value
            // would have been published subject only to format validation. The tester's correction to the
            // brief was right: it was two floors plus a shape, not three. This clause is the third.
            .Where(setting => !setting.IsSecret)

            // Projected to key and value only. The permission and the description have no business on an
            // anonymous payload's read path, and a projection that does not select them cannot leak them.
            .Select(setting => new { setting.Key, setting.Value })
            .ToDictionaryAsync(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var social = new List<SiteSocialLinkDto>(4);
        AddSocial(social, SiteSocialNetworks.Facebook, values.GetValueOrDefault(SettingKeys.Social.FacebookUrl));
        AddSocial(social, SiteSocialNetworks.Instagram, values.GetValueOrDefault(SettingKeys.Social.InstagramUrl));
        AddSocial(social, SiteSocialNetworks.TikTok, values.GetValueOrDefault(SettingKeys.Social.TikTokUrl));
        AddSocial(social, SiteSocialNetworks.YouTube, values.GetValueOrDefault(SettingKeys.Social.YouTubeUrl));

        var whatsApp = SettingFieldRules.Clean(values.GetValueOrDefault(SettingKeys.Contact.WhatsApp));

        return new SiteSettingsDto(
            await ReadLogoAsync(values.GetValueOrDefault(SettingKeys.Store.LogoAssetId), cancellationToken),
            SettingFieldRules.IsAcceptableWhatsAppNumber(whatsApp) ? whatsApp : null,
            social,
            ReadContact(values));
    }

    /// <summary>
    /// The <c>Contact</c> group as the storefront draws it (GLB-07, GLB-10).
    /// <para>
    /// <b>Every value is re-validated on the way out</b>, by the same functions the write path uses, for
    /// the reason the type remarks give in full: a row can hold something this build would refuse, written
    /// by an earlier build or by a database edit, and the browser is what follows the map address. A value
    /// that fails is simply absent, which renders as nothing.
    /// </para>
    /// <para>
    /// <b>The telephone number and the address are not re-validated the same way, deliberately.</b> There
    /// is nothing a browser <i>follows</i> in them: they are interpolated as text, and the <c>tel:</c>
    /// target is built from the digits. Refusing a number the client typed slightly differently would show
    /// a shopper no telephone number at all, which is strictly worse than showing one with an unusual
    /// bracket in it. The length and control-character bounds are the write path's and they hold on the
    /// row.
    /// </para>
    /// </summary>
    private static SiteContactDto ReadContact(IReadOnlyDictionary<string, string?> values)
    {
        var map = SettingFieldRules.Clean(values.GetValueOrDefault(SettingKeys.Contact.MapEmbedUrl));

        return new SiteContactDto(
            SettingFieldRules.Clean(values.GetValueOrDefault(SettingKeys.Contact.Phone)),
            SettingFieldRules.Clean(values.GetValueOrDefault(SettingKeys.Contact.Email)),
            Lines(values.GetValueOrDefault(SettingKeys.Contact.Address)),
            Lines(values.GetValueOrDefault(SettingKeys.Contact.OpeningHours)),
            SettingFieldRules.TryNormaliseMapEmbedUrl(map, out var normalised) ? normalised : null);
    }

    /// <summary>
    /// A multi-line setting as the lines the storefront renders: trimmed, blank lines dropped, and capped.
    /// <para>
    /// The cap is not about the column — the write path bounds that — but about what a footer can draw: a
    /// value that somehow holds two hundred newlines would otherwise become two hundred paragraphs on
    /// every page of the shop.
    /// </para>
    /// </summary>
    private static List<string> Lines(string? value)
    {
        if (SettingFieldRules.Clean(value) is not { } text)
        {
            return [];
        }

        return text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(MaxLines)
            .ToList();
    }

    /// <summary>How many lines of an address or a set of opening hours the storefront will draw.</summary>
    private const int MaxLines = 8;

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
        if (SettingFieldRules.TryNormaliseProfileUrl(value, out var normalised))
        {
            into.Add(new SiteSocialLinkDto(network, normalised));
        }
    }
}
