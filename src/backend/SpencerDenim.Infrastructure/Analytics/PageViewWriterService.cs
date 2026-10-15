using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SpencerDenim.Infrastructure.Analytics;

/// <summary>
/// The batched writer doc 08 §3.8 specifies: "Batched insert from an in-memory channel (every 5 s or 200
/// rows)".
/// <para>
/// <b>The cycle.</b> Wait for the first queued view; then wait up to <see cref="TrafficOptions.FlushSeconds"/>
/// for the batch to fill to <see cref="TrafficOptions.BatchSize"/>, waking early if it does; then write
/// everything queued, <see cref="TrafficOptions.BatchSize"/> rows per insert. So a view waits at most five
/// seconds, and under load rows go in 200 at a time.
/// </para>
/// <para>
/// <b>A failed write drops its batch.</b> The database being down must cost views, not pages (and not
/// memory): the batch is counted as lost, the failure is logged once and then quietly, and the next batch is
/// tried as normal. EF's retry-on-failure still applies to the insert itself, so a transient blip is retried
/// before anything is given up; while that happens the queue keeps accepting views up to its bound.
/// </para>
/// <para>
/// <b>Tests and shutdown drain it deterministically</b> through <see cref="FlushAsync"/>, which writes
/// everything queued and returns only when it is committed. One lock serialises it with the background
/// cycle, so a test that has flushed knows no write of its views is still in flight — the lesson of the
/// dashboard tests' background order breaking <c>OrderExpiryTests</c>. On stop, whatever is queued is
/// written within five seconds or abandoned.
/// </para>
/// </summary>
internal sealed partial class PageViewWriterService(
    PageViewQueue queue,
    IServiceScopeFactory scopeFactory,
    TrafficOptions options,
    ILogger<PageViewWriterService> logger) : BackgroundService
{
    /// <summary>How long a stopping host waits for the last views to be written.</summary>
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Serialises <see cref="FlushAsync"/> with the background cycle.</summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private int _consecutiveFailures;

    /// <summary>Rows written since start-up.</summary>
    public long Written => Interlocked.Read(ref _written);

    private long _written;

    /// <summary>
    /// Writes everything queued now, in batches, and returns the number of rows committed. Serialised with
    /// the background cycle: when it returns, no write of a view queued before the call is still in flight.
    /// </summary>
    public async Task<int> FlushAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var written = 0;

            while (true)
            {
                var batch = queue.Take(options.BatchSize);

                if (batch.Count == 0)
                {
                    return written;
                }

                written += await WriteAsync(batch, cancellationToken).ConfigureAwait(false);
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
            // Out of time: the rest is abandoned, which is what a recorder that must never hold anything up does.
        }
        catch (ObjectDisposedException)
        {
            // A second stop racing the first: under WebApplicationFactory the host is stopped both by the factory
            // and by Program's own RunAsync, which disposes the container as soon as its stop returns.
            // That first stop drained the queue before the disposal, so there is nothing left to do.
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
                if (!await queue.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                {
                    return;
                }

                await queue.WaitForBatchAsync(options.FlushInterval, stoppingToken).ConfigureAwait(false);

                // Not cancelled by the stop signal: a write that has started finishes (bounded by the command
                // timeout), so stopping the host does not throw away the batch in flight. StopAsync then
                // drains whatever is left.
                await FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // WriteAsync already contains a failed insert; anything reaching here is a fault in the cycle
                // itself. It must not end the loop (an exception out of ExecuteAsync stops recording for the
                // life of the process) and must not spin, so it pauses before trying again.
                LogCycleFailed(exception);

                try
                {
                    await Task.Delay(options.FlushInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task<int> WriteAsync(List<Domain.Analytics.PageView> batch, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<PageViewStore>();

            await store.InsertAsync(batch, cancellationToken).ConfigureAwait(false);

            Interlocked.Add(ref _written, batch.Count);

            if (_consecutiveFailures > 0)
            {
                LogRecovered(_consecutiveFailures);
                _consecutiveFailures = 0;
            }

            return batch.Count;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            queue.CountLost(batch.Count);
            throw;
        }
        catch (Exception exception)
        {
            queue.CountLost(batch.Count);
            _consecutiveFailures++;

            // The exception is logged, never the rows: there is nothing personal in them, but a log line per
            // path would be a second, unretained copy of the traffic table.
            if (_consecutiveFailures == 1)
            {
                LogWriteFailed(exception, batch.Count);
            }
            else
            {
                LogWriteStillFailing(batch.Count, _consecutiveFailures);
            }

            return 0;
        }
    }

    [LoggerMessage(
        EventId = 7111,
        Level = LogLevel.Error,
        Message = "Could not write {Count} page view(s); they were dropped. Recording carries on.")]
    private partial void LogWriteFailed(Exception exception, int count);

    [LoggerMessage(
        EventId = 7112,
        Level = LogLevel.Warning,
        Message = "Dropped another {Count} page view(s): {Failures} writes in a row have failed.")]
    private partial void LogWriteStillFailing(int count, int failures);

    [LoggerMessage(
        EventId = 7113,
        Level = LogLevel.Information,
        Message = "Page views are being written again after {Failures} failed write(s).")]
    private partial void LogRecovered(int failures);

    [LoggerMessage(
        EventId = 7114,
        Level = LogLevel.Error,
        Message = "The page-view writer's cycle failed; it will try again.")]
    private partial void LogCycleFailed(Exception exception);
}
