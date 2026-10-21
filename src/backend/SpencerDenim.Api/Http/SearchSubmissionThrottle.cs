using System.Threading.RateLimiting;

namespace SpencerDenim.Api.Http;

/// <summary>
/// The search-submission beacon's rate limit (issue #78 tester pass), partitioned by the caller's address, the
/// same shape <see cref="NewsletterSignupThrottle"/> and <see cref="ContactEnquiryThrottle"/> use.
/// <para>
/// <b>Why the beacon needed one at all.</b> It has none of the other public write endpoints' natural brakes — no
/// email to validate, no order to place, no file to store — so nothing stopped a script sending any number of
/// submissions a second. Two things followed directly from that: a crafted term can be pushed onto the public
/// list with as few as five requests (closed separately, by <c>SearchTerms.HasUncheckedTokens</c>, because a
/// rate limit alone cannot tell a crafted phrase from five honest searches); and distinct junk terms can fill
/// <see cref="SearchStatsOptions.MaxBufferedTerms"/>'s buffer faster than a flush can drain it, which would start
/// dropping other shoppers' real searches.
/// </para>
/// <para>
/// <b>Thirty a minute, and the window matches the flush interval on purpose.</b> The beacon fires once per term
/// the results page shows — never on keystrokes — so a shopper's own browsing submits a handful of distinct
/// searches in a sitting, nowhere near thirty in sixty seconds. Lining the window up with
/// <see cref="SearchStatsOptions.DefaultFlushSeconds"/> means one address can never put more than thirty of the
/// buffer's entries in before the next flush empties it again — a budget to be measured against, not merely a
/// guess, and one far below <see cref="SearchStatsOptions.MaxBufferedTerms"/>'s default of 5,000.
/// </para>
/// <para>
/// <b>A refused submission is silently dropped</b>, exactly like a term the privacy rule refuses: the endpoint
/// still answers 204, because nothing about the answer is worth a caller retrying or learning from
/// (<c>TrafficEndpoints</c>' own rule, which the search beacon already follows).
/// </para>
/// </summary>
internal sealed partial class SearchSubmissionThrottle : IDisposable
{
    /// <summary>Submissions one address may make per window. See the type remarks for the sizing.</summary>
    public const int PermitLimit = 30;

    /// <summary>What a caller with no address at all shares. They are all one bucket.</summary>
    private const string UnknownPartition = "unknown";

    private static readonly TimeSpan FixedWindow = TimeSpan.FromMinutes(1);

    private readonly PartitionedRateLimiter<string> _limiter;
    private readonly ILogger<SearchSubmissionThrottle> _logger;

    /// <summary>Set once, so the warning below is logged once rather than on every submission.</summary>
    private int _warned;

    public SearchSubmissionThrottle(ILogger<SearchSubmissionThrottle> logger)
    {
        _logger = logger;
        _limiter = PartitionedRateLimiter.Create<string, string>(partition =>
            RateLimitPartition.GetFixedWindowLimiter(
                partition,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PermitLimit,
                    Window = FixedWindow,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
    }

    public bool TryAcquire(string? partitionKey)
    {
        var partition = string.IsNullOrWhiteSpace(partitionKey) ? UnknownPartition : partitionKey;

        WarnIfTheAddressIsNotAVisitor(partition);

        using var lease = _limiter.AttemptAcquire(ClientAddressPartition.For(partition) ?? partition);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();

    /// <summary>
    /// The signal that the deployment cannot see its visitors — a loopback or private address here means every
    /// caller shares one bucket. Harmless on a developer's machine; in production it is W0-15's to fix, the same
    /// warning <see cref="NewsletterSignupThrottle"/> and <see cref="ContactEnquiryThrottle"/> give, reusing
    /// <see cref="ClientAddressPartition.IsInfrastructure"/> rather than a third private copy of the same test.
    /// </summary>
    private void WarnIfTheAddressIsNotAVisitor(string partition)
    {
        if (partition != UnknownPartition && !ClientAddressPartition.IsInfrastructure(partition))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _warned, 1, 0) != 0)
        {
            return;
        }

        LogSharedPartition(_logger, partition, PermitLimit, (int)FixedWindow.TotalMinutes);
    }

    [LoggerMessage(
        EventId = 1103,
        Level = LogLevel.Warning,
        Message = "Search submissions are arriving from {Address}, which is not a visitor's own address. Every "
            + "caller arriving this way shares one rate-limit partition of {PermitLimit} submissions per "
            + "{WindowMinutes} minute(s). This is expected on a developer machine. In a deployment it means the "
            + "reverse proxy's forwarded headers are not trusted: ForwardedHeaders__KnownProxies is unset or does "
            + "not match the web gateway's address (docs/15-deployment-runbook.md), and one abusive visitor could "
            + "throttle popular-searches counting for everyone.")]
    private static partial void LogSharedPartition(
        ILogger logger,
        string address,
        int permitLimit,
        int windowMinutes);
}

/// <summary>Registers the search-submission throttle as an Api-layer service, like the other address throttles.</summary>
internal static class SearchSubmissionThrottleRegistration
{
    public static IServiceCollection AddSearchSubmissionThrottle(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // A singleton: the window is shared by every request, which is the entire point of it.
        services.AddSingleton<SearchSubmissionThrottle>();

        return services;
    }
}
