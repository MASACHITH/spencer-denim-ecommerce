using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using SpencerDenim.Application.Marketing.Newsletter;

namespace SpencerDenim.Api.Http;

/// <summary>
/// The newsletter signup's rate limit (HOME-10, GLB-07), partitioned by the caller's address.
///
/// <para>
/// <b>The partition is the real socket peer, and behind Caddy that is Caddy.</b> Doc 03 §4 puts a
/// reverse proxy in front of the API, and <c>X-Forwarded-For</c> processing is deliberately off
/// (W1-01 round 3: it let anyone forge the address written into <c>ops.AuditLogs</c>, and there is
/// still no known proxy address to pin <c>KnownIPNetworks</c> to). So until W0-15 builds the real
/// Docker/Caddy networking, every visitor may land in one bucket, and the ceiling has to be one
/// that a whole launch day of genuine signups cannot reach. It is deliberately loose for that
/// reason and tightens the day the forwarded-headers work lands — which is on W0-15 as a launch
/// blocker, not as a comment.
/// </para>
/// <para>
/// What still holds in that shared-bucket state: a script cannot write millions of rows, and a
/// mistyped address costs nothing at all, because the use case only asks for a permit once it has
/// an address it is about to store.
/// </para>
/// </summary>
internal sealed partial class NewsletterSignupThrottle : INewsletterSignupThrottle, IDisposable
{
    /// <summary>
    /// Signups one partition may make per window.
    ///
    /// <para>
    /// Sized for the bad case, not the good one: if every visitor shares Caddy's address, this is
    /// the whole site's budget. 600 per ten minutes is one a second sustained — far above any
    /// plausible day for a new Sri Lankan clothing store, and far below what makes filling the
    /// table worth a script's time. Once the deployment can see real client addresses this is far
    /// too loose for one person and should come down to something like ten.
    /// </para>
    /// </summary>
    private const int PermitLimit = 600;

    /// <summary>What a caller with no address at all shares. They are all one bucket.</summary>
    private const string UnknownPartition = "unknown";

    private static readonly TimeSpan FixedWindow = TimeSpan.FromMinutes(10);

    private readonly PartitionedRateLimiter<string> _limiter;
    private readonly ILogger<NewsletterSignupThrottle> _logger;

    /// <summary>Set once, so the warning below is logged once rather than on every signup.</summary>
    private int _warned;

    public NewsletterSignupThrottle(ILogger<NewsletterSignupThrottle> logger)
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

    public TimeSpan Window => FixedWindow;

    public bool TryAcquire(string? partitionKey)
    {
        var partition = string.IsNullOrWhiteSpace(partitionKey) ? UnknownPartition : partitionKey;

        WarnIfTheAddressIsNotAVisitor(partition);

        using var lease = _limiter.AttemptAcquire(partition);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();

    /// <summary>
    /// The signal that the deployment cannot see its visitors.
    ///
    /// <para>
    /// A loopback or private address here means every caller is arriving through something else —
    /// a reverse proxy, a container network — and the whole site is sharing one bucket. On a
    /// developer's machine that is expected and harmless; in production it is the thing W0-15 has
    /// to fix, and this is what says so out loud rather than leaving it to be noticed when a
    /// launch-day signup is refused.
    /// </para>
    /// </summary>
    private void WarnIfTheAddressIsNotAVisitor(string partition)
    {
        if (Interlocked.CompareExchange(ref _warned, 1, 0) != 0)
        {
            return;
        }

        if (!IsLocal(partition))
        {
            return;
        }

        LogSharedPartition(_logger, partition, PermitLimit, (int)FixedWindow.TotalMinutes);
    }

    private static bool IsLocal(string partition)
    {
        if (partition == UnknownPartition)
        {
            return true;
        }

        if (!IPAddress.TryParse(partition, out var address))
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fc00::/7, the IPv6 unique local range, plus a mapped IPv4 address behind it.
            return address.IsIPv6LinkLocal
                || address.IsIPv6UniqueLocal
                || (address.IsIPv4MappedToIPv6 && IsPrivateV4(address.MapToIPv4()));
        }

        return IsPrivateV4(address);
    }

    /// <summary>RFC 1918 and the link-local range.</summary>
    private static bool IsPrivateV4(IPAddress address)
    {
        var octets = address.GetAddressBytes();

        return octets[0] switch
        {
            10 => true,
            172 => octets[1] >= 16 && octets[1] <= 31,
            192 => octets[1] == 168,
            169 => octets[1] == 254,
            _ => false,
        };
    }

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Warning,
        Message = "Newsletter signups are arriving from {Address}, which is not a visitor's own address. " +
            "Every caller is therefore sharing one rate-limit partition of {PermitLimit} signups per " +
            "{WindowMinutes} minutes. This is expected on a developer machine. In production it means the " +
            "reverse proxy's forwarded headers are not configured (W0-15), and one busy period could refuse " +
            "signups for everyone.")]
    private static partial void LogSharedPartition(
        ILogger logger,
        string address,
        int permitLimit,
        int windowMinutes);
}

/// <summary>Registers the signup throttle as the Application layer's port.</summary>
internal static class NewsletterSignupThrottleRegistration
{
    public static IServiceCollection AddNewsletterSignupThrottle(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // A singleton: the window is shared by every request, which is the entire point of it.
        services.AddSingleton<INewsletterSignupThrottle, NewsletterSignupThrottle>();

        return services;
    }
}
