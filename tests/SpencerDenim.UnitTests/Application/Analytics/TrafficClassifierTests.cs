using System.Reflection;
using SpencerDenim.Application.Analytics;
using SpencerDenim.Domain.Analytics;

namespace SpencerDenim.UnitTests.Application.Analytics;

/// <summary>Channel, device, bot and personal-data rules for traffic recording.</summary>
public sealed class TrafficClassifierTests
{
    private const string ChromeWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36";

    private const string SafariIphone =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Mobile/15E148 Safari/604.1";

    private const string ChromeAndroidPhone =
        "Mozilla/5.0 (Linux; Android 14; SM-A546E) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Mobile Safari/537.36";

    private const string ChromeAndroidTablet =
        "Mozilla/5.0 (Linux; Android 13; SM-X200) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36";

    private const string SafariIpad =
        "Mozilla/5.0 (iPad; CPU OS 16_7 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.6 Mobile/15E148 Safari/604.1";

    private static readonly IReadOnlySet<string> NoInternalHosts = new HashSet<string>();

    [Theory]
    [InlineData(ChromeWindows, DeviceType.Desktop)]
    [InlineData(SafariIphone, DeviceType.Mobile)]
    [InlineData(ChromeAndroidPhone, DeviceType.Mobile)]
    [InlineData(ChromeAndroidTablet, DeviceType.Tablet)]
    [InlineData(SafariIpad, DeviceType.Tablet)]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0", DeviceType.Desktop)]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_6) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Safari/605.1.15", DeviceType.Desktop)]
    [InlineData("SmartTV-Something/1.0 (compatible; Tizen)", DeviceType.Other)]
    [InlineData(null, DeviceType.Other)]
    public void Device_comes_from_the_user_agent(string? userAgent, DeviceType expected)
    {
        Assert.Equal(expected, UserAgentClassifier.Device(userAgent));
    }

    [Theory]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)")]
    [InlineData("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm) Chrome/116.0.1938.76 Safari/537.36")]
    [InlineData("facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)")]
    [InlineData("WhatsApp/2.23.20.0 A")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/129.0.0.0 Safari/537.36")]
    [InlineData("Mozilla/5.0 (Linux; Android 11; moto g power (2022)) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Mobile Safari/537.36 Chrome-Lighthouse")]
    [InlineData("curl/8.4.0")]
    [InlineData("python-requests/2.32.3")]
    [InlineData("Mozilla/5.0 (compatible; AhrefsBot/7.0; +http://ahrefs.com/robot/)")]
    [InlineData("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; GPTBot/1.2; +https://openai.com/gptbot)")]
    [InlineData("Mozilla/5.0")]
    [InlineData("short")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Crawlers_previewers_libraries_and_missing_user_agents_are_bots(string? userAgent)
    {
        Assert.True(UserAgentClassifier.IsBot(userAgent));
    }

    [Theory]
    [InlineData(ChromeWindows)]
    [InlineData(SafariIphone)]
    [InlineData(ChromeAndroidPhone)]
    [InlineData(SafariIpad)]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 [FBAN/FBIOS;FBAV/480.0.0.38.105;FBBV/644463632]")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 Instagram 350.0.0.25.84")]
    [InlineData("Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/129.0.0.0 Mobile DuckDuckGo/5 Safari/537.36")]
    public void Browsers_including_in_app_ones_are_people(string userAgent)
    {
        Assert.False(UserAgentClassifier.IsBot(userAgent));
    }

    [Fact]
    public void A_browser_that_reports_itself_automated_is_a_bot()
    {
        Assert.True(UserAgentClassifier.IsBot(ChromeWindows, automated: true));
    }

    [Theory]
    [InlineData(null, null, null, false, false, null, TrafficChannel.Direct)]
    [InlineData("google", "cpc", null, false, false, null, TrafficChannel.Paid)]
    [InlineData(null, null, null, true, false, "www.google.com", TrafficChannel.Paid)]
    [InlineData("facebook", "paid_social", null, false, true, "l.facebook.com", TrafficChannel.Paid)]
    [InlineData("newsletter", null, null, false, false, null, TrafficChannel.Email)]
    [InlineData(null, "email", null, false, false, null, TrafficChannel.Email)]
    [InlineData(null, null, null, false, false, "mail.google.com", TrafficChannel.Email)]
    [InlineData(null, null, null, false, false, "com.google.android.gm", TrafficChannel.Email)]
    [InlineData(null, null, null, false, false, "l.facebook.com", TrafficChannel.Social)]
    [InlineData(null, null, null, false, false, "www.instagram.com", TrafficChannel.Social)]
    [InlineData(null, null, null, false, false, "t.co", TrafficChannel.Social)]
    [InlineData(null, null, null, false, true, null, TrafficChannel.Social)]
    [InlineData("tiktok", null, null, false, false, null, TrafficChannel.Social)]
    [InlineData(null, null, null, false, false, "www.google.com", TrafficChannel.Organic)]
    [InlineData(null, null, null, false, false, "www.google.lk", TrafficChannel.Organic)]
    [InlineData(null, null, null, false, false, "www.google.co.uk", TrafficChannel.Organic)]
    [InlineData(null, null, null, false, false, "duckduckgo.com", TrafficChannel.Organic)]
    [InlineData(null, null, null, false, false, "www.bing.com", TrafficChannel.Organic)]
    [InlineData(null, "organic", null, false, false, null, TrafficChannel.Organic)]
    [InlineData(null, null, null, false, false, "fashionblog.lk", TrafficChannel.Referral)]
    [InlineData(null, null, null, false, false, "notgoogle.com", TrafficChannel.Referral)]
    [InlineData(null, "referral", null, false, false, null, TrafficChannel.Referral)]
    [InlineData("partner", "affiliate", null, false, false, null, TrafficChannel.Other)]
    [InlineData(null, null, "spring", false, false, null, TrafficChannel.Other)]
    public void The_channel_follows_the_documented_rules(
        string? source,
        string? medium,
        string? campaign,
        bool paidClick,
        bool socialClick,
        string? referrerHost,
        TrafficChannel expected)
    {
        Assert.Equal(
            expected,
            TrafficSourceClassifier.Classify(new CampaignTags(source, medium, campaign, paidClick, socialClick), referrerHost));
    }

    [Theory]
    [InlineData("https://www.google.com/search?q=jane%40example.com", "www.google.com")]
    [InlineData("https://WWW.Google.LK./", "www.google.lk")]
    [InlineData("android-app://com.google.android.gm/", "com.google.android.gm")]
    [InlineData("https://shop.example/product/x", null)]
    [InlineData("ftp://files.example/", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("not a url", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Only_an_external_referrers_host_is_kept(string? referrer, string? expected)
    {
        var internalHosts = new HashSet<string> { "shop.example" };

        Assert.Equal(expected, TrafficSourceClassifier.ReferrerHost(referrer, internalHosts));
    }

    [Theory]
    [InlineData("jane@example.com", true)]
    [InlineData("0771234567", true)]
    [InlineData("077 123 4567", true)]
    [InlineData("+94 (77) 123-4567", true)]
    [InlineData("levis 501 32x34", false)]
    [InlineData("123456", false)]
    [InlineData("linen shirt", false)]
    [InlineData("", false)]
    public void Personal_data_is_an_at_sign_or_a_seven_digit_run(string value, bool expected)
    {
        Assert.Equal(expected, PersonalDataPatterns.LooksPersonal(value));
    }

    [Fact]
    public void A_classified_view_keeps_the_host_not_the_referrers_path_and_carries_no_ip()
    {
        var occurred = new DateTime(2026, 10, 15, 18, 29, 59, 900, DateTimeKind.Utc);
        var session = Guid.NewGuid();

        var view = PageViewClassifier.Classify(
            new PageViewSubmission(
                "/order/3f2a6c1e-9b4d-4e8a-a7c2-5d1e0f9b8a76?token=x",
                "https://www.google.com/search?q=jane%40example.com",
                SafariIphone,
                Automated: false,
                session,
                occurred),
            NoInternalHosts);

        Assert.NotNull(view);
        Assert.Equal("/order/*", view.Path);
        Assert.Equal("www.google.com", view.ReferrerHost);
        Assert.Equal(TrafficChannel.Organic, view.Channel);
        Assert.Equal(DeviceType.Mobile, view.DeviceType);
        Assert.False(view.IsBot);
        Assert.Equal(session, view.SessionId);

        // Truncated, never rounded: 23:59:59.9 Colombo must not become the next day.
        Assert.Equal(new DateTime(2026, 10, 15, 18, 29, 59, DateTimeKind.Utc), view.OccurredAtUtc);
    }

    [Fact]
    public void A_view_with_no_session_is_not_recorded()
    {
        Assert.Null(PageViewClassifier.Classify(
            new PageViewSubmission("/", null, ChromeWindows, false, Guid.Empty, DateTime.UtcNow),
            NoInternalHosts));
    }

    /// <summary>
    /// The privacy rule by construction: no type on the recording path has anywhere to put an IP address.
    /// A field added "for bot detection" would have to get past this test first.
    /// </summary>
    [Theory]
    [InlineData(typeof(PageView))]
    [InlineData(typeof(PageViewSubmission))]
    [InlineData(typeof(TrafficDaily))]
    [InlineData(typeof(PageDaily))]
    public void No_type_on_the_recording_path_has_an_ip_or_address_member(Type type)
    {
        var members = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(property => property.Name)
            .Where(name => name.Contains("Ip", StringComparison.Ordinal)
                || name.Contains("IP", StringComparison.Ordinal)
                || name.Contains("Address", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Forwarded", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Visitor", StringComparison.OrdinalIgnoreCase)
                || name.Contains("User", StringComparison.Ordinal) && !name.Equals("UserAgent", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(members);
    }
}
