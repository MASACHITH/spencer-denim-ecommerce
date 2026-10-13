using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using SpencerDenim.Application.Marketing.Newsletter;

namespace SpencerDenim.Api.Http;

/// <summary>
/// The newsletter signup's rate limit (HOME-10, GLB-07), partitioned by the caller's address.
///
/// <para>
/// <b>The partition is the visitor's own address</b> once the deployment is configured: the web
/// gateway's address is trusted for <c>X-Forwarded-For</c> (<c>ForwardedHeaders__KnownProxies</c>,
/// W0-15, docs/15), so <c>RemoteIpAddress</c> is the client the host's nginx saw, not the gateway.
/// The ceiling is therefore sized for one address, not for the whole site.
/// </para>
/// <para>
/// If that setting is missing or does not match the gateway's subnet, every visitor lands in the
/// gateway's bucket and this ceiling becomes the whole site's budget. That is a misconfiguration,
/// not a supported mode, and the warning below says so in the log the first time it happens.
/// </para>
/// <para>
/// What holds either way: a script cannot write millions of rows, and a mistyped address costs
/// nothing at all, because the use case only asks for a permit once it has an address it is about
/// to store.
/// </para>
/// </summary>
internal sealed partial class NewsletterSignupThrottle : INewsletterSignupThrottle, IDisposable
{
    /// <summary>
    /// Signups one address may make per window.
    ///
    /// <para>
    /// Twenty per ten minutes. A person signs up once; a family or an office sharing one connection
    /// signs up a handful of times. The margin above that is for carrier-grade NAT: Sri Lankan mobile
    /// networks put many subscribers behind one public address, and a campaign link shared on social
    /// media can bring several of them to the form within minutes. Twenty caps one address at under
    /// three thousand rows a day, where 600 allowed eighty-six thousand. (It was 600 while every visitor
    /// shared the reverse proxy's address; W0-15 fixed that.)
    /// </para>
    /// </summary>
    private const int PermitLimit = 20;

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

        using var lease = _limiter.AttemptAcquire(ClientAddressPartition.For(partition)!);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();

    /// <summary>
    /// The signal that the deployment cannot see its visitors.
    ///
    /// <para>
    /// A loopback or private address here means the caller arrived through something else — a
    /// reverse proxy, a container network — whose forwarded headers the API is not trusting. On a
    /// developer's machine that is expected and harmless. In a deployment it means
    /// <c>ForwardedHeaders__KnownProxies</c> is unset or does not match the gateway's address, so
    /// every visitor is sharing one bucket, and this says so out loud rather than leaving it to be
    /// noticed when a genuine signup is refused.
    /// </para>
    /// <para>
    /// <b>The address is tested before the flag is spent</b>, and the order is the whole point. The
    /// other way round, the first call burned the once-only flag whatever address it came from — so
    /// a single signup from a real visitor, a monitor or a probe silenced this warning for the life
    /// of the process. The flag exists to stop the warning repeating, not to stop it happening.
    /// </para>
    /// </summary>
    private void WarnIfTheAddressIsNotAVisitor(string partition)
    {
        if (!IsLocal(partition))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _warned, 1, 0) != 0)
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
            "Every caller arriving this way shares one rate-limit partition of {PermitLimit} signups per " +
            "{WindowMinutes} minutes. This is expected on a developer machine. In a deployment it means the " +
            "reverse proxy's forwarded headers are not trusted: ForwardedHeaders__KnownProxies is unset or does " +
            "not match the web gateway's address (docs/15-deployment-runbook.md), and genuine signups will be refused.")]
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
