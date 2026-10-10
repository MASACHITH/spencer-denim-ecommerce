using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SpencerDenim.Application.Sales.Payments.PayPal;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Sales.Payments;
using SpencerDenim.Infrastructure.Sales.Payments.PayPal;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales.PayPal;

/// <summary>
/// <c>RecoverPayPalCaptures</c> (doc 08 §4.2 step 5, N1; doc 11 P8 step 8): a payment stuck in Capturing is
/// looked at again, and PayPal's answer is applied exactly as the buyer's own return would have applied it.
/// The job's runner is called directly with the test clock, so a two-minute threshold, a backoff ladder and a
/// twenty-four-hour window are crossed in an instant.
/// </summary>
public sealed class PayPalRecoveryTests(SqlServerDatabaseFixture sqlServer) : PayPalTestBase(sqlServer)
{
    /// <summary>
    /// A payment left Capturing: the buyer approved, the shop claimed and called capture, and the answer never
    /// came. <paramref name="atPayPal"/> says what really happened over there.
    /// </summary>
    private async Task<(PayPalOrder Order, string PayPalOrderId, DateTime ClaimedAt)> StuckAsync(
        FakePayPalGateway.CaptureScript atPayPal = FakePayPalGateway.CaptureScript.AnswerLost,
        int quantity = 1,
        bool quiet = true)
    {
        // A test that sticks two payments passes quiet: false the second time, or it would finish the first.
        if (quiet)
        {
            await QuietLeftoversAsync();
        }

        await ConfigurePayPalAsync();

        var order = await PlacePayPalOrderAsync(quantity);
        var paypalOrderId = await StartAsync(order);
        PayPal.Approve(paypalOrderId);

        var claimedAt = Clock.Now;
        PayPal.Script(atPayPal);

        using var response = await GetAsync(ReturnPath(order.PublicId));
        Assert.Equal(PayPalPageHints.Processing, HintOf(response));
        Assert.Equal(PaymentStatus.Capturing, (await PaymentAsync(order.PaymentId)).Status);

        return (order, paypalOrderId, claimedAt);
    }

    private Task<PayPalRecoveryReport> RecoverAsync() =>
        InPayPalScopeAsync(services => services.GetRequiredService<PayPalCaptureRecovery>().RunOnceAsync(Token));

    // ------------------------------------------------------------------------------ what PayPal says

    /// <summary>"COMPLETED (capture exists): apply" - the money arrived and the answer was lost.</summary>
    [Fact]
    public async Task A_capture_that_went_through_but_whose_answer_was_lost_is_completed_by_recovery()
    {
        var (order, paypalOrderId, _) = await StuckAsync(quantity: 2);
        var before = await StockAsync(order.VariantId);
        var seriesBefore = await InvoiceSeriesNextValueAsync();

        Clock.Advance(TimeSpan.FromMinutes(3));
        var report = await RecoverAsync();

        Assert.Equal(1, report.Examined);
        Assert.Equal(1, report.Applied);

        var row = await OrderRowAsync(order.OrderId);
        Assert.Equal(OrderStatus.Paid, row.Status);
        Assert.Null(row.CaptureLockedUntilUtc);
        Assert.NotNull(row.InvoiceNumber);

        var payment = await PaymentAsync(order.PaymentId);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.True(payment.IsOrderSettlement);
        Assert.Equal(PayPal.CaptureIdOf(paypalOrderId), payment.ProviderTransactionId);

        Assert.Equal(before.OnHand - 2, (await StockAsync(order.VariantId)).OnHand);
        Assert.Equal((seriesBefore ?? 1) + 1, await InvoiceSeriesNextValueAsync());
        Assert.Equal(1, await OutboxCountAsync(order.PublicId, EmailTemplateCodes.PaymentConfirmed));

        // PayPal was asked once, to read - recovery did not capture again.
        Assert.Equal(1, PayPal.CaptureCalls);
    }

    [Fact]
    public async Task A_payment_is_not_looked_at_until_it_has_been_Capturing_for_two_minutes()
    {
        await StuckAsync();

        Clock.Advance(TimeSpan.FromSeconds(90));
        var report = await RecoverAsync();

        Assert.Equal(0, report.Examined);
        Assert.Empty(PayPal.Gets);
    }

    /// <summary>
    /// "APPROVED (not captured), order payable: re-claim, then re-capture with the SAME PayPal-Request-Id."
    /// </summary>
    [Fact]
    public async Task An_approved_order_that_was_never_captured_is_captured_again_under_the_same_request_id()
    {
        var (order, paypalOrderId, _) = await StuckAsync(FakePayPalGateway.CaptureScript.Unreachable);
        Assert.Equal(0, PayPal.CapturesAtPayPal(paypalOrderId));

        Clock.Advance(TimeSpan.FromMinutes(3));
        var report = await RecoverAsync();

        Assert.Equal(1, report.Applied);

        // Two capture calls in all - the buyer's and recovery's - and one request id between them.
        Assert.Equal(2, PayPal.Captures.Count);
        Assert.All(PayPal.Captures, call => Assert.Equal($"capture-{order.PaymentId}", call.RequestId));
        Assert.Equal(1, PayPal.CapturesAtPayPal(paypalOrderId));

        Assert.Equal(OrderStatus.Paid, (await OrderRowAsync(order.OrderId)).Status);
        Assert.Equal(PaymentStatus.Succeeded, (await PaymentAsync(order.PaymentId)).Status);
    }

    /// <summary>"APPROVED, order not payable: Failed (ORDER_NOT_PAYABLE), lock cleared. Nothing is captured."</summary>
    [Fact]
    public async Task An_approved_order_whose_shop_order_expired_is_failed_not_captured()
    {
        var (order, paypalOrderId, _) = await StuckAsync(FakePayPalGateway.CaptureScript.Unreachable);

        // The lock lapses and the order expires with the payment still Capturing.
        Clock.Advance(TimeSpan.FromHours(4));
        Assert.True(await ExpireAtAsync(order.OrderId, Clock.Now));
        var capturesBefore = PayPal.CaptureCalls;

        var report = await RecoverAsync();

        Assert.Equal(1, report.Failed);

        var payment = await PaymentAsync(order.PaymentId);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal("ORDER_NOT_PAYABLE", payment.FailureCode);

        // The lock the claim set is cleared, and not one more capture was asked for.
        Assert.Null((await OrderRowAsync(order.OrderId)).CaptureLockedUntilUtc);
        Assert.Equal(OrderStatus.Expired, (await OrderRowAsync(order.OrderId)).Status);
        Assert.Equal(capturesBefore, PayPal.CaptureCalls);
        Assert.Equal(0, PayPal.CapturesAtPayPal(paypalOrderId));
    }

    [Fact]
    public async Task A_voided_PayPal_order_fails_the_payment_and_clears_the_lock()
    {
        var (order, paypalOrderId, _) = await StuckAsync(FakePayPalGateway.CaptureScript.Unreachable);
        PayPal.Void(paypalOrderId);

        Clock.Advance(TimeSpan.FromMinutes(3));
        var report = await RecoverAsync();

        Assert.Equal(1, report.Failed);

        var payment = await PaymentAsync(order.PaymentId);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal("VOIDED", payment.FailureCode);

        // The order is still payable and unlocked, so the buyer can try again.
        var row = await OrderRowAsync(order.OrderId);
        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Null(row.CaptureLockedUntilUtc);
    }

    [Fact]
    public async Task A_PayPal_order_that_does_not_exist_fails_the_payment()
    {
        var (order, paypalOrderId, _) = await StuckAsync(FakePayPalGateway.CaptureScript.Unreachable);
        PayPal.Forget(paypalOrderId);

        Clock.Advance(TimeSpan.FromMinutes(3));
        var report = await RecoverAsync();

        Assert.Equal(1, report.Failed);
        Assert.Equal("NOT_FOUND", (await PaymentAsync(order.PaymentId)).FailureCode);
        Assert.Null((await OrderRowAsync(order.OrderId)).CaptureLockedUntilUtc);
    }

    /// <summary>The buyer never approved: nothing can be captured, so the claim is handed back and they can go and approve.</summary>
    [Fact]
    public async Task An_order_the_buyer_never_approved_gives_the_claim_back()
    {
        await QuietLeftoversAsync();
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();
        await StartAsync(order);

        // Arranged: a payment left Capturing against a PayPal order that is still only CREATED.
        await ForceCapturingAsync(order, Clock.Now);

        Clock.Advance(TimeSpan.FromMinutes(3));
        var report = await RecoverAsync();

        Assert.Equal(1, report.Released);

        var payment = await PaymentAsync(order.PaymentId);
        Assert.Equal(PaymentStatus.Initiated, payment.Status);
        Assert.Null(payment.CaptureStartedAtUtc);
        Assert.Null((await OrderRowAsync(order.OrderId)).CaptureLockedUntilUtc);
        Assert.Empty(PayPal.Captures);
    }

    /// <summary>A capture PayPal is holding (an eCheck) is the same news as at the buyer's return.</summary>
    [Fact]
    public async Task A_capture_PayPal_reports_as_PENDING_moves_the_payment_to_Pending_and_extends_the_hold()
    {
        await QuietLeftoversAsync();
        await ConfigurePayPalAsync();
        var order = await PlacePayPalOrderAsync();
        var paypalOrderId = await StartAsync(order);
        PayPal.Approve(paypalOrderId);

        // PayPal holds the capture. The buyer's own call would have applied that; arrange the lost-answer
        // shape instead by putting the payment back to Capturing, as it was when the answer went missing.
        PayPal.Script(FakePayPalGateway.CaptureScript.Pending);
        using (var response = await GetAsync(ReturnPath(order.PublicId)))
        {
            Assert.Equal(PayPalPageHints.Pending, HintOf(response));
        }

        await ForceCapturingAsync(order, Clock.Now);

        Clock.Advance(TimeSpan.FromMinutes(3));
        var report = await RecoverAsync();

        Assert.Equal(1, report.Applied);
        Assert.Equal(PaymentStatus.Pending, (await PaymentAsync(order.PaymentId)).Status);

        var row = await OrderRowAsync(order.OrderId);
        Assert.Equal(row.PlacedAtUtc.AddDays(5), row.PaymentDueAtUtc, TimeSpan.FromMilliseconds(5));
        Assert.Null(row.CaptureLockedUntilUtc);
    }

    // ------------------------------------------------------------------------------ unreachable: backoff, then give up

    /// <summary>
    /// "PayPal unreachable: retry with backoff (2, 5, 15, 30, 60 min …)". The ladder is a function of the
    /// claim time and the last attempt, so no counter is stored.
    /// </summary>
    [Fact]
    public async Task While_PayPal_is_unreachable_recovery_backs_off_on_the_ladder_and_changes_nothing()
    {
        var (order, _, claimedAt) = await StuckAsync(FakePayPalGateway.CaptureScript.Unreachable);
        PayPal.Down = true;

        // First look at claim + 2 min (a second late, because the stored claim time is rounded to the ms).
        var firstLook = claimedAt.AddMinutes(2).AddSeconds(1);
        Clock.Now = firstLook;
        var first = await RecoverAsync();
        Assert.Equal(1, first.StillUnreachable);
        Assert.Single(PayPal.Gets);

        // Stamped, and still Capturing with its lock.
        var payment = await PaymentAsync(order.PaymentId);
        Assert.Equal(PaymentStatus.Capturing, payment.Status);
        Assert.Equal(firstLook, payment.UpdatedAtUtc!.Value, TimeSpan.FromMilliseconds(5));
        Assert.NotNull((await OrderRowAsync(order.OrderId)).CaptureLockedUntilUtc);

        // The ladder, a rung at a time: +4, +9, +24, +54 minutes from the claim (gaps of 2, 5, 15, 30).
        // One second before a rung nobody is asked; one second after it, PayPal is asked again.
        var asked = 1;

        foreach (var rungMinutes in new[] { 4, 9, 24, 54 })
        {
            Clock.Now = claimedAt.AddMinutes(rungMinutes).AddSeconds(-1);
            Assert.Equal(1, (await RecoverAsync()).NotDue);
            Assert.Equal(asked, PayPal.Gets.Count);

            Clock.Now = claimedAt.AddMinutes(rungMinutes).AddSeconds(1);
            Assert.Equal(1, (await RecoverAsync()).StillUnreachable);
            Assert.Equal(++asked, PayPal.Gets.Count);
        }

        Assert.Equal(5, PayPal.Gets.Count);
        Assert.Equal(PaymentStatus.Capturing, (await PaymentAsync(order.PaymentId)).Status);
    }

    /// <summary>
    /// "After CaptureRecoveryMaxHours (24 h) since CaptureStartedAtUtc: Failed (RECOVERY_TIMEOUT), lock
    /// cleared, plus CaptureRecoveryFailed" - and the alert is written in the same transaction.
    /// </summary>
    [Fact]
    public async Task After_the_recovery_window_the_payment_fails_with_RECOVERY_TIMEOUT_and_staff_are_alerted()
    {
        var (order, _, claimedAt) = await StuckAsync(FakePayPalGateway.CaptureScript.Unreachable);
        PayPal.Down = true;

        try
        {
            Clock.Now = claimedAt.AddHours(24).AddMinutes(-1);
            Assert.Equal(1, (await RecoverAsync()).StillUnreachable);
            Assert.Empty(await AlertsOfTypeAsync(AdminAlertTypes.CaptureRecoveryFailed, order.PublicId));

            // The window has closed: the payment is due at once, whatever the ladder's next rung says (it
            // would be most of an hour away), PayPal is asked one last time, and the payment fails.
            Clock.Now = claimedAt.AddHours(24).AddSeconds(1);
            var report = await RecoverAsync();

            Assert.Equal(1, report.GaveUp);

            var payment = await PaymentAsync(order.PaymentId);
            Assert.Equal(PaymentStatus.Failed, payment.Status);
            Assert.Equal("RECOVERY_TIMEOUT", payment.FailureCode);
            Assert.False(payment.IsOrderSettlement);
            Assert.Null((await OrderRowAsync(order.OrderId)).CaptureLockedUntilUtc);

            var alert = Assert.Single(await AlertsOfTypeAsync(AdminAlertTypes.CaptureRecoveryFailed, order.PublicId));
            Assert.Equal(AdminAlertSeverity.Warning, alert.Severity);
            Assert.Equal(AdminAlert.OrderEntityType, alert.RelatedEntityType);
            Assert.Contains("RECOVERY_TIMEOUT", alert.DetailsJson, StringComparison.Ordinal);

            // A payment that is no longer Capturing is not looked at again, and raises no second banner.
            Clock.Advance(TimeSpan.FromHours(2));
            Assert.Equal(0, (await RecoverAsync()).Examined);
            Assert.Single(await AlertsOfTypeAsync(AdminAlertTypes.CaptureRecoveryFailed, order.PublicId));
        }
        finally
        {
            await DeleteAlertsAsync(order.PublicId);
        }
    }

    /// <summary>
    /// Doc 08 §5.9 R1, the acceptance test it names: "late COMPLETED after a recovery timeout: Capturing -&gt;
    /// Failed (RECOVERY_TIMEOUT), then a verified COMPLETED webhook arrives. If the order is still payable -&gt;
    /// Succeeded and the order is Paid. Never Ignored, never an endless retry."
    /// </summary>
    [Fact]
    public async Task A_COMPLETED_webhook_after_a_recovery_timeout_pays_the_order_if_it_is_still_payable()
    {
        var (order, paypalOrderId, claimedAt) = await StuckAsync(FakePayPalGateway.CaptureScript.Unreachable);
        PayPal.Down = true;

        try
        {
            Clock.Now = claimedAt.AddHours(25);
            Assert.Equal(1, (await RecoverAsync()).GaveUp);
            Assert.Equal(PaymentStatus.Failed, (await PaymentAsync(order.PaymentId)).Status);

            // The buyer paid after all; PayPal tells us so.
            PayPal.Down = false;
            using var webhook = await PostWebhookAsync(CaptureEvent(
                "PAYMENT.CAPTURE.COMPLETED",
                "WH-R1-1",
                "CAP-R1-1",
                paypalOrderId,
                order.PaymentId,
                order.UsdAmount));

            Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);

            var payment = await PaymentAsync(order.PaymentId);
            Assert.Equal(PaymentStatus.Succeeded, payment.Status);
            Assert.True(payment.IsOrderSettlement);
            Assert.Null(payment.FailureCode);
            Assert.Equal(OrderStatus.Paid, (await OrderRowAsync(order.OrderId)).Status);

            var stored = Assert.Single(await EventsOfAsync(order.PaymentId), e => e.ProviderEventId == "WH-R1-1");
            Assert.Equal(PaymentEventProcessingStatus.Processed, stored.ProcessingStatus);
        }
        finally
        {
            await DeleteAlertsAsync(order.PublicId);
        }
    }

    /// <summary>... and if the order has gone, the money is kept as an exception rather than lost.</summary>
    [Fact]
    public async Task A_COMPLETED_webhook_after_a_recovery_timeout_is_an_exception_if_the_order_has_gone()
    {
        var (order, paypalOrderId, claimedAt) = await StuckAsync(FakePayPalGateway.CaptureScript.Unreachable);
        PayPal.Down = true;

        try
        {
            Clock.Now = claimedAt.AddHours(25);
            Assert.Equal(1, (await RecoverAsync()).GaveUp);
            Assert.True(await ExpireAtAsync(order.OrderId, Clock.Now));

            PayPal.Down = false;
            using var webhook = await PostWebhookAsync(CaptureEvent(
                "PAYMENT.CAPTURE.COMPLETED",
                "WH-R1-2",
                "CAP-R1-2",
                paypalOrderId,
                order.PaymentId,
                order.UsdAmount));

            Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, (await PaymentAsync(order.PaymentId)).Status);
            Assert.Equal(OrderStatus.Expired, (await OrderRowAsync(order.OrderId)).Status);
            Assert.Single(await AlertsOfTypeAsync(AdminAlertTypes.PaymentException, order.PublicId));
        }
        finally
        {
            await DeleteAlertsAsync(order.PublicId);
        }
    }

    // ------------------------------------------------------------------------------ overlap, errors, the service

    /// <summary>No ops.JobLeases table: two passes at once over the same payment must be harmless.</summary>
    [Fact]
    public async Task Two_recovery_passes_at_once_settle_a_payment_exactly_once()
    {
        var (order, _, _) = await StuckAsync(quantity: 2);
        var before = await StockAsync(order.VariantId);
        var seriesBefore = await InvoiceSeriesNextValueAsync();

        Clock.Advance(TimeSpan.FromMinutes(3));

        var reports = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => Task.Run(() => RecoverAsync(), Token)));

        Assert.Equal(0, reports.Sum(report => report.Errors));
        Assert.Equal(OrderStatus.Paid, (await OrderRowAsync(order.OrderId)).Status);
        Assert.Equal(before.OnHand - 2, (await StockAsync(order.VariantId)).OnHand);
        Assert.Equal((seriesBefore ?? 1) + 1, await InvoiceSeriesNextValueAsync());
        Assert.Single((await OrderRowAsync(order.OrderId)).StatusHistory, entry => entry.ToStatus == OrderStatus.Paid);
        Assert.Equal(1, await OutboxCountAsync(order.PublicId, EmailTemplateCodes.PaymentConfirmed));
    }

    /// <summary>
    /// Recovery racing the buyer's own late return and a webhook: whichever applies first wins, the others
    /// find the payment settled, and the money counts once.
    /// </summary>
    [Fact]
    public async Task Recovery_a_webhook_and_a_return_racing_settle_once()
    {
        var (order, paypalOrderId, _) = await StuckAsync(quantity: 2);
        var before = await StockAsync(order.VariantId);

        Clock.Advance(TimeSpan.FromMinutes(3));

        var body = CaptureEvent(
            "PAYMENT.CAPTURE.COMPLETED",
            "WH-TRIPLE-1",
            PayPal.CaptureIdOf(paypalOrderId)!,
            paypalOrderId,
            order.PaymentId,
            order.UsdAmount);

        var results = await Task.WhenAll(
            Task.Run(async () => (object)await RecoverAsync(), Token),
            Task.Run(async () => (object)await PostWebhookAsync(body), Token),
            Task.Run(async () => (object)await GetAsync(ReturnPath(order.PublicId)), Token));

        foreach (var result in results.OfType<HttpResponseMessage>())
        {
            Assert.True(result.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect);
            result.Dispose();
        }

        Assert.Equal(OrderStatus.Paid, (await OrderRowAsync(order.OrderId)).Status);
        Assert.Equal(before.OnHand - 2, (await StockAsync(order.VariantId)).OnHand);
        Assert.Single((await OrderRowAsync(order.OrderId)).StatusHistory, entry => entry.ToStatus == OrderStatus.Paid);
    }

    /// <summary>One payment's trouble must not stop the others, and must not escape the job.</summary>
    [Fact]
    public async Task A_payment_whose_recovery_throws_does_not_stop_the_others_or_escape_the_job()
    {
        var (first, _, _) = await StuckAsync();
        var (second, _, _) = await StuckAsync(quiet: false);

        // Only the first payment's PayPal order blows up.
        PayPal.GetThrowsFor.Add(PayPal.OrderIdFor(PayPalRequestIds.Create(first.PaymentId))!);

        Clock.Advance(TimeSpan.FromMinutes(3));
        var report = await RecoverAsync();

        Assert.Equal(2, report.Examined);
        Assert.Equal(1, report.Errors);
        Assert.Equal(1, report.Applied);

        // The one that threw is still Capturing, still locked, and found again next time.
        Assert.Equal(PaymentStatus.Capturing, (await PaymentAsync(first.PaymentId)).Status);
        Assert.Equal(PaymentStatus.Succeeded, (await PaymentAsync(second.PaymentId)).Status);

        PayPal.GetThrowsFor.Clear();
        Assert.Equal(1, (await RecoverAsync()).Applied);
        Assert.Equal(PaymentStatus.Succeeded, (await PaymentAsync(first.PaymentId)).Status);
    }

    /// <summary>The hosted wrapper never lets an exception reach the host: a failing pass is logged and the loop goes on.</summary>
    [Fact]
    public async Task The_hosted_job_swallows_a_failing_pass()
    {
        await QuietLeftoversAsync();

        var scopes = PayPalHost.Services.GetRequiredService<IServiceScopeFactory>();
        var recovery = new RecoverPayPalCapturesService(scopes, NullLogger<RecoverPayPalCapturesService>.Instance);
        var retry = new RetryPaymentEventsService(scopes, NullLogger<RetryPaymentEventsService>.Instance);

        // A healthy pass, and an empty one, both complete without throwing.
        await recovery.RunAsync(Token);
        await retry.RunAsync(Token);

        // And a pass whose scope cannot even be built reports nothing to the host.
        var broken = new RecoverPayPalCapturesService(new ThrowingScopeFactory(), NullLogger<RecoverPayPalCapturesService>.Instance);
        await broken.RunAsync(Token);
        await broken.RunAsync(Token);
    }

    // ------------------------------------------------------------------------------ helpers

    private async Task ForceCapturingAsync(PayPalOrder order, DateTime startedAt)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Orders
            .Where(candidate => candidate.Id == order.OrderId)
            .ExecuteUpdateAsync(
                candidate => candidate.SetProperty(row => row.CaptureLockedUntilUtc, startedAt.AddHours(3)),
                Token);

        var moved = await db.Payments
            .Where(candidate => candidate.Id == order.PaymentId)
            .ExecuteUpdateAsync(
                candidate => candidate
                    .SetProperty(row => row.Status, PaymentStatus.Capturing)
                    .SetProperty(row => row.CaptureStartedAtUtc, startedAt)
                    .SetProperty(row => row.UpdatedAtUtc, startedAt)
                    .SetProperty(row => row.ProviderTransactionId, (string?)null),
                Token);

        Assert.Equal(1, moved);
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("The container is gone.");
    }
}
