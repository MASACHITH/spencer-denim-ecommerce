using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Analytics;
using SpencerDenim.Domain.Analytics;

namespace SpencerDenim.Infrastructure.Analytics;

/// <summary>
/// Doc 08 §3.8's "in-memory channel": the bounded queue between the recording endpoint and
/// <see cref="PageViewWriterService"/>.
/// <para>
/// <b>Bounded, and full means drop.</b> <see cref="TryRecord"/> uses <see cref="ChannelWriter{T}.TryWrite"/>
/// on a channel in <see cref="BoundedChannelFullMode.Wait"/> mode, which never waits: it answers
/// <see langword="false"/> when the channel is full, and the view is counted and discarded. Nothing on the
/// request path awaits anything, so a database that is down or slow cannot hold a page up, and the queue
/// cannot grow past <see cref="TrafficOptions.QueueCapacity"/> however long it is down.
/// </para>
/// </summary>
internal sealed partial class PageViewQueue : IPageViewRecorder
{
    /// <summary>How often a sustained run of drops is reported, after the first.</summary>
    private static readonly TimeSpan DropReportInterval = TimeSpan.FromMinutes(1);

    private readonly Channel<PageView> _channel;
    private readonly TrafficOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<PageViewQueue> _logger;

    /// <summary>
    /// Completed when the queue reaches a full batch, so the writer need not wait out its interval. A task
    /// rather than a semaphore: nothing to dispose, so a host that drains the queue while it is being torn down
    /// cannot trip over a disposed primitive.
    /// </summary>
    private TaskCompletionSource _batchReady = NewSignal();

    private long _accepted;
    private long _dropped;
    private long _droppedSinceReport;
    private long _lastDropReportTicks;

    public PageViewQueue(TrafficOptions options, IClock clock, ILogger<PageViewQueue> logger)
    {
        _options = options;
        _clock = clock;
        _logger = logger;
        _channel = Channel.CreateBounded<PageView>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    /// <summary>The writer's end.</summary>
    public ChannelReader<PageView> Reader => _channel.Reader;

    /// <summary>Views accepted onto the queue since start-up.</summary>
    public long Accepted => Interlocked.Read(ref _accepted);

    /// <summary>Views discarded because the queue was full, since start-up.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public bool TryRecord(PageView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (!_options.RecordingEnabled)
        {
            return false;
        }

        if (_channel.Writer.TryWrite(view))
        {
            Interlocked.Increment(ref _accepted);

            if (_channel.Reader.Count >= _options.BatchSize)
            {
                SignalBatchReady();
            }

            return true;
        }

        Interlocked.Increment(ref _dropped);
        ReportDrop();
        return false;
    }

    /// <summary>
    /// Waits until a full batch is queued or <paramref name="timeout"/> passes, whichever is first.
    /// </summary>
    public async Task WaitForBatchAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var signal = Volatile.Read(ref _batchReady);

        // A signal left over from the last cycle is spent: replace it, then look at the queue again, so a batch
        // that filled between the two reads is not missed.
        if (signal.Task.IsCompleted)
        {
            Interlocked.CompareExchange(ref _batchReady, NewSignal(), signal);
            signal = Volatile.Read(ref _batchReady);
        }

        if (_channel.Reader.Count >= _options.BatchSize)
        {
            return;
        }

        try
        {
            await signal.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The interval is up: write what there is.
        }
    }

    /// <summary>Takes up to <paramref name="max"/> queued views.</summary>
    public List<PageView> Take(int max)
    {
        var batch = new List<PageView>(Math.Min(max, Math.Max(_channel.Reader.Count, 1)));

        while (batch.Count < max && _channel.Reader.TryRead(out var view))
        {
            batch.Add(view);
        }

        return batch;
    }

    /// <summary>Counts views a failed write lost, so <see cref="Dropped"/> is every view that never reached the table.</summary>
    public void CountLost(int views) => Interlocked.Add(ref _dropped, views);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void SignalBatchReady() => Volatile.Read(ref _batchReady).TrySetResult();

    /// <summary>
    /// The first drop is logged at once and a continuing run at most once a minute, with the count. A drop
    /// per log line would turn a database outage into a log flood on top of it.
    /// </summary>
    private void ReportDrop()
    {
        var pending = Interlocked.Increment(ref _droppedSinceReport);
        var now = _clock.UtcNow.UtcTicks;
        var last = Interlocked.Read(ref _lastDropReportTicks);

        if (last != 0 && now - last < DropReportInterval.Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastDropReportTicks, now, last) != last)
        {
            return;
        }

        Interlocked.Add(ref _droppedSinceReport, -pending);
        LogDropped(pending, _options.QueueCapacity);
    }

    [LoggerMessage(
        EventId = 7101,
        Level = LogLevel.Warning,
        Message = "The page-view queue is full ({Capacity} views); {Dropped} view(s) were dropped. The page-view "
            + "writer is not keeping up — is the database reachable?")]
    private partial void LogDropped(long dropped, int capacity);
}
