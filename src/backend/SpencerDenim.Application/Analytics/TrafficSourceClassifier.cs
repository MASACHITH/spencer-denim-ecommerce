using System.Text.RegularExpressions;
using SpencerDenim.Domain.Analytics;

namespace SpencerDenim.Application.Analytics;

/// <summary>
/// Picks doc 08 §3.8's channel for a landing view from its UTM tags, its click ids and its referring host.
/// <para>
/// <b>The rules, first match wins:</b>
/// </para>
/// <list type="number">
/// <item><description><b>Paid</b> — <c>utm_medium</c> is a paid medium (<c>cpc</c>, <c>ppc</c>, <c>paid</c>,
/// <c>display</c>, <c>cpm</c>, <c>paid_social</c>, …) or an ads click id (<c>gclid</c>, <c>msclkid</c>, …) was present.</description></item>
/// <item><description><b>Email</b> — <c>utm_medium</c> is <c>email</c>/<c>newsletter</c>, <c>utm_source</c> is
/// <c>email</c>/<c>newsletter</c>, or the referrer is a webmail site or mail app.</description></item>
/// <item><description><b>Social</b> — a social <c>utm_medium</c> or <c>utm_source</c>, a social referrer (Facebook,
/// Instagram, TikTok, X, LinkedIn, Pinterest, YouTube, Reddit, WhatsApp, …) or <c>fbclid</c>/<c>ttclid</c>.</description></item>
/// <item><description><b>Organic</b> — <c>utm_medium=organic</c> or a search-engine referrer (Google in any country,
/// Bing, Yahoo search, DuckDuckGo, Yandex, Baidu, Ecosia, Brave, Startpage, Qwant).</description></item>
/// <item><description><b>Referral</b> — <c>utm_medium=referral</c> or any other external referrer.</description></item>
/// <item><description><b>Other</b> — UTM tags that matched none of the above and no referrer.</description></item>
/// <item><description><b>Direct</b> — nothing at all: typed, bookmarked, or a referrer the browser withheld.</description></item>
/// </list>
/// <para>
/// A page reached by clicking inside the shop has an internal referrer, which the caller has already
/// removed, so it classifies as Direct here. That is not what the reports say about it: the nightly rollup
/// attributes every view of a session to the channel of the session's first view that day (see
/// <c>TrafficRollup</c>), which is the definition doc 11 P16's "where visitors come from" needs.
/// </para>
/// </summary>
public static partial class TrafficSourceClassifier
{
    private static readonly HashSet<string> PaidMedia = new(StringComparer.Ordinal)
    {
        "cpc", "ppc", "paid", "paidsearch", "paid-search", "paid_search", "paidsocial", "paid-social",
        "paid_social", "display", "cpm", "cpv", "cpa", "banner", "retargeting", "remarketing", "ads", "ad",
    };

    private static readonly HashSet<string> EmailNames = new(StringComparer.Ordinal)
    {
        "email", "e-mail", "e_mail", "newsletter", "mail",
    };

    private static readonly HashSet<string> SocialMedia = new(StringComparer.Ordinal)
    {
        "social", "social-network", "social_network", "social-media", "social_media", "sm", "socialmedia",
    };

    private static readonly HashSet<string> SocialSources = new(StringComparer.Ordinal)
    {
        "facebook", "fb", "instagram", "ig", "tiktok", "twitter", "x", "linkedin", "pinterest", "youtube",
        "whatsapp", "reddit", "snapchat", "threads", "messenger", "telegram",
    };

    private static readonly HashSet<string> EmailHosts = new(StringComparer.Ordinal)
    {
        "mail.google.com", "inbox.google.com", "outlook.live.com", "outlook.office.com", "outlook.office365.com",
        "mail.yahoo.com", "mail.aol.com", "mail.proton.me", "mail.zoho.com",

        // Android reports the app that opened a link as `android-app://<package>`; these are mail apps.
        "com.google.android.gm", "com.microsoft.office.outlook", "com.yahoo.mobile.client.android.mail",
    };

    private static readonly string[] SocialDomains =
    [
        "facebook.com", "fb.com", "fb.me", "messenger.com", "instagram.com", "t.co", "twitter.com", "x.com",
        "linkedin.com", "lnkd.in", "pinterest.com", "tiktok.com", "youtube.com", "youtu.be", "reddit.com",
        "whatsapp.com", "wa.me", "threads.net", "snapchat.com", "tumblr.com", "vk.com", "t.me", "quora.com",

        // Android app packages.
        "com.facebook.katana", "com.facebook.orca", "com.instagram.android", "com.zhiliaoapp.musically",
        "com.twitter.android", "com.whatsapp", "org.telegram.messenger", "com.linkedin.android", "com.pinterest",
        "com.reddit.frontpage",
    ];

    private static readonly string[] SearchDomains =
    [
        "bing.com", "search.yahoo.com", "duckduckgo.com", "baidu.com", "ecosia.org", "search.brave.com",
        "startpage.com", "qwant.com", "com.google.android.googlequicksearchbox",
    ];

    /// <summary>The channel for a view with these campaign tags and this (external) referring host.</summary>
    public static TrafficChannel Classify(CampaignTags campaign, string? referrerHost)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var medium = campaign.Medium;
        var source = campaign.Source;

        if ((medium is not null && PaidMedia.Contains(medium)) || campaign.HasPaidClickId)
        {
            return TrafficChannel.Paid;
        }

        if ((medium is not null && EmailNames.Contains(medium))
            || (source is not null && EmailNames.Contains(source))
            || (referrerHost is not null && EmailHosts.Contains(referrerHost)))
        {
            return TrafficChannel.Email;
        }

        if ((medium is not null && SocialMedia.Contains(medium))
            || (source is not null && SocialSources.Contains(source))
            || (referrerHost is not null && IsUnder(referrerHost, SocialDomains))
            || campaign.HasSocialClickId)
        {
            return TrafficChannel.Social;
        }

        if (medium == "organic" || (referrerHost is not null && IsSearchEngine(referrerHost)))
        {
            return TrafficChannel.Organic;
        }

        if (medium == "referral" || referrerHost is not null)
        {
            return TrafficChannel.Referral;
        }

        return campaign.HasUtm ? TrafficChannel.Other : TrafficChannel.Direct;
    }

    /// <summary>
    /// The referring host doc 08 §3.8 stores: the host of an absolute <c>http</c>, <c>https</c> or
    /// <c>android-app</c> URL, lower-cased, without a trailing dot — never its path or query, which can carry
    /// anything at all. Null for an internal host, a malformed value or anything else, <b>and for an IP address</b>:
    /// "no IP anywhere" is a rule about the column, not only about the visitor's address — a referrer typed as
    /// <c>http://192.168.1.25/</c> or a shop browsed by its server's address would otherwise store one (W4-03 tester).
    /// </summary>
    public static string? ReferrerHost(string? referrer, IReadOnlySet<string> internalHosts)
    {
        ArgumentNullException.ThrowIfNull(internalHosts);

        if (string.IsNullOrWhiteSpace(referrer) || referrer.Length > PagePath.MaxRawLength
            || !Uri.TryCreate(referrer.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return null;
        }

        if (uri.Scheme is not ("http" or "https" or "android-app"))
        {
            return null;
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();

        if (host.Length is 0 or > PageView.ReferrerHostMaxLength || !HostPattern().IsMatch(host)
            || internalHosts.Contains(host))
        {
            return null;
        }

        return host;
    }

    private static bool IsSearchEngine(string host) =>
        GoogleOrYandex().IsMatch(host) && !EmailHosts.Contains(host) || IsUnder(host, SearchDomains);

    private static bool IsUnder(string host, string[] domains) =>
        domains.Any(domain => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));

    /// <summary>Google and Yandex search in every country (<c>google.lk</c>, <c>www.google.co.uk</c>, <c>yandex.ru</c>).</summary>
    [GeneratedRegex(@"(?:^|\.)(?:google|yandex)\.(?:[a-z]{2,3})(?:\.[a-z]{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex GoogleOrYandex();

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9.\-_]*[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();
}
