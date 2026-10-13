using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;

namespace SpencerDenim.Infrastructure.Marketing;

/// <summary>
/// <c>RefreshCampaignPricing</c> (doc 11 P3 step 3, doc 11 line 51, doc 08 §7.4): every five minutes,
/// recompute the stored price summary for the products of every campaign that started or ended since the
/// last pass, so listings, the Sale page (DEPT-03) and the Sale homepage block (HOME-07) catch up with a
/// boundary.
/// <para>
/// <b>Five minutes is the document's own figure and the staleness it promises.</b> Doc 08 §7.4 gives
/// these columns "≤ 5 min at campaign boundaries" and says in the same row that checkout always prices
/// live. So this job is never on the revenue path: the worst thing a missed pass can do is leave a Sale
/// badge up for a few minutes longer, and a shopper who clicks it is charged the live price.
/// </para>
/// <para>
/// <b>It must never take the host down and it must never go quiet.</b> Both halves are lessons
/// <c>CatalogSeedHostedService</c> paid for and <c>ExpireUnpaidOrdersService</c> and
/// <c>AutoDeliverShippedService</c> inherited: an exception out of <see cref="ExecuteAsync"/> faults the
/// host, so every pass is wrapped; and a repeated failure logged identically every five minutes is a log
/// nobody reads, so it drops to <see cref="LogLevel.Warning"/> after the first and resets on a success.
/// </para>
/// <para>
/// <b>No lease and no leader election.</b> <c>ops.JobLeases</c> is specified (doc 08 §3.8) and does not
/// exist, and this job does not need one: every statement the refresher runs recomputes the whole answer
/// from the source tables, so two instances racing for one product write the same values. Two instances
/// cost wasted work, never a wrong figure — the same argument the other two jobs make.
/// </para>
/// </summary>
internal sealed partial class RefreshCampaignPricingService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<RefreshCampaignPricingService> logger) : BackgroundService
{
    /// <summary>Doc 08 §7.4 and doc 11 line 51: "every 5 min".</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How far back the <b>first</b> pass after start-up looks for boundaries.
    /// </summary>
    /// <remarks>
    /// <b>This is the mitigation for not having <c>ops.JobLeases</c>, and it is a mitigation rather than a
    /// fix.</b> The cursor below lives in memory, so a restart forgets where the last pass got to and a
    /// boundary that fell during downtime would never be picked up — the same limitation
    /// <c>AutoDeliverShippedService</c> records about itself. A day-wide first window covers any realistic
    /// restart and costs almost nothing: it selects the campaigns with a boundary in the last 24 hours,
    /// which is a handful, and recomputing a product whose summary is already right writes the same
    /// values. A permanent cursor is what <c>ops.JobLeases</c> would give, and the honest statement is
    /// that this is a wide retry and not a persisted position.
    /// </remarks>
    private static readonly TimeSpan FirstPassLookback = TimeSpan.FromHours(24);

    /// <summary>
    /// The end of the last <b>successful</b> pass. Not advanced by a failed one, so the window widens and
    /// the next pass retries the boundaries the failure may have missed.
    /// </summary>
    private DateTime? _lastSuccessfulPassUtc;

    private int _consecutiveFailures;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first pass waits one interval rather than running at start-up: starting work before the
        // host is listening is what made the seeder's readiness signal unreachable for a minute (W3-02
        // round 4). Nothing here is urgent — a campaign boundary is already allowed to take five minutes
        // to show — and `FirstPassLookback` is what stops the wait costing a missed boundary.
        using var timer = new PeriodicTimer(Interval);

        while (await WaitAsync(timer, stoppingToken))
        {
            await RefreshAsync(stoppingToken);
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

    private async Task RefreshAsync(CancellationToken stoppingToken)
    {
        var until = clock.UtcNow.UtcDateTime;
        var since = _lastSuccessfulPassUtc ?? until - FirstPassLookback;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var refresher = scope.ServiceProvider.GetRequiredService<ICampaignPricingRefresher>();

            var pass = await refresher.RefreshBoundariesAsync(since, until, stoppingToken);

            // Advanced only now, and only on the way out of a successful call. A failure leaves it where
            // it was, so the next window is (same start, later end) and nothing is skipped.
            _lastSuccessfulPassUtc = until;
            _consecutiveFailures = 0;

            if (pass.Products > 0)
            {
                // Information, because "the shop's Sale page changed at 09:05 because two campaigns
                // ended" is a fact a client may need explained, and there is no other record of the pass.
                LogRefreshed(pass.Products, pass.Batches, pass.RowsWritten, since, until);
            }
        }
        catch (OperationCanceledException)
        {
            // The host is stopping. Not a failure, and the cursor stays put.
        }
        catch (Exception exception)
        {
            _consecutiveFailures++;

            if (_consecutiveFailures == 1)
            {
                LogPassFailed(exception, since, until);
            }
            else
            {
                LogPassStillFailing(exception, _consecutiveFailures, since);
            }
        }
    }

    [LoggerMessage(
        EventId = 6601,
        Level = LogLevel.Information,
        Message = "Refreshed the stored sale price of {Products} product(s) in {Batches} batch(es), "
            + "writing {Rows} ProductStats row(s), for campaign boundaries after {Since:O} up to {Until:O}.")]
    private partial void LogRefreshed(int products, int batches, int rows, DateTime since, DateTime until);

    [LoggerMessage(
        EventId = 6602,
        Level = LogLevel.Error,
        Message = "The campaign pricing refresh failed for boundaries after {Since:O} up to {Until:O}. "
            + "Listing prices and the Sale page may be stale until a later pass succeeds; the bag and "
            + "checkout are unaffected because they price live.")]
    private partial void LogPassFailed(Exception exception, DateTime since, DateTime until);

    [LoggerMessage(
        EventId = 6603,
        Level = LogLevel.Warning,
        Message = "The campaign pricing refresh has now failed {Attempts} times in a row; it is still "
            + "retrying every boundary after {Since:O}.")]
    private partial void LogPassStillFailing(Exception exception, int attempts, DateTime since);
}
