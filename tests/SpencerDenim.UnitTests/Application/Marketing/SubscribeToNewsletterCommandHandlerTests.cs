using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Marketing.Newsletter;
using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.UnitTests.Application.Marketing;

/// <summary>
/// The newsletter signup use case (HOME-10). What reaches the store, and what never does.
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

        var outcome = await HandleAsync(store, new SubscribeToNewsletterCommand(email, NewsletterSource.Homepage, null));

        Assert.Equal(NewsletterSubscriptionOutcome.InvalidEmail, outcome);
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task A_valid_address_is_written_trimmed_with_its_normalised_key()
    {
        var store = new StubStore();

        var outcome = await HandleAsync(
            store,
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
    public async Task A_missing_source_falls_back_to_the_homepage_rather_than_being_blank()
    {
        var store = new StubStore();

        await HandleAsync(store, new SubscribeToNewsletterCommand("shopper@example.com", "  ", null));

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
            new SubscribeToNewsletterCommand("shopper@example.com", NewsletterSource.Homepage, null));

        Assert.Equal(expected, outcome);
    }

    private static Task<NewsletterSubscriptionOutcome> HandleAsync(
        StubStore store,
        SubscribeToNewsletterCommand command) =>
        new SubscribeToNewsletterCommandHandler(store, new FixedClock(Now))
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

        public Task<NewsletterSubscriptionOutcome> SubscribeAsync(
            string email,
            string emailNormalized,
            string source,
            DateTime utcNow,
            string? ipAddress,
            CancellationToken cancellationToken)
        {
            _calls.Add(new StoreCall(email, emailNormalized, source, utcNow, ipAddress));
            return Task.FromResult(Outcome);
        }
    }
}
