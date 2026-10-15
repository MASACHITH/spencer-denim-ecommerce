namespace SpencerDenim.Domain.Analytics;

/// <summary>
/// <c>analytics.TrafficDaily</c> (doc 08 §3.8; ADM-06, ADM-20): one Colombo day's traffic by channel,
/// device and source, rebuilt nightly for the last three days by <c>NightlyStats</c> (doc 08 §8.3).
/// <para>
/// <b>Attribution is per session.</b> Every human view of a session is credited to the channel, device and
/// source of that session's first view of the day, so a visitor who arrives from Google and clicks through
/// six pages is six Organic page views and one Organic session — not one Organic and five Direct, which is
/// what the raw rows say (a click inside the shop has no outside referrer). Bot views have no session worth
/// the name and are counted under their own view's channel, device and source.
/// </para>
/// </summary>
public sealed class TrafficDaily
{
    /// <summary><c>nvarchar(100)</c>, doc 08 §3.8.</summary>
    public const int SourceKeyMaxLength = 100;

    /// <summary>The source of a session with no UTM source and no referrer.</summary>
    public const string DirectSourceKey = "(direct)";

    /// <summary>Doc 08 §8.3: "SourceKey is limited to the top 50 per day, with the rest as (other)".</summary>
    public const string OtherSourceKey = "(other)";

    /// <summary>The source of a non-Direct session with no UTM source and no referrer (an untagged campaign).</summary>
    public const string NotSetSourceKey = "(not set)";

    /// <summary>Doc 08 §8.3: the top 50 sources per day keep their own key.</summary>
    public const int TopSourcesPerDay = 50;

    /// <summary>The Colombo calendar day (doc 08 §3.8: "All Date columns are Colombo dates").</summary>
    public DateOnly Date { get; set; }

    public TrafficChannel Channel { get; set; }

    public DeviceType DeviceType { get; set; }

    /// <summary>The UTM source if there was one, else the referring host, else <see cref="DirectSourceKey"/>.</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>Human page views (bots excluded).</summary>
    public int PageViews { get; set; }

    /// <summary>Distinct human sessions.</summary>
    public int Sessions { get; set; }

    /// <summary>Bot page views, kept apart from every visitor figure.</summary>
    public int BotPageViews { get; set; }
}
