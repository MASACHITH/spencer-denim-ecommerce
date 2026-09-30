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

        cache.SetPage(Page);

        Assert.Same(Page, cache.GetPage());
    }

    [Fact]
    public void It_is_still_there_one_tick_before_five_minutes()
    {
        var time = new FakeTimeProvider(Now);
        var cache = new HomePageCache(time);

        cache.SetPage(Page);
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

        cache.SetPage(Page);
        time.Advance(HomePageCachePolicy.Lifetime);

        Assert.Null(cache.GetPage());
    }

    [Fact]
    public void Evicting_drops_it_at_once()
    {
        var cache = new HomePageCache(new FakeTimeProvider(Now));

        cache.SetPage(Page);
        cache.Evict();

        Assert.Null(cache.GetPage());
    }

    [Fact]
    public void Storing_again_restarts_the_five_minutes()
    {
        var time = new FakeTimeProvider(Now);
        var cache = new HomePageCache(time);

        cache.SetPage(Page);
        time.Advance(TimeSpan.FromMinutes(4));
        cache.SetPage(Page);
        time.Advance(TimeSpan.FromMinutes(4));

        Assert.Same(Page, cache.GetPage());
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

    [Fact]
    public async Task The_page_is_assembled_once_and_served_from_memory_after_that()
    {
        var inner = new CountingInner();
        var handler = new CachedHomePageQueryHandler(inner, new CountingCache());

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
        var handler = new CachedHomePageQueryHandler(inner, cache);

        var before = await handler.HandleAsync(new GetHomePageQuery(), Token);
        cache.Evict();
        var after = await handler.HandleAsync(new GetHomePageQuery(), Token);

        Assert.Equal(2, inner.Count);
        Assert.NotSame(before, after);
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

        public void SetPage(HomePageDto page) => _page = page;

        public void Evict() => _page = null;
    }
}
