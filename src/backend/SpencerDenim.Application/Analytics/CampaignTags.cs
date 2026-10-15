namespace SpencerDenim.Application.Analytics;

/// <summary>
/// What a landing URL said about where its visitor came from, read from the query string <i>before</i>
/// <see cref="PagePath"/> drops it. The UTM tags are kept (doc 08 §3.8 has a column for each); the click ids
/// are only ever used to pick a channel and are never stored, because a click id is a per-click identifier
/// an advertiser can join back to a person.
/// </summary>
/// <param name="Source"><c>utm_source</c>, lower-cased, at most 100 characters, or null.</param>
/// <param name="Medium"><c>utm_medium</c>, lower-cased, at most 100 characters, or null.</param>
/// <param name="Campaign"><c>utm_campaign</c>, at most 100 characters, or null.</param>
/// <param name="HasPaidClickId">A Google or Microsoft ads click id was present (<c>gclid</c>, <c>gbraid</c>, <c>wbraid</c>, <c>dclid</c>, <c>msclkid</c>).</param>
/// <param name="HasSocialClickId"><c>fbclid</c> or <c>ttclid</c> was present. Facebook adds <c>fbclid</c> to every outbound link, paid or not.</param>
public sealed record CampaignTags(
    string? Source,
    string? Medium,
    string? Campaign,
    bool HasPaidClickId,
    bool HasSocialClickId)
{
    public static CampaignTags None { get; } = new(null, null, null, false, false);

    /// <summary>Whether any UTM tag survived.</summary>
    public bool HasUtm => Source is not null || Medium is not null || Campaign is not null;
}
