using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Infrastructure.Ops.Email;

namespace SpencerDenim.UnitTests.Infrastructure.Ops;

/// <summary>
/// One sweep of <c>SendEmails</c>, against a fake transport and a fake outbox.
/// <para>
/// <b>No test in this project may touch a real SMTP server or send to a real address</b>, so the
/// transport is a fake that records and never connects, and the integration suite registers one too. The
/// shape of the job — claim, send, record, back off, die — is therefore provable without a mailbox
/// anywhere; the <c>SmtpClient</c> conversation itself is the one thing left untested, which is the
/// smallest honest surface to leave.
/// </para>
/// </summary>
public sealed class SendEmailsServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The instant every sweep below runs at, so an assertion about a time is exact.</summary>
    private static readonly DateTime SweepClock = new(2026, 10, 9, 8, 33, 0, DateTimeKind.Utc);

    /// <summary>
    /// <b>The behaviour that matters today: an unconfigured SMTP queues and does not send.</b>
    /// </summary>
    /// <remarks>
    /// We do not have the client's mail credentials. So the sweep must claim nothing — not mark a message
    /// Sent, not spend a rung of the ladder, not retry four hours apart against a host that does not
    /// exist — and must say how many are waiting so an operator can tell "cannot send" from "nothing to
    /// send". Every clause below is a separate way this could have gone wrong.
    /// </remarks>
    [Fact]
    public async Task With_no_smtp_configured_nothing_is_claimed_and_the_count_is_reported()
    {
        var store = new FakeStore { DueCount = 3 };
        var transport = new FakeTransport { IsConfigured = false };

        await SweepAsync(store, transport);

        Assert.Equal(0, store.ClaimCalls);
        Assert.Equal(0, store.ReleaseCalls);
        Assert.Empty(transport.Sent);
        Assert.Empty(store.Sent);
        Assert.Empty(store.Failed);
        Assert.Equal(1, store.CountDueCalls);
    }

    /// <summary>
    /// And with nothing queued it asks once and says nothing at all, which is the other half of the
    /// signal: silence means there is nothing to send.
    /// </summary>
    [Fact]
    public async Task With_no_smtp_configured_and_an_empty_queue_it_does_nothing()
    {
        var store = new FakeStore { DueCount = 0 };

        await SweepAsync(store, new FakeTransport { IsConfigured = false });

        Assert.Equal(1, store.CountDueCalls);
        Assert.Equal(0, store.ClaimCalls);
    }

    /// <summary>
    /// With SMTP configured, the queue is never counted: a count on every sweep of a healthy shop would
    /// be a query nothing reads.
    /// </summary>
    [Fact]
    public async Task A_configured_host_does_not_count_the_queue()
    {
        var store = new FakeStore { DueCount = 5 };
        store.Queue.Add(Claim(1));

        await SweepAsync(store, new FakeTransport());

        Assert.Equal(0, store.CountDueCalls);
        Assert.Equal(1, store.ClaimCalls);
    }

    [Fact]
    public async Task A_claimed_message_is_sent_and_marked_sent()
    {
        var store = new FakeStore();
        store.Queue.Add(Claim(42, template: EmailTemplateCodes.OrderPlaced));
        var transport = new FakeTransport();

        await SweepAsync(store, transport);

        var sent = Assert.Single(transport.Sent);
        Assert.Equal("buyer@example.com", sent.ToEmail);
        Assert.Equal("We have your order SD-2026-000123", sent.Subject);

        Assert.Equal([(42L, EmailTemplateCodes.OrderPlaced)], store.Sent);
        Assert.Empty(store.Failed);
    }

    /// <summary>
    /// A refused message is recorded as failed and <b>is not</b> marked sent.
    /// </summary>
    /// <remarks>
    /// The second assertion is the one that matters. A sweep that recorded the failure and then went on to
    /// mark the row Sent would empty the queue and tell nobody — the worst outcome available here — and a
    /// test that only checked the failure was recorded would pass against it.
    /// </remarks>
    [Fact]
    public async Task A_refused_message_is_recorded_as_failed_and_not_as_sent()
    {
        var store = new FakeStore();
        store.Queue.Add(Claim(7));
        var transport = new FakeTransport { Throw = new InvalidOperationException("550 mailbox unavailable") };

        await SweepAsync(store, transport);

        var failure = Assert.Single(store.Failed);
        Assert.Equal(7L, failure.Id);
        Assert.Contains("550 mailbox unavailable", failure.Failure, StringComparison.Ordinal);
        Assert.Empty(store.Sent);
    }

    /// <summary>
    /// The recorded failure carries the exception's message and <b>not</b> the body or the recipient.
    /// </summary>
    /// <remarks>
    /// Doc 08 §3.8 keeps <c>ops.EmailOutbox</c> out of the audit log because a body can hold a reset
    /// link; writing a rendered body into <c>LastError</c> would put the thing we are careful about into
    /// the column we are not.
    /// </remarks>
    [Fact]
    public async Task A_recorded_failure_holds_no_body_and_no_address()
    {
        var store = new FakeStore();
        store.Queue.Add(Claim(7));

        await SweepAsync(store, new FakeTransport { Throw = new InvalidOperationException("refused") });

        var failure = Assert.Single(store.Failed);
        Assert.DoesNotContain("buyer@example.com", failure.Failure, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", failure.Failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// One bad address does not stop the batch: the other messages still go.
    /// </summary>
    /// <remarks>
    /// Doc 11 §4 is explicit that a wrong address on a placed order cannot be corrected, so a permanently
    /// refused recipient is a state the queue will hold for hours. If it stopped the loop, one of those
    /// would hold up every other customer's bank details.
    /// </remarks>
    [Fact]
    public async Task One_failure_does_not_stop_the_rest_of_the_batch()
    {
        var store = new FakeStore();
        store.Queue.Add(Claim(1, email: "bad@example.com"));
        store.Queue.Add(Claim(2));
        store.Queue.Add(Claim(3));

        var transport = new FakeTransport { ThrowForEmail = "bad@example.com" };

        await SweepAsync(store, transport);

        Assert.Equal([2L, 3L], store.Sent.Select(row => row.Id));
        Assert.Equal([1L], store.Failed.Select(row => row.Id));
    }

    /// <summary>
    /// Stranded exhausted rows are killed, then expired leases are released, then anything is claimed.
    /// </summary>
    /// <remarks>
    /// The order is the assertion, and each step of it is a different defect.
    /// <para>
    /// <b>The release is before the claim</b> because a sweep that claimed first would leave a crashed
    /// sender's batch waiting a whole interval longer for no reason — the release is what puts those rows
    /// back inside <c>IX_EmailOutbox_Due</c>'s filter, so claiming first means they cannot be in the batch
    /// about to be sent.
    /// </para>
    /// <para>
    /// <b>The kill is before the release</b> (W4-02a) because a message on its final attempt whose sender
    /// died must not be requeued: its <c>NextAttemptAtUtc</c> is already in the past, so it would be
    /// reclaimed at once and spend a seventh attempt on a six-rung ladder before the failure path noticed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Expired_leases_are_released_before_the_claim()
    {
        var store = new FakeStore { Released = 2 };
        store.Queue.Add(Claim(1));

        await SweepAsync(store, new FakeTransport());

        Assert.Equal(["kill", "release", "claim"], store.Calls);
    }

    /// <summary>
    /// A sweep whose own claim throws does not escape: the host must stay up.
    /// </summary>
    /// <remarks>
    /// An exception out of <c>ExecuteAsync</c> faults the host. <c>ExpireUnpaidOrders</c> records the
    /// same lesson, and it is the reason every cycle is wrapped rather than only the per-message send.
    /// </remarks>
    [Fact]
    public async Task A_sweep_that_throws_does_not_bring_the_host_down()
    {
        var store = new FakeStore { ThrowOnClaim = new TimeoutException("the database is away") };
        store.Queue.Add(Claim(1));

        await SweepAsync(store, new FakeTransport());

        Assert.Empty(store.Sent);
    }

    /// <summary>
    /// A message whose body has been destroyed is refused rather than sent empty.
    /// </summary>
    /// <remarks>
    /// This is the real <see cref="SmtpEmailTransport"/>, not the fake — it is the one branch of that
    /// class reachable without a socket, and it is the branch that matters: doc 08 §3.8 clears the body
    /// of a <c>PasswordReset</c> as soon as it is sent and of everything after thirty days, so a row with
    /// no body is a state this table really reaches. Sending it would deliver a blank message from the
    /// shop and record it as a success.
    /// </remarks>
    [Fact]
    public async Task A_message_with_no_body_is_refused_rather_than_sent_blank()
    {
        var transport = new SmtpEmailTransport(new EmailOptions
        {
            Smtp = new SmtpOptions { Host = "localhost", FromAddress = "shop@example.com" },
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.SendAsync(
                new OutboundEmail("buyer@example.com", null, "Subject", null, null),
                Token));

        Assert.Contains("no body", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And an unconfigured transport throws rather than reporting success, even though the job never
    /// calls it in that state.
    /// </summary>
    /// <remarks>
    /// Defence in depth with a named worst case: a transport that returned quietly would have its caller
    /// mark the row <c>Sent</c>, which empties the queue and tells nobody.
    /// </remarks>
    [Fact]
    public async Task An_unconfigured_transport_refuses_rather_than_pretending()
    {
        var transport = new SmtpEmailTransport(new EmailOptions());

        Assert.False(transport.IsConfigured);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.SendAsync(
                new OutboundEmail("buyer@example.com", null, "Subject", "<p>Hi</p>", "Hi"),
                Token));
    }

    /// <summary>
    /// A host with a mail server but no From address is not configured: the message could not be sent.
    /// </summary>
    [Theory]
    [InlineData(null, null, false)]
    [InlineData("smtp.example.com", null, false)]
    [InlineData(null, "shop@example.com", false)]
    [InlineData("smtp.example.com", "shop@example.com", true)]
    public void Configuration_needs_a_host_and_a_from_address(string? host, string? from, bool expected)
    {
        var options = new SmtpOptions { Host = host, FromAddress = from };

        Assert.Equal(expected, options.IsConfigured);
    }

    /// <summary>STARTTLS is on unless somebody turns it off, never off by omission.</summary>
    [Fact]
    public void Starttls_is_on_by_default()
    {
        Assert.True(new SmtpOptions().UseStartTls);
        Assert.Equal(587, new SmtpOptions().Port);
    }

    private static async Task SweepAsync(FakeStore store, FakeTransport transport) =>
        await SweepAsync(store, (IEmailTransport)transport);

    private static async Task SweepAsync(FakeStore store, FakeTransport transport, FakeAlerts alerts) =>
        await SweepAsync(store, (IEmailTransport)transport, alerts);

    private static async Task SweepAsync(
        IEmailOutboxStore store,
        IEmailTransport transport,
        IDeadEmailAlerts? alerts = null,
        ILogger<SendEmailsService>? logger = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(transport);
        services.AddSingleton(alerts ?? new FakeAlerts());

        await using var provider = services.BuildServiceProvider();

        var options = new EmailOptions { BatchSize = 20, LeaseMinutes = 5 };

        var job = new SendEmailsService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(new DateTimeOffset(SweepClock, TimeSpan.Zero)),
            options,
            logger ?? NullLogger<SendEmailsService>.Instance);

        await job.SweepAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// <b>The error line for a dead email does not carry the order's public token</b> (issue #201).
    /// </summary>
    /// <remarks>
    /// For an order email the outbox row's <c>RelatedEntityId</c> <i>is</i> the order's public token, which is
    /// the order page's only credential, and this line is written at <c>Error</c> to a log Docker keeps. Found by
    /// #201's tester reading the code: the line printed <c>{OrderPublicId}</c>. Every property of every event the
    /// sweep writes is searched, not only the rendered text, because a structured sink keeps the property
    /// even when the message template stops mentioning it. The row id stays: it is how the line is matched to
    /// the banner, which is the place the order is named.
    /// </remarks>
    [Fact]
    public async Task The_dead_email_error_line_names_the_row_and_never_the_orders_token()
    {
        var store = new FakeStore();
        var claim = Claim(41, attemptCount: EmailBackoff.MaxAttempts);
        store.Queue.Add(claim);

        var logger = new CapturingLogger();

        await SweepAsync(
            store,
            new FakeTransport { Throw = new InvalidOperationException("550") },
            new FakeAlerts(),
            logger);

        var dead = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);

        Assert.Contains("41", dead.Text, StringComparison.Ordinal);

        var token = claim.RelatedEntityId!;
        Assert.False(string.IsNullOrWhiteSpace(token), "the fixture must give the claim a token for this test to mean anything");

        foreach (var entry in logger.Entries)
        {
            Assert.DoesNotContain(token, entry.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                entry.Properties,
                property => (property.Value?.ToString() ?? string.Empty)
                    .Contains(token, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>A logger that keeps what it was given: the level, the rendered text and every structured property.</summary>
    private sealed class CapturingLogger : ILogger<SendEmailsService>
    {
        public List<(LogLevel Level, string Text, IReadOnlyList<KeyValuePair<string, object?>> Properties)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            // The exception is part of the text searched: a logger that is handed an exception writes its message and stack.
            var rendered = formatter(state, exception);
            var text = exception is null ? rendered : $"{rendered}{Environment.NewLine}{exception}";

            Entries.Add((logLevel, text, properties));
        }
    }

    /// <summary>
    /// The sweep claims with a lease of <c>Email:LeaseMinutes</c> ahead of its own clock, and a batch of
    /// <c>Email:BatchSize</c>.
    /// </summary>
    /// <remarks>
    /// <b>Added by W3-06's tester, because replacing <c>now.AddMinutes(options.LeaseMinutes)</c> with
    /// <c>now</c> left all 1,920 tests green.</b> That mutant takes a lease that has <i>already</i>
    /// expired, so the very next sweep's <c>ReleaseExpiredLeasesAsync</c> puts the row back in the queue
    /// while the first sender's SMTP conversation is still in the air — and a second sender sends the
    /// same message. <c>EmailOptions.LeaseMinutes</c>' own remark names that as the one failure the field
    /// exists to prevent, and the options validator only checks the figure is positive, never that
    /// anything uses it.
    /// <para>
    /// Both figures are asserted here rather than left to the integration suite, whose
    /// <c>ClaimAsync</c> helper passes its own lease and therefore proves only the store's pass-through.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_sweep_claims_its_batch_under_a_lease_of_the_configured_length()
    {
        var store = new FakeStore();
        store.Queue.Add(Claim(1));

        await SweepAsync(store, new FakeTransport());

        var claim = Assert.NotNull(store.LastClaim);

        Assert.Equal(20, claim.BatchSize);
        Assert.Equal(SweepClock, claim.NowUtc);
        Assert.Equal(SweepClock.AddMinutes(5), claim.LeaseUntilUtc);
    }

    private static EmailOutboxClaim Claim(
        long id,
        string template = EmailTemplateCodes.OrderPlaced,
        string email = "buyer@example.com",
        short attemptCount = 1) =>
        new(
            id,
            template,
            email,
            "Nimal Perera",
            "We have your order SD-2026-000123",
            "<p>Hello Nimal,</p>",
            "Hello Nimal,",
            attemptCount,
            EmailBackoff.MaxAttempts,
            "7f3a1c90-2b44-4d6e-9f11-0c5e8a6d3b22");

    /// <summary>A clock that does not move, so every assertion about a time is exact.</summary>
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    /// <summary>
    /// <b>A message that runs out of ladder raises the <c>EmailDeliveryFailed</c> alert</b> (doc 11 §4,
    /// §7 G-37) — the hole this whole task exists to close.
    /// </summary>
    /// <remarks>
    /// Before W4-02a the sweep logged an <c>Error</c> and that was the entire signal, so a customer who
    /// never got their bank transfer instructions simply waited and nobody was told.
    /// </remarks>
    [Fact]
    public async Task A_message_that_runs_out_of_ladder_raises_the_dead_email_alert()
    {
        var store = new FakeStore();
        store.Queue.Add(Claim(41, attemptCount: EmailBackoff.MaxAttempts));

        var alerts = new FakeAlerts();

        await SweepAsync(store, new FakeTransport { Throw = new InvalidOperationException("550") }, alerts);

        Assert.Equal([41], alerts.Raised);
    }

    /// <summary>
    /// <b>And a message that still has an attempt left raises none.</b>
    /// </summary>
    /// <remarks>
    /// The other half of the same rule, and the one that would hurt a shopper if it were wrong: a banner
    /// telling a Super Admin to telephone a customer whose email is going out in five minutes' time is
    /// worse than no banner.
    /// </remarks>
    [Fact]
    public async Task A_message_that_will_be_retried_raises_no_alert()
    {
        var store = new FakeStore();
        store.Queue.Add(Claim(41, attemptCount: 1));

        var alerts = new FakeAlerts();

        await SweepAsync(store, new FakeTransport { Throw = new InvalidOperationException("550") }, alerts);

        Assert.Empty(alerts.Raised);
    }

    [Fact]
    public async Task A_message_that_sends_raises_no_alert()
    {
        var store = new FakeStore();
        store.Queue.Add(Claim(41, attemptCount: 1));

        var alerts = new FakeAlerts();

        await SweepAsync(store, new FakeTransport(), alerts);

        Assert.Empty(alerts.Raised);
        Assert.Single(store.Sent);
    }

    /// <summary>
    /// <b>The second way a row reaches <c>Dead</c> raises the alert too.</b>
    /// </summary>
    /// <remarks>
    /// A sender that died holding its lease on a message's <i>final</i> attempt is killed by
    /// <c>KillStrandedExhaustedAsync</c>, not by the failure handler — so until W4-02a that row went Dead
    /// with nobody told at all, not even by a log line about a dead message. It is the one path to Dead
    /// that is invisible from <c>SendOneAsync</c>.
    /// </remarks>
    [Fact]
    public async Task A_stranded_message_on_its_final_attempt_raises_the_alert_too()
    {
        var store = new FakeStore { StrandedExhausted = [77, 78] };
        var alerts = new FakeAlerts();

        await SweepAsync(store, new FakeTransport(), alerts);

        Assert.Equal([77, 78], alerts.Raised);
    }

    /// <summary>
    /// The exhausted rows are killed <b>before</b> the requeue, and the order is asserted.
    /// </summary>
    /// <remarks>
    /// If the requeue ran first, a message on its final attempt would go back to <c>Failed</c> with an
    /// attempt time already in the past, be reclaimed at once, and spend a seventh attempt on a six-rung
    /// ladder before the failure path noticed — and the alert would then name an attempt that never
    /// happened.
    /// </remarks>
    [Fact]
    public async Task The_kill_runs_before_the_requeue()
    {
        var store = new FakeStore { StrandedExhausted = [77] };

        await SweepAsync(store, new FakeTransport(), new FakeAlerts());

        Assert.Equal(["kill", "release", "claim"], store.Calls);
    }

    /// <summary>
    /// <b>An alert that cannot be written does not stop the batch.</b>
    /// </summary>
    /// <remarks>
    /// Nineteen other customers' messages are in the same claim, and an alert nobody could record must not
    /// be the reason they wait another sweep. The sweep still reports its counts, so the failure is one
    /// message's and not the job's.
    /// </remarks>
    [Fact]
    public async Task An_alert_that_cannot_be_raised_does_not_stop_the_sweep()
    {
        var store = new FakeStore();
        store.Queue.Add(Claim(41, attemptCount: EmailBackoff.MaxAttempts));
        store.Queue.Add(Claim(42, attemptCount: 1));

        var alerts = new FakeAlerts { Throw = new InvalidOperationException("the alert table is gone") };

        await SweepAsync(
            store,
            new FakeTransport { ThrowForEmail = "dead@example.com" },
            alerts);

        // The dead one was asked for, threw, and the next message still went out.
        Assert.Equal([41], alerts.Raised);
        Assert.Equal([42], store.Sent.Select(sent => sent.Id));
    }

    /// <summary>
    /// With no SMTP configured nothing is killed, nothing is requeued and no alert is raised.
    /// </summary>
    /// <remarks>
    /// The state the shop is actually in. The queue is left exactly as it was found, which is what stops
    /// the deploy that has no mail credentials raising a banner per queued message five hours later.
    /// </remarks>
    [Fact]
    public async Task With_no_smtp_configured_no_alert_is_raised()
    {
        var store = new FakeStore { DueCount = 3, StrandedExhausted = [77] };
        var alerts = new FakeAlerts();

        await SweepAsync(store, new FakeTransport { IsConfigured = false }, alerts);

        Assert.Empty(alerts.Raised);
        Assert.Equal(0, store.KillCalls);
        Assert.Equal(0, store.ReleaseCalls);
    }

    /// <summary>A claim shaped like the ones the real statement returns.</summary>
    private static EmailOutboxClaim Claim(long id, short attemptCount) => new(
        id,
        EmailTemplateCodes.BankTransferInstructions,
        attemptCount >= EmailBackoff.MaxAttempts ? "dead@example.com" : "live@example.com",
        "Nimali Perera",
        "How to pay for order SD-2026-000123",
        "<p>body</p>",
        "body",
        attemptCount,
        EmailBackoff.MaxAttempts,
        "44444444-4444-4444-8444-444444444444");

    /// <summary>
    /// A transport that records and never connects. <b>It has no address book and no socket</b>, which is
    /// what makes "no test sends to a real address" a property of the code rather than of care.
    /// </summary>
    private sealed class FakeTransport : IEmailTransport
    {
        public bool IsConfigured { get; init; } = true;

        public Exception? Throw { get; init; }

        public string? ThrowForEmail { get; init; }

        public List<OutboundEmail> Sent { get; } = [];

        public Task SendAsync(OutboundEmail email, CancellationToken cancellationToken)
        {
            if (Throw is { } always)
            {
                throw always;
            }

            if (ThrowForEmail is { } refused && email.ToEmail == refused)
            {
                throw new InvalidOperationException($"550 no mailbox for {refused}");
            }

            Sent.Add(email);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A fake <see cref="IDeadEmailAlerts"/> that records which rows it was asked to raise a banner for.
    /// </summary>
    /// <remarks>
    /// The real one reads the outbox row back and refuses rows that are not <c>Dead</c>
    /// (<c>DeadEmailAlerts</c>, proved against a real database in <c>AdminAlertsDeadEmailTests</c>).
    /// What these unit tests own is the other half: that the <i>job</i> asks, on both paths to Dead and
    /// on neither other path.
    /// </remarks>
    private sealed class FakeAlerts : IDeadEmailAlerts
    {
        public Exception? Throw { get; init; }

        public List<long> Raised { get; } = [];

        public Task<bool> RaiseAsync(long outboxMessageId, CancellationToken cancellationToken)
        {
            Raised.Add(outboxMessageId);

            if (Throw is { } exception)
            {
                throw exception;
            }

            return Task.FromResult(true);
        }
    }

    private sealed class FakeStore : IEmailOutboxStore
    {
        public List<EmailOutboxClaim> Queue { get; } = [];

        public int DueCount { get; init; }

        public int Released { get; init; }

        /// <summary>Ids <c>KillStrandedExhaustedAsync</c> answers with — a sender that died on a final attempt.</summary>
        public List<long> StrandedExhausted { get; init; } = [];

        public int KillCalls { get; private set; }

        public Exception? ThrowOnClaim { get; init; }

        public int CountDueCalls { get; private set; }

        public int ClaimCalls { get; private set; }

        public int ReleaseCalls { get; private set; }

        /// <summary>The order the job called the statements in, which one test asserts directly.</summary>
        public List<string> Calls { get; } = [];

        public List<(long Id, string TemplateCode)> Sent { get; } = [];

        public List<(long Id, string Failure)> Failed { get; } = [];

        public Task<int> CountDueAsync(DateTime nowUtc, CancellationToken cancellationToken)
        {
            CountDueCalls++;
            Calls.Add("count");
            return Task.FromResult(DueCount);
        }

        public Task<IReadOnlyList<long>> KillStrandedExhaustedAsync(
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            KillCalls++;
            Calls.Add("kill");
            return Task.FromResult<IReadOnlyList<long>>(StrandedExhausted);
        }

        public Task<int> ReleaseExpiredLeasesAsync(DateTime nowUtc, CancellationToken cancellationToken)
        {
            ReleaseCalls++;
            Calls.Add("release");
            return Task.FromResult(Released);
        }

        /// <summary>The arguments of the last claim, so a test can assert the batch and the lease.</summary>
        public (int BatchSize, DateTime NowUtc, DateTime LeaseUntilUtc)? LastClaim { get; private set; }

        public Task<IReadOnlyList<EmailOutboxClaim>> ClaimAsync(
            int batchSize,
            DateTime nowUtc,
            DateTime leaseUntilUtc,
            CancellationToken cancellationToken)
        {
            ClaimCalls++;
            LastClaim = (batchSize, nowUtc, leaseUntilUtc);
            Calls.Add("claim");

            if (ThrowOnClaim is { } exception)
            {
                throw exception;
            }

            return Task.FromResult<IReadOnlyList<EmailOutboxClaim>>(Queue.Take(batchSize).ToList());
        }

        public Task<bool> MarkSentAsync(
            EmailOutboxClaim claim,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            Sent.Add((claim.Id, claim.TemplateCode));
            return Task.FromResult(true);
        }

        public Task<EmailFailureOutcome> MarkFailedAsync(
            EmailOutboxClaim claim,
            string failure,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            Failed.Add((claim.Id, failure));

            return Task.FromResult(
                EmailBackoff.DelayAfter(claim.AttemptCount, claim.MaxAttempts) is null
                    ? EmailFailureOutcome.Dead
                    : EmailFailureOutcome.Retrying);
        }
    }
}
