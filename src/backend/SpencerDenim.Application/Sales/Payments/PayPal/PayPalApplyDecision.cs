using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Payments.PayPal;

/// <summary>What to do about a PayPal capture result, given where the payment is now.</summary>
public enum PayPalApplyAction
{
    /// <summary>The money is ours: settle the order, or put it on record as an exception (doc 08 §4.2 step 3).</summary>
    Settle = 0,

    /// <summary>PayPal is holding the capture: payment Pending, lock cleared, hold extended (step 4, N16).</summary>
    MarkPending = 1,

    /// <summary>PayPal refused it: payment Failed, lock cleared, and a Pending hold is cut back (step 4, R2).</summary>
    MarkDeclined = 2,

    /// <summary>
    /// Nothing to do. The payment is already past what this news could change — a duplicate, or an event
    /// that arrived out of order. The event is marked Ignored.
    /// </summary>
    Ignore = 3,

    /// <summary>
    /// A refund or a reversal: the event is stored for reconciliation and changes nothing. Doc 08 §4.3's
    /// dashboard-refund handling (the Refunds row, the order's Refunded transition, the alert) belongs to the
    /// refund task, not to this one.
    /// </summary>
    RecordOnly = 4,
}

/// <summary>
/// The one decision table behind every way PayPal tells the shop what happened to a capture: the capture
/// response, a verified webhook and the recovery job all ask it (doc 08 §4.2 step 3, step 4 and step 6; doc 11
/// P8 step 6). A pure function of two values, so the table is tested whole and the three callers cannot drift
/// apart.
/// <para>
/// <b>It decides what to <i>attempt</i>, not what is true.</b> The payment's status was read a moment before,
/// and the store call that follows re-checks it inside its own transaction (a conditional <c>UPDATE</c>
/// whose row count is the answer). So a stale read can only ever cost a no-op, never a wrong write.
/// </para>
/// </summary>
public static class PayPalApplyDecision
{
    /// <summary>
    /// Doc 08 §4.2 step 3.1 (R1): a COMPLETED capture is accepted for any payment not already captured —
    /// Initiated, Pending, Capturing, Failed, Cancelled or Expired. Only Succeeded, CapturedRequiresRefund,
    /// PartiallyRefunded and Refunded count as duplicates.
    /// </summary>
    public static bool AcceptsCompleted(PaymentStatus current) =>
        current is PaymentStatus.Initiated
            or PaymentStatus.Pending
            or PaymentStatus.Capturing
            or PaymentStatus.Failed
            or PaymentStatus.Cancelled
            or PaymentStatus.Expired;

    /// <summary>The action for a capture of status <paramref name="capture"/> on a payment now at <paramref name="current"/>.</summary>
    public static PayPalApplyAction Decide(PaymentStatus current, PayPalCaptureStatus capture) =>
        capture switch
        {
            // R1: the late COMPLETED on a Failed or Cancelled payment is NOT a duplicate. Never Ignored,
            // never an endless retry (doc 08 §5.9 R1 test).
            PayPalCaptureStatus.Completed => AcceptsCompleted(current)
                ? PayPalApplyAction.Settle
                : PayPalApplyAction.Ignore,

            // PENDING can only move a payment that is still waiting on it. A PENDING that arrives after the
            // payment has been settled, failed or expired is out of order: the later news won.
            PayPalCaptureStatus.Pending => current is PaymentStatus.Initiated or PaymentStatus.Capturing
                ? PayPalApplyAction.MarkPending
                : PayPalApplyAction.Ignore,

            // A refusal only fails a payment that is still open. DENIED after COMPLETED is ignored: money
            // that has arrived does not un-arrive because a stale event says otherwise.
            PayPalCaptureStatus.Declined or PayPalCaptureStatus.Failed =>
                current is PaymentStatus.Initiated or PaymentStatus.Pending or PaymentStatus.Capturing
                    ? PayPalApplyAction.MarkDeclined
                    : PayPalApplyAction.Ignore,

            PayPalCaptureStatus.Refunded
                or PayPalCaptureStatus.PartiallyRefunded
                or PayPalCaptureStatus.Reversed => PayPalApplyAction.RecordOnly,

            _ => PayPalApplyAction.Ignore,
        };
}

/// <summary>
/// When the recovery job asks PayPal about a payment stuck in Capturing (doc 08 §4.2 step 5): the first time
/// two minutes after the claim, then after a growing gap — 2, 5, 15, 30 and then 60 minutes — until the
/// recovery window closes.
/// <para>
/// <b>No attempt counter is stored, because no column holds one and this task adds no migration.</b> The
/// schedule is a function of two instants that are already on the row: <c>CaptureStartedAtUtc</c> and
/// <c>UpdatedAtUtc</c>, which the job stamps each time PayPal turns out to be unreachable. The rungs below
/// are the cumulative times since the claim at which an attempt is due; the next one is the first rung after
/// the last attempt. A job that ran a little late skips a rung, never repeats one, and the arithmetic needs
/// nothing but the row.
/// </para>
/// </summary>
public static class PayPalRecoverySchedule
{
    /// <summary>A payment is not looked at until it has been Capturing this long (doc 08 §4.2 step 5).</summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(2);

    /// <summary>The gaps between successive attempts once PayPal has proved unreachable (doc 08 §4.2 step 5).</summary>
    private static readonly int[] GapMinutes = [2, 5, 15, 30, 60];

    /// <summary>The last gap, repeated for ever: "…, 60 min, …".</summary>
    private static readonly int SteadyGapMinutes = GapMinutes[^1];

    /// <summary>
    /// Whether an attempt is due at <paramref name="nowUtc"/>.
    /// </summary>
    /// <param name="startedAtUtc">When the capture was claimed.</param>
    /// <param name="lastAttemptUtc">
    /// When the job last found PayPal unreachable for this payment, or null / not after the claim if it has
    /// not yet been asked.
    /// </param>
    /// <param name="nowUtc">The time now.</param>
    public static bool IsDue(DateTime startedAtUtc, DateTime? lastAttemptUtc, DateTime nowUtc) =>
        nowUtc >= NextAttemptAtUtc(startedAtUtc, lastAttemptUtc);

    /// <summary>The instant the next attempt is due.</summary>
    public static DateTime NextAttemptAtUtc(DateTime startedAtUtc, DateTime? lastAttemptUtc)
    {
        // First look: two minutes after the claim.
        var elapsedAtLastAttempt = lastAttemptUtc is { } last && last > startedAtUtc
            ? last - startedAtUtc
            : TimeSpan.Zero;

        var rung = MinimumAge;
        var gap = 0;

        while (rung <= elapsedAtLastAttempt)
        {
            var step = gap < GapMinutes.Length ? GapMinutes[gap] : SteadyGapMinutes;
            rung += TimeSpan.FromMinutes(step);
            gap++;
        }

        return startedAtUtc + rung;
    }

    /// <summary>
    /// Whether the recovery window has closed: <c>CaptureRecoveryMaxHours</c> have passed since the claim
    /// (doc 08 §4.2 step 5, "after 24 h … RECOVERY_TIMEOUT").
    /// </summary>
    public static bool WindowHasClosed(DateTime startedAtUtc, DateTime nowUtc, int recoveryMaxHours) =>
        nowUtc - startedAtUtc >= TimeSpan.FromHours(recoveryMaxHours);
}
