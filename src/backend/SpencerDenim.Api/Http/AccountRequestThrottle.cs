using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using SpencerDenim.Infrastructure.Identity.Customers;

namespace SpencerDenim.Api.Http;

/// <summary>
/// Which public account endpoint a permit is being taken for. Each has its own budget.
/// </summary>
/// <remarks>
/// <b>Separate buckets, because the four cost different things and are abused differently.</b> A sign-in
/// attempt costs a password hash and is the one an attacker repeats thousands of times; a registration
/// writes a user row; a forgot-password mints a token and queues a message somebody else receives; a
/// resend does the same for a caller who is already signed in. One shared budget would mean a burst of
/// sign-in attempts locking a genuine shopper out of the registration form, which is the failure
/// <c>INewsletterSignupThrottle</c>'s own remarks warn about from the other direction.
/// </remarks>
internal enum AccountRequestKind
{
    SignIn = 1,
    Register = 2,
    ForgotPassword = 3,
    ResendConfirmation = 4,
    RedeemToken = 5,
}

/// <summary>
/// The rate limit on the public customer-account endpoints, partitioned by the caller's address.
/// </summary>
/// <remarks>
/// <b>Nothing in this build rate-limited an authentication endpoint before W3-01.</b> Doc 11 §7
/// <b>G-41</b> asks for the same control on the guest order link and names doc 08 as the place to write
/// it down; nothing limits that either, and that is still outstanding (task report). What makes it
/// non-negotiable <i>here</i> is that these four endpoints are the ones that let an unauthenticated
/// caller spend the shop's resources: a password hash per request, a user row per request, or an email
/// to somebody else's inbox per request.
/// <para>
/// <b>It is not the lockout, and it does not replace it.</b> <c>CustomerAccountLockout</c> protects one
/// account from being guessed at (10 failures, 15 minutes, doc 11 P11 step B.5); this protects the shop
/// from one source trying many accounts, which a per-account lockout cannot see at all. Both are needed
/// and neither is sufficient.
/// </para>
/// <para>
/// <b>Partitioned by <see cref="ClientAddressPartition"/>, so an IPv6 subscriber cannot walk a /64.</b>
/// W0-15's tester sent 25 newsletter signups from 25 addresses in one /64 and every one was accepted;
/// the same class would apply to a sign-in form. The warning <c>NewsletterSignupThrottle</c> logs about a
/// shared partition applies here too and is logged for the same reason: behind an untrusted proxy every
/// visitor lands in one bucket, which is a misconfiguration and not a supported mode.
/// </para>
/// </remarks>
internal sealed partial class AccountRequestThrottle : IDisposable
{
    /// <summary>What a caller with no address at all shares. They are all one bucket.</summary>
    private const string UnknownPartition = "unknown";

    private static readonly TimeSpan FixedWindow = TimeSpan.FromMinutes(10);

    private readonly PartitionedRateLimiter<string> _limiter;
    private readonly ILogger<AccountRequestThrottle> _logger;
    private readonly CustomerSecurityOptions _security;

    /// <summary>Set once, so the shared-partition warning is logged once rather than per request.</summary>
    private int _warned;

    public AccountRequestThrottle(
        ILogger<AccountRequestThrottle> logger,
        IOptions<CustomerSecurityOptions> security)
    {
        ArgumentNullException.ThrowIfNull(security);

        _logger = logger;
        _security = security.Value;

        _limiter = PartitionedRateLimiter.Create<string, string>(partition =>
            RateLimitPartition.GetFixedWindowLimiter(
                partition,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PermitsFor(KindOf(partition)),
                    Window = FixedWindow,

                    // No queue: a refused caller is told to come back, never held open. A queued
                    // authentication request is a connection the shop is paying to keep while an
                    // attacker decides whether to send another.
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
    }

    /// <summary>How long a rejected caller should wait, for the <c>Retry-After</c> header.</summary>
    public static TimeSpan Window => FixedWindow;

    /// <summary>
    /// Takes a permit for this caller and this endpoint, or answers <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <b>Taken before the use case runs, unlike <c>INewsletterSignupThrottle</c>.</b> That port is
    /// deliberately asked late — "an address this store will not even store must not consume anybody's
    /// budget" — because its cost is a row. These endpoints' cost is a password hash, which is spent
    /// <i>during</i> the use case and cannot be reclaimed, so the permit has to come first. A request
    /// refused here does no database work, spends no hash, and counts towards no account's lockout.
    /// </remarks>
    public bool TryAcquire(AccountRequestKind kind, string? address)
    {
        var partition = string.IsNullOrWhiteSpace(address) ? UnknownPartition : address;

        WarnIfTheAddressIsNotAVisitor(partition);

        var key = string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)kind}:{ClientAddressPartition.For(partition)}");

        using var lease = _limiter.AttemptAcquire(key);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();

    /// <summary>
    /// The kind encoded in a partition key, so <see cref="PermitsFor"/> can size the bucket the limiter
    /// is creating.
    /// </summary>
    /// <remarks>
    /// The key is built here and read here, and the factory the limiter calls only ever sees keys this
    /// class made — so an unparseable one is impossible rather than merely unlikely, and it falls back to
    /// the tightest budget rather than the loosest.
    /// </remarks>
    private static AccountRequestKind KindOf(string partition)
    {
        var separator = partition.IndexOf(':', StringComparison.Ordinal);

        return separator > 0
            && int.TryParse(
                partition.AsSpan(0, separator),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value)
            && Enum.IsDefined((AccountRequestKind)value)
            ? (AccountRequestKind)value
            : AccountRequestKind.ForgotPassword;
    }

    /// <summary>
    /// The budget for one kind, from <see cref="CustomerSecurityOptions"/>.
    /// </summary>
    /// <remarks>
    /// <b>The forgot-password and resend budgets are the same number deliberately</b>
    /// (<c>LinkRequestsPerWindow</c>): both end with a message in somebody's inbox, and splitting them
    /// would be two settings nobody could explain the difference between. They are separate
    /// <i>buckets</i>, so exhausting one does not close the other.
    /// </remarks>
    private int PermitsFor(AccountRequestKind kind) => kind switch
    {
        AccountRequestKind.SignIn => _security.SignInAttemptsPerWindow,
        AccountRequestKind.Register => _security.RegistrationsPerWindow,
        AccountRequestKind.RedeemToken => _security.LinkRedemptionsPerWindow,
        _ => _security.LinkRequestsPerWindow,
    };

    /// <summary>
    /// Logs once when requests are arriving from something that is not a visitor's own address.
    /// </summary>
    /// <remarks>
    /// <c>NewsletterSignupThrottle</c>'s warning, for the same deployment mistake: if
    /// <c>ForwardedHeaders__KnownProxies</c> does not name the web gateway, every caller shares the
    /// gateway's bucket and these ceilings become the whole site's budget — which on a sign-in form means
    /// genuine customers being refused.
    /// </remarks>
    private void WarnIfTheAddressIsNotAVisitor(string partition)
    {
        if (Interlocked.Exchange(ref _warned, 1) != 0)
        {
            return;
        }

        if (partition == UnknownPartition
            || !IPAddress.TryParse(partition, out var address)
            || IPAddress.IsLoopback(address))
        {
            LogSharedPartition(_logger, partition, _security.SignInAttemptsPerWindow, (int)FixedWindow.TotalMinutes);
        }
    }

    [LoggerMessage(
        EventId = 7201,
        Level = LogLevel.Warning,
        Message = "Customer account requests are arriving from {Address}, which is not a visitor's own "
            + "address. Every caller arriving this way shares one rate-limit partition — {SignInPermits} "
            + "sign-in attempts per {WindowMinutes} minutes for the whole site. This is expected on a "
            + "developer machine. In a deployment it means the reverse proxy's forwarded headers are not "
            + "trusted: ForwardedHeaders__KnownProxies is unset or does not match the web gateway's "
            + "address (docs/15-deployment-runbook.md), and genuine customers will be refused.")]
    private static partial void LogSharedPartition(
        ILogger logger,
        string address,
        int signInPermits,
        int windowMinutes);
}

/// <summary>Registers the account throttle.</summary>
internal static class AccountRequestThrottleRegistration
{
    public static IServiceCollection AddAccountRequestThrottle(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // A singleton: the window is shared by every request, which is the entire point of it.
        services.AddSingleton<AccountRequestThrottle>();

        return services;
    }
}
