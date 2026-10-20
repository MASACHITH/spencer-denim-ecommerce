using System.Globalization;
using System.Threading.RateLimiting;

namespace SpencerDenim.Api.Http;

/// <summary>
/// The rate limit on writing and editing reviews (PDP-08, issue #189), partitioned by the <b>signed-in customer</b>.
/// </summary>
/// <remarks>
/// <b>By account, not by address, unlike the anonymous throttles.</b> Every review write already needs a customer
/// session, so the account is a partition the caller cannot forge or spread across a /64 — and behind a proxy whose
/// forwarded headers are not trusted (the misconfiguration <c>AccountRequestThrottle</c> warns about) an address
/// partition would make one shopper's burst everybody's refusal. A spammer with many accounts is limited by
/// registration's own throttle, which is per address.
/// <para>
/// <b>Ten writes an hour.</b> One review per product means a genuine customer writes a handful in a sitting and edits
/// each a few times; ten is far above that and far below what makes flooding the moderation queue worth a script.
/// No queue: a refused write is answered at once with <c>Retry-After</c>, the same shape as the account endpoints.
/// Taken before the use case runs, so a refused request does no database work.
/// </para>
/// </remarks>
internal sealed class ReviewWriteThrottle : IDisposable
{
    /// <summary>Writes (create or edit) one customer may make per window.</summary>
    public const int PermitLimit = 10;

    private static readonly TimeSpan FixedWindow = TimeSpan.FromHours(1);

    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(partition =>
        RateLimitPartition.GetFixedWindowLimiter(
            partition,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = PermitLimit,
                Window = FixedWindow,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    /// <summary>How long a refused caller should wait, for <c>Retry-After</c>.</summary>
    public static TimeSpan Window => FixedWindow;

    /// <summary>Takes a permit for this customer, or answers <see langword="false"/>.</summary>
    public bool TryAcquire(int customerUserId)
    {
        using var lease = _limiter.AttemptAcquire(customerUserId.ToString(CultureInfo.InvariantCulture));
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}

/// <summary>Registers the review throttle.</summary>
internal static class ReviewWriteThrottleRegistration
{
    public static IServiceCollection AddReviewWriteThrottle(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // A singleton: the window is shared by every request, which is the entire point of it.
        services.AddSingleton<ReviewWriteThrottle>();

        return services;
    }
}
