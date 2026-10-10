using System.Text.Json;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.PayHere;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Application.Sales.PayHere;

/// <summary>
/// What each PayHere status does once a notification has been verified and stored (doc 11 P8b step 4's
/// table), the retry ladder, and the cases that need an alert - against fakes, so each rule is pinned
/// without a database. The real transitions are exercised against SQL Server in
/// <c>PayHereNotifyTests</c>.
/// </summary>
public sealed class PayHereNotificationProcessorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Arrangement
    {
        public FakePayHereEvents Events { get; } = new();

        public FakePayHerePayments Payments { get; } = new();

        public FakePayHereSettlements Settlements { get; } = new();

        public FakePayHereAlerts Alerts { get; } = new();

        public PayHereNotificationProcessor Processor => new(
            Payments,
            Settlements,
            Events,
            new FakePayHereSettings(PayHereSample.Settings(pendingMaxDays: 7, expiryMinutes: 45)),
            Alerts,
            new PayHereTestClock(PayHereSample.Now));

        public Task<PayHereProcessingOutcome> ProcessAsync(PayHereNotification notification, int failedAttempts = 0) =>
            Processor.ProcessAsync(1, notification, failedAttempts, Token);
    }

    // ---- 2 success ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_success_settles_through_the_shared_store_with_the_amount_and_currency_PayHere_reported()
    {
        var arranged = new Arrangement();

        var outcome = await arranged.ProcessAsync(PayHereSample.Notification());

        Assert.Equal(PayHereOutcomeKind.Applied, outcome.Kind);

        var settlement = arranged.Settlements.Settled!;
        Assert.Equal(PayHereSample.PaymentId, settlement.PaymentId);
        Assert.Equal("PayHere", settlement.ProviderCode);
        Assert.Equal(1500.50m, settlement.ConfirmedAmount);
        Assert.Equal("LKR", settlement.ConfirmedCurrency);
        Assert.Equal(PayHereSample.PayHerePaymentId, settlement.ProviderTransactionId);
        Assert.Equal(PayHereSample.Now, settlement.NowUtc);

        // PayHere's id is recorded as the provider reference before the settlement.
        Assert.Equal(["Find", "RecordReference"], arranged.Payments.Calls);
        Assert.Equal(PayHereSample.PayHerePaymentId, arranged.Payments.LastReference);
        Assert.Equal("Processed", Assert.Single(arranged.Events.Marks).Outcome);
        Assert.Equal((1L, (int?)PayHereSample.PaymentId, (int?)77), Assert.Single(arranged.Events.Links));
    }

    [Fact]
    public async Task A_success_for_an_order_that_can_no_longer_take_it_is_applied_as_an_exception()
    {
        var arranged = new Arrangement();
        arranged.Settlements.Result =
            new PaymentSettlementResult.CapturedRequiresRefund(PaymentExceptionReasons.OrderExpired, PayHereSample.OrderNumber);

        var outcome = await arranged.ProcessAsync(PayHereSample.Notification());

        Assert.Equal(PayHereOutcomeKind.Applied, outcome.Kind);
        Assert.Contains("order_expired", outcome.Detail, StringComparison.Ordinal);
        Assert.Equal("Processed", Assert.Single(arranged.Events.Marks).Outcome);
    }

    [Fact]
    public async Task A_success_whose_amount_is_not_a_number_is_routed_to_the_exception_path_rather_than_guessed_at()
    {
        var arranged = new Arrangement();
        arranged.Settlements.Result =
            new PaymentSettlementResult.CapturedRequiresRefund(PaymentExceptionReasons.AmountMismatch, PayHereSample.OrderNumber);

        // Signed, so it cannot come from PayHere in practice; the shape rule normally refuses it first.
        var notification = PayHereSample.Notification(amount: "1500.50.1");

        await arranged.ProcessAsync(notification);

        Assert.Null(arranged.Settlements.Settled);
        var routed = arranged.Settlements.Routed!;
        Assert.Equal(PaymentExceptionReasons.AmountMismatch, routed.Reason);
        Assert.Null(routed.ConfirmedAmount);
    }

    [Fact]
    public async Task A_success_on_a_payment_that_has_already_settled_with_the_same_PayHere_id_is_a_duplicate()
    {
        var arranged = new Arrangement();
        arranged.Payments.Payment = PayHereSample.Payment(PaymentStatus.Succeeded, OrderStatus.Paid, PayHereSample.PayHerePaymentId);
        arranged.Settlements.Result = new PaymentSettlementResult.AlreadyHandled(PaymentStatus.Succeeded);

        var outcome = await arranged.ProcessAsync(PayHereSample.Notification());

        Assert.Equal(PayHereOutcomeKind.Ignored, outcome.Kind);
        Assert.Equal("Ignored", Assert.Single(arranged.Events.Marks).Outcome);
        Assert.Empty(arranged.Alerts.Raised);
    }

    /// <summary>
    /// The customer paid twice at PayHere. The second PayHere payment is real money with no record on our
    /// side unless it is put on the dashboard.
    /// </summary>
    [Fact]
    public async Task A_success_with_a_different_PayHere_id_on_a_settled_payment_is_a_second_charge_and_raises_an_alert()
    {
        var arranged = new Arrangement();
        arranged.Payments.Payment = PayHereSample.Payment(PaymentStatus.Succeeded, OrderStatus.Paid, "999000111");
        arranged.Settlements.Result = new PaymentSettlementResult.AlreadyHandled(PaymentStatus.Succeeded);

        var outcome = await arranged.ProcessAsync(PayHereSample.Notification());

        Assert.Equal(PayHereOutcomeKind.Applied, outcome.Kind);

        var alert = Assert.Single(arranged.Alerts.Raised);
        Assert.Equal(AdminAlertTypes.PaymentException, alert.AlertType);
        Assert.Equal(PayHereSample.OrderPublicId.ToString("D"), alert.RelatedEntityId);
        Assert.Contains("already paid", alert.Title, StringComparison.Ordinal);
        Assert.Equal("Processed", Assert.Single(arranged.Events.Marks).Outcome);
    }

    /// <summary>
    /// The first success was parked as an exception (the order had expired); a second, different PayHere
    /// payment arrives for the same attempt. The order was not paid, so the banner must not say it was.
    /// </summary>
    [Fact]
    public async Task A_second_charge_on_a_payment_parked_as_an_exception_does_not_claim_the_order_was_paid()
    {
        var arranged = new Arrangement();
        arranged.Payments.Payment = PayHereSample.Payment(
            PaymentStatus.CapturedRequiresRefund, OrderStatus.Expired, "999000111");
        arranged.Settlements.Result = new PaymentSettlementResult.AlreadyHandled(PaymentStatus.CapturedRequiresRefund);

        await arranged.ProcessAsync(PayHereSample.Notification());

        var alert = Assert.Single(arranged.Alerts.Raised);

        using var details = JsonDocument.Parse(alert.DetailsJson!);
        Assert.Equal("order_not_payable", details.RootElement.GetProperty("reason").GetString());
        Assert.DoesNotContain("already paid", alert.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_notification_for_a_payment_the_store_does_not_know_is_unmatched()
    {
        var arranged = new Arrangement();
        arranged.Settlements.Result = new PaymentSettlementResult.NotFound();

        var outcome = await arranged.ProcessAsync(PayHereSample.Notification());

        Assert.Equal(PayHereOutcomeKind.Unmatched, outcome.Kind);
        Assert.Equal("Ignored", Assert.Single(arranged.Events.Marks).Outcome);
    }

    // ---- matching the payment ----------------------------------------------------------------------

    [Theory]
    [InlineData("SD-2026-000123")]
    [InlineData("SD-2026-000123-0")]
    [InlineData("garbage-id")]
    [InlineData("SD-2026-000123-4567-9")]
    public async Task An_order_id_this_shop_never_issued_is_unmatched_and_touches_nothing(string orderId)
    {
        var arranged = new Arrangement();

        var outcome = await arranged.ProcessAsync(PayHereSample.Notification(orderId: orderId));

        Assert.Equal(PayHereOutcomeKind.Unmatched, outcome.Kind);
        Assert.Null(arranged.Settlements.Settled);
        Assert.Empty(arranged.Events.Links);
        Assert.Equal("Ignored", Assert.Single(arranged.Events.Marks).Outcome);
    }

    [Theory]
    [InlineData("SD-2026-000999-4567")]
    [InlineData("SD-2026-000123-4568")]
    public async Task A_well_formed_order_id_that_names_another_orders_payment_is_unmatched(string orderId)
    {
        var arranged = new Arrangement();

        var outcome = await arranged.ProcessAsync(PayHereSample.Notification(orderId: orderId));

        Assert.Equal(PayHereOutcomeKind.Unmatched, outcome.Kind);
        Assert.Null(arranged.Settlements.Settled);
        Assert.Empty(arranged.Alerts.Raised);
    }

    // ---- 0 pending ---------------------------------------------------------------------------------

    [Fact]
    public async Task Pending_extends_the_hold_by_the_configured_days()
    {
        var arranged = new Arrangement();

        var outcome = await arranged.ProcessAsync(PayHereSample.Notification(status: "0"));

        Assert.Equal(PayHereOutcomeKind.Applied, outcome.Kind);
        Assert.Contains("MarkPending", arranged.Payments.Calls);
        Assert.Equal(7, arranged.Payments.LastPendingDays);
        Assert.Null(arranged.Settlements.Settled);
    }

    [Fact]
    public async Task Pending_on_a_payment_that_is_no_longer_awaiting_PayHere_is_ignored()
    {
        var arranged = new Arrangement();
        arranged.Payments.PendingResult = PayHereTransition.NotApplicable;

        var outcome = await arranged.ProcessAsync(PayHereSample.Notification(status: "0"));

        Assert.Equal(PayHereOutcomeKind.Ignored, outcome.Kind);
        Assert.Equal("Ignored", Assert.Single(arranged.Events.Marks).Outcome);
    }

    // ---- -1 / -2 failed ----------------------------------------------------------------------------

    [Theory]
    [InlineData("-1", "CANCELLED")]
    [InlineData("-2", "FAILED")]
    public async Task A_cancelled_or_failed_notice_fails_the_payment_with_PayHeres_message(string status, string code)
    {
        var arranged = new Arrangement();

        var outcome = await arranged.ProcessAsync(PayHereSample.Notification(status: status, message: "Card declined"));

        Assert.Equal(PayHereOutcomeKind.Applied, outcome.Kind);

        var failure = arranged.Payments.LastFailure!;
        Assert.Equal(code, failure.FailureCode);
        Assert.Equal("Card declined", failure.FailureMessage);
        Assert.Equal(PayHereSample.PaymentId, failure.PaymentId);
        Assert.Equal(77, failure.OrderId);
        Assert.Equal(PayHereSample.PayHerePaymentId, failure.PayHerePaymentId);
        Assert.Equal(45, failure.ExpiryMinutes);
        Assert.Equal(PayHereSample.Now, failure.NowUtc);
        Assert.Null(arranged.Settlements.Settled);
    }

    [Fact]
    public async Task A_failure_with_no_message_from_PayHere_gets_a_plain_sentence()
    {
        var arranged = new Arrangement();

        await arranged.ProcessAsync(PayHereSample.Notification(status: "-2", message: null));

        Assert.Equal("PayHere reported the payment as failed.", arranged.Payments.LastFailure!.FailureMessage);
    }

    [Fact]
    public async Task A_failure_on_a_payment_that_is_no_longer_open_is_ignored()
    {
        var arranged = new Arrangement();
        arranged.Payments.FailedResult = new PayHereFailureResult(PayHereTransition.NotApplicable, false, false);

        var outcome = await arranged.ProcessAsync(PayHereSample.Notification(status: "-2"));

        Assert.Equal(PayHereOutcomeKind.Ignored, outcome.Kind);
    }

    [Fact]
    public async Task A_failure_that_cut_a_pending_hold_back_and_opened_a_new_attempt_says_so()
    {
        var arranged = new Arrangement();
        arranged.Payments.FailedResult = new PayHereFailureResult(PayHereTransition.Applied, true, true);

        var outcome = await arranged.ProcessAsync(PayHereSample.Notification(status: "-2"));

        Assert.Contains("cut back", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("new attempt", outcome.Detail, StringComparison.Ordinal);
    }

    // ---- -3 chargeback -----------------------------------------------------------------------------

    [Fact]
    public async Task A_chargeback_raises_an_alert_and_changes_nothing_else()
    {
        var arranged = new Arrangement();
        arranged.Payments.Payment = PayHereSample.Payment(PaymentStatus.Succeeded, OrderStatus.Paid, PayHereSample.PayHerePaymentId);

        var outcome = await arranged.ProcessAsync(PayHereSample.Notification(status: "-3"));

        Assert.Equal(PayHereOutcomeKind.Applied, outcome.Kind);

        var alert = Assert.Single(arranged.Alerts.Raised);
        Assert.Equal(AdminAlertTypes.PaymentException, alert.AlertType);
        Assert.Equal(AdminAlertSeverity.Warning, alert.Severity);
        Assert.Contains("Chargeback", alert.Title, StringComparison.Ordinal);
        Assert.Contains(PayHereSample.OrderNumber, alert.Title, StringComparison.Ordinal);
        Assert.Equal(PayHereSample.OrderPublicId.ToString("D"), alert.RelatedEntityId);

        using var details = JsonDocument.Parse(alert.DetailsJson!);
        Assert.Equal("chargeback", details.RootElement.GetProperty("reason").GetString());

        // No state change of any kind.
        Assert.Null(arranged.Settlements.Settled);
        Assert.Null(arranged.Settlements.Routed);
        Assert.Null(arranged.Payments.LastFailure);
        Assert.Null(arranged.Payments.LastPendingDays);
    }

    [Fact]
    public async Task A_status_code_this_shop_does_not_act_on_is_ignored()
    {
        var arranged = new Arrangement();

        var outcome = await arranged.ProcessAsync(PayHereSample.Notification(status: "1"));

        Assert.Equal(PayHereOutcomeKind.Ignored, outcome.Kind);
        Assert.Null(arranged.Settlements.Settled);
        Assert.Empty(arranged.Alerts.Raised);
    }

    // ---- failure to apply, and the retry ladder ----------------------------------------------------

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 5)]
    [InlineData(2, 15)]
    [InlineData(3, 60)]
    [InlineData(4, 240)]
    public async Task A_failure_is_retried_on_the_ladder(int failedBefore, int expectedMinutes)
    {
        var arranged = new Arrangement();
        arranged.Settlements.Throw = new InvalidOperationException("boom");

        var outcome = await arranged.ProcessAsync(PayHereSample.Notification(), failedBefore);

        Assert.Equal(PayHereOutcomeKind.Failed, outcome.Kind);

        var mark = Assert.Single(arranged.Events.Marks);
        Assert.Equal("Failed", mark.Outcome);
        Assert.Equal(PayHereSample.Now.AddMinutes(expectedMinutes), mark.RetryAt);
        Assert.Contains("InvalidOperationException", mark.Failure, StringComparison.Ordinal);
        Assert.Empty(arranged.Alerts.Raised);
    }

    [Fact]
    public async Task When_the_ladder_is_spent_the_event_is_left_for_a_person_and_an_alert_says_so()
    {
        var arranged = new Arrangement();
        arranged.Settlements.Throw = new InvalidOperationException("boom");

        var outcome = await arranged.ProcessAsync(PayHereSample.Notification(), failedAttempts: 5);

        Assert.Equal(PayHereOutcomeKind.GaveUp, outcome.Kind);

        var mark = Assert.Single(arranged.Events.Marks);
        Assert.Equal("Failed", mark.Outcome);
        Assert.Null(mark.RetryAt);

        var alert = Assert.Single(arranged.Alerts.Raised);
        Assert.Equal(AdminAlertTypes.PaymentException, alert.AlertType);
        Assert.Contains("could not be applied", alert.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ladder_has_five_rungs_and_a_sixth_failure_is_the_last()
    {
        Assert.Equal(6, PayHereNotificationProcessor.MaxAttempts);
        Assert.Null(PayHereNotificationProcessor.RetryDelay(0));
        Assert.Equal(TimeSpan.FromMinutes(1), PayHereNotificationProcessor.RetryDelay(1));
        Assert.Equal(TimeSpan.FromHours(4), PayHereNotificationProcessor.RetryDelay(5));
        Assert.Null(PayHereNotificationProcessor.RetryDelay(6));
    }

    [Fact]
    public async Task Cancellation_is_not_a_failure_to_apply()
    {
        var arranged = new Arrangement();
        arranged.Settlements.Throw = new OperationCanceledException(Token);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => arranged.Processor.ProcessAsync(1, PayHereSample.Notification(), 0, cancelled.Token));

        Assert.Empty(arranged.Events.Marks);
    }
}
