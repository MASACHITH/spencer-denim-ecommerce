using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.IntegrationTests.Sales;

namespace SpencerDenim.IntegrationTests.Ops;

/// <summary>
/// The four statements <c>SendEmails</c> runs, against a real SQL Server: the claim of doc 08 §3.8, the
/// lease, the <see cref="EmailBackoff"/> ladder and the terminal <c>Dead</c> status.
/// <para>
/// <b>Nothing here sends anything</b> — there is no transport in this suite at all. What is under test is
/// the queue: which rows a sweep can see, what the claim does to them, and what the two markers write.
/// The <c>SmtpClient</c> conversation is deliberately untested, and the sweep's own logic is covered by
/// the unit suite against a fake transport.
/// </para>
/// <para>
/// <b>Every row here is inserted by hand, which is the opposite of
/// <see cref="OrderEmailTransactionTests"/>'s rule and right for the opposite reason.</b> That suite is
/// about messages being queued by real transactions. This one is about states a real transaction cannot
/// produce — a row on its fifth failed attempt, a row with an expired lease, a row under a template this
/// build has no composer for — and an insert is the only way to reach them.
/// </para>
/// </summary>
public sealed class EmailOutboxSendingTests(SqlServerDatabaseFixture sqlServer)
    : SalesSchemaTestBase(sqlServer)
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A due <c>Pending</c> row is claimed and moved to <c>Sending</c> under a lease, with its counter
    /// incremented; a row due later is left alone.
    /// </summary>
    /// <remarks>
    /// <b>The counter is incremented by the claim, not by the failure path</b>, and this is where that
    /// shows: a message claimed and then lost has been attempted as far as anybody can tell, and a
    /// counter that only moved on a recorded failure would let a crash loop retry one message for ever.
    /// </remarks>
    [Fact]
    public async Task A_due_row_is_claimed_under_a_lease_and_a_future_one_is_not()
    {
        await ResetAsync();

        var due = await InsertAsync(nextAttemptAtUtc: Now.AddMinutes(-1));
        var notYet = await InsertAsync(nextAttemptAtUtc: Now.AddMinutes(1));

        var claimed = await ClaimAsync();

        var row = Assert.Single(claimed, candidate => candidate.Id == due);
        Assert.Equal(1, row.AttemptCount);
        Assert.Equal(EmailBackoff.MaxAttempts, row.MaxAttempts);
        Assert.DoesNotContain(notYet, claimed.Select(candidate => candidate.Id));

        var stored = await RowAsync(due);
        Assert.Equal(EmailOutboxStatus.Sending, stored.Status);
        Assert.Equal(Now.AddMinutes(5), stored.LockedUntilUtc);

        Assert.Equal(EmailOutboxStatus.Pending, (await RowAsync(notYet)).Status);
    }

    /// <summary>
    /// A <c>Failed</c> row whose next attempt has come round is claimed too — status 3 is inside
    /// <c>IX_EmailOutbox_Due</c>'s filter for exactly this reason.
    /// </summary>
    [Fact]
    public async Task A_failed_row_whose_time_has_come_is_claimed_again()
    {
        await ResetAsync();

        var id = await InsertAsync(
            status: EmailOutboxStatus.Failed,
            attemptCount: 2,
            nextAttemptAtUtc: Now.AddSeconds(-1));

        var claimed = await ClaimAsync();

        var row = Assert.Single(claimed, candidate => candidate.Id == id);
        Assert.Equal(3, row.AttemptCount);
    }

    /// <summary>
    /// <b>A row in flight cannot be claimed twice, which is the invariant the whole design is for.</b>
    /// </summary>
    /// <remarks>
    /// <c>Sending</c> is outside the due index's filter, so the second claim cannot see the row at all.
    /// The terminal statuses are checked in the same test because they are the same mechanism: once a
    /// message is Sent, Dead or Cancelled, no sweep will ever pick it up again.
    /// </remarks>
    [Theory]
    [InlineData(EmailOutboxStatus.Sending)]
    [InlineData(EmailOutboxStatus.Sent)]
    [InlineData(EmailOutboxStatus.Dead)]
    [InlineData(EmailOutboxStatus.Cancelled)]
    public async Task A_row_in_these_states_is_never_claimed(EmailOutboxStatus status)
    {
        await ResetAsync();

        var id = await InsertAsync(status: status, lockedUntilUtc: Now.AddMinutes(5));

        var claimed = await ClaimAsync();

        Assert.DoesNotContain(id, claimed.Select(candidate => candidate.Id));
    }

    /// <summary>
    /// Two senders racing for the same queue get <b>disjoint</b> sets, and between them every due row.
    /// </summary>
    /// <remarks>
    /// <b>This is the test that makes "concurrent senders cannot send the same row twice" a measurement
    /// rather than a claim.</b> The two claims run on separate scopes — separate <c>AppDbContext</c>s and
    /// therefore separate connections — against the real engine, which is the only place the
    /// <c>UPDLOCK, READPAST</c> behaviour exists. An intersection of one row would be a customer getting
    /// their bank details twice.
    /// </remarks>
    [Fact]
    public async Task No_message_is_ever_claimed_twice_however_many_senders_race_for_it()
    {
        await ResetAsync();

        var ids = new List<long>();
        for (var i = 0; i < 24; i++)
        {
            ids.Add(await InsertAsync(nextAttemptAtUtc: Now.AddMinutes(-1)));
        }

        var claimed = new List<long>();

        // Three senders at once, in batches small enough that they have to come back for more. Rounds
        // rather than one pass, because what is being measured is that no row is handed out twice across
        // *every* claim any of them ever makes — which is the invariant, and which a single pass of two
        // large batches would satisfy trivially by one sender taking everything.
        for (var round = 0; round < 10 && ids.Except(claimed).Any(); round++)
        {
            var batches = await Task.WhenAll(
                ClaimAsync(batchSize: 4),
                ClaimAsync(batchSize: 4),
                ClaimAsync(batchSize: 4));

            foreach (var batch in batches)
            {
                claimed.AddRange(batch.Select(row => row.Id));
            }
        }

        Assert.Equal(claimed.Count, claimed.Distinct().Count());
        Assert.All(ids, id => Assert.Contains(id, claimed));
    }

    /// <summary>
    /// Marking a message sent stamps it, clears the lease and clears the transport override (doc 08 §3.8
    /// R5) — and leaves the body, because this template is not one of the two that carry a token.
    /// </summary>
    [Fact]
    public async Task Marking_sent_stamps_the_row_clears_the_lease_and_keeps_an_ordinary_body()
    {
        await ResetAsync();

        var id = await InsertAsync(
            template: EmailTemplateCodes.OrderPlaced,
            transportOverrideProtected: "ciphertext-probe");

        var claim = Assert.Single(await ClaimAsync(), row => row.Id == id);

        Assert.True(await MarkSentAsync(claim));

        var stored = await RowAsync(id);
        Assert.Equal(EmailOutboxStatus.Sent, stored.Status);
        Assert.Equal(Now, stored.SentAtUtc);
        Assert.Null(stored.LockedUntilUtc);
        Assert.Null(stored.LastError);

        // Doc 08 §3.8 R5: NULLed on any terminal status. Nothing writes this column in this build, so the
        // row was given one by hand — which is the only way to prove the rule is honoured.
        Assert.Null(stored.TransportOverrideProtected);

        // The record of what the shop told this customer survives, to be cleared by the thirty-day rule.
        Assert.NotNull(stored.BodyHtml);
        Assert.NotNull(stored.BodyText);
    }

    /// <summary>
    /// Doc 08 §3.8 / G-03: the two templates whose link carries a single-use token lose their body the
    /// moment they are sent — "the body must not sit in the outbox table or in a backup".
    /// </summary>
    /// <remarks>
    /// <b>Neither template exists yet</b> (both are W3-01's), so the rows are inserted under those codes
    /// by hand. This is the one part of W3-01 this task was asked to prepare, and it is prepared here
    /// rather than left as a sentence in a document for that task to come and find.
    /// </remarks>
    [Theory]
    [InlineData("PasswordReset")]
    [InlineData("EmailConfirmation")]
    public async Task A_token_bearing_message_loses_its_body_the_moment_it_is_sent(string template)
    {
        await ResetAsync();

        var id = await InsertAsync(template: template);
        var claim = Assert.Single(await ClaimAsync(), row => row.Id == id);

        // The sender was handed the body, which is what makes it sendable at all.
        Assert.NotNull(claim.BodyHtml);
        Assert.NotNull(claim.BodyText);

        Assert.True(await MarkSentAsync(claim));

        var stored = await RowAsync(id);
        Assert.Equal(EmailOutboxStatus.Sent, stored.Status);
        Assert.Null(stored.BodyHtml);
        Assert.Null(stored.BodyText);
        Assert.NotNull(stored.Subject);
    }

    /// <summary>
    /// A failed attempt moves the row on to the next rung of the ladder, and no further.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 60)]
    [InlineData(5, 240)]
    public async Task A_failed_attempt_waits_the_ladders_next_rung(short priorAttempts, int expectedMinutes)
    {
        await ResetAsync();

        var id = await InsertAsync(
            status: priorAttempts == 1 ? EmailOutboxStatus.Pending : EmailOutboxStatus.Failed,
            attemptCount: (short)(priorAttempts - 1),
            nextAttemptAtUtc: Now.AddMinutes(-1));

        var claim = Assert.Single(await ClaimAsync(), row => row.Id == id);
        Assert.Equal(priorAttempts, claim.AttemptCount);

        Assert.Equal(EmailFailureOutcome.Retrying, await MarkFailedAsync(claim, "550 refused"));

        var stored = await RowAsync(id);
        Assert.Equal(EmailOutboxStatus.Failed, stored.Status);
        Assert.Equal(Now.AddMinutes(expectedMinutes), stored.NextAttemptAtUtc);
        Assert.Null(stored.LockedUntilUtc);
        Assert.Equal("550 refused", stored.LastError);
    }

    /// <summary>
    /// The sixth failure is terminal: <c>Dead</c>, and no sweep will ever see the row again.
    /// </summary>
    /// <remarks>
    /// <b>The second half is the assertion that matters.</b> A status written into the column proves
    /// nothing on its own — what makes <c>Dead</c> terminal is that it is outside
    /// <c>IX_EmailOutbox_Due</c>'s filter, so the claim cannot match it. So the row is claimed again and
    /// the result has to be empty.
    /// </remarks>
    [Fact]
    public async Task The_last_failure_kills_the_message_and_no_later_sweep_sees_it()
    {
        await ResetAsync();

        var id = await InsertAsync(
            status: EmailOutboxStatus.Failed,
            attemptCount: (short)(EmailBackoff.MaxAttempts - 1),
            nextAttemptAtUtc: Now.AddMinutes(-1),
            transportOverrideProtected: "ciphertext-probe");

        var claim = Assert.Single(await ClaimAsync(), row => row.Id == id);
        Assert.Equal(EmailBackoff.MaxAttempts, claim.AttemptCount);

        Assert.Equal(EmailFailureOutcome.Dead, await MarkFailedAsync(claim, "550 refused, finally"));

        var stored = await RowAsync(id);
        Assert.Equal(EmailOutboxStatus.Dead, stored.Status);
        Assert.Null(stored.LockedUntilUtc);
        Assert.Null(stored.TransportOverrideProtected);

        Assert.DoesNotContain(id, (await ClaimAsync()).Select(row => row.Id));
    }

    /// <summary>
    /// The whole ladder, walked once: six attempts, then nothing.
    /// </summary>
    /// <remarks>
    /// Written as a loop over the real statements rather than as arithmetic, because the thing worth
    /// proving is that the row's own counter and the claim's own filter agree about when to stop — which
    /// is a property of two SQL statements, not of <see cref="EmailBackoff"/>, whose own test is in the
    /// unit suite.
    /// </remarks>
    [Fact]
    public async Task A_message_that_never_sends_is_attempted_exactly_six_times()
    {
        await ResetAsync();

        var id = await InsertAsync(nextAttemptAtUtc: Now.AddMinutes(-1));
        var attempts = 0;

        for (var pass = 0; pass < 10; pass++)
        {
            // Each pass runs at the row's **own** next-attempt time, so the ladder's wait has elapsed
            // exactly and no further. A fixed step per pass is not good enough and this test proved it:
            // an hour per pass walked the first four rungs and then stopped one short, because the fifth
            // wait is four hours — so the loop reported five attempts for a six-rung ladder. Reading the
            // column is also the honest version of the question, since the column is what gates the claim.
            var at = pass == 0 ? Now : (await RowAsync(id)).NextAttemptAtUtc;

            var claimed = (await ClaimAsync(nowUtc: at)).Where(row => row.Id == id).ToList();
            if (claimed.Count == 0)
            {
                // The queue is empty of this row, which is the end of the ladder. The assertion below is
                // what distinguishes that from "the claim could not see it", and ResetAsync is what makes
                // the distinction sound: nothing else is in the queue to crowd it out.
                break;
            }

            attempts++;
            await MarkFailedAsync(claimed[0], "550 refused", at);
        }

        Assert.Equal(EmailBackoff.MaxAttempts, attempts);
        Assert.Equal(EmailOutboxStatus.Dead, (await RowAsync(id)).Status);
    }

    /// <summary>
    /// An expired lease is put back on the queue; a live one is left alone.
    /// </summary>
    /// <remarks>
    /// Without this, a sender killed between the claim and the send would strand its batch for ever —
    /// twenty bank-transfer instructions in a status no sweep can see.
    /// </remarks>
    [Fact]
    public async Task An_expired_lease_is_released_and_a_live_one_is_left_alone()
    {
        await ResetAsync();

        var stranded = await InsertAsync(
            status: EmailOutboxStatus.Sending,
            attemptCount: 1,
            lockedUntilUtc: Now.AddMinutes(-1));

        var inFlight = await InsertAsync(
            status: EmailOutboxStatus.Sending,
            attemptCount: 1,
            lockedUntilUtc: Now.AddMinutes(1));

        var released = await ReleaseAsync();
        Assert.True(released >= 1);

        var back = await RowAsync(stranded);
        Assert.Equal(EmailOutboxStatus.Failed, back.Status);
        Assert.Null(back.LockedUntilUtc);

        // Due at once rather than a rung further along: a sender that stopped is not evidence that the
        // mail server is unwell (see `EmailOutboxStore.ReleaseExpiredLeasesAsync`).
        Assert.Equal(Now, back.NextAttemptAtUtc);
        Assert.Contains("did not finish", back.LastError!, StringComparison.Ordinal);

        Assert.Equal(EmailOutboxStatus.Sending, (await RowAsync(inFlight)).Status);
    }

    /// <summary>
    /// A stranded row with no attempt left goes straight to <c>Dead</c> rather than back on the ladder.
    /// </summary>
    /// <remarks>
    /// The <c>CASE</c> in <c>ReleaseExpiredLeasesAsync</c> is what does this, and without it the row would
    /// be reclaimed at once — its next attempt time is already past — and spend a seventh attempt on a
    /// six-rung ladder before the failure path noticed.
    /// </remarks>
    [Fact]
    public async Task A_stranded_row_with_no_attempt_left_is_killed_rather_than_requeued()
    {
        await ResetAsync();

        var id = await InsertAsync(
            status: EmailOutboxStatus.Sending,
            attemptCount: EmailBackoff.MaxAttempts,
            lockedUntilUtc: Now.AddMinutes(-1));

        await ReleaseAsync();

        Assert.Equal(EmailOutboxStatus.Dead, (await RowAsync(id)).Status);
    }

    /// <summary>
    /// A sender whose lease expired cannot record a result against the row somebody else now holds.
    /// </summary>
    /// <remarks>
    /// <b>This is the only shape in which a customer can get the same message twice</b>, and the guard
    /// does not prevent that — both sends really happen. What it prevents is the <i>record</i> being
    /// wrong: the second sender's attempt is not overwritten by the first sender's late answer, and the
    /// caller is told, so the duplicate appears in the log instead of only in somebody's inbox.
    /// </remarks>
    [Fact]
    public async Task A_sender_whose_lease_expired_cannot_record_against_the_reclaimed_row()
    {
        await ResetAsync();

        var id = await InsertAsync(nextAttemptAtUtc: Now.AddMinutes(-1));

        var first = Assert.Single(await ClaimAsync(), row => row.Id == id);

        // The first sender hangs past its lease; a later sweep releases it and claims it again.
        await ReleaseAsync(nowUtc: Now.AddMinutes(10));
        var second = Assert.Single(await ClaimAsync(nowUtc: Now.AddMinutes(10)), row => row.Id == id);

        Assert.Equal(first.AttemptCount + 1, second.AttemptCount);

        // The first sender finally answers. It must change nothing.
        Assert.False(await MarkSentAsync(first));
        Assert.Equal(EmailFailureOutcome.NotOurs, await MarkFailedAsync(first, "too late"));

        var stored = await RowAsync(id);
        Assert.Equal(EmailOutboxStatus.Sending, stored.Status);
        Assert.Equal(second.AttemptCount, stored.AttemptCount);

        // And the second sender's own answer still lands.
        Assert.True(await MarkSentAsync(second, nowUtc: Now.AddMinutes(10)));
        Assert.Equal(EmailOutboxStatus.Sent, (await RowAsync(id)).Status);
    }

    /// <summary>
    /// The claim respects its batch size, which is doc 08 §3.8's <c>TOP (20)</c>.
    /// </summary>
    [Fact]
    public async Task The_claim_takes_no_more_than_its_batch()
    {
        await ResetAsync();

        for (var i = 0; i < 5; i++)
        {
            await InsertAsync(nextAttemptAtUtc: Now.AddMinutes(-1));
        }

        Assert.Equal(2, (await ClaimAsync(batchSize: 2)).Count);
    }

    /// <summary>
    /// <c>CountDueAsync</c> counts what a claim would take, which is what the "cannot send" warning
    /// reports.
    /// </summary>
    /// <remarks>
    /// Absolute rather than a difference, because <see cref="ResetAsync"/> has emptied the queue: the
    /// three rows that are not counted are the three reasons a row can be invisible to a sweep — too
    /// early, already sent, already dead.
    /// </remarks>
    [Fact]
    public async Task The_due_count_is_the_number_a_sweep_would_claim()
    {
        await ResetAsync();

        Assert.Equal(0, await CountDueAsync());

        await InsertAsync(nextAttemptAtUtc: Now.AddMinutes(-1));
        await InsertAsync(nextAttemptAtUtc: Now.AddMinutes(-1));
        await InsertAsync(nextAttemptAtUtc: Now.AddMinutes(10));
        await InsertAsync(status: EmailOutboxStatus.Sent);
        await InsertAsync(status: EmailOutboxStatus.Dead);

        // Two of the five: the future one, the sent one and the dead one are all outside
        // IX_EmailOutbox_Due's filter or its range, which is what the warning's count has to mean.
        Assert.Equal(2, await CountDueAsync());
    }

    /// <summary>
    /// One claim, on a scope and therefore a connection of its own.
    /// </summary>
    /// <remarks>
    /// <b>Every helper below is <c>async</c> and <c>await</c>s inside its own <c>using</c>, and the first
    /// version of them was not.</b> Returning the <see cref="Task"/> straight out of a
    /// <c>using var scope</c> disposes the scope — and closes the connection — the moment the method
    /// returns, so the statement ran against a closed connection and all twenty-two tests here failed
    /// with "The connection is closed". It is a fixture bug rather than a product one, and it is written
    /// down because the symptom points squarely at the production code.
    /// <para>
    /// The scope-per-call is deliberate: two concurrent claims then use two connections, which is the only
    /// arrangement in which the engine's own row locking is the thing under test.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<EmailOutboxClaim>> ClaimAsync(
        int batchSize = 20,
        DateTime? nowUtc = null)
    {
        var at = nowUtc ?? Now;

        using var scope = SqlServer.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IEmailOutboxStore>()
            .ClaimAsync(batchSize, at, at.AddMinutes(5), Token);
    }

    private async Task<bool> MarkSentAsync(EmailOutboxClaim claim, DateTime? nowUtc = null)
    {
        using var scope = SqlServer.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IEmailOutboxStore>()
            .MarkSentAsync(claim, nowUtc ?? Now, Token);
    }

    private async Task<EmailFailureOutcome> MarkFailedAsync(
        EmailOutboxClaim claim,
        string failure,
        DateTime? nowUtc = null)
    {
        using var scope = SqlServer.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IEmailOutboxStore>()
            .MarkFailedAsync(claim, failure, nowUtc ?? Now, Token);
    }

    private async Task<int> ReleaseAsync(DateTime? nowUtc = null)
    {
        using var scope = SqlServer.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IEmailOutboxStore>()
            .ReleaseExpiredLeasesAsync(nowUtc ?? Now, Token);
    }

    private async Task<int> CountDueAsync(DateTime? nowUtc = null)
    {
        using var scope = SqlServer.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IEmailOutboxStore>()
            .CountDueAsync(nowUtc ?? Now, Token);
    }

    /// <summary>
    /// Skips when there is no SQL Server, and empties the queue so every count and every claim in the
    /// test that follows is exact.
    /// </summary>
    /// <remarks>
    /// <b>Deleting from this table is allowed, unlike the tables around it.</b> Doc 08 §1.6's
    /// never-deleted list is Users, Orders, OrderLines, Payments, Refunds, PaymentEvents,
    /// InventoryMovements and AuditLogs; <c>ops.EmailOutbox</c> is not on it, and §8.4 gives it a
    /// one-year purge, so a set-based delete is a thing the system itself will do.
    /// <para>
    /// <b>It is necessary rather than tidy.</b> The claim is a set-based statement over the whole queue —
    /// it cannot be filtered to one test's rows — so a neighbour's due message crowds a batch and the
    /// row under test is simply absent. Three tests here failed that way before this existed, and each
    /// failure pointed at the product code rather than at the fixture. The rows being removed belong to
    /// orders this same suite placed minutes earlier in a throwaway database the fixture created, and
    /// <see cref="OrderEmailTransactionTests"/> only ever asserts over messages it has just queued.
    /// </para>
    /// </remarks>
    private async Task ResetAsync()
    {
        SqlServer.SkipIfUnavailable();

        await DbContext.Database.ExecuteSqlRawAsync("DELETE FROM ops.EmailOutbox;", Token);
    }

    /// <summary>
    /// One row in exactly the state a test needs, inserted with SQL.
    /// </summary>
    /// <remarks>
    /// <b>Raw SQL rather than EF, because several of these states are ones the application cannot
    /// write</b> — a row already on its fifth attempt, a row in <c>Sending</c> with an expired lease, a
    /// row with a <c>TransportOverrideProtected</c> ciphertext that nothing in this build sets. Going
    /// through the entity would mean the fixture could only produce states the code under test already
    /// produces, which is the definition of a test that cannot fail.
    /// </remarks>
    private async Task<long> InsertAsync(
        string template = EmailTemplateCodes.OrderPlaced,
        EmailOutboxStatus status = EmailOutboxStatus.Pending,
        short attemptCount = 0,
        DateTime? nextAttemptAtUtc = null,
        DateTime? lockedUntilUtc = null,
        string? transportOverrideProtected = null)
    {
        var id = await DbContext.Database
            .SqlQuery<long>($"""
                INSERT INTO ops.EmailOutbox
                    (TemplateCode, ToEmail, ToName, Subject, BodyHtml, BodyText, RelatedEntityType,
                     RelatedEntityId, Status, AttemptCount, MaxAttempts, NextAttemptAtUtc, LockedUntilUtc,
                     TransportOverrideProtected, CreatedAtUtc)
                OUTPUT inserted.Id AS [Value]
                VALUES
                    ({template}, 'queue-probe@example.invalid', 'Queue Probe', 'A queue probe',
                     '<p>A queue probe</p>', 'A queue probe', 'Order',
                     {Guid.NewGuid().ToString("D")}, {(byte)status}, {attemptCount},
                     {EmailBackoff.MaxAttempts}, {nextAttemptAtUtc ?? Now.AddMinutes(-1)},
                     {lockedUntilUtc}, {transportOverrideProtected}, {Now});
                """)
            .ToListAsync(Token);

        return id.Single();
    }

    private async Task<EmailOutboxMessage> RowAsync(long id)
    {
        using var scope = SqlServer.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().EmailOutbox
            .AsNoTracking()
            .SingleAsync(message => message.Id == id, Token);
    }
}
