using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpencerDenim.Infrastructure.Sales.Payments.PayPal;

namespace SpencerDenim.Infrastructure.Sales.Payments;

/// <summary>
/// The shape <c>ExpireUnpaidOrdersService</c> established for a job that runs on a timer, written once for the
/// two payment sweeps: a <see cref="PeriodicTimer"/>, a scope per pass, and a loop that <b>never takes the host
/// down and never goes quiet</b>.
/// <para>
/// An exception out of <see cref="ExecuteAsync"/> faults the host, so every pass is wrapped; and a job that
/// logs its failure once per cycle for a day is a job whose log nobody reads, so a repeated failure drops to
/// a one-line warning after the first and resets on the first success. The first pass waits one interval
/// rather than running at startup, for the seeder's reason: starting work before the host is listening is what
/// made the readiness signal unreachable for a minute (W3-02 round 4).
/// </para>
/// <para>
/// <b>No lease and no leader election</b> — <c>ops.JobLeases</c> is specified and not built, and neither sweep
/// needs it: each claims what it works on with a conditional statement, so two passes that overlap cost a few
/// wasted claims and never a double apply.
/// </para>
/// </summary>
internal abstract partial class PeriodicSweepService(
    IServiceScopeFactory scopeFactory,
    ILogger logger,
    TimeSpan interval,
    string jobName) : BackgroundService
{
    private int _consecutiveFailures;

    /// <summary>The logger, for a derived job's own summary line.</summary>
    protected ILogger Logger { get; } = logger;

    /// <summary>One pass, inside its own scope.</summary>
    protected abstract Task SweepAsync(IServiceProvider services, CancellationToken stoppingToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);

        while (await WaitAsync(timer, stoppingToken))
        {
            await RunAsync(stoppingToken);
        }
    }

    /// <summary>Waits for the next tick, answering false when the host is stopping.</summary>
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

    /// <summary>Runs one pass, here so a test can drive a sweep without waiting for the timer.</summary>
    internal async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await SweepAsync(scope.ServiceProvider, stoppingToken);
            _consecutiveFailures = 0;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down between the read and the first claim. Nothing to say.
        }
        catch (Exception exception)
        {
            var failures = Interlocked.Increment(ref _consecutiveFailures);

            if (failures == 1)
            {
                LogSweepFailed(Logger, jobName, exception);
            }
            else
            {
                LogSweepStillFailing(Logger, jobName, failures);
            }
        }
    }

    [LoggerMessage(
        EventId = 3580,
        Level = LogLevel.Error,
        Message = "The {Job} sweep could not run. The host stays up and the next attempt is on schedule.")]
    private static partial void LogSweepFailed(ILogger logger, string job, Exception exception);

    [LoggerMessage(
        EventId = 3581,
        Level = LogLevel.Warning,
        Message = "The {Job} sweep has now failed {Failures} times in a row. The first failure was logged with its exception.")]
    private static partial void LogSweepStillFailing(ILogger logger, string job, int failures);
}

/// <summary>
/// <c>RecoverPayPalCaptures</c> (doc 08 §4.2 step 5, doc 11 P8 step 8): every two minutes, look again at every
/// capture that has been in flight for more than two minutes, so a timeout between claiming and hearing back
/// never strands a customer's money.
/// </summary>
internal sealed partial class RecoverPayPalCapturesService(
    IServiceScopeFactory scopeFactory,
    ILogger<RecoverPayPalCapturesService> logger)
    : PeriodicSweepService(scopeFactory, logger, Interval, "RecoverPayPalCaptures")
{
    /// <summary>Doc 08 §4.2 step 5: "runs every 2 min".</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    protected override async Task SweepAsync(IServiceProvider services, CancellationToken stoppingToken)
    {
        var report = await services.GetRequiredService<PayPalCaptureRecovery>().RunOnceAsync(stoppingToken);

        if (report.Examined > 0)
        {
            LogSwept(
                Logger,
                report.Examined,
                report.Applied,
                report.Failed,
                report.Released,
                report.StillUnreachable,
                report.GaveUp,
                report.NotDue,
                report.Errors);
        }
    }

    [LoggerMessage(
        EventId = 3590,
        Level = LogLevel.Information,
        Message = "PayPal capture recovery looked at {Examined} stuck payment(s): {Applied} applied, {Failed} "
            + "failed, {Released} handed back, {Unreachable} still unreachable, {GaveUp} gave up, {NotDue} not "
            + "yet due, {Errors} errors.")]
    private static partial void LogSwept(
        ILogger logger,
        int examined,
        int applied,
        int failed,
        int released,
        int unreachable,
        int gaveUp,
        int notDue,
        int errors);
}

/// <summary>
/// <c>RetryPaymentEvents</c> (doc 08 §3.8): every two minutes, apply again the stored provider events whose
/// processing did not finish — the ones a crash left Received and the ones that failed and are due. A stored
/// event is first given two minutes (<see cref="Application.Sales.Payments.Abstractions.PaymentEventRetry"/>)
/// so the request that stored it can finish applying it.
/// </summary>
internal sealed partial class RetryPaymentEventsService(
    IServiceScopeFactory scopeFactory,
    ILogger<RetryPaymentEventsService> logger)
    : PeriodicSweepService(scopeFactory, logger, Interval, "RetryPaymentEvents")
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    protected override async Task SweepAsync(IServiceProvider services, CancellationToken stoppingToken)
    {
        var report = await services.GetRequiredService<PaymentEventRetryRunner>().RunOnceAsync(stoppingToken);

        if (report.Claimed > 0)
        {
            LogSwept(Logger, report.Claimed, report.Processed, report.Ignored, report.Failed);
        }
    }

    [LoggerMessage(
        EventId = 3591,
        Level = LogLevel.Information,
        Message = "Payment event retry picked up {Claimed} event(s): {Processed} applied, {Ignored} needed "
            + "nothing, {Failed} failed again.")]
    private static partial void LogSwept(ILogger logger, int claimed, int processed, int ignored, int failed);
}
