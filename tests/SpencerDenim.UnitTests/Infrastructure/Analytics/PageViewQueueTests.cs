using Microsoft.Extensions.Logging.Abstractions;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Domain.Analytics;
using SpencerDenim.Infrastructure.Analytics;

namespace SpencerDenim.UnitTests.Infrastructure.Analytics;

/// <summary>
/// Doc 08 §3.8's in-memory channel: bounded, and a full one drops the new view instead of waiting or growing.
/// </summary>
public sealed class PageViewQueueTests
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 15, 4, 0, 0, TimeSpan.Zero);
    }

    private static PageViewQueue Queue(int capacity = 3, int batchSize = 2, bool enabled = true) =>
        new(
            new TrafficOptions { QueueCapacity = capacity, BatchSize = batchSize, RecordingEnabled = enabled },
            new FixedClock(),
            NullLogger<PageViewQueue>.Instance);

    private static PageView View(string path = "/") => new() { Path = path, SessionId = Guid.NewGuid() };

    [Fact]
    public void A_full_queue_drops_the_new_view_and_counts_it_without_blocking()
    {
        var queue = Queue(capacity: 3);

        Assert.True(queue.TryRecord(View("/a")));
        Assert.True(queue.TryRecord(View("/b")));
        Assert.True(queue.TryRecord(View("/c")));

        // The fourth and fifth: refused at once, counted, and the queue is still exactly three long.
        Assert.False(queue.TryRecord(View("/d")));
        Assert.False(queue.TryRecord(View("/e")));

        Assert.Equal(3, queue.Accepted);
        Assert.Equal(2, queue.Dropped);
        Assert.Equal(3, queue.Reader.Count);
        Assert.Equal(["/a", "/b", "/c"], queue.Take(10).Select(view => view.Path));
    }

    [Fact]
    public void Recording_off_queues_nothing()
    {
        var queue = Queue(enabled: false);

        Assert.False(queue.TryRecord(View()));
        Assert.Equal(0, queue.Reader.Count);
        Assert.Equal(0, queue.Dropped);
    }

    [Fact]
    public void Take_never_returns_more_than_asked()
    {
        var queue = Queue(capacity: 10);

        for (var i = 0; i < 5; i++)
        {
            queue.TryRecord(View());
        }

        Assert.Equal(2, queue.Take(2).Count);
        Assert.Equal(3, queue.Take(10).Count);
        Assert.Empty(queue.Take(10));
    }

    [Fact]
    public async Task A_full_batch_wakes_the_writer_before_its_interval()
    {
        var queue = Queue(capacity: 10, batchSize: 2);

        var waiting = queue.WaitForBatchAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);

        queue.TryRecord(View());
        queue.TryRecord(View());

        var finished = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Same(waiting, finished);
    }

    [Fact]
    public async Task With_less_than_a_batch_the_writer_waits_out_its_interval()
    {
        var queue = Queue(capacity: 10, batchSize: 5);
        queue.TryRecord(View());

        var started = DateTime.UtcNow;
        await queue.WaitForBatchAsync(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(250));
    }
}
