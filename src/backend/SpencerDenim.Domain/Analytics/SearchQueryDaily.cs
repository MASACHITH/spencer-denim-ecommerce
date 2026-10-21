namespace SpencerDenim.Domain.Analytics;

/// <summary>
/// <c>analytics.SearchQueriesDaily</c> (doc 08 §3.8, DB25; SRCH-01 "popular searches", issue #78): how many
/// times one normalised search term was <b>submitted</b> on one Colombo day, and how many of those found nothing.
/// <para>
/// <b>There is no raw search log, and there must never be one.</b> A row is a count against a term and a day: no
/// session, no user, no IP address, no time of day. The term itself has already been normalised and vetted by
/// <c>SearchQueryText</c> — lower-cased, trimmed, single-spaced, at most 100 characters, and dropped altogether
/// when it looks like an email address, a telephone number or an order number — so nothing a shopper typed by
/// mistake into a search box is kept for the table's 13 months (doc 08 §8.4).
/// </para>
/// <para>
/// Written only by the buffered, race-safe upsert DB25 specifies (<c>SearchQueryStore</c>), never by EF change
/// tracking; mapped so the schema, the migration and the purge share one definition.
/// </para>
/// </summary>
public sealed class SearchQueryDaily
{
    /// <summary>Doc 08 §3.8: <c>QueryNormalized nvarchar(100)</c>.</summary>
    public const int QueryMaxLength = 100;

    /// <summary>The Colombo calendar day the searches were made (doc 08 §3.8: "All Date columns are Colombo dates").</summary>
    public DateOnly Date { get; set; }

    /// <summary>The normalised term. Part of the key with <see cref="Date"/>.</summary>
    public string QueryNormalized { get; set; } = string.Empty;

    /// <summary>Submitted searches for the term that day.</summary>
    public int Searches { get; set; }

    /// <summary>Of <see cref="Searches"/>, how many matched no product when they were counted.</summary>
    public int ZeroResultSearches { get; set; }
}

/// <summary>
/// <c>analytics.HiddenSearchTerms</c> (issue #78, owner decision recorded in
/// <c>docs/journal/2026-10-21-78-118-search.md</c>): a term staff have taken off the public "popular searches"
/// list. Showing other shoppers' search terms in the storefront is a place for an offensive term to appear,
/// however unlikely the other filters make it; this is the staff override.
/// <para>
/// <b>Who hid it is in <c>ops.AuditLogs</c>, not here.</b> The analytics schema carries no column that names or
/// identifies a person (<c>AnalyticsSchemaTests</c>), and the audit trail already records the actor, the time
/// and the term for every hide and every un-hide.
/// </para>
/// </summary>
public sealed class HiddenSearchTerm
{
    /// <summary>The normalised term, exactly as <see cref="SearchQueryDaily.QueryNormalized"/> holds it.</summary>
    public string QueryNormalized { get; set; } = string.Empty;

    /// <summary>When it was hidden (UTC).</summary>
    public DateTime HiddenAtUtc { get; set; }
}
