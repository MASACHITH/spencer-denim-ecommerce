using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Marketing.Newsletter;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Tests.MediaFixtures;
using SpencerDenim.UnitTests.Support;

namespace SpencerDenim.UnitTests.Application.Marketing;

/// <summary>
/// The newsletter signup use case (HOME-10, GLB-07). What reaches the store, what never does, and
/// in what order.
/// <para>
/// The store stub answers from the arguments it is given and records them, so an assertion about
/// the outcome is an assertion about what would have been written.
/// </para>
/// </summary>
public sealed class SubscribeToNewsletterCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("shopper@localhost")]
    public async Task An_address_the_store_will_not_hold_never_reaches_it(string? email)
    {
        var store = new StubStore();
        var throttle = new StubThrottle();

        var outcome = await HandleAsync(
            store,
            throttle,
            new SubscribeToNewsletterCommand(email, NewsletterSource.Homepage, null));

        Assert.Equal(NewsletterSubscriptionOutcome.InvalidEmail, outcome);
        Assert.Empty(store.Calls);
    }

    /// <summary>
    /// The ordering the rate limit depends on. ASP.NET Core's own limiter takes its permit before
    /// the endpoint runs, so ten typos would have spent ten permits — and behind a reverse proxy,
    /// where every visitor shares one partition, that is ten permits out of everyone's budget.
    /// </summary>
    [Fact]
    public async Task A_mistyped_address_does_not_consume_a_permit()
    {
        var throttle = new StubThrottle();

        await HandleAsync(
            new StubStore(),
            throttle,
            new SubscribeToNewsletterCommand("not-an-address", NewsletterSource.Homepage, "203.0.113.7"));

        Assert.Empty(throttle.Asked);
    }

    [Fact]
    public async Task A_caller_with_no_permits_left_is_told_so_and_nothing_is_written()
    {
        var store = new StubStore();
        var throttle = new StubThrottle { Allow = false };

        var outcome = await HandleAsync(
            store,
            throttle,
            new SubscribeToNewsletterCommand("shopper@example.com", NewsletterSource.Homepage, "203.0.113.7"));

        Assert.Equal(NewsletterSubscriptionOutcome.RateLimited, outcome);
        Assert.Empty(store.Calls);
        Assert.Equal(["203.0.113.7"], throttle.Asked);
    }

    [Fact]
    public async Task A_valid_address_is_written_trimmed_with_its_normalised_key()
    {
        var store = new StubStore();

        var outcome = await HandleAsync(
            store,
            new StubThrottle(),
            new SubscribeToNewsletterCommand("  Shopper@Example.com ", NewsletterSource.Homepage, "203.0.113.7"));

        Assert.Equal(NewsletterSubscriptionOutcome.Subscribed, outcome);

        var call = Assert.Single(store.Calls);
        Assert.Equal("Shopper@Example.com", call.Email);
        Assert.Equal("SHOPPER@EXAMPLE.COM", call.EmailNormalized);
        Assert.Equal(NewsletterSource.Homepage, call.Source);
        Assert.Equal("203.0.113.7", call.IpAddress);

        // Doc 08 §3.6 records when the consent was given; it is the clock's answer, not the
        // database's, so the same value can be asserted in a test and correlated in an audit.
        Assert.Equal(Now.UtcDateTime, call.UtcNow);
    }

    [Fact]
    public async Task The_footer_is_recorded_as_a_different_source_from_the_homepage()
    {
        var store = new StubStore();

        await HandleAsync(
            store,
            new StubThrottle(),
            new SubscribeToNewsletterCommand("shopper@example.com", NewsletterSource.Footer, null));

        Assert.Equal(NewsletterSource.Footer, Assert.Single(store.Calls).Source);
    }

    [Fact]
    public async Task A_missing_source_falls_back_to_the_homepage_rather_than_being_blank()
    {
        var store = new StubStore();

        await HandleAsync(
            store,
            new StubThrottle(),
            new SubscribeToNewsletterCommand("shopper@example.com", "  ", null));

        Assert.Equal(NewsletterSource.Homepage, Assert.Single(store.Calls).Source);
    }

    [Theory]
    [InlineData(NewsletterSubscriptionOutcome.Subscribed)]
    [InlineData(NewsletterSubscriptionOutcome.AlreadySubscribed)]
    [InlineData(NewsletterSubscriptionOutcome.Resubscribed)]
    public async Task The_store_decides_which_of_the_three_write_outcomes_it_was(
        NewsletterSubscriptionOutcome expected)
    {
        var store = new StubStore { Outcome = expected };

        var outcome = await HandleAsync(
            store,
            new StubThrottle(),
            new SubscribeToNewsletterCommand("shopper@example.com", NewsletterSource.Homepage, null));

        Assert.Equal(expected, outcome);
    }

    /// <summary>
    /// The timing floor. A new address is a SELECT, an INSERT and a commit; a known one returns
    /// after the SELECT — measured at roughly 95 ms against 7.6 ms on the real stack, which is a
    /// wide enough gap to read an address book through. Every answer now waits out the same floor.
    /// <para>
    /// Driven through a fake <see cref="TimeProvider"/>, so this asserts the delay that <i>would</i>
    /// be taken rather than sleeping for it: the handler asks for exactly the remainder, and a test
    /// that slept would be a test nobody runs twice.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0, 250)]
    [InlineData(8, 242)]
    [InlineData(95, 155)]
    public async Task Every_answer_is_padded_to_the_same_floor(int workMilliseconds, int expectedDelay)
    {
        var time = new FakeTimeProvider(Now);
        var store = new StubStore { Work = () => time.Advance(TimeSpan.FromMilliseconds(workMilliseconds)) };

        await HandleAsync(
            store,
            new StubThrottle(),
            new SubscribeToNewsletterCommand("shopper@example.com", NewsletterSource.Homepage, null),
            time);

        var delay = Assert.Single(time.Delays);
        Assert.Equal(TimeSpan.FromMilliseconds(expectedDelay), delay);
    }

    /// <summary>The rejected address is padded too: the timing must not branch on anything.</summary>
    [Fact]
    public async Task A_rejected_address_is_padded_as_well()
    {
        var time = new FakeTimeProvider(Now);

        await HandleAsync(
            new StubStore(),
            new StubThrottle(),
            new SubscribeToNewsletterCommand("not-an-address", NewsletterSource.Homepage, null),
            time);

        Assert.Equal(SubscribeToNewsletterCommandHandler.MinimumDuration, Assert.Single(time.Delays));
    }

    [Fact]
    public async Task Work_that_already_outlasts_the_floor_is_not_padded_further()
    {
        var time = new FakeTimeProvider(Now);
        var store = new StubStore { Work = () => time.Advance(TimeSpan.FromSeconds(3)) };

        await HandleAsync(
            store,
            new StubThrottle(),
            new SubscribeToNewsletterCommand("shopper@example.com", NewsletterSource.Homepage, null),
            time);

        Assert.Empty(time.Delays);
    }

    private static Task<NewsletterSubscriptionOutcome> HandleAsync(
        StubStore store,
        StubThrottle throttle,
        SubscribeToNewsletterCommand command,
        TimeProvider? timeProvider = null) =>
        new SubscribeToNewsletterCommandHandler(
                store,
                throttle,
                new FixedClock(Now),
                timeProvider ?? new FakeTimeProvider(Now))
            .HandleAsync(command, TestContext.Current.CancellationToken);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed record StoreCall(
        string Email,
        string EmailNormalized,
        string Source,
        DateTime UtcNow,
        string? IpAddress);

    private sealed class StubStore : INewsletterSubscriptions
    {
        private readonly List<StoreCall> _calls = [];

        public IReadOnlyList<StoreCall> Calls => _calls;

        public NewsletterSubscriptionOutcome Outcome { get; init; } = NewsletterSubscriptionOutcome.Subscribed;

        /// <summary>How long the write appears to take, for the padding tests.</summary>
        public Action? Work { get; init; }

        public Task<NewsletterSubscriptionOutcome> SubscribeAsync(
            string email,
            string emailNormalized,
            string source,
            DateTime utcNow,
            string? ipAddress,
            CancellationToken cancellationToken)
        {
            Work?.Invoke();
            _calls.Add(new StoreCall(email, emailNormalized, source, utcNow, ipAddress));
            return Task.FromResult(Outcome);
        }
    }

    /// <summary>Records what it was asked about, which is the whole assertion for the ordering tests.</summary>
    private sealed class StubThrottle : INewsletterSignupThrottle
    {
        private readonly List<string?> _asked = [];

        public IReadOnlyList<string?> Asked => _asked;

        public bool Allow { get; init; } = true;

        public TimeSpan Window => TimeSpan.FromMinutes(10);

        public bool TryAcquire(string? partitionKey)
        {
            _asked.Add(partitionKey);
            return Allow;
        }
    }
}
