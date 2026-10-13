using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using SpencerDenim.Application.Sales.Payments.Abstractions;

namespace SpencerDenim.Api.Http;

/// <summary>
/// Doc 08 §3.3 N10's slip-upload limit, in two partitions: one per order and one per address.
/// <para>
/// <b>The per-order half is doc 08's figure exactly. The per-address half is not, and the difference is
/// deliberate.</b> Five per hour per IP is the right rule when the address is a visitor's own. Behind a
/// reverse proxy it is not an address at all: doc 03 §4 puts Caddy in front of the API and
/// <c>X-Forwarded-For</c> processing is deliberately off (W1-01 round 3 — it let anyone forge the address
/// written into <c>ops.AuditLogs</c>, and there is still no known proxy address to pin
/// <c>KnownIPNetworks</c> to). So in production today <i>every</i> customer shares one bucket, and five an
/// hour would mean the sixth genuine shopper of the hour cannot send their slip at all — on a shop whose
/// only payment method is bank transfer. That is a worse failure than the one the limit prevents.
/// </para>
/// <para>
/// So the address bucket is <b>five when the address looks like a real visitor's and
/// <see cref="SharedAddressPermitLimit"/> when it does not</b>, with a one-time warning naming W0-15 —
/// the same shape and the same reasoning as <c>NewsletterSignupThrottle</c>, which met this first. The
/// per-order limit is unaffected by any of it, because an order token is never shared, and it is the half
/// doc 11 P7 describes as stopping "one customer flooding their own order".
/// </para>
/// <para>
/// <b>In memory, per instance</b>, like the newsletter throttle: doc 03 §4 runs one API container, a
/// restart losing the window costs at most a few extra uploads, and a distributed limiter is a dependency
/// this shop does not have. The per-order half is also backed by something durable — a replaced slip
/// soft-deletes its predecessor, so flooding costs disk for the retention window and nothing else.
/// </para>
/// </summary>
internal sealed partial class PaymentSlipUploadThrottle : IPaymentSlipUploadThrottle, IDisposable
{
    /// <summary>Doc 08 §3.3 N10: five per hour, per order.</summary>
    private const int OrderPermitLimit = 5;

    /// <summary>Doc 08 §3.3 N10: five per hour, per address — when the address is really an address.</summary>
    private const int AddressPermitLimit = 5;

    /// <summary>
    /// What the address bucket allows when every caller is sharing it.
    /// <para>
    /// Sized for the bad case the way <c>NewsletterSignupThrottle.PermitLimit</c> is: 300 an hour is five a
    /// minute sustained, far above any plausible hour for a new Sri Lankan clothing store whose shoppers
    /// upload at most a file or two each, and far below what makes filling the private volume worth a
    /// script's time at 5 MB a go. The per-order limit of five still applies underneath it, so a script
    /// cannot spend this budget on one order.
    /// </para>
    /// </summary>
    private const int SharedAddressPermitLimit = 300;

    /// <summary>What a caller with no address at all shares. They are all one bucket.</summary>
    private const string UnknownPartition = "unknown";

    private static readonly TimeSpan FixedWindow = TimeSpan.FromHours(1);

    private readonly PartitionedRateLimiter<string> _orders;
    private readonly PartitionedRateLimiter<string> _addresses;
    private readonly ILogger<PaymentSlipUploadThrottle> _logger;

    /// <summary>Set once, so the warning below is logged once rather than on every upload.</summary>
    private int _warned;

    public PaymentSlipUploadThrottle(ILogger<PaymentSlipUploadThrottle> logger)
    {
        _logger = logger;

        _orders = PartitionedRateLimiter.Create<string, string>(partition =>
            RateLimitPartition.GetFixedWindowLimiter(partition, _ => Window(OrderPermitLimit)));

        _addresses = PartitionedRateLimiter.Create<string, string>(partition =>
            RateLimitPartition.GetFixedWindowLimiter(
                partition,

                // The ceiling is chosen per partition, when that partition is first seen, which is the only
                // place a PartitionedRateLimiter lets it vary. A shared address therefore gets the loose
                // bucket and a real one the strict bucket, decided once each.
                _ => Window(IsLocal(partition) ? SharedAddressPermitLimit : AddressPermitLimit)));
    }

    TimeSpan IPaymentSlipUploadThrottle.Window => FixedWindow;

    /// <summary>
    /// Takes the order's permit first, then the address's.
    /// </summary>
    /// <remarks>
    /// <b>The order matters, and it is the cheaper mistake first.</b> A customer who has genuinely uploaded
    /// five slips this hour should be told that, specifically, because it is about something they did and
    /// they can act on it. Asking the address first would answer "try again shortly" to a customer whose
    /// own order is the thing that is exhausted, and behind a shared proxy address that is the <i>common</i>
    /// case rather than the rare one.
    /// <para>
    /// <b>A refused address does not give the order's permit back</b>, and that is an accepted
    /// imprecision. <c>FixedWindowRateLimiter</c> has no return-permit operation, the loss is at most one
    /// of five in an hour, and spending it is the conservative direction: the alternative — asking the
    /// address first — refuses legitimate customers for a reason they cannot see.
    /// </para>
    /// </remarks>
    public SlipUploadPermit TryAcquire(Guid orderPublicId, string? ipAddress)
    {
        using var orderLease = _orders.AttemptAcquire(
            orderPublicId.ToString("N", CultureInfo.InvariantCulture));

        if (!orderLease.IsAcquired)
        {
            return SlipUploadPermit.OrderLimitReached;
        }

        var partition = string.IsNullOrWhiteSpace(ipAddress) ? UnknownPartition : ipAddress;

        WarnIfTheAddressIsNotAVisitor(partition);

        using var addressLease = _addresses.AttemptAcquire(ClientAddressPartition.For(partition)!);

        return addressLease.IsAcquired ? SlipUploadPermit.Granted : SlipUploadPermit.AddressLimitReached;
    }

    public void Dispose()
    {
        _orders.Dispose();
        _addresses.Dispose();
    }

    private static FixedWindowRateLimiterOptions Window(int permitLimit) =>
        new()
        {
            PermitLimit = permitLimit,
            Window = FixedWindow,
            QueueLimit = 0,
            AutoReplenishment = true,
        };

    /// <summary>
    /// The signal that the deployment cannot see its visitors — the same check and the same once-only flag
    /// as <c>NewsletterSignupThrottle</c>, and tested before the flag is spent for the reason recorded
    /// there: the other way round, the first upload from any address at all silences the warning for the
    /// life of the process.
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

        LogSharedPartition(_logger, partition, SharedAddressPermitLimit, OrderPermitLimit);
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
        EventId = 3310,
        Level = LogLevel.Warning,
        Message = "Payment-slip uploads are arriving from {Address}, which is not a visitor's own address. "
            + "Every customer is therefore sharing one address partition, so doc 08 §3.3 N10's five-per-IP "
            + "limit has been loosened to {SharedLimit} per hour to avoid refusing genuine slips; the "
            + "per-order limit of {OrderLimit} is unaffected. This is expected on a developer machine. In "
            + "production it means the reverse proxy's forwarded headers are not configured (W0-15).")]
    private static partial void LogSharedPartition(
        ILogger logger,
        string address,
        int sharedLimit,
        int orderLimit);
}

/// <summary>Registers the slip-upload throttle as the Application layer's port.</summary>
internal static class PaymentSlipUploadThrottleRegistration
{
    public static IServiceCollection AddPaymentSlipUploadThrottle(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // A singleton: the window is shared by every request, which is the entire point of it.
        services.AddSingleton<IPaymentSlipUploadThrottle, PaymentSlipUploadThrottle>();

        return services;
    }
}
