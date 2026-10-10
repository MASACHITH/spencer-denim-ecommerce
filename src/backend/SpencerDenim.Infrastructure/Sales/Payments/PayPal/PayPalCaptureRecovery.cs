using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Payments.PayPal;

namespace SpencerDenim.Infrastructure.Sales.Payments.PayPal;

/// <summary>What one pass of <c>RecoverPayPalCaptures</c> did.</summary>
/// <param name="Examined">Payments found Capturing for more than two minutes.</param>
/// <param name="NotDue">Left alone: PayPal was unreachable last time and the backoff has not run out.</param>
/// <param name="Applied">PayPal's answer was applied.</param>
/// <param name="Failed">Failed (voided, not found, not payable).</param>
/// <param name="Released">Handed back to Initiated: the buyer never approved.</param>
/// <param name="StillUnreachable">PayPal could not be reached; tried again later.</param>
/// <param name="GaveUp">The recovery window closed: RECOVERY_TIMEOUT.</param>
/// <param name="Errors">Payments whose recovery threw; they stay Capturing and are tried again next pass.</param>
public sealed record PayPalRecoveryReport(
    int Examined,
    int NotDue,
    int Applied,
    int Failed,
    int Released,
    int StillUnreachable,
    int GaveUp,
    int Errors);

/// <summary>
/// One pass of the <c>RecoverPayPalCaptures</c> job (doc 08 §4.2 step 5, N1; doc 11 P8 step 8): every payment
/// that has been Capturing for more than two minutes is looked at again — PayPal is asked what the order is,
/// and the answer is applied exactly as the buyer's own return would have applied it.
/// <para>
/// <b>Two overlapping passes are harmless, and the SQL is what makes them so.</b> There is no
/// <c>ops.JobLeases</c> table, and none is needed: a second pass that finds the same payment asks PayPal the
/// same question, and every state change that follows is a conditional <c>UPDATE</c> on the payment's current
/// status — the first pass to apply wins, the second finds the payment moved and changes nothing. A repeat
/// capture goes out under the <b>same</b> <c>PayPal-Request-Id</c>, so PayPal itself answers it with the
/// first one's result.
/// </para>
/// <para>
/// <b>Backoff without a counter</b> — see <see cref="PayPalRecoverySchedule"/>: the schedule is a function of
/// <c>CaptureStartedAtUtc</c> and the time PayPal was last found unreachable, so no column is added.
/// </para>
/// </summary>
internal sealed partial class PayPalCaptureRecovery(
    IPayPalPaymentStore payments,
    PayPalCaptureProcessor processor,
    IClock clock,
    ILogger<PayPalCaptureRecovery> logger)
{
    /// <summary>
    /// How many stuck payments one pass takes. Stuck captures are rare; the ceiling stops one bad hour's
    /// backlog turning into a single pass that holds the box for minutes.
    /// </summary>
    public const int BatchSize = 25;

    public async Task<PayPalRecoveryReport> RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow.UtcDateTime;

        var stuck = await payments.FindCapturingAsync(
            now - PayPalRecoverySchedule.MinimumAge,
            BatchSize,
            cancellationToken);

        int notDue = 0, applied = 0, failed = 0, released = 0, unreachable = 0, gaveUp = 0, errors = 0;

        foreach (var payment in stuck)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Re-read per payment: a pass over several can take a while, and the backoff is judged against
            // the time the payment is actually looked at.
            var current = clock.UtcNow.UtcDateTime;

            if (!PayPalRecoverySchedule.IsDue(payment.CaptureStartedAtUtc, payment.LastAttemptAtUtc, current))
            {
                notDue++;
                continue;
            }

            try
            {
                switch (await processor.RecoverAsync(payment, cancellationToken))
                {
                    case PayPalRecoveryResult.Applied:
                        applied++;
                        break;
                    case PayPalRecoveryResult.Failed:
                        failed++;
                        break;
                    case PayPalRecoveryResult.Released:
                        released++;
                        break;
                    case PayPalRecoveryResult.StillUnreachable:
                        unreachable++;
                        break;
                    case PayPalRecoveryResult.GaveUp:
                        gaveUp++;
                        break;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One payment's trouble must not stop the others; it stays Capturing and is found again.
                errors++;
                LogRecoveryFailed(logger, payment.PaymentId, exception);
            }
        }

        return new PayPalRecoveryReport(stuck.Count, notDue, applied, failed, released, unreachable, gaveUp, errors);
    }

    [LoggerMessage(
        EventId = 3570,
        Level = LogLevel.Error,
        Message = "Recovering PayPal payment {PaymentId} threw. It stays Capturing, its order stays locked "
            + "against expiry, and the next pass will try again.")]
    private static partial void LogRecoveryFailed(ILogger logger, int paymentId, Exception exception);
}
