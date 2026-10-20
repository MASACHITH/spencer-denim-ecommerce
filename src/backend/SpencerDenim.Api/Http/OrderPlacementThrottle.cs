using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Sales.Checkout.Abstractions;

namespace SpencerDenim.Api.Http;

/// <summary>
/// The order-placement limit's settings, bound from <c>OrderPlacementThrottle__*</c> environment variables
/// (Azure-portable: nothing here is a compile-time constant a deployment cannot change).
/// </summary>
/// <remarks>
/// <b>Why these three, and why they are settings.</b> The ceiling is a judgement about a shop that has not
/// launched yet (see <see cref="PermitsPerWindow"/>), and being wrong in the tight direction costs a sale
/// at the last step of a checkout. An operator who finds out on a campaign day has to be able to raise it
/// with an environment variable and a restart, not a release.
/// </remarks>
internal sealed class OrderPlacementThrottleOptions
{
    /// <summary>The configuration section: <c>OrderPlacementThrottle</c>.</summary>
    public const string SectionName = "OrderPlacementThrottle";

    /// <summary>
    /// Orders one visitor address may place per window. <b>Twenty.</b>
    /// </summary>
    /// <remarks>
    /// <b>Generous on purpose, and the reason is the same one the newsletter's twenty gives.</b> Sri Lankan
    /// mobile carriers put thousands of subscribers behind a handful of public addresses (carrier-grade
    /// NAT), so "one address" is often "one carrier's customers in a district", not one person; and a link
    /// shared on social media can bring a crowd of them to checkout within minutes. Twenty in ten minutes
    /// is two a minute from one address, held for the full window, where the whole shop's plausible peak
    /// is a handful of orders in the same ten minutes — so no real address can reach it by shopping, and a
    /// shopper refused at the last step of a checkout is a lost sale, which costs the shop more than the
    /// squatting this limit slows.
    /// <para>
    /// <b>What it buys, stated plainly.</b> It is a speed limit and not a ceiling: one address can still
    /// place its quota every window (about 2,900 orders a day), and a script with many addresses is not
    /// slowed at all. It stops the accident and the naive loop — the tester who left 91 unpaid orders on
    /// one variant did it in one run from one address — and it makes the hold the expiry job releases
    /// arrive at a rate that job (50 orders every five minutes) can keep up with from a single source. The
    /// defences that bound the total are the 48-hour expiry and, if the owner wants it, a cap on open
    /// unpaid orders per contact (see the issue #107 journal).
    /// </para>
    /// </remarks>
    public int PermitsPerWindow { get; set; } = 20;

    /// <summary>
    /// Orders per window for a caller the API cannot see as a visitor — loopback, a private or container
    /// address, no address at all. <b>Two hundred.</b>
    /// </summary>
    /// <remarks>
    /// Such a caller is not one person but everybody behind the same proxy, which happens when the web
    /// gateway's forwarded headers are not trusted (<c>ForwardedHeaders__KnownProxies</c>, docs/15 §3). The
    /// per-visitor figure would then be the whole shop's budget and would refuse genuine shoppers, which is
    /// a worse failure than the one this limit prevents; two hundred is twenty a minute for the whole site.
    /// The throttle logs a warning the first time it sees such a caller, because this is a misconfiguration
    /// to fix and not a supported mode. The same arrangement <c>PaymentSlipUploadThrottle</c> made first.
    /// </remarks>
    public int SharedAddressPermitsPerWindow { get; set; } = 200;

    /// <summary>
    /// The window, in seconds, and the <c>Retry-After</c> a refused caller is sent. <b>600</b>: ten minutes,
    /// the newsletter's and the account endpoints' window, so every public limit on the site turns at the
    /// same pace.
    /// </summary>
    public int WindowSeconds { get; set; } = 600;
}

/// <summary>
/// The order-placement rate limit (issue #107), partitioned by the visitor's address.
/// <para>
/// <b>The partition is the visitor's own address</b> once the deployment is configured: the web gateway's
/// address is trusted for <c>X-Forwarded-For</c> (<c>ForwardedHeaders__KnownProxies</c>, docs/15 §3), so
/// <c>RemoteIpAddress</c> is the client the host's nginx saw. An IPv6 visitor is bucketed by their /64
/// (<see cref="ClientAddressPartition"/>), because a subscriber controls the whole network and a bucket per
/// address would be no limit.
/// </para>
/// <para>
/// <b>Which requests reach it is the use case's decision, not this class's</b>: see
/// <see cref="IOrderPlacementThrottle"/>. A replay of an order that exists, a request with a missing field
/// and a total that moved never ask for a permit.
/// </para>
/// <para>
/// <b>In memory, per instance</b>, like every other throttle here: one API container (doc 03 §4), a
/// restart losing the window costs a few extra orders, and a distributed limiter is a dependency this shop
/// does not have. If the API is ever scaled out, the effective limit is the figure times the instances.
/// </para>
/// </summary>
internal sealed partial class OrderPlacementThrottle : IOrderPlacementThrottle, IDisposable
{
    /// <summary>What a caller with no address at all shares. They are all one bucket.</summary>
    private const string UnknownPartition = "unknown";

    private readonly PartitionedRateLimiter<string> _limiter;
    private readonly ILogger<OrderPlacementThrottle> _logger;
    private readonly int _sharedPermitLimit;
    private readonly TimeSpan _window;

    /// <summary>Set once, so the warning below is logged once rather than on every order.</summary>
    private int _warned;

    public OrderPlacementThrottle(
        IOptions<OrderPlacementThrottleOptions> options,
        ILogger<OrderPlacementThrottle> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        // `.Value` runs the options validation, so a setting that makes no sense stops the throttle being
        // built — and with it the first order — rather than quietly limiting nothing.
        var settings = options.Value;

        _logger = logger;
        _window = TimeSpan.FromSeconds(settings.WindowSeconds);
        _sharedPermitLimit = settings.SharedAddressPermitsPerWindow;

        _limiter = PartitionedRateLimiter.Create<string, string>(partition =>
            RateLimitPartition.GetFixedWindowLimiter(
                partition,

                // The ceiling is chosen per partition when that partition is first seen, which is the only
                // place a PartitionedRateLimiter lets it vary: an address that is infrastructure gets the
                // shared budget, a visitor the strict one, decided once each.
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = ClientAddressPartition.IsInfrastructure(partition) || partition == UnknownPartition
                        ? settings.SharedAddressPermitsPerWindow
                        : settings.PermitsPerWindow,
                    Window = _window,

                    // No queue: a refused shopper is told to come back, never held open on a connection.
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
    }

    public TimeSpan Window => _window;

    public bool TryAcquire(string? partitionKey)
    {
        var address = string.IsNullOrWhiteSpace(partitionKey) ? UnknownPartition : partitionKey;

        WarnIfTheAddressIsNotAVisitor(address);

        using var lease = _limiter.AttemptAcquire(ClientAddressPartition.For(address)!);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();

    /// <summary>
    /// The signal that the deployment cannot see its visitors: a loopback, private or missing address means
    /// everybody is sharing one bucket. Harmless on a developer's machine; in a deployment it means
    /// <c>ForwardedHeaders__KnownProxies</c> is unset or does not match the gateway.
    /// <para>
    /// <b>The address is tested before the flag is spent</b>, the ordering <c>NewsletterSignupThrottle</c>
    /// had to be corrected into: the other way round, the first order from any address at all would silence
    /// the warning for the life of the process.
    /// </para>
    /// </summary>
    private void WarnIfTheAddressIsNotAVisitor(string address)
    {
        if (address != UnknownPartition && !ClientAddressPartition.IsInfrastructure(address))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _warned, 1, 0) != 0)
        {
            return;
        }

        LogSharedPartition(_logger, address, _sharedPermitLimit, (int)_window.TotalSeconds);
    }

    [LoggerMessage(
        EventId = 3311,
        Level = LogLevel.Warning,
        Message = "Orders are arriving from {Address}, which is not a visitor's own address. Every caller "
            + "arriving this way shares one placement rate-limit partition of {SharedLimit} orders per "
            + "{WindowSeconds} seconds. This is expected on a developer machine. In a deployment it means "
            + "the reverse proxy's forwarded headers are not trusted: ForwardedHeaders__KnownProxies is "
            + "unset or does not match the web gateway's address (docs/15-deployment-runbook.md), and one "
            + "busy hour could refuse genuine orders.")]
    private static partial void LogSharedPartition(
        ILogger logger,
        string address,
        int sharedLimit,
        int windowSeconds);
}

/// <summary>Registers the placement throttle as the Application layer's port.</summary>
internal static class OrderPlacementThrottleRegistration
{
    public static IServiceCollection AddOrderPlacementThrottle(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<OrderPlacementThrottleOptions>()
            .BindConfiguration(OrderPlacementThrottleOptions.SectionName)
            .Validate(
                options => options.PermitsPerWindow > 0,
                "OrderPlacementThrottle:PermitsPerWindow must be greater than zero: a limit of nothing "
                + "would refuse every order.")
            .Validate(
                options => options.SharedAddressPermitsPerWindow >= options.PermitsPerWindow,
                "OrderPlacementThrottle:SharedAddressPermitsPerWindow must be at least "
                + "OrderPlacementThrottle:PermitsPerWindow: it is the budget for everybody behind one proxy, "
                + "so it cannot be smaller than the budget for one visitor.")
            .Validate(
                options => options.WindowSeconds is >= 1 and <= 86_400,
                "OrderPlacementThrottle:WindowSeconds must be between 1 and 86400 (a day).")
            .ValidateOnStart();

        // A singleton: the window is shared by every request, which is the entire point of it.
        services.AddSingleton<IOrderPlacementThrottle, OrderPlacementThrottle>();

        return services;
    }
}
