namespace SpencerDenim.Infrastructure.Analytics;

/// <summary>
/// Traffic recording and aggregation (doc 08 §3.8, §8.3; ADM-06, ADM-20), bound from the <c>Traffic</c>
/// section — <c>Traffic__RecordingEnabled</c>, <c>Traffic__InternalHosts</c> and so on.
/// <para>
/// <b>Configuration, not <c>ops.Settings</c>.</b> These are how the machinery runs, which a shop has no
/// opinion about. The two retention periods doc 08 §3.8 puts in <c>ops.Settings</c>
/// (<c>Traffic.RawRetentionDays</c>, <c>Traffic.BotRetentionDays</c>) are there since issue #193, edited on the
/// Settings screen's Traffic form; <see cref="RawRetentionDays"/> and <see cref="BotRetentionDays"/> here are only
/// the fallback — see <see cref="RawRetentionDays"/>.
/// </para>
/// </summary>
public sealed class TrafficOptions
{
    public const string SectionName = "Traffic";

    /// <summary>Doc 08 §3.8: "every 5 s or 200 rows".</summary>
    public const int DefaultBatchSize = 200;

    /// <summary>Doc 08 §3.8: "every 5 s or 200 rows".</summary>
    public const int DefaultFlushSeconds = 5;

    /// <summary>
    /// Off means the endpoint still answers 204 and nothing is queued or written. On by default: traffic that
    /// was not recorded cannot be recovered later.
    /// </summary>
    public bool RecordingEnabled { get; set; } = true;

    /// <summary>
    /// How many views may wait for the writer. <b>A full queue drops the new view</b> rather than growing:
    /// at 200 rows per write this is fifty writes' worth, so it fills only when the database has stopped
    /// accepting rows, and then memory stays bounded at roughly a megabyte.
    /// </summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>Rows per insert, and the queue depth that wakes the writer before its interval is up.</summary>
    public int BatchSize { get; set; } = DefaultBatchSize;

    /// <summary>The longest a queued view waits before it is written.</summary>
    public int FlushSeconds { get; set; } = DefaultFlushSeconds;

    /// <summary>
    /// The shop's own host names, comma-separated (e.g. <c>colombowardrobecompany.com,www.colombowardrobecompany.com</c>).
    /// A referrer on one of these is a click inside the shop, not a source. The host the beacon itself was
    /// sent to is always treated as internal, so this is only needed for a second name.
    /// </summary>
    public string? InternalHosts { get; set; }

    /// <summary>Whether the nightly <c>NightlyStats</c> traffic rollup and the <c>Retention</c> purge run on this host.</summary>
    public bool JobsEnabled { get; set; } = true;

    /// <summary>Doc 08 §8.4 and §3.8's seed for <c>Traffic.RawRetentionDays</c>: 90 Colombo days of raw page views.</summary>
    public const int DefaultRawRetentionDays = 90;

    /// <summary>Doc 08 §8.4 and §3.8's seed for <c>Traffic.BotRetentionDays</c>: 7 Colombo days of bot page views.</summary>
    public const int DefaultBotRetentionDays = 7;

    /// <summary>
    /// The shortest either period may be: the nightly rollup rebuilds the last three completed days, so a day it may
    /// still rebuild must not have lost its raw rows. Four keeps today and those three.
    /// </summary>
    public const int MinimumRetentionDays = 4;

    /// <summary>
    /// The <b>fallback</b> number of Colombo days of raw page views kept (<c>Traffic__RawRetentionDays</c>).
    /// <para>
    /// <b>The settings row wins.</b> Since issue #193 the purge and the rollup's catch-up read
    /// <c>Traffic.RawRetentionDays</c> from <c>ops.Settings</c> (seeded 90, edited on the Settings screen) and use
    /// this figure only while that row is unset or holds something outside the form's bounds
    /// (<c>TrafficRetentionPeriods.Resolve</c>). The default stays doc 08's 90, the same as the seed, so a
    /// deployment that sets nothing behaves identically whether or not the seeder has run.
    /// </para>
    /// </summary>
    public int RawRetentionDays { get; set; } = DefaultRawRetentionDays;

    /// <summary>
    /// The <b>fallback</b> number of Colombo days of bot page views kept (<c>Traffic__BotRetentionDays</c>); the
    /// <c>Traffic.BotRetentionDays</c> settings row wins, as for <see cref="RawRetentionDays"/>.
    /// </summary>
    public int BotRetentionDays { get; set; } = DefaultBotRetentionDays;

    public TimeSpan FlushInterval => TimeSpan.FromSeconds(FlushSeconds);

    /// <summary><see cref="InternalHosts"/> as a set, lower-cased.</summary>
    public IReadOnlySet<string> InternalHostSet() =>
        (InternalHosts ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(host => host.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
}
