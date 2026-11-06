using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Marketing.Newsletter;
using SpencerDenim.Application.Marketing.Newsletter.Admin;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Tests.MediaFixtures;
using SpencerDenim.UnitTests.Support;

namespace SpencerDenim.UnitTests.Application.Marketing;

/// <summary>
/// #262: the subscriber's own unsubscribe link and the staff Unsubscribe action, at the level of their handlers. The
/// conditional UPDATE, the audit row's transaction and the HTTP answers are pinned against SQL Server in
/// <c>NewsletterUnsubscribeEndpointTests</c>.
/// </summary>
public sealed class NewsletterUnsubscribeHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 30, 0, TimeSpan.Zero);

    private static readonly Guid Token = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    [Fact]
    public async Task A_token_is_handed_to_the_store_with_the_time()
    {
        var store = new RecordingStore { TokenAnswer = true };

        var changed = await UnsubscribeAsync(store, $"  {Token:D}  ");

        Assert.True(changed);
        Assert.Equal((Token, Now.UtcDateTime), Assert.Single(store.TokenCalls));
    }

    /// <summary>Not a GUID, empty, or the all-zero GUID: a token that matches nobody, and the store is never asked.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-token")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("' OR 1=1 --")]
    public async Task Anything_that_is_not_a_token_changes_nothing(string? token)
    {
        var store = new RecordingStore();

        Assert.False(await UnsubscribeAsync(store, token));
        Assert.Empty(store.TokenCalls);
    }

    /// <summary>
    /// The signup's floor, whatever happened: a live token, a dead one and garbage all take the same time, so the
    /// answer's timing cannot be used to test tokens either.
    /// </summary>
    [Theory]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e", true)]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e", false)]
    [InlineData("garbage", false)]
    public async Task Every_answer_takes_the_signups_floor(string token, bool storeAnswer)
    {
        var time = new FakeTimeProvider(Now);
        var store = new RecordingStore { TokenAnswer = storeAnswer };

        await UnsubscribeAsync(store, token, time);

        Assert.Equal(SubscribeToNewsletterCommandHandler.MinimumDuration, Assert.Single(time.Delays));
    }

    [Fact]
    public async Task The_staff_action_audits_the_row_by_id_and_never_by_address()
    {
        var store = new RecordingStore { StaffAnswer = StaffUnsubscribeOutcome.Unsubscribed };
        var actor = new AdminActor(7, "manager@example.com", "203.0.113.9", "Browser", "corr-1");

        var outcome = await new UnsubscribeNewsletterSubscriberCommandHandler(store, new FixedClock(Now))
            .HandleAsync(new UnsubscribeNewsletterSubscriberCommand(42, actor), TestContext.Current.CancellationToken);

        Assert.Equal(StaffUnsubscribeOutcome.Unsubscribed, outcome);
        var (id, audit, at) = Assert.Single(store.StaffCalls);
        Assert.Equal(42, id);
        Assert.Equal(Now.UtcDateTime, at);
        Assert.Equal(AuditActions.StatusChange, audit.Action);
        Assert.Equal("NewsletterSubscriber", audit.EntityType);
        Assert.Equal("42", audit.EntityId);
        Assert.Equal((7, "manager@example.com", "203.0.113.9"), (audit.UserId, audit.UserEmail, audit.IpAddress));
        Assert.Contains("\"Subscribed\"", audit.BeforeJson, StringComparison.Ordinal);
        Assert.Contains("\"Unsubscribed\"", audit.AfterJson, StringComparison.Ordinal);
        Assert.DoesNotContain("@", audit.BeforeJson + audit.AfterJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StaffUnsubscribeOutcome.AlreadyUnsubscribed)]
    [InlineData(StaffUnsubscribeOutcome.NotFound)]
    public async Task The_staff_action_passes_the_stores_answer_back(StaffUnsubscribeOutcome answer)
    {
        var store = new RecordingStore { StaffAnswer = answer };

        var outcome = await new UnsubscribeNewsletterSubscriberCommandHandler(store, new FixedClock(Now))
            .HandleAsync(
                new UnsubscribeNewsletterSubscriberCommand(5, AdminActor.Unknown),
                TestContext.Current.CancellationToken);

        Assert.Equal(answer, outcome);
    }

    [Fact]
    public void The_export_link_is_the_storefront_page_with_the_token_in_the_query_string()
    {
        Assert.Equal(
            "https://shop.test/unsubscribe?token=0f8fad5b-d9cb-469f-a165-70867728950e",
            ExportNewsletterSubscribersCommandHandler.UnsubscribeLink("https://shop.test", Token));
        Assert.Equal("Unsubscribe link", ExportNewsletterSubscribersCommandHandler.Header[^1]);
    }

    private static Task<bool> UnsubscribeAsync(RecordingStore store, string? token, TimeProvider? time = null) =>
        new UnsubscribeFromNewsletterCommandHandler(store, new FixedClock(Now), time ?? new FakeTimeProvider(Now))
            .HandleAsync(new UnsubscribeFromNewsletterCommand(token), TestContext.Current.CancellationToken);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class RecordingStore : INewsletterSubscriptions
    {
        public List<(Guid Token, DateTime At)> TokenCalls { get; } = [];

        public List<(int Id, AuditLog Audit, DateTime At)> StaffCalls { get; } = [];

        public bool TokenAnswer { get; init; }

        public StaffUnsubscribeOutcome StaffAnswer { get; init; } = StaffUnsubscribeOutcome.Unsubscribed;

        public Task<NewsletterSubscriptionOutcome> SubscribeAsync(
            string email,
            string emailNormalized,
            string source,
            DateTime utcNow,
            string? ipAddress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> UnsubscribeByTokenAsync(Guid token, DateTime utcNow, CancellationToken cancellationToken)
        {
            TokenCalls.Add((token, utcNow));
            return Task.FromResult(TokenAnswer);
        }

        public Task<StaffUnsubscribeOutcome> UnsubscribeByStaffAsync(
            int subscriberId,
            AuditLog audit,
            DateTime utcNow,
            CancellationToken cancellationToken)
        {
            StaffCalls.Add((subscriberId, audit, utcNow));
            return Task.FromResult(StaffAnswer);
        }
    }
}
