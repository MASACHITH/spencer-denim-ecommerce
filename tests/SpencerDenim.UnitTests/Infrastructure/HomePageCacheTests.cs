using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Contracts;
using SpencerDenim.Application.Content.Home;
using SpencerDenim.Domain.Content;
using SpencerDenim.Infrastructure.Content;
using SpencerDenim.Tests.MediaFixtures;
using SpencerDenim.UnitTests.Support;

namespace SpencerDenim.UnitTests.Infrastructure;

/// <summary>
/// Doc 08 §3.7's five-minute homepage cache: "homepage output cached 5 min and evicted on save".
/// </summary>
public sealed class HomePageCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly HomePageDto Page = new([
        new HomeSectionDto(9, HomeSectionType.Benefits, null, null, 9, null, null, null, [], []),
    ]);

    [Fact]
    public void An_empty_cache_answers_nothing()
    {
        Assert.Null(new HomePageCache(new FakeTimeProvider(Now)).GetPage());
    }

    [Fact]
    public void A_stored_page_comes_back()
    {
        var cache = new HomePageCache(new FakeTimeProvider(Now));

        cache.SetPage(Page, null, cache.Generation);

        Assert.Same(Page, cache.GetPage());
    }

    [Fact]
    public void It_is_still_there_one_tick_before_five_minutes()
    {
        var time = new FakeTimeProvider(Now);
        var cache = new HomePageCache(time);

        cache.SetPage(Page, null, cache.Generation);
        time.Advance(HomePageCachePolicy.Lifetime - TimeSpan.FromTicks(1));

        Assert.Same(Page, cache.GetPage());
    }

    /// <summary>
    /// Exactly at the lifetime, not a moment after: the boundary is where an off-by-one would
    /// live, and "five minutes" is the number doc 08 §3.7 gives.
    /// </summary>
    [Fact]
    public void It_is_gone_at_five_minutes()
    {
        var time = new FakeTimeProvider(Now);
        var cache = new HomePageCache(time);

        cache.SetPage(Page, null, cache.Generation);
        time.Advance(HomePageCachePolicy.Lifetime);

        Assert.Null(cache.GetPage());
    }

    [Fact]
    public void Evicting_drops_it_at_once()
    {
        var cache = new HomePageCache(new FakeTimeProvider(Now));

        cache.SetPage(Page, null, cache.Generation);
        cache.Evict();

        Assert.Null(cache.GetPage());
    }

    [Fact]
    public void Storing_again_restarts_the_five_minutes()
    {
        var time = new FakeTimeProvider(Now);
        var cache = new HomePageCache(time);

        cache.SetPage(Page, null, cache.Generation);
        time.Advance(TimeSpan.FromMinutes(4));
        cache.SetPage(Page, null, cache.Generation);
        time.Advance(TimeSpan.FromMinutes(4));

        Assert.Same(Page, cache.GetPage());
    }

    // ---- issue #37: an entry never outlives the content in it -------------------------------------

    /// <summary>
    /// A section that ends two minutes into a warm entry is gone at two minutes, not at five. Measured
    /// before the fix: the API served it ~250 s after its end.
    /// </summary>
    [Fact]
    public void An_entry_expires_at_the_next_schedule_boundary_when_that_comes_first()
    {
        var time = new FakeTimeProvider(Now);
        var cache = new HomePageCache(time);
        var boundary = Now.UtcDateTime.AddMinutes(2);

        cache.SetPage(Page, boundary, cache.Generation);

        time.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromTicks(1));
        Assert.Same(Page, cache.GetPage());

        // Exactly at the boundary: ContentSchedule's end is exclusive, so a window ending at 18:00 is
        // over at 18:00, and the page that still showed it must be too.
        time.Advance(TimeSpan.FromTicks(1));
        Assert.Null(cache.GetPage());
    }

    [Fact]
    public void A_boundary_beyond_five_minutes_leaves_the_five_minute_cap_in_charge()
    {
        var time = new FakeTimeProvider(Now);
        var cache = new HomePageCache(time);

        cache.SetPage(Page, Now.UtcDateTime.AddMinutes(30), cache.Generation);

        time.Advance(HomePageCachePolicy.Lifetime - TimeSpan.FromTicks(1));
        Assert.Same(Page, cache.GetPage());

        time.Advance(TimeSpan.FromTicks(1));
        Assert.Null(cache.GetPage());
    }

    /// <summary>
    /// A boundary that is already due means the page is stale on arrival, and it is not kept even for an
    /// instant: the next request assembles a fresh one.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_boundary_that_is_already_due_is_not_cached_at_all(int secondsFromNow)
    {
        var cache = new HomePageCache(new FakeTimeProvider(Now));

        cache.SetPage(Page, Now.UtcDateTime.AddSeconds(secondsFromNow), cache.Generation);

        Assert.Null(cache.GetPage());
    }

    /// <summary>A page that is stale on arrival must also drop the one cached before it.</summary>
    [Fact]
    public void Storing_a_page_that_is_already_stale_drops_the_one_before_it()
    {
        var cache = new HomePageCache(new FakeTimeProvider(Now));
        cache.SetPage(Page, null, cache.Generation);

        cache.SetPage(new HomePageDto([]), Now.UtcDateTime, cache.Generation);

        Assert.Null(cache.GetPage());
    }

    // ---- #192 tester D2: an eviction is not undone by a page assembled across it -------------------

    [Fact]
    public void A_page_assembled_before_an_eviction_is_not_stored()
    {
        var cache = new HomePageCache(new FakeTimeProvider(Now));
        var before = cache.Generation;

        cache.Evict();
        cache.SetPage(Page, null, before);

        Assert.Null(cache.GetPage());
        Assert.Equal(before + 1, cache.Generation);
    }

    /// <summary>A page stored after the eviction is not dropped by a stale page arriving later.</summary>
    [Fact]
    public void A_stale_page_does_not_displace_one_stored_after_the_eviction()
    {
        var cache = new HomePageCache(new FakeTimeProvider(Now));
        var stale = cache.Generation;
        cache.Evict();

        var fresh = new HomePageDto([]);
        cache.SetPage(fresh, null, cache.Generation);
        cache.SetPage(Page, null, stale);

        Assert.Same(fresh, cache.GetPage());
    }

    [Fact]
    public async Task Concurrent_evictions_are_all_counted()
    {
        var cache = new HomePageCache(new FakeTimeProvider(Now));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var index = 0; index < 1_000; index++)
            {
                cache.Evict();
            }
        })));

        Assert.Equal(8_000, cache.Generation);
    }

    [Fact]
    public void Five_minutes_is_what_doc_08_says()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), HomePageCachePolicy.Lifetime);
    }
}

/// <summary>
/// The decorator that puts the cache in front of the assembly (doc 08 §3.7).
/// <para>
/// These count how many times the page was <i>assembled</i>, not how many times the cache was
/// asked — an assertion about the cache's own bookkeeping would pass over a decorator that called
/// the database anyway and threw the answer away.
/// </para>
/// </summary>
public sealed class CachedHomePageQueryHandlerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_page_is_assembled_once_and_served_from_memory_after_that()
    {
        var inner = new CountingInner();
        var handler = new CachedHomePageQueryHandler(
            inner, new CountingCache(), new BoundaryReader(null), new FixedClock(Now));

        var first = await handler.HandleAsync(new GetHomePageQuery(), Token);
        var second = await handler.HandleAsync(new GetHomePageQuery(), Token);
        var third = await handler.HandleAsync(new GetHomePageQuery(), Token);

        Assert.Equal(1, inner.Count);
        Assert.Same(first, second);
        Assert.Same(first, third);
    }

    [Fact]
    public async Task Evicting_makes_the_next_visitor_pay_for_a_fresh_page()
    {
        var inner = new CountingInner();
        var cache = new CountingCache();
        var handler = new CachedHomePageQueryHandler(inner, cache, new BoundaryReader(null), new FixedClock(Now));

        var before = await handler.HandleAsync(new GetHomePageQuery(), Token);
        cache.Evict();
        var after = await handler.HandleAsync(new GetHomePageQuery(), Token);

        Assert.Equal(2, inner.Count);
        Assert.NotSame(before, after);
    }

    /// <summary>
    /// Issue #37: the decorator hands the cache the next schedule boundary, asked for from the instant
    /// <b>before</b> the page was assembled, so it can only ever expire the entry early, never late.
    /// </summary>
    [Fact]
    public async Task The_next_schedule_boundary_reaches_the_cache_measured_from_before_the_assembly()
    {
        var boundary = Now.UtcDateTime.AddSeconds(90);
        var reader = new BoundaryReader(boundary);
        var cache = new CountingCache();
        var handler = new CachedHomePageQueryHandler(new CountingInner(), cache, reader, new FixedClock(Now));

        await handler.HandleAsync(new GetHomePageQuery(), Token);

        Assert.Equal(boundary, cache.ValidUntil);
        Assert.Equal(Now.UtcDateTime, reader.AskedFrom);
    }

    [Fact]
    public async Task Nothing_scheduled_means_no_boundary_and_the_lifetime_alone_applies()
    {
        var cache = new CountingCache();
        var handler = new CachedHomePageQueryHandler(
            new CountingInner(), cache, new BoundaryReader(null), new FixedClock(Now));

        await handler.HandleAsync(new GetHomePageQuery(), Token);

        Assert.True(cache.WasSet);
        Assert.Null(cache.ValidUntil);
    }

    /// <summary>A cache hit asks nothing of the content store: the boundary query is a miss's cost only.</summary>
    [Fact]
    public async Task A_cache_hit_does_not_ask_for_the_boundary()
    {
        var reader = new BoundaryReader(null);
        var handler = new CachedHomePageQueryHandler(
            new CountingInner(), new CountingCache(), reader, new FixedClock(Now));

        await handler.HandleAsync(new GetHomePageQuery(), Token);
        await handler.HandleAsync(new GetHomePageQuery(), Token);

        Assert.Equal(1, reader.Asked);
    }

    private sealed class FixedClock(DateTimeOffset now) : SpencerDenim.Application.Abstractions.Time.IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class BoundaryReader(DateTime? boundary) : IHomeContentReader
    {
        public DateTime? AskedFrom { get; private set; }

        public int Asked { get; private set; }

        public Task<DateTime?> GetNextScheduleBoundaryAsync(DateTime utcNow, CancellationToken cancellationToken)
        {
            Asked++;
            AskedFrom = utcNow;
            return Task.FromResult(boundary);
        }

        public Task<IReadOnlyList<HomeSectionContent>> GetLiveSectionsAsync(
            DateTime utcNow,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The decorator never reads sections itself.");

        public Task<IReadOnlyList<AnnouncementDto>> GetLiveAnnouncementsAsync(
            DateTime utcNow,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The decorator never reads announcements.");
    }

    /// <summary>
    /// A stand-in for the assembly, counting how many times the page was really built.
    ///
    /// <para>
    /// The decorator takes the handler <i>interface</i> rather than the concrete assembler, which
    /// is what makes this possible: the two are registered against the same interface, so the
    /// uncached one carries a key (see <c>AddApplication</c>) and neither resolves the other in a
    /// circle.
    /// </para>
    /// </summary>
    private sealed class CountingInner : IQueryHandler<GetHomePageQuery, HomePageDto>
    {
        public int Count { get; private set; }

        public Task<HomePageDto> HandleAsync(GetHomePageQuery query, CancellationToken cancellationToken)
        {
            Count++;
            // A different instance each time, so "served from the cache" cannot be confused with
            // "assembled again and happened to be equal".
            return Task.FromResult(new HomePageDto([]));
        }
    }

    private sealed class CountingCache : IHomePageCache
    {
        private HomePageDto? _page;

        public HomePageDto? GetPage() => _page;

        public DateTime? ValidUntil { get; private set; }

        public bool WasSet { get; private set; }

        public long Generation { get; private set; }

        public void SetPage(HomePageDto page, DateTime? validUntilUtc, long generation)
        {
            if (generation != Generation)
            {
                return;
            }

            _page = page;
            ValidUntil = validUntilUtc;
            WasSet = true;
        }

        public void Evict()
        {
            Generation++;
            _page = null;
        }
    }
}
