using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Sales.Payments.PayHere;

namespace SpencerDenim.Infrastructure.Sales.Payments.PayHere;

/// <summary>
/// <c>RetryPaymentEvents</c> for PayHere (doc 11 P8b step 5): every minute, finishes applying the verified
/// notifications whose first delivery died or threw (see <see cref="PayHereRetrySweep"/>).
/// <para>
/// <b>It must never take the host down and it must never go quiet</b> — the lessons of
/// <see cref="ExpireUnpaidOrdersService"/>, followed line for line: every pass is wrapped, a repeated failure
/// drops to a warning after the first and resets on the first success, and a pass that has nothing to do
/// says nothing.
/// </para>
/// <para>
/// <b>PayHere-only, and a stopgap in name only.</b> The groundwork gave the event store the
/// <c>Received</c>/<c>Failed</c> + retry-time semantics but no sweep. If a provider-neutral one is added later
/// this service goes away and nothing else changes, because the work is in <see cref="PayHereRetrySweep"/>.
/// </para>
/// </summary>
internal sealed partial class RetryPayHereEventsService(
    IServiceScopeFactory scopeFactory,
    ILogger<RetryPayHereEventsService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private int _consecutiveFailures;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first pass waits one interval, for ExpireUnpaidOrders' reason: work started before the host
        // is listening delays its readiness, and nothing here is that urgent.
        using var timer = new PeriodicTimer(Interval);

        while (await WaitAsync(timer, stoppingToken))
        {
            await SweepAsync(stoppingToken);
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>One pass. <c>internal</c> so a test can run it without a timer.</summary>
    internal async Task SweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var sweep = scope.ServiceProvider.GetRequiredService<PayHereRetrySweep>();

            var result = await sweep.RunAsync(stoppingToken);

            _consecutiveFailures = 0;

            if (result.Found > 0)
            {
                LogSwept(logger, result.Found, result.Claimed, result.Applied, result.Failed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down mid-pass. Nothing to say.
        }
        catch (Exception exception)
        {
            var failures = Interlocked.Increment(ref _consecutiveFailures);

            if (failures == 1)
            {
                LogSweepFailed(logger, exception);
            }
            else
            {
                LogSweepStillFailing(logger, failures);
            }
        }
    }

    [LoggerMessage(
        EventId = 3501,
        Level = LogLevel.Information,
        Message = "PayHere retry sweep: {Found} notification(s) were due, {Claimed} taken, {Applied} applied "
            + "or ignored, {Failed} failed again.")]
    private static partial void LogSwept(ILogger logger, int found, int claimed, int applied, int failed);

    [LoggerMessage(
        EventId = 3502,
        Level = LogLevel.Error,
        Message = "The PayHere retry sweep could not run, so a verified payment notification that failed to "
            + "apply is waiting. The next pass will try again.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 3503,
        Level = LogLevel.Warning,
        Message = "The PayHere retry sweep has failed {Failures} passes in a row.")]
    private static partial void LogSweepStillFailing(ILogger logger, int failures);
}
