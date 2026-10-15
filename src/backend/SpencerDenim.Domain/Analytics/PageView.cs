namespace SpencerDenim.Domain.Analytics;

/// <summary>
/// <c>analytics.PageViews</c> (doc 08 §3.8; G4, ADM-06, ADM-20): one storefront page view.
/// <para>
/// <b>What is deliberately not here.</b> No IP address, in any form — not hashed, not truncated, not for
/// a while "for bot detection" (doc 08 §3.8, doc 11 P16). No persistent visitor ID: <see cref="SessionId"/>
/// is a random value from a cookie that expires after thirty minutes without a page view. No user ID, no
/// email, no order. <see cref="Path"/> has been through <c>PagePath</c>, which drops the query string except
/// the listing's filter keys and replaces any segment that could carry a secret (the guest order page's
/// token, an id, anything with an <c>@</c> or a phone-length run of digits) with <c>*</c>.
/// </para>
/// <para>
/// Append-only and high-volume, so <c>bigint</c> identity (doc 08 §1.3), page-compressed, never audited
/// (doc 08 §3.8's "Tables never audited"), and purged after <c>Traffic.RawRetentionDays</c> (bots after
/// <c>Traffic.BotRetentionDays</c>).
/// </para>
/// </summary>
public sealed class PageView
{
    /// <summary><c>nvarchar(400)</c>, doc 08 §3.8.</summary>
    public const int PathMaxLength = 400;

    /// <summary><c>varchar(255)</c>, doc 08 §3.8.</summary>
    public const int ReferrerHostMaxLength = 255;

    /// <summary><c>nvarchar(100)</c>, doc 08 §3.8, for each of the three UTM columns.</summary>
    public const int UtmMaxLength = 100;

    public long Id { get; set; }

    /// <summary>UTC, to the second (<c>datetime2(0)</c>): when the API received the view.</summary>
    public DateTime OccurredAtUtc { get; set; }

    /// <summary>The visit-session cookie's value. Not a visitor ID: it lapses after thirty idle minutes.</summary>
    public Guid SessionId { get; set; }

    public string Path { get; set; } = string.Empty;

    public PageType PageType { get; set; }

    /// <summary>The product a product page showed, resolved from its slug. No FK (doc 08 §3.8).</summary>
    public int? ProductId { get; set; }

    /// <summary>The category a listing showed, resolved from its path. No FK (doc 08 §3.8).</summary>
    public int? CategoryId { get; set; }

    /// <summary>The referring site's host only — never its path or query, which can carry anything.</summary>
    public string? ReferrerHost { get; set; }

    public string? UtmSource { get; set; }

    public string? UtmMedium { get; set; }

    public string? UtmCampaign { get; set; }

    public TrafficChannel Channel { get; set; }

    public DeviceType DeviceType { get; set; }

    public bool IsBot { get; set; }
}
