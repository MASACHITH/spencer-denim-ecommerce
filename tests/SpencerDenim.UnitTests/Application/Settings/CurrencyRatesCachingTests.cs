using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Application.Settings.Contracts;
using SpencerDenim.Application.Settings.Site;
using SpencerDenim.Infrastructure.Settings;
using SpencerDenim.UnitTests.Support;

namespace SpencerDenim.UnitTests.Application.Settings;

/// <summary>
/// The currency feed's sixty-second cache (issue #248, m2), arranged as the site-settings feed's is: a singleton
/// cache, a decorating query handler, and eviction by the save. These pin the three things that matter about a
/// cache in front of money: a second call does not read the database, a save makes the next call read it again, and
/// a failed read is never remembered.
/// </summary>
public sealed class CurrencyRatesCachingTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static IReadOnlyList<CurrencyRateDto> Usd(decimal rate) =>
        [new CurrencyRateDto("USD", "US$", rate, null)];

    // ---- the cache itself --------------------------------------------------------------------------------

    [Fact]
    public void A_cache_that_has_been_given_nothing_has_nothing()
    {
        var cache = new CurrencyRatesCache(new FakeTimeProvider(Start));

        Assert.Null(cache.GetRates());
    }

    [Fact]
    public void An_entry_is_returned_until_exactly_sixty_seconds_have_passed()
    {
        var time = new FakeTimeProvider(Start);
        var cache = new CurrencyRatesCache(time);
        var rates = Usd(301.5m);

        cache.SetRates(rates);

        Assert.Same(rates, cache.GetRates());

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Same(rates, cache.GetRates());

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(cache.GetRates());
    }

    [Fact]
    public void The_lifetime_is_the_sixty_seconds_doc_08_gives_options_classes()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), CurrencyRatesCachePolicy.Lifetime);
    }

    [Fact]
    public void An_expired_entry_is_dropped_and_a_new_one_starts_a_new_minute()
    {
        var time = new FakeTimeProvider(Start);
        var cache = new CurrencyRatesCache(time);

        cache.SetRates(Usd(301.5m));
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(cache.GetRates());

        var fresh = Usd(305m);
        cache.SetRates(fresh);
        time.Advance(TimeSpan.FromSeconds(59));

        Assert.Same(fresh, cache.GetRates());
    }

    [Fact]
    public void Evict_drops_the_entry_at_once()
    {
        var cache = new CurrencyRatesCache(new FakeTimeProvider(Start));
        cache.SetRates(Usd(301.5m));

        cache.Evict();

        Assert.Null(cache.GetRates());
    }

    /// <summary>
    /// "No rate set" is a true answer and a common one, and it must come back as an empty list rather than as
    /// "nothing cached", or the cache would be bypassed for exactly the shop that has not set a rate.
    /// </summary>
    [Fact]
    public void An_empty_list_is_a_real_cached_answer_and_not_the_same_as_nothing_cached()
    {
        var cache = new CurrencyRatesCache(new FakeTimeProvider(Start));
        IReadOnlyList<CurrencyRateDto> none = [];

        cache.SetRates(none);

        var cached = cache.GetRates();
        Assert.NotNull(cached);
        Assert.Empty(cached);
    }

    [Fact]
    public void A_null_list_is_refused_rather_than_stored_as_if_it_were_an_answer()
    {
        var cache = new CurrencyRatesCache(new FakeTimeProvider(Start));

        Assert.Throws<ArgumentNullException>(() => cache.SetRates(null!));
    }

    // ---- the decorator -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_second_call_is_answered_from_the_cache_and_does_not_read_the_database()
    {
        var reads = new CountingInner(Usd(301.5m));
        var handler = Decorate(reads, new FakeTimeProvider(Start));

        var first = await handler.HandleAsync(new GetCurrencyRatesQuery(), Token);
        var second = await handler.HandleAsync(new GetCurrencyRatesQuery(), Token);

        Assert.Equal(1, reads.Calls);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task A_save_evicting_the_cache_makes_the_next_call_read_the_new_rate()
    {
        var time = new FakeTimeProvider(Start);
        var cache = new CurrencyRatesCache(time);
        var reads = new CountingInner(Usd(301.5m));
        var handler = new CachedCurrencyRatesQueryHandler(reads, cache);

        await handler.HandleAsync(new GetCurrencyRatesQuery(), Token);
        reads.Answer = Usd(310m);

        // Still inside the minute: the old rate, because nothing has told the cache.
        Assert.Equal(301.5m, (await handler.HandleAsync(new GetCurrencyRatesQuery(), Token))[0].LkrPerUnit);

        // The save's eviction: the very next call reads again and sees the new rate, with no wait.
        cache.Evict();

        Assert.Equal(310m, (await handler.HandleAsync(new GetCurrencyRatesQuery(), Token))[0].LkrPerUnit);
        Assert.Equal(2, reads.Calls);
    }

    [Fact]
    public async Task After_a_minute_the_next_call_reads_again_without_any_eviction()
    {
        var time = new FakeTimeProvider(Start);
        var reads = new CountingInner(Usd(301.5m));
        var handler = Decorate(reads, time);

        await handler.HandleAsync(new GetCurrencyRatesQuery(), Token);
        time.Advance(TimeSpan.FromSeconds(60));
        await handler.HandleAsync(new GetCurrencyRatesQuery(), Token);

        Assert.Equal(2, reads.Calls);
    }

    /// <summary>
    /// <b>A failure must not poison the cache.</b> The reader throws, the exception travels on (the endpoint turns
    /// it into an empty answer with no cache lifetime) and nothing is stored, so the next call reads again and gets
    /// the real answer; and only that answer is then cached.
    /// </summary>
    [Fact]
    public async Task A_failed_read_is_not_remembered_and_the_next_call_reads_again()
    {
        var reads = new CountingInner(Usd(301.5m)) { FailTimes = 1 };
        var cache = new CurrencyRatesCache(new FakeTimeProvider(Start));
        var handler = new CachedCurrencyRatesQueryHandler(reads, cache);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(new GetCurrencyRatesQuery(), Token));
        Assert.Null(cache.GetRates());

        var recovered = await handler.HandleAsync(new GetCurrencyRatesQuery(), Token);
        var again = await handler.HandleAsync(new GetCurrencyRatesQuery(), Token);

        Assert.Equal(301.5m, recovered[0].LkrPerUnit);
        Assert.Same(recovered, again);
        Assert.Equal(2, reads.Calls);
    }

    /// <summary>
    /// A read that succeeds and finds no rate is the true answer for a shop that has not set one, and it is cached
    /// like any other (or the cache would do nothing for the common case); the save evicts it.
    /// </summary>
    [Fact]
    public async Task A_successful_empty_read_is_cached_and_a_save_makes_the_first_rate_appear_at_once()
    {
        var cache = new CurrencyRatesCache(new FakeTimeProvider(Start));
        var reads = new CountingInner([]);
        var handler = new CachedCurrencyRatesQueryHandler(reads, cache);

        Assert.Empty(await handler.HandleAsync(new GetCurrencyRatesQuery(), Token));
        Assert.Empty(await handler.HandleAsync(new GetCurrencyRatesQuery(), Token));
        Assert.Equal(1, reads.Calls);

        reads.Answer = Usd(301.5m);
        cache.Evict();

        Assert.Single(await handler.HandleAsync(new GetCurrencyRatesQuery(), Token));
        Assert.Equal(2, reads.Calls);
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static CachedCurrencyRatesQueryHandler Decorate(CountingInner inner, TimeProvider time) =>
        new(inner, new CurrencyRatesCache(time));

    private sealed class CountingInner(IReadOnlyList<CurrencyRateDto> answer)
        : IQueryHandler<GetCurrencyRatesQuery, IReadOnlyList<CurrencyRateDto>>
    {
        public IReadOnlyList<CurrencyRateDto> Answer { get; set; } = answer;

        public int Calls { get; private set; }

        public int FailTimes { get; set; }

        public Task<IReadOnlyList<CurrencyRateDto>> HandleAsync(GetCurrencyRatesQuery query, CancellationToken cancellationToken)
        {
            Calls++;

            if (FailTimes > 0)
            {
                FailTimes--;
                throw new InvalidOperationException("The database is not there.");
            }

            return Task.FromResult(Answer);
        }
    }
}
