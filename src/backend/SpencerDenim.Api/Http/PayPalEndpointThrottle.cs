using System.Globalization;
using System.Threading.RateLimiting;

namespace SpencerDenim.Api.Http;

/// <summary>
/// The limits on PayPal's public endpoints, in the shape <c>PaymentSlipUploadThrottle</c> established for the
/// other anonymous routes that hang off an order link: one bucket per order, one per address — and, for the
/// webhook, one for the whole endpoint.
/// <para>
/// <b>What each limit is for.</b> The start, return and cancel routes are reached with nothing but an order
/// token. Each is cheap to call and idempotent behind the claim (a second start finds the PayPal order it
/// already made; a second return finds the capture claimed), so the limits are not what keeps money safe —
/// they stop a script walking order links, or one customer's reload loop, from spending the shop's PayPal
/// rate limit and database. The webhook is the more exposed route: every request that looks like a PayPal
/// notification costs a call to PayPal to verify, so an attacker who cannot forge a signature could still try
/// to exhaust ours by volume. A single bucket for the whole endpoint caps that; PayPal's own retries are
/// slow, so a genuine burst is far below it.
/// </para>
/// <para>
/// <b>The address limit is loose on purpose</b>, for the reason <c>PaymentSlipUploadThrottle</c> records at
/// length: behind the reverse proxy every visitor shares one address until <c>X-Forwarded-For</c> handling is
/// set up (W0-15), so a strict per-address limit would refuse genuine customers. The per-order limit is
/// unaffected by that, because an order token is never shared.
/// </para>
/// <para>In memory and per instance, like the other throttles: one API container, and a restart losing a window costs a few extra calls.</para>
/// </summary>
internal sealed class PayPalEndpointThrottle : IDisposable
{
    /// <summary>Thirty visits per order per ten minutes: far above a customer, far below a loop.</summary>
    private const int OrderPermitLimit = 30;

    /// <summary>
    /// Per address per ten minutes. Sized for the shared-address case: 600 in ten minutes is a hundred
    /// customers a minute all being sent to PayPal at once.
    /// </summary>
    private const int AddressPermitLimit = 600;

    /// <summary>One endpoint-wide bucket for notifications: 120 a minute.</summary>
    private const int WebhookPermitLimit = 120;

    private static readonly TimeSpan CustomerWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan WebhookWindow = TimeSpan.FromMinutes(1);

    private readonly PartitionedRateLimiter<string> _orders = PartitionedRateLimiter.Create<string, string>(partition =>
        RateLimitPartition.GetFixedWindowLimiter(partition, _ => Window(OrderPermitLimit, CustomerWindow)));

    private readonly PartitionedRateLimiter<string> _addresses = PartitionedRateLimiter.Create<string, string>(partition =>
        RateLimitPartition.GetFixedWindowLimiter(partition, _ => Window(AddressPermitLimit, CustomerWindow)));

    private readonly FixedWindowRateLimiter _webhook = new(Window(WebhookPermitLimit, WebhookWindow));

    /// <summary>How long a refused customer should wait, for <c>Retry-After</c>.</summary>
    public static TimeSpan CustomerRetryAfter => CustomerWindow;

    /// <summary>How long a refused notification sender should wait, for <c>Retry-After</c>.</summary>
    public static TimeSpan WebhookRetryAfter => WebhookWindow;

    /// <summary>Takes one permit for this order and one for this address.</summary>
    public bool TryAcquireCustomer(Guid orderPublicId, string? ipAddress)
    {
        using var order = _orders.AttemptAcquire(orderPublicId.ToString("N", CultureInfo.InvariantCulture));

        if (!order.IsAcquired)
        {
            return false;
        }

        using var address = _addresses.AttemptAcquire(string.IsNullOrWhiteSpace(ipAddress) ? "unknown" : ipAddress);

        return address.IsAcquired;
    }

    /// <summary>Takes one permit from the notification bucket.</summary>
    public bool TryAcquireWebhook()
    {
        using var lease = _webhook.AttemptAcquire();

        return lease.IsAcquired;
    }

    public void Dispose()
    {
        _orders.Dispose();
        _addresses.Dispose();
        _webhook.Dispose();
    }

    private static FixedWindowRateLimiterOptions Window(int permitLimit, TimeSpan window) =>
        new()
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true,
        };
}

/// <summary>Registers the PayPal endpoint throttle.</summary>
internal static class PayPalEndpointThrottleRegistration
{
    public static IServiceCollection AddPayPalEndpointThrottle(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // A singleton: the window is shared by every request, which is the entire point of it.
        services.AddSingleton<PayPalEndpointThrottle>();

        return services;
    }
}
