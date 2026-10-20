using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Contracts;
using SpencerDenim.Application.Content.Home;
using SpencerDenim.Infrastructure.Content;
using SpencerDenim.Tests.MediaFixtures;
using SpencerDenim.UnitTests.Support;

namespace SpencerDenim.UnitTests.Infrastructure;

/// <summary>
/// Independent tester's additions for #37 and #192: the cache and the decorator under the interleavings a
/// warm homepage meets in production — a save landing while a page is being assembled, and many visitors
/// missing at one shared schedule boundary.
/// </summary>
public sealed class HomePageCacheRaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A save that lands while a visitor's page is being assembled evicts the cache — but the visitor's
    /// request then stores the page it assembled from the rows <i>before</i> the save. That page must not be
    /// what the next visitor sees for the next five minutes: "evicted on save" (doc 08 §3.7) has to win.
    /// <para>
    /// DEFECT (tester, #192): the decorator calls <c>SetPage</c> unconditionally after assembly, so the
    /// eviction is lost and the pre-save page is served until the lifetime or the next boundary. A
    /// generation counter read before assembly (and compared in <c>SetPage</c>) closes it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_save_during_assembly_is_not_undone_by_the_page_assembled_before_it()
    {
        var time = new FakeTimeProvider(Now);
        var cache = new HomePageCache(time);
        var stale = new HomePageDto([]);

        // The inner handler reads the rows, then a save lands and evicts, then the handler returns.
        var inner = new InnerThatEvictsMidway(cache, stale);
        var handler = new CachedHomePageQueryHandler(inner, cache, new NoBoundary(), new FixedClock(Now));

        await handler.HandleAsync(new GetHomePageQuery(), Token);

        Assert.Null(cache.GetPage());
    }

    /// <summary>
    /// Many entries sharing one boundary is one expiry, not many: the entry goes once at the boundary and
    /// the next page is cached again straight away, with the following boundary.
    /// </summary>
    [Fact]
    public void A_shared_boundary_expires_the_entry_once_and_the_next_page_is_cached_at_once()
    {
        var time = new FakeTimeProvider(Now);
        var cache = new HomePageCache(time);
        var boundary = Now.UtcDateTime.AddSeconds(30);

        cache.SetPage(new HomePageDto([]), boundary, cache.Generation);
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(cache.GetPage());

        var next = new HomePageDto([]);
        cache.SetPage(next, null, cache.Generation);
        Assert.Same(next, cache.GetPage());
    }

    /// <summary>
    /// A boundary measured as a non-UTC kind still names the instant it was stored as (the converter stamps
    /// UTC on read; this pins that the cache does not shift a value by the server's offset).
    /// </summary>
    [Fact]
    public void A_boundary_of_unspecified_kind_is_read_as_utc()
    {
        var time = new FakeTimeProvider(Now);
        var cache = new HomePageCache(time);

        cache.SetPage(new HomePageDto([]), DateTime.SpecifyKind(Now.UtcDateTime.AddMinutes(1), DateTimeKind.Unspecified), cache.Generation);

        time.Advance(TimeSpan.FromMinutes(1) - TimeSpan.FromTicks(1));
        Assert.NotNull(cache.GetPage());
        time.Advance(TimeSpan.FromTicks(1));
        Assert.Null(cache.GetPage());
    }

    private sealed class InnerThatEvictsMidway(IHomePageCache cache, HomePageDto page) : IQueryHandler<GetHomePageQuery, HomePageDto>
    {
        public Task<HomePageDto> HandleAsync(GetHomePageQuery query, CancellationToken cancellationToken)
        {
            cache.Evict();
            return Task.FromResult(page);
        }
    }

    private sealed class NoBoundary : IHomeContentReader
    {
        public Task<IReadOnlyList<HomeSectionContent>> GetLiveSectionsAsync(DateTime utcNow, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DateTime?> GetNextScheduleBoundaryAsync(DateTime utcNow, CancellationToken cancellationToken) =>
            Task.FromResult<DateTime?>(null);

        public Task<IReadOnlyList<AnnouncementDto>> GetLiveAnnouncementsAsync(DateTime utcNow, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedClock(DateTimeOffset now) : SpencerDenim.Application.Abstractions.Time.IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
