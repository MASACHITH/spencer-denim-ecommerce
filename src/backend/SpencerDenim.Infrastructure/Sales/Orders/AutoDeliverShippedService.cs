using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Infrastructure.Sales.Orders;

/// <summary>
/// <c>AutoDeliverShipped</c> (doc 11 P9 step 8, doc 08 §4.1 D4): once a day, close orders that shipped
/// more than <c>Orders.AutoDeliverAfterDays</c> ago and that nobody has confirmed.
/// <para>
/// <b>It must never take the host down and it must never go quiet.</b> Both halves are lessons
/// <c>CatalogSeedHostedService</c> paid for across three rounds and <c>ExpireUnpaidOrdersService</c>
/// inherited: an exception out of <see cref="ExecuteAsync"/> faults the host, so every cycle is wrapped;
/// and a job that logs its failure once a cycle for a day is a job whose log nobody reads, so a repeated
/// failure drops to <see cref="LogLevel.Warning"/> after the first and resets on the first success.
/// </para>
/// <para>
/// <b>No lease and no leader election.</b> <c>ops.JobLeases</c> is specified (doc 08 §3.8) and does not
/// exist, and this job does not need one: <see cref="IAutoDeliverStore.AutoDeliverAsync"/>'s conditional
/// claim means that of any number of instances racing for one order exactly one moves it. Two instances
/// cost a few wasted claims, never a double write.
/// </para>
/// <para>
/// <b>Daily is doc 08 §4.1's own word</b> ("System (<c>AutoDeliverShipped</c>, daily)"), and it is a
/// constant rather than a setting for the reason <c>ExpireUnpaidOrdersService</c> records: the <i>age</i> is
/// configurable, because that is the part a shop has an opinion about, and how often a job sweeps cannot
/// make anything happen sooner than its own cut-off allows.
/// </para>
/// </summary>
internal sealed partial class AutoDeliverShippedService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<AutoDeliverShippedService> logger) : BackgroundService
{
    /// <summary>Doc 08 §4.1: "daily".</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>
    /// A ceiling per pass, matching the expiry job's 50.
    /// <para>
    /// A backlog of more than fifty simply takes more than one pass — which for a daily job means
    /// tomorrow, and that is acceptable: an order already seven days past dispatch is not made worse by an
    /// eighth. The ceiling is what stops a one-off import holding row locks for minutes while live
    /// transitions wait behind it.
    /// </para>
    /// </summary>
    private const int BatchSize = 50;

    private int _consecutiveFailures;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first sweep waits one interval rather than running at start-up. Starting work before the host
        // is listening is what made the seeder's readiness signal unreachable for a minute (W3-02 round 4),
        // and nothing here is urgent: an order cannot become due until seven days after it shipped.
        //
        // **The consequence is worth stating, because it is a real limitation.** A host that restarts more
        // often than once a day never reaches a sweep. Doc 08 §3.8's ops.JobLeases is the mechanism that
        // would fix it properly — a persisted "last run" the job could catch up from — and it does not
        // exist. In the meantime the staff path (Mark as delivered) is the primary one and this job is the
        // safety net, which is the order doc 11 P9 step 8 puts them in.
        using var timer = new PeriodicTimer(Interval);

        while (await WaitAsync(timer, stoppingToken))
        {
            await SweepAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Waits for the next tick, answering <see langword="false"/> when the host is stopping.
    /// </summary>
    /// <remarks>
    /// <see cref="OperationCanceledException"/> is swallowed rather than allowed out: it is the ordinary
    /// way a <see cref="PeriodicTimer"/> ends on shutdown, and letting it propagate logs a cancelled task
    /// out of a perfectly healthy container stop.
    /// </remarks>
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

    /// <summary>One pass: read the setting, find what is due, then move each order in its own transaction.</summary>
    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<IOrderSettingsReader>();

            if (await settings.ReadAutoDeliverAfterDaysAsync(stoppingToken) is not { } days)
            {
                // **The pass is skipped rather than run on an assumed 7.** The key is seeded with doc 08
                // §3.8's 7, so a null means somebody deleted the row or put something that is not a number
                // in it — and marking orders Delivered on a figure no screen can show is exactly the kind
                // of silent decision about money's worth of orders this project does not make. Logged once
                // loudly, because it is a configuration fault a person has to fix.
                LogSettingUnusable(SettingKeys.Orders.AutoDeliverAfterDays);
                return;
            }

            var store = scope.ServiceProvider.GetRequiredService<IAutoDeliverStore>();

            var now = clock.UtcNow.UtcDateTime;
            var shippedBefore = now.AddDays(-days);

            var due = await store.FindDueOrdersAsync(BatchSize, shippedBefore, stoppingToken);

            if (due.Count == 0)
            {
                _consecutiveFailures = 0;
                return;
            }

            var delivered = 0;

            // One transaction per order (the same shape as the expiry job): a failure on the eleventh does
            // not undo the first ten, and a long batch never holds one transaction open across all of them.
            foreach (var orderId in due)
            {
                try
                {
                    if (await store.AutoDeliverAsync(orderId, shippedBefore, now, stoppingToken))
                    {
                        delivered++;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One order's failure does not stop the pass. There is nothing in this transition that
                    // can legitimately fail — it writes one status and one history row — so a throw here is
                    // a real fault worth a line each time rather than a routine race.
                    LogOrderFailed(exception, orderId);
                }
            }

            // Logged at Information, because "the system closed nine orders last night" is a fact a shop
            // may need to explain to a customer and there is no other record of the pass as a whole. The
            // per-order record is the history row.
            LogSwept(delivered, due.Count, days);

            _consecutiveFailures = 0;
        }
        catch (OperationCanceledException)
        {
            // The host is stopping. Not a failure.
        }
        catch (Exception exception)
        {
            _consecutiveFailures++;

            if (_consecutiveFailures == 1)
            {
                LogSweepFailed(exception);
            }
            else
            {
                LogSweepStillFailing(exception, _consecutiveFailures);
            }
        }
    }

    [LoggerMessage(
        EventId = 6401,
        Level = LogLevel.Information,
        Message = "Auto-delivered {Delivered} of {Candidates} order(s) shipped more than {Days} day(s) ago.")]
    private partial void LogSwept(int delivered, int candidates, int days);

    [LoggerMessage(
        EventId = 6402,
        Level = LogLevel.Error,
        Message = "Setting {Key} is missing or out of range, so no order was auto-delivered. Orders stay "
            + "Shipped until staff confirm them.")]
    private partial void LogSettingUnusable(string key);

    [LoggerMessage(
        EventId = 6403,
        Level = LogLevel.Error,
        Message = "Could not auto-deliver order {OrderId}.")]
    private partial void LogOrderFailed(Exception exception, int orderId);

    [LoggerMessage(
        EventId = 6404,
        Level = LogLevel.Error,
        Message = "The auto-deliver sweep failed.")]
    private partial void LogSweepFailed(Exception exception);

    [LoggerMessage(
        EventId = 6405,
        Level = LogLevel.Warning,
        Message = "The auto-deliver sweep has now failed {Attempts} times in a row.")]
    private partial void LogSweepStillFailing(Exception exception, int attempts);
}
