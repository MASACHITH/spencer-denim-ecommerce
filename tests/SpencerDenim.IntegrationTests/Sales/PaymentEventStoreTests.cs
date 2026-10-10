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
    /// Two redeliveries arriving on two threads: exactly one is told it is new. The unique index is a
    /// backstop here, not the mechanism — if the lock hint on the guarded insert were ever lost, this is
    /// the test that would see a unique-violation surface instead of a clean answer.
    /// </summary>
    [Fact]
    public async Task Parallel_deliveries_of_one_event_store_it_exactly_once()
    {
        var key = NewKey();

        var receipts = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(() => RecordAsync(Record(key)), Token)));

        Assert.Single(receipts, receipt => receipt.IsNew);
        Assert.Single(receipts.Select(receipt => receipt.EventId).Distinct());
        Assert.Equal(1, await CountAsync(key));
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

    private async Task<int> CountAsync(string key)
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().PaymentEvents
            .AsNoTracking()
            .CountAsync(row => row.IdempotencyKey == key, Token);
    }
}
