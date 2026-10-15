namespace SpencerDenim.Domain.Analytics;

/// <summary>
/// <c>analytics.PageDaily</c> (doc 08 §3.8; ADM-20's top pages): one Colombo day's human views of one
/// stored path. Rebuilt nightly with <see cref="TrafficDaily"/>.
/// </summary>
public sealed class PageDaily
{
    /// <summary>The Colombo calendar day.</summary>
    public DateOnly Date { get; set; }

    /// <summary><c>binary(16)</c>: <c>HASHBYTES('MD5', Path)</c>, so a 400-character path is not a key column.</summary>
    public byte[] PathHash { get; set; } = [];

    public string Path { get; set; } = string.Empty;

    public PageType PageType { get; set; }

    public int? ProductId { get; set; }

    /// <summary>Human page views (bots excluded).</summary>
    public int PageViews { get; set; }

    /// <summary>Distinct human sessions that viewed the path.</summary>
    public int Sessions { get; set; }
}
