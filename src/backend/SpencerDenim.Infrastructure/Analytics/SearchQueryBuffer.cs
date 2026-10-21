using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Application.Analytics.Search;

namespace SpencerDenim.Infrastructure.Analytics;

/// <summary>One (Colombo day, normalised term) pair waiting to be counted.</summary>
internal readonly record struct SearchQueryKey(DateOnly Date, string Term);

/// <summary>
/// Doc 08 DB25's "Buffered in memory": submitted-search counts held per (Colombo day, term) until
/// <see cref="SearchQueryWriterService"/> flushes them.
/// <para>
/// <b>A count, not a queue.</b> A thousand searches for "jeans" in a minute are one dictionary entry holding 1000,
/// so a flush writes one row per distinct term and day, never one per search. The day is fixed when the search is
/// counted, not when it is flushed, so a minute that straddles Colombo midnight credits each search to its own day.
/// </para>
/// <para>
/// <b>Bounded.</b> At most <see cref="SearchStatsOptions.MaxBufferedTerms"/> distinct pairs wait; a full buffer
/// still counts every term it already holds and drops new ones (reported, at most once a minute). One short lock
/// guards the add and the swap — a request holds it for a dictionary increment, nothing more — so a search counted
/// while a flush takes the buffer lands either in the batch being written or in the next one, and is never lost
/// between the two.
/// </para>
/// </summary>
internal sealed partial class SearchQueryBuffer(SearchStatsOptions options, Application.Abstractions.Time.IClock clock, ILogger<SearchQueryBuffer> logger)
    : ISearchQueryRecorder
{
    private static readonly TimeSpan DropReportInterval = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private Dictionary<SearchQueryKey, int> _counts = [];
    private long _accepted;
    private long _dropped;
    private DateTimeOffset _lastDropReport = DateTimeOffset.MinValue;

    /// <summary>Searches counted since start-up.</summary>
    public long Accepted => Interlocked.Read(ref _accepted);

    /// <summary>Searches not counted because the buffer was full or a flush failed, since start-up.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Distinct (day, term) pairs waiting.</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _counts.Count;
            }
        }
    }

    public bool TryRecord(string term, DateTimeOffset occurredAtUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(term);

        if (!options.RecordingEnabled)
        {
            return false;
        }

        var key = new SearchQueryKey(ReportPeriod.ColomboDayOf(occurredAtUtc), term);
        bool accepted;
        var reportDrop = false;

        lock (_gate)
        {
            if (_counts.TryGetValue(key, out var count))
            {
                _counts[key] = count + 1;
                accepted = true;
            }
            else if (_counts.Count < options.MaxBufferedTerms)
            {
                _counts[key] = 1;
                accepted = true;
            }
            else
            {
                accepted = false;
                var now = clock.UtcNow;
                if (now - _lastDropReport >= DropReportInterval)
                {
                    _lastDropReport = now;
                    reportDrop = true;
                }
            }
        }

        if (!accepted)
        {
            Interlocked.Increment(ref _dropped);

            if (reportDrop)
            {
                LogBufferFull(options.MaxBufferedTerms);
            }

            return false;
        }

        Interlocked.Increment(ref _accepted);
        return true;
    }

    /// <summary>Takes everything waiting and leaves the buffer empty.</summary>
    public IReadOnlyDictionary<SearchQueryKey, int> TakeAll()
    {
        lock (_gate)
        {
            var taken = _counts;
            _counts = [];
            return taken;
        }
    }

    /// <summary>Counts searches a failed flush lost, so <see cref="Dropped"/> is every search that never reached the table.</summary>
    public void CountLost(int searches) => Interlocked.Add(ref _dropped, searches);

    [LoggerMessage(
        EventId = 7151,
        Level = LogLevel.Warning,
        Message = "The search-statistics buffer holds {Capacity} distinct terms; new terms are not counted until the next "
            + "flush. Is the database reachable, or is something flooding the search beacon?")]
    private partial void LogBufferFull(int capacity);
}
