using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Analytics.Search;

namespace SpencerDenim.Infrastructure.Analytics;

/// <summary>
/// Doc 08 DB25's flush: every <see cref="SearchStatsOptions.FlushSeconds"/> (60) the buffered counts are written with
/// the race-safe upsert (<see cref="SearchQueryStore"/>).
/// <para>
/// <b>There is no nightly rollup for searches, and that is doc 08's design rather than a gap.</b> Traffic keeps raw
/// page views and rebuilds its daily tables from them each night (§8.3); DB25 keeps <i>no</i> raw search row at all —
/// the buffer is the aggregation, and the upsert writes the daily table directly — so there is nothing to rebuild
/// and no raw searches to purge. What #180's pattern does give this table is the bounded in-memory buffer, the
/// deterministic drain, the log-once failure handling below, and the nightly purge (13 months, in
/// <see cref="TrafficRetentionService"/>).
/// </para>
/// <para>
/// <b>A failed flush drops its counts</b>, as a failed page-view write drops its batch: the database being down must
/// cost statistics, not memory. EF's retry-on-failure still retries each statement before anything is given up.
/// </para>
/// <para>
/// <b>Tests and shutdown drain it deterministically</b> through <see cref="FlushAsync"/>, serialised with the
/// background cycle by one lock, so a test that has flushed knows nothing of its searches is still in flight. On
/// stop, whatever is buffered is written within five seconds or abandoned.
/// </para>
/// </summary>
internal sealed partial class SearchQueryWriterService(
    SearchQueryBuffer buffer,
    IServiceScopeFactory scopeFactory,
    SearchStatsOptions options,
    ILogger<SearchQueryWriterService> logger) : BackgroundService
{
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _consecutiveFailures;
    private long _written;

    /// <summary>Searches written since start-up.</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>Writes everything buffered now and answers the number of searches committed.</summary>
    public async Task<int> FlushAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var counts = buffer.TakeAll();
            if (counts.Count == 0)
            {
                return 0;
            }

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var written = await scope.ServiceProvider.GetRequiredService<SearchQueryStore>()
                    .UpsertAsync(counts, cancellationToken)
                    .ConfigureAwait(false);

                Interlocked.Add(ref _written, written);

                if (_consecutiveFailures > 0)
                {
                    LogRecovered(_consecutiveFailures);
                    _consecutiveFailures = 0;
                }

                return written;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                buffer.CountLost(counts.Values.Sum());
                throw;
            }
            catch (Exception exception)
            {
                buffer.CountLost(counts.Values.Sum());
                _consecutiveFailures++;

                // The exception, never the terms: a log line per term would be a second, unretained copy of the table.
                if (_consecutiveFailures == 1)
                {
                    LogWriteFailed(exception, counts.Count);
                }
                else
                {
                    LogWriteStillFailing(counts.Count, _consecutiveFailures);
                }

                return 0;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        using var drain = new CancellationTokenSource(ShutdownDrainTimeout);

        try
        {
            await FlushAsync(drain.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Out of time: the rest is abandoned.
        }
        catch (ObjectDisposedException)
        {
            // A second stop racing the first under WebApplicationFactory; see PageViewWriterService.StopAsync.
        }
    }

    public override void Dispose()
    {
        _writeLock.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.FlushInterval, stoppingToken).ConfigureAwait(false);

                // Not cancelled by the stop signal: a flush that has started finishes, and StopAsync drains the rest.
                await FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // FlushAsync contains a failed write; anything here is a fault in the cycle itself, which must not end
                // the loop. The next iteration's delay keeps it from spinning.
                LogCycleFailed(exception);
            }
        }
    }

    [LoggerMessage(
        EventId = 7152,
        Level = LogLevel.Error,
        Message = "Could not write the search counts for {Terms} term(s); they were dropped. Counting carries on.")]
    private partial void LogWriteFailed(Exception exception, int terms);

    [LoggerMessage(
        EventId = 7153,
        Level = LogLevel.Warning,
        Message = "Dropped the search counts for another {Terms} term(s): {Failures} flushes in a row have failed.")]
    private partial void LogWriteStillFailing(int terms, int failures);

    [LoggerMessage(
        EventId = 7154,
        Level = LogLevel.Information,
        Message = "Search counts are being written again after {Failures} failed flush(es).")]
    private partial void LogRecovered(int failures);

    [LoggerMessage(EventId = 7155, Level = LogLevel.Error, Message = "The search-statistics flush cycle failed; it will try again.")]
    private partial void LogCycleFailed(Exception exception);
}
