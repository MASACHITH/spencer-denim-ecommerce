using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Analytics.Search;
using SpencerDenim.Domain.Analytics;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Analytics;

/// <summary>
/// The public list's short-lived cache: one entry, five minutes, evicted on this instance by every hide and show.
/// <para>
/// Doc 08 §8.2 caches dashboard reads for five minutes; this is the same idea for a list that changes, at most, once
/// a minute (the flush) and that every overlay on every page may ask for. Another instance relies on the TTL, as the
/// catalogue cache does (doc 08 §7.5). A generation counter, not a flag: a read that started before an eviction must
/// not store its stale answer after it.
/// </para>
/// </summary>
internal sealed class PopularSearchCache(IClock clock)
{
    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    // Only an answer read under the current generation is ever stored (Store), and Evict clears the entry, so an
    // entry present is always current: TryGet needs no generation check of its own.
    private (DateTimeOffset ExpiresAt, int Minimum, DateOnly Today, IReadOnlyList<PopularSearchRow> Rows)? _entry;
    private long _generation;

    public long Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    public IReadOnlyList<PopularSearchRow>? TryGet(DateOnly today, int minimum)
    {
        lock (_gate)
        {
            return _entry is { } entry && entry.ExpiresAt > clock.UtcNow
                && entry.Minimum == minimum && entry.Today == today
                ? entry.Rows
                : null;
        }
    }

    public void Store(long generation, DateOnly today, int minimum, IReadOnlyList<PopularSearchRow> rows)
    {
        lock (_gate)
        {
            if (generation == _generation)
            {
                _entry = (clock.UtcNow + TimeToLive, minimum, today, rows);
            }
        }
    }

    public void Evict()
    {
        lock (_gate)
        {
            _generation++;
            _entry = null;
        }
    }
}

/// <summary>
/// Reads the popular list from <c>analytics.SearchQueriesDaily</c> (doc 08 §7.6: "TOP 8 SearchQueriesDaily rows from
/// the last 30 days with results") with the abuse controls applied in the same statement (issue #78):
/// <list type="bullet">
/// <item><description>only searches <b>with results</b> count (<c>Searches - ZeroResultSearches</c>);</description></item>
/// <item><description>at least <c>SearchStats:PopularMinimumSearches</c> of them in the window;</description></item>
/// <item><description>no longer than <see cref="PopularSearchRules.MaxShownLength"/> characters;</description></item>
/// <item><description>not on the staff hide list.</description></item>
/// </list>
/// One range seek on the clustered key (Date first) and an anti-semi-join to the hide list's key.
/// </summary>
internal sealed class PopularSearchReader(AppDbContext dbContext, PopularSearchCache cache) : IPopularSearchReader
{
    internal const string TopSql = """
        SELECT TOP (@take) s.[QueryNormalized] AS [Term], SUM(s.[Searches] - s.[ZeroResultSearches]) AS [Searches]
        FROM [analytics].[SearchQueriesDaily] AS s
        WHERE s.[Date] >= @from AND s.[Date] <= @to
          AND LEN(s.[QueryNormalized]) <= @maxLength
          AND NOT EXISTS (SELECT 1 FROM [analytics].[HiddenSearchTerms] AS h WHERE h.[QueryNormalized] = s.[QueryNormalized])
        GROUP BY s.[QueryNormalized]
        HAVING SUM(s.[Searches] - s.[ZeroResultSearches]) >= @minimum
        ORDER BY SUM(s.[Searches] - s.[ZeroResultSearches]) DESC, s.[QueryNormalized]
        """;

    public async Task<IReadOnlyList<PopularSearchRow>> GetPublicAsync(DateOnly today, int minimumSearches, CancellationToken cancellationToken)
    {
        if (cache.TryGet(today, minimumSearches) is { } cached)
        {
            return cached;
        }

        var generation = cache.Generation;
        var rows = await GetFreshAsync(today, minimumSearches, cancellationToken);
        cache.Store(generation, today, minimumSearches, rows);

        return rows;
    }

    public async Task<IReadOnlyList<PopularSearchRow>> GetFreshAsync(DateOnly today, int minimumSearches, CancellationToken cancellationToken)
    {
        var (from, to) = PopularSearchRules.Window(today);

        return await dbContext.Database
            .SqlQueryRaw<PopularSearchRow>(
                TopSql,
                new SqlParameter("@take", System.Data.SqlDbType.Int) { Value = PopularSearchRules.Take },
                TrafficRollup.Day("@from", from),
                TrafficRollup.Day("@to", to),
                new SqlParameter("@maxLength", System.Data.SqlDbType.Int) { Value = PopularSearchRules.MaxShownLength },
                new SqlParameter("@minimum", System.Data.SqlDbType.Int) { Value = Math.Max(1, minimumSearches) })
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HiddenSearchTermRow>> GetHiddenAsync(CancellationToken cancellationToken) =>
        await dbContext.HiddenSearchTerms
            .AsNoTracking()
            .OrderBy(row => row.QueryNormalized)
            .Select(row => new HiddenSearchTermRow(row.QueryNormalized, row.HiddenAtUtc))
            .ToListAsync(cancellationToken);
}

/// <summary>
/// Writes the hide list. Each change and its <c>ops.AuditLogs</c> row commit together or not at all (issue #59's
/// rule), and either way the public list's cache on this instance is evicted, so the next shopper's overlay no longer
/// carries a term staff have just hidden.
/// <para>
/// <b>Idempotent under races.</b> Two staff hiding the same term at once: the key admits one, the other's insert fails
/// with 2627 and answers "already hidden" with nothing written. Two showing it at once: the delete that removes no
/// row writes no audit row.
/// </para>
/// </summary>
internal sealed class HiddenSearchTermStore(AppDbContext dbContext, PopularSearchCache cache) : IHiddenSearchTermStore
{
    public async Task<bool> HideAsync(string term, DateTime hiddenAtUtc, AuditLog audit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(term);
        ArgumentNullException.ThrowIfNull(audit);

        try
        {
            if (await dbContext.HiddenSearchTerms.AsNoTracking().AnyAsync(row => row.QueryNormalized == term, cancellationToken))
            {
                return false;
            }

            dbContext.HiddenSearchTerms.Add(new HiddenSearchTerm
            {
                QueryNormalized = term,
                HiddenAtUtc = hiddenAtUtc,
            });
            dbContext.AuditLogs.Add(audit);

            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.GetBaseException() is SqlException { Number: 2627 or 2601 })
        {
            return false;
        }
        finally
        {
            dbContext.ChangeTracker.Clear();
            cache.Evict();
        }
    }

    public async Task<bool> ShowAsync(string term, AuditLog audit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(term);
        ArgumentNullException.ThrowIfNull(audit);

        try
        {
            var strategy = dbContext.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

                var removed = await dbContext.HiddenSearchTerms
                    .Where(row => row.QueryNormalized == term)
                    .ExecuteDeleteAsync(cancellationToken);

                if (removed == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }

                dbContext.AuditLogs.Add(audit);
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return true;
            });
        }
        finally
        {
            dbContext.ChangeTracker.Clear();
            cache.Evict();
        }
    }
}

/// <summary>
/// Doc 08 §8.4: <c>SearchQueriesDaily</c> is kept <b>13 months</b>. Run by <see cref="TrafficRetentionService"/> at
/// 01:30 Colombo with the same shape as the traffic purge — <c>DELETE TOP (4000)</c> loops, below the lock-escalation
/// threshold, time-boxed — over a range seek on the clustered key, whose leading column is <c>Date</c>.
/// <para>
/// A fixed period and not a setting: doc 08 gives it as a number, not as an <c>ops.Settings</c> key, and the traffic
/// retention settings (#193) are about raw page views, which this table does not have.
/// </para>
/// </summary>
internal sealed class SearchQueryRetention(AppDbContext dbContext)
{
    public const int RetentionMonths = 13;

    /// <summary>The first Colombo day kept as of <paramref name="today"/>; everything before it is deleted.</summary>
    public static DateOnly FirstDayKept(DateOnly today) => today.AddMonths(-RetentionMonths);

    public async Task<int> PurgeAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var cutoff = FirstDayKept(today);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var total = 0;

        while (clock.Elapsed < TrafficRetention.TimeBox)
        {
            var deleted = await dbContext.Database.ExecuteSqlRawAsync(
                "DELETE TOP (4000) FROM [analytics].[SearchQueriesDaily] WHERE [Date] < @cutoff;",
                [TrafficRollup.Day("@cutoff", cutoff)],
                cancellationToken);
            total += deleted;

            if (deleted < TrafficRetention.BatchSize)
            {
                break;
            }
        }

        return total;
    }
}
