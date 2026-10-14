using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Payments;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// <c>IPaymentEventStore</c> against a real SQL Server (doc 08 §3.5 DB13): an event is stored once per
/// idempotency key, however many times and however concurrently it is delivered, and what became of it is
/// recorded without a second worker being able to rewrite the first one's outcome.
/// </summary>
public sealed class PaymentEventStoreTests(SqlServerDatabaseFixture sqlServer) : BankTransferTestBase(sqlServer)
{
    private const string Provider = PaymentProviderCodes.PayPal;

    /// <summary>Every key this test stored, so <see cref="DisposeAsync"/> can remove the rows.</summary>
    private readonly List<string> _keys = [];

    public override async ValueTask DisposeAsync()
    {
        if (SqlServer.IsAvailable && _keys.Count > 0)
        {
            using var scope = SqlServer.CreateScope();
            var keys = _keys.ToList();

            await scope.ServiceProvider.GetRequiredService<AppDbContext>().PaymentEvents
                .Where(row => keys.Contains(row.IdempotencyKey))
                .ExecuteDeleteAsync(CancellationToken.None);
        }

        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_new_event_is_stored_once_and_a_redelivery_is_recognised_without_changing_the_row()
    {
        var key = NewKey();
        var received = new DateTime(2026, 10, 10, 9, 15, 0, DateTimeKind.Utc);
        var first = Record(key, received, """{"id":"WH-1","amount":"31.25"}""", paymentId: null);

        var stored = await RecordAsync(first);

        Assert.True(stored.IsNew);
        Assert.Equal(PaymentEventProcessingStatus.Received, stored.ProcessingStatus);
        Assert.False(stored.PayloadTruncated);
        Assert.False(stored.PayloadRedacted);

        var row = await ReadAsync(stored.EventId);
        Assert.Equal(Provider, row.ProviderCode);
        Assert.Equal(PaymentEventSource.VerifiedWebhook, row.EventSource);
        Assert.Equal("PAYMENT.CAPTURE.COMPLETED", row.EventType);
        Assert.Equal("WH-1", row.ProviderEventId);
        Assert.Equal(key, row.IdempotencyKey);
        Assert.Equal("""{"id":"WH-1","amount":"31.25"}""", row.PayloadJson);
        Assert.Equal(PaymentEventProcessingStatus.Received, row.ProcessingStatus);
        Assert.Equal((short)0, row.AttemptCount);
        Assert.Equal(received, row.ReceivedAtUtc);
        Assert.Null(row.ProcessedAtUtc);
        Assert.Null(row.LastError);

        // A Received row carries a retry time (doc 08 §3.5's IX_PaymentEvents_Retry selects on it), so an
        // event stored and then never applied is found by the sweep.
        Assert.Equal(received + PaymentEventRetry.GraceBeforeFirstRetry, row.NextAttemptAtUtc);

        // The redelivery — different bytes, same key: the first write wins and is not touched.
        var again = await RecordAsync(
            Record(key, received.AddMinutes(10), """{"id":"WH-1","amount":"99.99"}""", paymentId: null));

        Assert.False(again.IsNew);
        Assert.Equal(stored.EventId, again.EventId);
        Assert.Equal(PaymentEventProcessingStatus.Received, again.ProcessingStatus);

        var unchanged = await ReadAsync(stored.EventId);
        Assert.Equal("""{"id":"WH-1","amount":"31.25"}""", unchanged.PayloadJson);
        Assert.Equal(received, unchanged.ReceivedAtUtc);
        Assert.Equal(1, await CountAsync(key));
    }

    [Fact]
    public async Task A_redelivery_reports_how_far_the_first_delivery_got()
    {
        var key = NewKey();
        var stored = await RecordAsync(Record(key));

        Assert.True(await MarkProcessedAsync(stored.EventId));

        var again = await RecordAsync(Record(key));

        Assert.False(again.IsNew);

        // The endpoint answers 200 either way; what it must not do is apply the event a second time on the
        // provider's say-so, and this is how it can tell.
        Assert.Equal(PaymentEventProcessingStatus.Processed, again.ProcessingStatus);
    }

    /// <summary>
    /// Eight redeliveries arriving at the guarded insert together: exactly one is told it is new. The unique
    /// index is a backstop here, not the mechanism — if the <c>UPDLOCK, HOLDLOCK</c> on the guarded insert
    /// were ever lost, this is the test that sees a unique violation surface instead of a clean answer.
    /// </summary>
    /// <remarks>
    /// <b>Rewritten by the W4-01 verification, because the first version could not fail.</b> It fired eight
    /// <c>Task.Run</c>s and asserted the end state only. Measured with <c>UPDLOCK, HOLDLOCK</c> deleted from
    /// <c>PaymentEventStore</c>: the test went red 7 times in 8 when run <i>alone</i> (a cold connection
    /// pool staggers the eight tasks so they finish opening together and then collide) and <b>0 times in 4
    /// when the class ran as a class</b> — a warm pool hands the connections out in sequence, the first
    /// insert commits before the eighth task is scheduled, and nothing contends. A full run is always the
    /// second case, so the hint that is the whole mechanism was guarded by nothing.
    /// <para>
    /// Two changes fix that. Each delivery <b>opens and warms its own scope first</b> and then waits on a
    /// barrier, so the eight reach the statement together whatever the pool is doing; and the arrivals at
    /// the contended statement are <b>counted</b> (<c>CommandCountingInterceptor.Watch</c>, which needed a
    /// <c>NonQueryExecuting</c> override to see an <c>INSERT</c> at all) and asserted to be eight. An end
    /// state that is right because nothing raced is the failure mode this project has hit repeatedly; the
    /// count is what tells the two apart.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Parallel_deliveries_of_one_event_store_it_exactly_once()
    {
        const int deliveries = 8;
        const string GuardedInsertFragment = "INSERT INTO sales.PaymentEvents";

        var key = NewKey();

        CommandCounter.Watch(GuardedInsertFragment);

        using var gate = new Barrier(deliveries);

        var receipts = await Task.WhenAll(Enumerable.Range(0, deliveries).Select(_ => Task.Run(
            async () =>
            {
                using var scope = CreateApplicationScope();
                var store = scope.ServiceProvider.GetRequiredService<IPaymentEventStore>();

                // Warm this scope's connection before the gate, so what the barrier releases is eight
                // calls about to run the statement and not eight calls about to open a connection.
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
                    .ExecuteSqlRawAsync("SELECT 1;", Token);

                // Bounded, so one delivery that fails before the gate ends the test instead of hanging the
                // other seven on a barrier nobody will ever complete.
                Assert.True(
                    gate.SignalAndWait(TimeSpan.FromSeconds(30), Token),
                    "The eight deliveries did not all reach the barrier.");

                return await store.RecordAsync(Record(key), Token);
            },
            Token)));

        // The arrangement, asserted: all eight really reached the guarded insert. A fragment that stops
        // matching counts zero and this fails loudly, which is the right failure — the test would then be
        // measuring nothing and says so.
        Assert.Equal(deliveries, CommandCounter.CountOf(GuardedInsertFragment));

        Assert.Single(receipts, receipt => receipt.IsNew);
        Assert.Single(receipts.Select(receipt => receipt.EventId).Distinct());
        Assert.Equal(1, await CountAsync(key));
    }

    /// <summary>
    /// <b>The race the lock hints exist for, made to happen — the deterministic half.</b> Another session
    /// holds the key range of <c>UX_PaymentEvents_IdempotencyKey</c> for a key that does not exist yet, so a
    /// delivery of that key must <i>wait</i>; the holder then inserts the row and commits, and the waiting
    /// delivery must come back "this is a redelivery" rather than fail.
    /// </summary>
    /// <remarks>
    /// <b>This is the test the W4-01 verification added, because neither the parallel test above nor anything
    /// else guarded the hints.</b> Measured with <c>UPDLOCK, HOLDLOCK</c> deleted from
    /// <c>PaymentEventStore</c>'s guarded insert: the parallel test reds 7 times in 8 run alone, 0 times in 4
    /// when its class runs as a class, and 1 time in 4 even with the barrier — the first writer's insert
    /// finishes in well under a millisecond, so eight threads arriving "together" still mostly arrive after
    /// it. Scheduling cannot be asserted on. This test does not try:
    /// <list type="bullet">
    /// <item><description>
    /// <b>With the hints</b> the guard's own <c>SELECT … WITH (UPDLOCK, HOLDLOCK)</c> blocks on the holder's
    /// range lock, so the delivery waits, sees the committed row and answers <c>IsNew: false</c>.
    /// </description></item>
    /// <item><description>
    /// <b>Without them</b> the guard reads no lock and no row, falls through to the <c>INSERT</c>, blocks
    /// there on the same range lock instead, and fails with 2627 when the holder commits — which is exactly
    /// the "unique violation as control flow" that doc 08 §4.2 says this path must never rely on.
    /// </description></item>
    /// </list>
    /// Either way the delivery is observed blocked through <c>sys.dm_exec_requests</c> before the holder
    /// commits, so the window is held open by a lock and not by a sleep.
    /// </remarks>
    [Fact]
    public async Task A_delivery_waits_for_a_key_another_session_is_holding_and_is_told_it_is_a_redelivery()
    {
        var key = NewKey();

        await using var holder = new SqlConnection(SqlServer.ConnectionString);
        await holder.OpenAsync(Token);

        await using var transaction = (SqlTransaction)await holder.BeginTransactionAsync(Token);

        try
        {
            int holderSpid;

            await using (var spid = holder.CreateCommand())
            {
                spid.Transaction = transaction;
                spid.CommandText = "SELECT @@SPID;";
                holderSpid = Convert.ToInt32(await spid.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture);
            }

            // The same range lock the store's own guard takes, on a key that has no row yet.
            await using (var hold = holder.CreateCommand())
            {
                hold.Transaction = transaction;
                hold.CommandText = """
                    SELECT 1 FROM sales.PaymentEvents WITH (UPDLOCK, HOLDLOCK)
                     WHERE IdempotencyKey = CONVERT(varchar(160), @key);
                    """;
                hold.Parameters.AddWithValue("@key", key);
                await hold.ExecuteNonQueryAsync(Token);
            }

            var delivering = Task.Run(() => RecordAsync(Record(key)), Token);

            // The arrangement, asserted: the delivery really is waiting on this session.
            await WaitUntilBlockedByAsync(holderSpid);

            await using (var insert = holder.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO sales.PaymentEvents
                        (ProviderCode, EventSource, EventType, IdempotencyKey, PayloadJson,
                         ProcessingStatus, AttemptCount, ReceivedAtUtc)
                    VALUES ('PayPal', 1, 'holder.test', CONVERT(varchar(160), @key), '{"held":true}',
                            0, 0, SYSUTCDATETIME());
                    """;
                insert.Parameters.AddWithValue("@key", key);
                Assert.Equal(1, await insert.ExecuteNonQueryAsync(Token));
            }

            await transaction.CommitAsync(Token);

            var receipt = await delivering.WaitAsync(TimeSpan.FromSeconds(30), Token);

            // It waited, found the holder's row, and said so. Without the hints the insert below the guard
            // would have been reached and the holder's row would have made it throw 2627 instead.
            Assert.False(receipt.IsNew);
            Assert.Equal(1, await CountAsync(key));
        }
        catch
        {
            if (holder.State == System.Data.ConnectionState.Open)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
    }

    /// <summary>
    /// Polls <c>sys.dm_exec_requests</c> until some session is blocked by <paramref name="blockerSpid"/>, so
    /// the test proceeds on a fact rather than on a sleep.
    /// </summary>
    private async Task WaitUntilBlockedByAsync(int blockerSpid)
    {
        await using var monitor = new SqlConnection(SqlServer.ConnectionString);
        await monitor.OpenAsync(Token);

        for (var attempt = 0; attempt < 150; attempt++)
        {
            await using var command = monitor.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = @spid;";
            command.Parameters.AddWithValue("@spid", blockerSpid);

            if (Convert.ToInt32(await command.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture) > 0)
            {
                return;
            }

            await Task.Delay(100, Token);
        }

        Assert.Fail($"No session became blocked by SPID {blockerSpid}: the race was not set up.");
    }

    /// <summary>
    /// The guarded insert holds a key-range lock for its statement (<c>UPDLOCK, HOLDLOCK</c>), which is what
    /// serialises two deliveries of one key. This is the other half of that claim: deliveries of
    /// <i>different</i> keys that land in the same range queue behind one another and finish — none is
    /// killed as a deadlock victim. (The count is this host's own commands; the server's counter is shared
    /// with other runs.)
    /// </summary>
    [Fact]
    public async Task Parallel_deliveries_of_different_events_all_store_and_none_deadlocks()
    {
        var records = Enumerable.Range(0, 24).Select(_ => Record(NewKey())).ToList();

        CommandCounter.Reset();

        var receipts = await Task.WhenAll(records.Select(record => Task.Run(() => RecordAsync(record), Token)));

        Assert.All(receipts, receipt => Assert.True(receipt.IsNew));
        Assert.Equal(records.Count, receipts.Select(receipt => receipt.EventId).Distinct().Count());
        Assert.Equal(0, CommandCounter.DeadlockCount);
    }

    [Fact]
    public async Task Different_keys_are_different_events()
    {
        var one = await RecordAsync(Record(NewKey()));
        var two = await RecordAsync(Record(NewKey()));

        Assert.True(one.IsNew);
        Assert.True(two.IsNew);
        Assert.NotEqual(one.EventId, two.EventId);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"unterminated\":")]
    [InlineData("\"a bare string\"")]
    [InlineData("")]
    public async Task A_payload_that_is_not_a_JSON_object_is_refused_before_the_constraint_is_reached(string payload)
    {
        var key = NewKey();

        // Surfaced as the caller's mistake it is — an ArgumentException with a message — and not as the
        // database's check constraint (547) on CK_PaymentEvents_PayloadJson, which names no field.
        await Assert.ThrowsAsync<ArgumentException>(() => RecordAsync(Record(key, payload: payload)));

        Assert.Equal(0, await CountAsync(key));
    }

    [Fact]
    public async Task A_secret_in_the_payload_is_not_stored()
    {
        var key = NewKey();

        var stored = await RecordAsync(Record(
            key,
            payload: """{"id":"WH-9","client_secret":"must-not-be-kept","resource":{"amount":"31.25"}}"""));

        Assert.True(stored.PayloadRedacted);

        var row = await ReadAsync(stored.EventId);
        Assert.DoesNotContain("must-not-be-kept", row.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(PaymentEventPayload.RedactedValue, row.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"amount\":\"31.25\"", row.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_oversize_payload_is_recorded_as_a_marker_and_the_event_is_still_stored_once()
    {
        var key = NewKey();
        var big = "{\"data\":\"" + new string('x', PaymentEventPayload.MaxLength + 10) + "\"}";

        var stored = await RecordAsync(Record(key, payload: big));

        Assert.True(stored.IsNew);
        Assert.True(stored.PayloadTruncated);

        var row = await ReadAsync(stored.EventId);
        Assert.True(row.PayloadJson.Length < 100);
        Assert.Contains("\"truncated\":true", row.PayloadJson, StringComparison.Ordinal);

        // The idempotency key still does its job: the big event is not applied twice either.
        Assert.False((await RecordAsync(Record(key, payload: big))).IsNew);
    }

    [Fact]
    public async Task Fields_that_would_not_fit_their_columns_are_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => RecordAsync(Record(new string('k', PaymentEventKey.MaxLength + 1))));

        await Assert.ThrowsAsync<ArgumentException>(
            () => RecordAsync(Record(NewKey()) with { EventType = new string('t', 101) }));

        await Assert.ThrowsAsync<ArgumentException>(
            () => RecordAsync(Record(NewKey()) with { ProviderEventId = new string('e', 101) }));

        await Assert.ThrowsAsync<ArgumentException>(
            () => RecordAsync(Record(NewKey()) with { ProviderCode = new string('p', 31) }));

        await Assert.ThrowsAsync<ArgumentException>(
            () => RecordAsync(Record(NewKey()) with { Source = (PaymentEventSource)9 }));
    }

    /// <summary>
    /// <b>The key goes into a <c>varchar</c> column as <c>nvarchar</c> text, so a character outside the code
    /// page used to become <c>?</c> on the way in.</b> A Sinhala key recorded twice then threw a unique
    /// violation (the "already there?" check compared the unfolded text, found nothing, and the insert
    /// collided with its own earlier self), and two different Cyrillic keys collided with each other. Real
    /// gateway ids are ASCII, so the store refuses the rest with a message - before anything is stored, and
    /// as an <see cref="ArgumentException"/>, never as a <c>SqlException</c> 2627.
    /// </summary>
    [Theory]
    [InlineData("PayPal:WH-\u0DC3\u0DD2\u0D82\u0DC4\u0DBD")]
    [InlineData("PayPal:\u0434\u0430\u043D\u043D\u044B\u0435")]
    [InlineData("PayPal:\u0438\u0434-\u0434\u0440\u0443\u0433\u043E\u0439")]
    [InlineData("PayPal:caf\u00E9")]
    [InlineData("PayPal:WH-1 ")]
    [InlineData("PayPal: WH-1")]
    [InlineData("PayPal:WH 1")]
    [InlineData("PayPal:WH-1\t")]
    [InlineData("PayPal:WH-1\u0007")]
    [InlineData("PayPal:WH-1\u00A0")]
    public async Task A_key_the_column_cannot_hold_exactly_is_refused_with_a_message_not_a_unique_violation(string key)
    {
        // Twice, because the original defect was in the second call.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var thrown = await Assert.ThrowsAsync<ArgumentException>(() => RecordAsync(Record(key)));

            Assert.Contains("printable ASCII", thrown.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Two_different_non_ASCII_keys_cannot_collide_because_neither_is_stored()
    {
        var one = "PayPal:\u0434\u0430\u043D\u043D\u044B\u0435";
        var two = "PayPal:\u0438\u0434\u0443\u0442\u0441\u0442\u0440";

        await Assert.ThrowsAsync<ArgumentException>(() => RecordAsync(Record(one)));
        await Assert.ThrowsAsync<ArgumentException>(() => RecordAsync(Record(two)));

        // Folded to '?', both would have been "PayPal:?????" - one event standing for two.
        Assert.Equal(0, await CountFoldedAsync());
    }

    [Fact]
    public async Task The_other_text_columns_that_are_varchar_are_held_to_the_same_rule()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => RecordAsync(Record(NewKey()) with { EventType = "PAYMENT.\u0434.COMPLETED" }));

        await Assert.ThrowsAsync<ArgumentException>(
            () => RecordAsync(Record(NewKey()) with { ProviderEventId = "WH-\u0DC3" }));

        await Assert.ThrowsAsync<ArgumentException>(
            () => RecordAsync(Record(NewKey()) with { ProviderCode = "PayPa\u0142" }));

        await Assert.ThrowsAsync<ArgumentException>(
            () => RecordAsync(Record(NewKey()) with { EventType = "PAYMENT CAPTURE" }));
    }

    /// <summary>
    /// The collation is case-insensitive, so keys that differ only by case are one event to the index. They
    /// are reported as a redelivery - not thrown as a violation - because the "already there?" check uses
    /// the same comparison.
    /// </summary>
    [Fact]
    public async Task Keys_that_differ_only_by_case_are_one_event_as_the_collation_says()
    {
        var key = NewKey();
        var upper = key.ToUpperInvariant();
        _keys.Add(upper);

        var first = await RecordAsync(Record(key));
        var second = await RecordAsync(Record(upper));

        Assert.True(first.IsNew);
        Assert.False(second.IsNew);
        Assert.Equal(first.EventId, second.EventId);
    }

    [Fact]
    public async Task Processing_an_event_counts_the_attempt_and_stops_the_sweep_picking_it_up()
    {
        var stored = await RecordAsync(Record(NewKey()));
        var done = new DateTime(2026, 10, 10, 9, 16, 0, DateTimeKind.Utc);

        Assert.True(await MarkProcessedAsync(stored.EventId, done));

        var row = await ReadAsync(stored.EventId);
        Assert.Equal(PaymentEventProcessingStatus.Processed, row.ProcessingStatus);
        Assert.Equal((short)1, row.AttemptCount);
        Assert.Equal(done, row.ProcessedAtUtc);

        // IX_PaymentEvents_Retry selects Received and Failed by NextAttemptAtUtc; a finished event has none.
        Assert.Null(row.NextAttemptAtUtc);
        Assert.Null(row.LastError);

        // A second worker finding it finished does not rewrite the outcome — in either direction.
        Assert.False(await MarkProcessedAsync(stored.EventId));
        Assert.False(await MarkFailedAsync(stored.EventId, "late worker", retryAtUtc: null));
        Assert.False(await MarkIgnoredAsync(stored.EventId));

        var after = await ReadAsync(stored.EventId);
        Assert.Equal(PaymentEventProcessingStatus.Processed, after.ProcessingStatus);
        Assert.Equal((short)1, after.AttemptCount);
    }

    [Fact]
    public async Task A_failure_is_recorded_and_retried_until_the_event_is_processed()
    {
        var stored = await RecordAsync(Record(NewKey()));
        var retryAt = new DateTime(2026, 10, 10, 9, 20, 0, DateTimeKind.Utc);

        Assert.True(await MarkFailedAsync(stored.EventId, "Timeout talking to the database.", retryAt));

        var failed = await ReadAsync(stored.EventId);
        Assert.Equal(PaymentEventProcessingStatus.Failed, failed.ProcessingStatus);
        Assert.Equal((short)1, failed.AttemptCount);
        Assert.Equal("Timeout talking to the database.", failed.LastError);
        Assert.Equal(retryAt, failed.NextAttemptAtUtc);
        Assert.Null(failed.ProcessedAtUtc);

        // A second failure counts a second attempt and moves the retry time.
        var later = retryAt.AddMinutes(5);
        Assert.True(await MarkFailedAsync(stored.EventId, "Still down.", later));

        var failedAgain = await ReadAsync(stored.EventId);
        Assert.Equal((short)2, failedAgain.AttemptCount);
        Assert.Equal("Still down.", failedAgain.LastError);
        Assert.Equal(later, failedAgain.NextAttemptAtUtc);

        // And the third attempt succeeds: processed, three attempts, the old error and the retry time gone.
        Assert.True(await MarkProcessedAsync(stored.EventId));

        var done = await ReadAsync(stored.EventId);
        Assert.Equal(PaymentEventProcessingStatus.Processed, done.ProcessingStatus);
        Assert.Equal((short)3, done.AttemptCount);
        Assert.Null(done.LastError);
        Assert.Null(done.NextAttemptAtUtc);
        Assert.NotNull(done.ProcessedAtUtc);
    }

    [Fact]
    public async Task A_failure_with_no_retry_time_stops_the_sweep_and_leaves_the_event_for_a_person()
    {
        var stored = await RecordAsync(Record(NewKey()));

        Assert.True(await MarkFailedAsync(stored.EventId, "Gave up.", retryAtUtc: null));

        var row = await ReadAsync(stored.EventId);
        Assert.Equal(PaymentEventProcessingStatus.Failed, row.ProcessingStatus);
        Assert.Null(row.NextAttemptAtUtc);
    }

    [Fact]
    public async Task An_error_longer_than_its_column_is_clipped_not_refused()
    {
        var stored = await RecordAsync(Record(NewKey()));

        // A failure to record the failure would lose the reason the event needs a retry.
        Assert.True(await MarkFailedAsync(stored.EventId, new string('e', 5_000), retryAtUtc: null));

        Assert.Equal(2_000, (await ReadAsync(stored.EventId)).LastError!.Length);
    }

    [Fact]
    public async Task An_event_that_needs_no_action_is_marked_ignored()
    {
        var stored = await RecordAsync(Record(NewKey()));

        Assert.True(await MarkIgnoredAsync(stored.EventId));

        var row = await ReadAsync(stored.EventId);
        Assert.Equal(PaymentEventProcessingStatus.Ignored, row.ProcessingStatus);
        Assert.Equal((short)1, row.AttemptCount);
        Assert.Null(row.NextAttemptAtUtc);
        Assert.NotNull(row.ProcessedAtUtc);
    }

    [Fact]
    public async Task Marking_an_event_that_does_not_exist_changes_nothing_and_says_so()
    {
        Assert.False(await MarkProcessedAsync(long.MaxValue));
        Assert.False(await MarkIgnoredAsync(long.MaxValue));
        Assert.False(await MarkFailedAsync(long.MaxValue, "nobody", retryAtUtc: null));
        Assert.False(await LinkAsync(long.MaxValue, 1, 1));
    }

    /// <summary>
    /// Doc 08 DB13: "an event can arrive that we cannot yet attribute to a payment or an order, and … still
    /// wants it stored rather than dropped" — and attributed later.
    /// </summary>
    [Fact]
    public async Task An_event_stored_before_it_could_be_attributed_can_be_linked_later_without_losing_what_was_known()
    {
        var (order, _) = await PlaceOrderAsync();
        var orderId = await OrderIdAsync(order.PublicId);
        var paymentId = (await ReadOrderAsync(order.PublicId)).Payments.Single().Id;

        var stored = await RecordAsync(Record(NewKey()));
        var unattributed = await ReadAsync(stored.EventId);
        Assert.Null(unattributed.PaymentId);
        Assert.Null(unattributed.OrderId);

        // The order first, then the payment: each call leaves what the other set.
        Assert.True(await LinkAsync(stored.EventId, paymentId: null, orderId));
        Assert.True(await LinkAsync(stored.EventId, paymentId, orderId: null));

        var linked = await ReadAsync(stored.EventId);
        Assert.Equal(orderId, linked.OrderId);
        Assert.Equal(paymentId, linked.PaymentId);
    }

    [Fact]
    public async Task An_event_can_be_stored_already_attributed()
    {
        var (order, _) = await PlaceOrderAsync();
        var orderId = await OrderIdAsync(order.PublicId);
        var paymentId = (await ReadOrderAsync(order.PublicId)).Payments.Single().Id;

        var stored = await RecordAsync(Record(NewKey(), paymentId: paymentId) with { OrderId = orderId });

        var row = await ReadAsync(stored.EventId);
        Assert.Equal(paymentId, row.PaymentId);
        Assert.Equal(orderId, row.OrderId);
    }

    // ---------------------------------------------------------------------------------------------------

    private string NewKey()
    {
        var key = PaymentEventKey.ForEvent(Provider, "T" + Guid.NewGuid().ToString("N"));
        _keys.Add(key);
        return key;
    }

    private static PaymentEventRecord Record(
        string key,
        DateTime? receivedAtUtc = null,
        string payload = """{"id":"WH-1"}""",
        int? paymentId = null) =>
        new(
            Provider,
            PaymentEventSource.VerifiedWebhook,
            "PAYMENT.CAPTURE.COMPLETED",
            ProviderEventId: "WH-1",
            key,
            payload,
            receivedAtUtc ?? DateTime.UtcNow,
            paymentId);

    private Task<PaymentEventReceipt> RecordAsync(PaymentEventRecord record) =>
        InScopeAsync(services => services.GetRequiredService<IPaymentEventStore>().RecordAsync(record, Token));

    private Task<bool> MarkProcessedAsync(long eventId, DateTime? nowUtc = null) =>
        InScopeAsync(services => services
            .GetRequiredService<IPaymentEventStore>()
            .MarkProcessedAsync(eventId, nowUtc ?? DateTime.UtcNow, Token));

    private Task<bool> MarkIgnoredAsync(long eventId) =>
        InScopeAsync(services => services
            .GetRequiredService<IPaymentEventStore>()
            .MarkIgnoredAsync(eventId, DateTime.UtcNow, Token));

    private Task<bool> MarkFailedAsync(long eventId, string failure, DateTime? retryAtUtc) =>
        InScopeAsync(services => services
            .GetRequiredService<IPaymentEventStore>()
            .MarkFailedAsync(eventId, failure, retryAtUtc, Token));

    private Task<bool> LinkAsync(long eventId, int? paymentId, int? orderId) =>
        InScopeAsync(services => services
            .GetRequiredService<IPaymentEventStore>()
            .LinkAsync(eventId, paymentId, orderId, Token));

    private async Task<PaymentEvent> ReadAsync(long eventId)
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().PaymentEvents
            .AsNoTracking()
            .SingleAsync(row => row.Id == eventId, Token);
    }

    /// <summary>How many events have a key made of folded characters: what a non-ASCII key would have become.</summary>
    private async Task<int> CountFoldedAsync()
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().PaymentEvents
            .AsNoTracking()
            .CountAsync(row => row.IdempotencyKey.StartsWith("PayPal:?"), Token);
    }

    private async Task<int> CountAsync(string key)
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().PaymentEvents
            .AsNoTracking()
            .CountAsync(row => row.IdempotencyKey == key, Token);
    }
}
