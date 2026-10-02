namespace SpencerDenim.Application.Settings.Contracts;

/// <summary>
/// What the storefront shell reads on every page: the logo, the WhatsApp number and the social profile
/// addresses (GLB-02, GLB-07, GLB-08).
/// <para>
/// <b>This contract is the secret guard, by construction rather than by rule.</b> It is a fixed,
/// hand-written allow-list of three things an anonymous visitor is entitled to see. There is no
/// <c>Value</c>, no key name and no group here, so a secret added to <c>ops.Settings</c> tomorrow —
/// PayPal's client secret, the SMTP password — has <i>nowhere to appear</i> on this payload. The
/// alternative shape, "every non-secret setting as a dictionary", would have put one bad
/// <c>IsSecret</c> flag between a bank account number and an unauthenticated endpoint.
/// </para>
/// <para>
/// <b>Every member is nullable or empty-able, and the storefront is built for that.</b> The client has
/// uploaded no logo and sent no profile addresses, so the honest answer today is a null logo and an empty
/// list — which is the normal state, not an error state, and the state most visitors will see.
/// </para>
/// </summary>
/// <param name="Logo">The header logo, or <see langword="null"/> to use the text wordmark.</param>
/// <param name="WhatsAppNumber">
/// Digits only, international form, or <see langword="null"/> when the client has not set one — in which
/// case the storefront keeps its own documented placeholder rather than removing GLB-08's button from
/// every page.
/// </param>
/// <param name="Social">
/// The profile addresses that are both <i>set</i> and <i>valid</i>, in display order. A key the client has
/// left empty is simply not in the list: there is no entry with a null URL to be rendered by accident.
/// </param>
public sealed record SiteSettingsDto(
    SiteLogoDto? Logo,
    string? WhatsAppNumber,
    IReadOnlyList<SiteSocialLinkDto> Social)
{
    /// <summary>
    /// The answer for a shop that has configured none of this — which is every deployment until the client
    /// fills the screen in, and also what the storefront falls back to if this payload cannot be fetched.
    /// </summary>
    public static SiteSettingsDto Empty { get; } = new(null, null, []);
}

/// <summary>The header logo.</summary>
/// <param name="Url">The image to draw. Never null or blank: a logo with no URL is not returned at all.</param>
/// <param name="Width">
/// The original's pixel width, or <see langword="null"/> when the asset never recorded one. The storefront
/// uses it with <paramref name="Height"/> to reserve the box before the image loads, which is what keeps
/// the header out of the page's CLS budget (doc 06 §4: CLS &lt; 0.1).
/// </param>
/// <param name="Height">The original's pixel height.</param>
/// <param name="AltText">
/// The asset's alt text, or <see langword="null"/>. The storefront does <b>not</b> fall back to the shop's
/// name here — it decides that itself, from its own constant, because the name is a compile-time value on
/// that side too (W1-15).
/// </param>
public sealed record SiteLogoDto(string Url, int? Width, int? Height, string? AltText);

/// <summary>One social profile link the footer may draw.</summary>
/// <param name="Network">
/// A fixed code — <c>facebook</c>, <c>instagram</c> or <c>tiktok</c> — which the storefront maps to an
/// icon and a label. A code rather than an icon name or a label, so the shape of the icon set is the
/// front end's business and a new network is one row and one glyph rather than a payload change.
/// </param>
/// <param name="Url">An absolute <c>http</c> or <c>https</c> address, re-checked on the way out.</param>
public sealed record SiteSocialLinkDto(string Network, string Url);

/// <summary>
/// The <see cref="SiteSocialLinkDto.Network"/> codes, in the order the footer draws them.
/// </summary>
public static class SiteSocialNetworks
{
    public const string Facebook = "facebook";

    public const string Instagram = "instagram";

    public const string TikTok = "tiktok";
}
