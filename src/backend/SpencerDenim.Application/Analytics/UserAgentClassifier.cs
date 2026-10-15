using System.Text.RegularExpressions;
using SpencerDenim.Domain.Analytics;

namespace SpencerDenim.Application.Analytics;

/// <summary>
/// Bot and device detection from the <c>User-Agent</c> header, the only signal there is: doc 08 §3.8 stores no
/// IP address, so nothing network-based (data-centre ranges, reverse DNS) is available, and none is wanted.
/// <para>
/// <b>What counts as a bot</b> (<see cref="IsBot"/>):
/// </para>
/// <list type="bullet">
/// <item><description>no user agent, or one shorter than <see cref="ShortestHumanUserAgent"/> characters, or the bare
/// <c>Mozilla/5.0</c> that scripts send to look like a browser;</description></item>
/// <item><description>a user agent naming a crawler, a link-preview fetcher, a monitor or an HTTP library
/// (<see cref="BotPattern"/>: <c>bot</c>, <c>crawl</c>, <c>spider</c>, <c>slurp</c>, <c>facebookexternalhit</c>,
/// <c>WhatsApp</c>, <c>preview</c>, <c>HeadlessChrome</c>, <c>Lighthouse</c>, <c>curl</c>, <c>python</c>, …);</description></item>
/// <item><description>a browser that reports itself automated (<c>navigator.webdriver</c>), which the storefront's
/// beacon passes on — that is how Playwright, Selenium and Puppeteer driving a real browser are caught.</description></item>
/// </list>
/// <para>
/// Bot views are still recorded, with <c>IsBot = 1</c>, because doc 08 §3.8 counts them
/// (<c>TrafficDaily.BotPageViews</c>) and keeps them seven days; they are excluded from every visitor figure.
/// A bot that sends a browser's user agent and runs no automation flag is counted as a person — there is no
/// way to tell without the IP, and that is the trade doc 08 made.
/// </para>
/// <para>
/// <b>Deliberately not bots:</b> the in-app browsers of Facebook (<c>FBAN</c>), Instagram and TikTok, the
/// DuckDuckGo and Yandex browsers, and Google's mobile app — real people use all of them.
/// </para>
/// </summary>
public static partial class UserAgentClassifier
{
    /// <summary>Every real browser's user agent is far longer than this; a shorter one is a script.</summary>
    public const int ShortestHumanUserAgent = 20;

    /// <summary>Whether a request with this user agent (and automation flag) is a bot.</summary>
    public static bool IsBot(string? userAgent, bool automated = false)
    {
        if (automated || string.IsNullOrWhiteSpace(userAgent))
        {
            return true;
        }

        var trimmed = userAgent.Trim();

        return trimmed.Length < ShortestHumanUserAgent || BotPattern().IsMatch(trimmed);
    }

    /// <summary>
    /// The device family. A tablet is tested first, because an iPad's and an Android tablet's user agents
    /// also match the phone patterns; an Android user agent without <c>Mobile</c> is a tablet by Google's own
    /// convention. iPadOS 13 and later report a Mac user agent by default and are counted as desktops: there is
    /// nothing in the header to tell them apart.
    /// </summary>
    public static DeviceType Device(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return DeviceType.Other;
        }

        if (TabletPattern().IsMatch(userAgent)
            || (userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase)
                && !userAgent.Contains("Mobile", StringComparison.OrdinalIgnoreCase)))
        {
            return DeviceType.Tablet;
        }

        if (MobilePattern().IsMatch(userAgent))
        {
            return DeviceType.Mobile;
        }

        return DesktopPattern().IsMatch(userAgent) ? DeviceType.Desktop : DeviceType.Other;
    }

    [GeneratedRegex(
        @"bot\b|bot/|bot-|bot;|bot\)|crawl|spider|slurp|scraper|facebookexternalhit|facebookcatalog|meta-externalagent|"
        + @"embedly|quora link preview|whatsapp|vkshare|w3c_validator|validator\.nu|preview|headless|phantomjs|"
        + @"selenium|puppeteer|playwright|lighthouse|pagespeed|gtmetrix|pingdom|uptime|statuscake|site24x7|"
        + @"monitor|curl/|wget/|python|java/|okhttp|go-http-client|axios/|node-fetch|undici|libwww|httpclient|"
        + @"http_request|postmanruntime|insomnia|ahrefs|semrush|mj12|dotbot|petalbot|baiduspider|"
        + @"google-inspectiontool|googleother|google-read-aloud|apis-google|mediapartners-google|adsbot|"
        + @"feedfetcher|ia_archiver|archive\.org|prerender|gptbot|chatgpt-user|oai-searchbot|claude-|"
        + @"anthropic-ai|perplexity|bytespider|ccbot|amazonbot|^mozilla/5\.0$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BotPattern();

    [GeneratedRegex(
        @"ipad|tablet|kindle|silk/|playbook|nexus (?:7|9|10)\b|sm-t\d|gt-p\d",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TabletPattern();

    [GeneratedRegex(
        @"mobi|iphone|ipod|android|windows phone|iemobile|blackberry|bb10|opera mini|webos",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MobilePattern();

    [GeneratedRegex(
        @"windows nt|macintosh|mac os x|x11|linux|cros",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DesktopPattern();
}
