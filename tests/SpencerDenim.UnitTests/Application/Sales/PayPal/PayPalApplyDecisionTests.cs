using SpencerDenim.Application.Sales.Payments.PayPal;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Application.Sales.PayPal;

/// <summary>
/// The one decision table behind the capture response, the verified webhook and the recovery job (doc 08 §4.2
/// steps 3, 4 and 6; doc 11 P8 step 6). Asserted over <b>every</b> payment status and every capture status, so
/// a cell cannot be left to chance and the late-COMPLETED rule (R1) cannot regress.
/// </summary>
public sealed class PayPalApplyDecisionTests
{
    private static readonly PaymentStatus[] AllStatuses = Enum.GetValues<PaymentStatus>();

    /// <summary>R1: a COMPLETED capture is accepted for any payment not already captured.</summary>
    [Theory]
    [InlineData(PaymentStatus.Initiated)]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Capturing)]
    [InlineData(PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Cancelled)]
    [InlineData(PaymentStatus.Expired)]
    public void A_completed_capture_settles_any_payment_that_has_not_been_captured(PaymentStatus current)
    {
        Assert.Equal(PayPalApplyAction.Settle, PayPalApplyDecision.Decide(current, PayPalCaptureStatus.Completed));
        Assert.True(PayPalApplyDecision.AcceptsCompleted(current));
    }

    /// <summary>Doc 08 §4.2 step 6: only 3, 7, 8 and 9 are duplicates.</summary>
    [Theory]
    [InlineData(PaymentStatus.Succeeded)]
    [InlineData(PaymentStatus.CapturedRequiresRefund)]
    [InlineData(PaymentStatus.PartiallyRefunded)]
    [InlineData(PaymentStatus.Refunded)]
    public void A_completed_capture_on_a_payment_already_captured_is_a_duplicate(PaymentStatus current)
    {
        Assert.Equal(PayPalApplyAction.Ignore, PayPalApplyDecision.Decide(current, PayPalCaptureStatus.Completed));
        Assert.False(PayPalApplyDecision.AcceptsCompleted(current));
    }

    /// <summary>
    /// <b>The late COMPLETED on a Failed or Cancelled payment is not a duplicate</b> (doc 08 §4.2 R1, §5.9). It
    /// is the rule that stops a customer's money being lost to a recovery timeout.
    /// </summary>
    [Theory]
    [InlineData(PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Cancelled)]
    public void A_late_completed_is_never_ignored(PaymentStatus current)
    {
        Assert.NotEqual(PayPalApplyAction.Ignore, PayPalApplyDecision.Decide(current, PayPalCaptureStatus.Completed));
    }

    [Theory]
    [InlineData(PaymentStatus.Initiated)]
    [InlineData(PaymentStatus.Capturing)]
    public void A_pending_capture_moves_a_payment_that_is_still_waiting(PaymentStatus current)
    {
        Assert.Equal(PayPalApplyAction.MarkPending, PayPalApplyDecision.Decide(current, PayPalCaptureStatus.Pending));
    }

    [Theory]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Succeeded)]
    [InlineData(PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Cancelled)]
    [InlineData(PaymentStatus.Expired)]
    [InlineData(PaymentStatus.CapturedRequiresRefund)]
    [InlineData(PaymentStatus.PartiallyRefunded)]
    [InlineData(PaymentStatus.Refunded)]
    public void A_pending_capture_after_the_payment_has_moved_on_is_out_of_order_and_ignored(PaymentStatus current)
    {
        Assert.Equal(PayPalApplyAction.Ignore, PayPalApplyDecision.Decide(current, PayPalCaptureStatus.Pending));
    }

    [Theory]
    [InlineData(PayPalCaptureStatus.Declined)]
    [InlineData(PayPalCaptureStatus.Failed)]
    public void A_refusal_fails_a_payment_that_is_still_open(PayPalCaptureStatus refusal)
    {
        foreach (var current in new[] { PaymentStatus.Initiated, PaymentStatus.Pending, PaymentStatus.Capturing })
        {
            Assert.Equal(PayPalApplyAction.MarkDeclined, PayPalApplyDecision.Decide(current, refusal));
        }
    }

    /// <summary>A stale DENIED must not undo money that has arrived (the brief's "DENIED after COMPLETED ignored").</summary>
    [Theory]
    [InlineData(PayPalCaptureStatus.Declined)]
    [InlineData(PayPalCaptureStatus.Failed)]
    public void A_refusal_after_the_payment_has_settled_or_closed_is_ignored(PayPalCaptureStatus refusal)
    {
        foreach (var current in new[]
        {
            PaymentStatus.Succeeded,
            PaymentStatus.Failed,
            PaymentStatus.Cancelled,
            PaymentStatus.Expired,
            PaymentStatus.CapturedRequiresRefund,
            PaymentStatus.PartiallyRefunded,
            PaymentStatus.Refunded,
        })
        {
            Assert.Equal(PayPalApplyAction.Ignore, PayPalApplyDecision.Decide(current, refusal));
        }
    }

    [Theory]
    [InlineData(PayPalCaptureStatus.Refunded)]
    [InlineData(PayPalCaptureStatus.PartiallyRefunded)]
    [InlineData(PayPalCaptureStatus.Reversed)]
    public void A_refund_or_reversal_is_recorded_whatever_the_payment_is(PayPalCaptureStatus status)
    {
        foreach (var current in AllStatuses)
        {
            Assert.Equal(PayPalApplyAction.RecordOnly, PayPalApplyDecision.Decide(current, status));
        }
    }

    [Fact]
    public void An_unknown_capture_status_is_never_acted_on()
    {
        foreach (var current in AllStatuses)
        {
            Assert.Equal(PayPalApplyAction.Ignore, PayPalApplyDecision.Decide(current, PayPalCaptureStatus.Unknown));
        }
    }

    /// <summary>
    /// The table is total: every status meets every capture status and gets one of the five answers - and
    /// only a COMPLETED capture can ever settle, so no other kind of news can pay an order.
    /// </summary>
    [Fact]
    public void Only_a_completed_capture_can_ever_settle()
    {
        foreach (var current in AllStatuses)
        {
            foreach (var status in Enum.GetValues<PayPalCaptureStatus>())
            {
                var action = PayPalApplyDecision.Decide(current, status);

                Assert.True(Enum.IsDefined(action));

                if (action == PayPalApplyAction.Settle)
                {
                    Assert.Equal(PayPalCaptureStatus.Completed, status);
                }
            }
        }
    }
}

/// <summary>
/// The recovery job's backoff (doc 08 §4.2 step 5): a first look two minutes after the claim, then gaps of 2,
/// 5, 15, 30 and 60 minutes and 60 for ever, with no stored counter.
/// </summary>
public sealed class PayPalRecoveryScheduleTests
{
    private static readonly DateTime Started = new(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_first_look_is_two_minutes_after_the_claim()
    {
        Assert.Equal(Started.AddMinutes(2), PayPalRecoverySchedule.NextAttemptAtUtc(Started, null));
        Assert.False(PayPalRecoverySchedule.IsDue(Started, null, Started.AddMinutes(1)));
        Assert.True(PayPalRecoverySchedule.IsDue(Started, null, Started.AddMinutes(2)));
    }

    /// <summary>The ladder: after an attempt at each rung, the next is the stated gap later.</summary>
    [Theory]
    [InlineData(2, 4)]
    [InlineData(4, 9)]
    [InlineData(9, 24)]
    [InlineData(24, 54)]
    [InlineData(54, 114)]
    [InlineData(114, 174)]
    [InlineData(174, 234)]
    public void Each_attempt_pushes_the_next_out_by_the_next_gap(int attemptMinute, int nextMinute)
    {
        Assert.Equal(
            Started.AddMinutes(nextMinute),
            PayPalRecoverySchedule.NextAttemptAtUtc(Started, Started.AddMinutes(attemptMinute)));
    }

    /// <summary>A job that ran a little late skips a rung and never repeats one.</summary>
    [Fact]
    public void An_attempt_a_little_after_its_rung_still_moves_on()
    {
        Assert.Equal(
            Started.AddMinutes(9),
            PayPalRecoverySchedule.NextAttemptAtUtc(Started, Started.AddMinutes(4).AddSeconds(30)));
    }

    [Fact]
    public void An_attempt_recorded_before_the_claim_is_ignored()
    {
        // A stale UpdatedAtUtc from the claim itself, or earlier, is "not yet asked".
        Assert.Equal(Started.AddMinutes(2), PayPalRecoverySchedule.NextAttemptAtUtc(Started, Started));
        Assert.Equal(Started.AddMinutes(2), PayPalRecoverySchedule.NextAttemptAtUtc(Started, Started.AddMinutes(-3)));
    }

    [Fact]
    public void A_payment_is_not_due_until_its_rung_and_is_due_from_then()
    {
        var lastAttempt = Started.AddMinutes(9);

        Assert.False(PayPalRecoverySchedule.IsDue(Started, lastAttempt, Started.AddMinutes(23)));
        Assert.True(PayPalRecoverySchedule.IsDue(Started, lastAttempt, Started.AddMinutes(24)));
    }

    [Fact]
    public void The_steady_state_is_one_attempt_an_hour()
    {
        var attempt = Started.AddHours(10);
        var next = PayPalRecoverySchedule.NextAttemptAtUtc(Started, attempt);

        Assert.True(next > attempt);
        Assert.True(next - attempt <= TimeSpan.FromMinutes(60));
    }

    [Theory]
    [InlineData(23, 59, false)]
    [InlineData(24, 0, true)]
    [InlineData(30, 0, true)]
    public void The_window_closes_after_the_recovery_hours(int hours, int minutes, bool closed)
    {
        var now = Started.AddHours(hours).AddMinutes(minutes);

        Assert.Equal(closed, PayPalRecoverySchedule.WindowHasClosed(Started, now, 24));
    }

    [Fact]
    public void The_window_is_the_settings_own()
    {
        Assert.True(PayPalRecoverySchedule.WindowHasClosed(Started, Started.AddHours(2), 2));
        Assert.False(PayPalRecoverySchedule.WindowHasClosed(Started, Started.AddHours(2), 3));
    }

    [Fact]
    public void A_payment_is_not_looked_at_until_it_is_two_minutes_old()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), PayPalRecoverySchedule.MinimumAge);
    }
}
