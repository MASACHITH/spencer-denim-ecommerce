using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using SpencerDenim.Application.Content.Abstractions;

namespace SpencerDenim.Api.Http;

/// <summary>
/// The Contact form's rate limit (GLB-10, doc 11 §7 G-06: "spam protection (rate limit + honeypot)"),
/// partitioned by the caller's address.
/// <para>
/// <b>The partition is the real socket peer, and behind Caddy that is Caddy.</b> Doc 03 §4 puts a reverse
/// proxy in front of the API and <c>X-Forwarded-For</c> processing is deliberately off (W1-01 round 3: it
/// let anyone forge the address written into <c>ops.AuditLogs</c>, and there is still no known proxy
/// address to pin <c>KnownIPNetworks</c> to). So until W0-15 builds the real Docker/Caddy networking,
/// every visitor may land in one bucket, and the ceiling has to be one a genuine day of enquiries cannot
/// reach. It tightens the day the forwarded-headers work lands.
/// </para>
/// <para>
/// <b>It is deliberately tighter than the newsletter's 600 per ten minutes, because the two forms are not
/// the same size.</b> A newsletter signup is one short row; an enquiry is up to four kilobytes of free
/// text that a staff member has to read, so the cost of abuse is somebody's morning rather than a row in a
/// table. 120 per ten minutes is twelve a minute sustained across the whole site, which is far above any
/// plausible day for a new Sri Lankan clothing store and far below what makes filling the inbox worth a
/// script's time. Once the deployment can see real client addresses this is far too loose for one person
/// and should come down to something like five.
/// </para>
/// <para>
/// <b>Stated plainly, because it is the number somebody will want:</b> 120 per ten minutes is about
/// <b>17,000 enquiries a day from one address</b>. The W2-11 tester raised it. It is left as it is on
/// purpose — the brief's instruction on spam protection was doc 11 §7's G-06 measures "and no more", and
/// a second, slower bucket is more than that says — so it is written here rather than buried in a
/// journal. A daily cap is the obvious next control and it is the owner's call, not a developer's.
/// </para>
/// </summary>
internal sealed partial class ContactEnquiryThrottle : IContactEnquiryThrottle, IDisposable
{
    /// <summary>Enquiries one partition may submit per window. See the type remarks for the sizing.</summary>
    private const int PermitLimit = 120;

    /// <summary>What a caller with no address at all shares. They are all one bucket.</summary>
    private const string UnknownPartition = "unknown";

    private static readonly TimeSpan FixedWindow = TimeSpan.FromMinutes(10);

    private readonly PartitionedRateLimiter<string> _limiter;
    private readonly ILogger<ContactEnquiryThrottle> _logger;

    /// <summary>Set once, so the warning below is logged once rather than on every enquiry.</summary>
    private int _warned;

    public ContactEnquiryThrottle(ILogger<ContactEnquiryThrottle> logger)
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
    /// The signal that the deployment cannot see its visitors — a loopback or private address here means
    /// every caller arrives through something else and the whole site shares one bucket. Harmless on a
    /// developer's machine; in production it is W0-15's to fix, and this says so rather than leaving it to
    /// be noticed when a real enquiry is refused.
    /// <para>
    /// <b>The address is tested before the flag is spent</b>, which is the ordering
    /// <c>NewsletterSignupThrottle</c> had to be corrected into: the other way round, the first call burns
    /// the once-only flag whatever address it came from, so a single enquiry from a monitor or a probe
    /// silences this for the life of the process.
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
        EventId = 1102,
        Level = LogLevel.Warning,
        Message = "Contact enquiries are arriving from {Address}, which is not a visitor's own address. " +
            "Every caller is therefore sharing one rate-limit partition of {PermitLimit} enquiries per " +
            "{WindowMinutes} minutes. This is expected on a developer machine. In production it means the " +
            "reverse proxy's forwarded headers are not configured (W0-15), and one spam run could refuse " +
            "the form for everyone.")]
    private static partial void LogSharedPartition(
        ILogger logger,
        string address,
        int permitLimit,
        int windowMinutes);
}

/// <summary>Registers the enquiry throttle as the Application layer's port.</summary>
internal static class ContactEnquiryThrottleRegistration
{
    public static IServiceCollection AddContactEnquiryThrottle(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // A singleton: the window is shared by every request, which is the entire point of it.
        services.AddSingleton<IContactEnquiryThrottle, ContactEnquiryThrottle>();

        return services;
    }
}
