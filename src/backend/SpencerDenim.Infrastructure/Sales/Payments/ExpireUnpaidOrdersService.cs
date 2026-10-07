using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Payments.Abstractions;

namespace SpencerDenim.Infrastructure.Sales.Payments;

/// <summary>
/// <c>ExpireUnpaidOrders</c> (doc 08 §4.1 / §5.6, doc 11 P7 step 7 and P10 flow B): every five minutes,
/// release the stock of orders nobody paid for.
/// <para>
/// <b>It must never take the host down, and it must never go quiet.</b> Both halves are lessons
/// <c>CatalogSeedHostedService</c> paid for across three rounds. An exception out of
/// <see cref="ExecuteAsync"/> faults the host, so every cycle is wrapped; and a job that logs its failure
/// once per cycle for a day is a job whose log nobody reads, so a repeated failure drops to
/// <see cref="LogLevel.Warning"/> after the first and resets on the first success.
/// </para>
/// <para>
/// <b>No lease and no leader election.</b> <c>ops.JobLeases</c> is specified (doc 08 §3.8) and does not
/// exist, and <see cref="IOrderExpiryStore.ExpireAsync"/> does not need one: its conditional claim means
/// that of any number of instances racing for one order exactly one expires it and the others are told to
/// skip. Two instances therefore cost a few wasted claims, never a double release.
/// </para>
/// <para>
/// <b>Five minutes is doc 08 §5.6's figure and 50 is its batch.</b> They are constants rather than
/// settings because doc 08 §3.8's settings list has neither — the <i>deadline</i> is configurable
/// (<c>Orders.BankTransferExpiryHours</c>), which is the part a shop has an opinion about; how often a job
/// sweeps is an implementation detail, and a shorter interval would not expire anything sooner than its
/// own deadline allows.
/// </para>
/// </summary>
internal sealed partial class ExpireUnpaidOrdersService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<ExpireUnpaidOrdersService> logger) : BackgroundService
{
    /// <summary>Doc 08 §5.6: "Every 5 min".</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Doc 08 §5.6 step 1's <c>TOP (50)</c>.
    /// <para>
    /// A ceiling per run rather than "everything due", so one bad afternoon's backlog cannot turn into a
    /// single pass holding stock-row locks for minutes while live checkouts wait behind it. A backlog of
    /// more than fifty simply takes more than one pass, five minutes apart.
    /// </para>
    /// </summary>
    private const int BatchSize = 50;

    /// <summary>
    /// How many consecutive failures have been logged, so the first one is loud and the rest are not.
    /// </summary>
    private int _consecutiveFailures;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first sweep waits one interval rather than running at startup. Starting work before the host
        // is listening is what made the seeder's readiness signal unreachable for a minute (W3-02 round 4),
        // and nothing is more urgent than five minutes here: an order cannot be due until its own deadline
        // has passed, which is hours away for every order placed since the last restart.
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
    /// <b><see cref="OperationCanceledException"/> is swallowed rather than allowed out.</b> It is the
    /// ordinary way a <see cref="PeriodicTimer"/> ends on shutdown, and letting it propagate logs a
    /// cancelled task out of a perfectly healthy container stop — the same "a normal operation produces an
    /// alarming error" shape round 3 of W3-02 had to remove from the seeder.
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

    /// <summary>One pass: find what is due, then expire each one in its own transaction.</summary>
    /// <remarks>
    /// <b>One scope for the pass, one transaction per order</b> (doc 08 §5.6 step 1). The scope exists
    /// because <see cref="IOrderExpiryStore"/> is scoped, as everything over <c>AppDbContext</c> is; the
    /// per-order transaction is doc 08's instruction, so a failure on the eleventh order does not undo the
    /// first ten and a long batch never holds one transaction open across all of them.
    /// <para>
    /// <b>A failure on one order does not stop the pass.</b> The loop logs and moves on, because the one
    /// thing that can fail here is a drifted stock row for a single variant (see
    /// <c>OrderExpiryStore.ReleaseStockAsync</c>) and letting that one order block every other order's
    /// release for ever would turn a bookkeeping fault into the stock-squatting problem this job exists to
    /// end (#107).
    /// </para>
    /// </remarks>
    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IOrderExpiryStore>();

            var now = clock.UtcNow.UtcDateTime;
            var due = await store.FindDueOrdersAsync(BatchSize, now, stoppingToken);

            if (due.Count == 0)
            {
                _consecutiveFailures = 0;
                return;
            }

            var expired = 0;
            var skipped = 0;
            var failed = 0;

            foreach (var orderId in due)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    // `now` is re-read per order, not reused from the batch: a pass over fifty orders can
                    // take long enough that an order due in two seconds when the batch was read is due by
                    // the time its claim runs, and the claim's own `PaymentDueAtUtc < @now` must be
                    // evaluated against the real present or it would refuse an order that is genuinely due.
                    if (await store.ExpireAsync(orderId, clock.UtcNow.UtcDateTime, stoppingToken))
                    {
                        expired++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    failed++;
                    LogOrderFailed(logger, orderId, exception);
                }
            }

            _consecutiveFailures = 0;
            LogSwept(logger, expired, skipped, failed);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down between the read and the first claim. Nothing to say.
        }
        catch (Exception exception)
        {
            // The pass itself could not run — the database is unreachable, or the batch read threw. The
            // host stays up and the next tick tries again; what must not happen is a stack trace every
            // five minutes for a day, which is how a signal stops being one.
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
        EventId = 3301,
        Level = LogLevel.Information,
        Message = "Expired {Expired} unpaid order(s); {Skipped} were already settled or locked and "
            + "{Failed} could not be expired.")]
    private static partial void LogSwept(ILogger logger, int expired, int skipped, int failed);

    [LoggerMessage(
        EventId = 3302,
        Level = LogLevel.Error,
        Message = "Order {OrderId} is past its payment deadline but could not be expired, so its stock is "
            + "still reserved. This usually means inventory.StockLevels and the order's lines disagree "
            + "(doc 08 §5.6, §5.9). The next run will try again.")]
    private static partial void LogOrderFailed(ILogger logger, int orderId, Exception exception);

    [LoggerMessage(
        EventId = 3303,
        Level = LogLevel.Error,
        Message = "The unpaid-order expiry sweep could not run, so no stock is being released from orders "
            + "that were never paid. The next attempt is in five minutes.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 3304,
        Level = LogLevel.Warning,
        Message = "The unpaid-order expiry sweep has now failed {Failures} times in a row. The first "
            + "failure was logged with its exception.")]
    private static partial void LogSweepStillFailing(ILogger logger, int failures);
}
