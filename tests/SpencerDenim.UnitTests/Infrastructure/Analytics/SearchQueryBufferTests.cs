using Microsoft.Extensions.Logging.Abstractions;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Analytics.Search;
using SpencerDenim.Infrastructure.Analytics;

namespace SpencerDenim.UnitTests.Infrastructure.Analytics;

/// <summary>Doc 08 DB25's in-memory buffer (issue #78): a count per (Colombo day, term), bounded, and lossless across a flush.</summary>
public sealed class SearchQueryBufferTests
{
    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 21, 4, 0, 0, TimeSpan.Zero);
    }

    private static readonly DateTimeOffset Morning = new(2026, 10, 21, 4, 0, 0, TimeSpan.Zero);

    private static SearchQueryBuffer Buffer(int capacity = 100, bool enabled = true, IClock? clock = null) =>
        new(
            new SearchStatsOptions { MaxBufferedTerms = capacity, RecordingEnabled = enabled },
            clock ?? new MutableClock(),
            NullLogger<SearchQueryBuffer>.Instance);

    [Fact]
    public void Repeated_searches_for_one_term_on_one_day_are_one_entry_holding_the_count()
    {
        var buffer = Buffer();

        for (var i = 0; i < 5; i++)
        {
            Assert.True(buffer.TryRecord("jeans", Morning.AddMinutes(i)));
        }

        Assert.True(buffer.TryRecord("linen", Morning));

        var taken = buffer.TakeAll();

        Assert.Equal(2, taken.Count);
        Assert.Equal(5, taken[new SearchQueryKey(new DateOnly(2026, 10, 21), "jeans")]);
        Assert.Equal(1, taken[new SearchQueryKey(new DateOnly(2026, 10, 21), "linen")]);
        Assert.Equal(6, buffer.Accepted);
    }

    [Fact]
    public void A_search_is_credited_to_its_own_colombo_day_however_late_the_flush_is()
    {
        var buffer = Buffer();

        // 18:29:59 UTC is 23:59:59 in Colombo; 18:30:00 UTC is midnight, the next day.
        buffer.TryRecord("saree", new DateTimeOffset(2026, 10, 21, 18, 29, 59, TimeSpan.Zero));
        buffer.TryRecord("saree", new DateTimeOffset(2026, 10, 21, 18, 30, 0, TimeSpan.Zero));

        var taken = buffer.TakeAll();

        Assert.Equal(1, taken[new SearchQueryKey(new DateOnly(2026, 10, 21), "saree")]);
        Assert.Equal(1, taken[new SearchQueryKey(new DateOnly(2026, 10, 22), "saree")]);
    }

    [Fact]
    public void Taking_empties_the_buffer_and_what_is_counted_afterwards_waits_for_the_next_flush()
    {
        var buffer = Buffer();
        buffer.TryRecord("jeans", Morning);

        Assert.Single(buffer.TakeAll());
        Assert.Equal(0, buffer.Pending);
        Assert.Empty(buffer.TakeAll());

        buffer.TryRecord("jeans", Morning);
        Assert.Equal(1, Assert.Single(buffer.TakeAll()).Value);
    }

    [Fact]
    public void A_full_buffer_keeps_counting_the_terms_it_holds_and_drops_new_ones()
    {
        var buffer = Buffer(capacity: 2);

        Assert.True(buffer.TryRecord("a1", Morning));
        Assert.True(buffer.TryRecord("b2", Morning));
        Assert.False(buffer.TryRecord("c3", Morning));
        Assert.True(buffer.TryRecord("a1", Morning));
        Assert.False(buffer.TryRecord("d4", Morning));

        var taken = buffer.TakeAll();

        Assert.Equal(2, taken.Count);
        Assert.Equal(2, taken[new SearchQueryKey(new DateOnly(2026, 10, 21), "a1")]);
        Assert.Equal(3, buffer.Accepted);
        Assert.Equal(2, buffer.Dropped);

        // Room again once the flush has taken everything.
        Assert.True(buffer.TryRecord("c3", Morning));
    }

    [Fact]
    public void Recording_switched_off_counts_nothing()
    {
        var buffer = Buffer(enabled: false);

        Assert.False(buffer.TryRecord("jeans", Morning));
        Assert.Empty(buffer.TakeAll());
        Assert.Equal(0, buffer.Accepted);
        Assert.Equal(0, buffer.Dropped);
    }

    [Fact]
    public void A_flush_that_failed_is_counted_as_lost()
    {
        var buffer = Buffer();
        buffer.CountLost(7);

        Assert.Equal(7, buffer.Dropped);
    }

    /// <summary>
    /// The lock is what makes a flush lossless: searches counted while another thread takes the buffer land in the
    /// batch being taken or in the next one, never in neither.
    /// </summary>
    [Fact]
    public async Task Nothing_is_lost_between_concurrent_counting_and_taking()
    {
        var buffer = Buffer(capacity: 10_000);
        const int Writers = 8;
        const int PerWriter = 5_000;
        var total = 0L;
        using var stop = new CancellationTokenSource();

        var taker = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                Interlocked.Add(ref total, buffer.TakeAll().Values.Sum());
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(Enumerable.Range(0, Writers).Select(writer => Task.Run(() =>
        {
            for (var i = 0; i < PerWriter; i++)
            {
                buffer.TryRecord($"term{i % 50}", Morning);
            }
        }, TestContext.Current.CancellationToken)));

        await stop.CancelAsync();
        await taker;
        total += buffer.TakeAll().Values.Sum();

        Assert.Equal(Writers * PerWriter, total);
        Assert.Equal(Writers * PerWriter, buffer.Accepted);
    }

    [Fact]
    public void The_drop_warning_is_rate_limited_but_every_drop_is_counted()
    {
        var clock = new MutableClock();
        var buffer = Buffer(capacity: 1, clock: clock);
        buffer.TryRecord("held", Morning);

        for (var i = 0; i < 10; i++)
        {
            Assert.False(buffer.TryRecord($"new{i}", Morning));
        }

        clock.UtcNow += TimeSpan.FromMinutes(2);
        Assert.False(buffer.TryRecord("later", Morning));

        Assert.Equal(11, buffer.Dropped);
    }
}

/// <summary>The public list's cache (issue #78): five minutes, keyed by day and threshold, and an eviction beats a slow read.</summary>
public sealed class PopularSearchCacheTests
{
    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 21, 4, 0, 0, TimeSpan.Zero);
    }

    private static readonly DateOnly Today = new(2026, 10, 21);
    private static readonly IReadOnlyList<PopularSearchRow> Rows = [new("jeans", 10)];

    [Fact]
    public void A_stored_list_is_served_until_it_is_five_minutes_old()
    {
        var clock = new MutableClock();
        var cache = new PopularSearchCache(clock);

        cache.Store(cache.Generation, Today, 5, Rows);
        Assert.Same(Rows, cache.TryGet(Today, 5));

        clock.UtcNow += PopularSearchCache.TimeToLive - TimeSpan.FromSeconds(1);
        Assert.Same(Rows, cache.TryGet(Today, 5));

        clock.UtcNow += TimeSpan.FromSeconds(1);
        Assert.Null(cache.TryGet(Today, 5));
    }

    [Fact]
    public void Another_day_or_threshold_is_a_miss()
    {
        var cache = new PopularSearchCache(new MutableClock());
        cache.Store(cache.Generation, Today, 5, Rows);

        Assert.Null(cache.TryGet(Today.AddDays(1), 5));
        Assert.Null(cache.TryGet(Today, 6));
    }

    [Fact]
    public void An_eviction_clears_the_entry_and_a_read_that_started_before_it_cannot_store_its_stale_answer()
    {
        var cache = new PopularSearchCache(new MutableClock());
        cache.Store(cache.Generation, Today, 5, Rows);

        var readStartedAt = cache.Generation;
        cache.Evict();
        Assert.Null(cache.TryGet(Today, 5));

        cache.Store(readStartedAt, Today, 5, Rows);
        Assert.Null(cache.TryGet(Today, 5));

        cache.Store(cache.Generation, Today, 5, Rows);
        Assert.Same(Rows, cache.TryGet(Today, 5));
    }

    [Fact]
    public void Thirteen_months_back_is_the_first_day_kept()
    {
        Assert.Equal(new DateOnly(2025, 9, 21), SearchQueryRetention.FirstDayKept(new DateOnly(2026, 10, 21)));
        Assert.Equal(new DateOnly(2025, 2, 28), SearchQueryRetention.FirstDayKept(new DateOnly(2026, 3, 31)));
    }
}
