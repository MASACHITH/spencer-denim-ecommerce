using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Sales.Checkout.Abstractions;

namespace SpencerDenim.UnitTests.Api;

/// <summary>
/// The order-placement limit (issue #107): <c>POST /api/checkout/orders</c>, per visitor address.
/// <para>
/// Every test builds the throttle through <c>AddOrderPlacementThrottle()</c> with real configuration
/// keys, the way the host does, so a misspelt key or a lost binding fails here rather than showing up as
/// the production default silently applying. The numbers in the tests are small because the thing under
/// test is the boundary — N succeeds, N + 1 does not — not the size of N.
/// </para>
/// </summary>
public sealed class OrderPlacementThrottleTests
{
    private const string Section = "OrderPlacementThrottle";

    private const string PublicAddress = "203.0.113.7";

    private const string OtherPublicAddress = "198.51.100.9";

    /// <summary>
    /// <b>The boundary, and the off-by-one it exists to catch.</b> A limit of N lets N through and refuses
    /// the N + 1th; a refused caller stays refused rather than being let through by a later call.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void The_limit_trips_at_one_more_than_the_permit_count_and_not_at_it(int permits)
    {
        using var rig = Rig.Create(permitsPerWindow: permits);

        for (var attempt = 1; attempt <= permits; attempt++)
        {
            Assert.True(rig.Throttle.TryAcquire(PublicAddress), $"permit {attempt} of {permits} was refused");
        }

        Assert.False(rig.Throttle.TryAcquire(PublicAddress), $"permit {permits + 1} was granted");
        Assert.False(rig.Throttle.TryAcquire(PublicAddress));
    }

    /// <summary>
    /// The shipped defaults, pinned because the report, the runbook and the README quote them: twenty a
    /// window for a visitor's own address, two hundred when the address is infrastructure, ten minutes.
    /// </summary>
    [Fact]
    public void The_defaults_are_twenty_per_visitor_two_hundred_when_shared_and_ten_minutes()
    {
        using var visitor = Rig.Create();
        using var shared = Rig.Create();

        Assert.Equal(20, Spend(visitor.Throttle, PublicAddress));
        Assert.Equal(200, Spend(shared.Throttle, "10.251.0.10"));
        Assert.Equal(TimeSpan.FromMinutes(10), visitor.Throttle.Window);
    }

    [Fact]
    public void One_address_spending_its_whole_budget_does_not_refuse_another()
    {
        using var rig = Rig.Create(permitsPerWindow: 3);

        Assert.Equal(3, Spend(rig.Throttle, PublicAddress));
        Assert.False(rig.Throttle.TryAcquire(PublicAddress));

        // A different visitor has a bucket of its own, to the same size.
        Assert.Equal(3, Spend(rig.Throttle, OtherPublicAddress));
    }

    /// <summary>
    /// An IPv6 subscriber holds a whole /64, so a bucket per <i>address</i> would be no limit at all (the
    /// W0-15 tester signed up 25 times from 25 addresses in one network). The partition is the network.
    /// </summary>
    [Fact]
    public void Addresses_in_one_ipv6_slash_64_share_a_bucket_and_another_network_does_not()
    {
        using var rig = Rig.Create(permitsPerWindow: 4);

        Assert.True(rig.Throttle.TryAcquire("2001:db8:1:2::1"));
        Assert.True(rig.Throttle.TryAcquire("2001:db8:1:2:abcd::7"));
        Assert.True(rig.Throttle.TryAcquire("2001:db8:1:2:ffff:ffff:ffff:ffff"));
        Assert.True(rig.Throttle.TryAcquire("2001:db8:1:2::99"));
        Assert.False(rig.Throttle.TryAcquire("2001:db8:1:2::1234"));

        Assert.True(rig.Throttle.TryAcquire("2001:db8:1:3::1"));
    }

    /// <summary>One visitor is one bucket whichever socket family the host happened to accept them on.</summary>
    [Fact]
    public void An_ipv4_mapped_ipv6_address_spends_the_ipv4_addresses_bucket()
    {
        using var rig = Rig.Create(permitsPerWindow: 2);

        Assert.True(rig.Throttle.TryAcquire(PublicAddress));
        Assert.True(rig.Throttle.TryAcquire("::ffff:203.0.113.7"));
        Assert.False(rig.Throttle.TryAcquire(PublicAddress));
    }

    /// <summary>
    /// A caller the API cannot see — loopback, a private or container address, no address at all — is not
    /// a visitor but everybody behind the same proxy, so it is given the shared budget and not the
    /// per-visitor one. Refusing the sixth genuine shopper of a day because the gateway was misconfigured
    /// is a worse failure than the one this limit prevents (the same reasoning
    /// <c>PaymentSlipUploadThrottle</c> records).
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.251.0.10")]
    [InlineData("172.17.0.3")]
    [InlineData("192.168.1.20")]
    [InlineData("169.254.7.7")]
    [InlineData("fd00::1")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_caller_that_is_not_a_visitor_gets_the_shared_budget(string? address)
    {
        using var rig = Rig.Create(permitsPerWindow: 3, sharedAddressPermitsPerWindow: 9);

        Assert.Equal(9, Spend(rig.Throttle, address));
    }

    [Fact]
    public void A_visitors_own_address_does_not_get_the_shared_budget()
    {
        using var rig = Rig.Create(permitsPerWindow: 3, sharedAddressPermitsPerWindow: 9);

        Assert.Equal(3, Spend(rig.Throttle, PublicAddress));
    }

    /// <summary>
    /// The limiter is shared by every request thread, so the count has to hold when they all arrive at
    /// once: two hundred concurrent callers on one address get exactly the budget, no more.
    /// </summary>
    [Fact]
    public async Task Concurrent_callers_on_one_address_never_get_more_than_the_budget()
    {
        using var rig = Rig.Create(permitsPerWindow: 20);

        var granted = 0;
        await Parallel.ForAsync(
            0,
            200,
            new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = TestContext.Current.CancellationToken },
            (_, _) =>
            {
                if (rig.Throttle.TryAcquire(PublicAddress))
                {
                    Interlocked.Increment(ref granted);
                }

                return ValueTask.CompletedTask;
            });

        Assert.Equal(20, granted);
    }

    /// <summary>
    /// A window that has turned gives the budget back, so a refusal is a delay and not a ban. A one-second
    /// window, polled rather than slept on, so a slow machine waits longer instead of failing.
    /// </summary>
    [Fact]
    public async Task The_budget_comes_back_when_the_window_turns()
    {
        using var rig = Rig.Create(permitsPerWindow: 2, windowSeconds: 1);

        Assert.Equal(2, Spend(rig.Throttle, PublicAddress));
        Assert.False(rig.Throttle.TryAcquire(PublicAddress));

        var deadline = DateTime.UtcNow.AddSeconds(15);
        var granted = false;

        while (!granted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            granted = rig.Throttle.TryAcquire(PublicAddress);
        }

        Assert.True(granted, "the budget never came back after the window turned");
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Throttle.Window);
    }

    [Fact]
    public void Retry_after_is_the_configured_window()
    {
        using var rig = Rig.Create(windowSeconds: 123);

        Assert.Equal(TimeSpan.FromSeconds(123), rig.Throttle.Window);
    }

    /// <summary>
    /// A setting that would disable the limit by accident, or take the host down in a way nobody can
    /// diagnose, is refused when the throttle is built — which, because the endpoint injects it, is the
    /// first request, and in the host the first thing the options validation reports.
    /// </summary>
    [Theory]
    [InlineData("0", "200", "600")]
    [InlineData("-5", "200", "600")]
    [InlineData("20", "19", "600")]
    [InlineData("20", "200", "0")]
    [InlineData("20", "200", "86401")]
    public void A_setting_that_makes_no_sense_is_refused(string permits, string shared, string window)
    {
        using var services = Services(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{Section}:PermitsPerWindow"] = permits,
            [$"{Section}:SharedAddressPermitsPerWindow"] = shared,
            [$"{Section}:WindowSeconds"] = window,
        });

        Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IOrderPlacementThrottle>());
    }

    // --- The shared-address warning --------------------------------------------------------------------

    /// <summary>
    /// A loopback or private caller means the gateway's forwarded headers are not trusted, and every
    /// visitor is sharing one bucket. The log says so, naming the setting to fix.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.251.0.10")]
    [InlineData(null)]
    public void A_shared_partition_is_warned_about_once(string? address)
    {
        var logger = new RecordingLogger();
        using var rig = Rig.Create(logger: logger);

        rig.Throttle.TryAcquire(address);
        rig.Throttle.TryAcquire(address);
        rig.Throttle.TryAcquire(address);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("ForwardedHeaders__KnownProxies", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("2001:db8::1")]
    public void A_real_visitor_address_is_not_warned_about(string address)
    {
        var logger = new RecordingLogger();
        using var rig = Rig.Create(logger: logger);

        rig.Throttle.TryAcquire(address);

        Assert.Empty(logger.Warnings);
    }

    /// <summary>
    /// The order the once-only flag and the address test are applied in: a public visitor first must not
    /// silence the warning for the shared address that follows (the bug <c>NewsletterSignupThrottle</c>
    /// had to be corrected for).
    /// </summary>
    [Fact]
    public void A_public_address_first_does_not_silence_the_warning_for_a_shared_one()
    {
        var logger = new RecordingLogger();
        using var rig = Rig.Create(logger: logger);

        rig.Throttle.TryAcquire(PublicAddress);
        Assert.Empty(logger.Warnings);

        rig.Throttle.TryAcquire("10.251.0.10");

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("10.251.0.10", warning, StringComparison.Ordinal);
    }

    // --- Helpers ---------------------------------------------------------------------------------------

    /// <summary>How many permits an address gets before the first refusal (capped, so a broken limiter ends).</summary>
    private static int Spend(IOrderPlacementThrottle throttle, string? address)
    {
        var spent = 0;

        while (spent < 10_000 && throttle.TryAcquire(address))
        {
            spent++;
        }

        return spent;
    }

    private static ServiceProvider Services(
        IReadOnlyDictionary<string, string?>? settings = null,
        ILogger<OrderPlacementThrottle>? logger = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>(StringComparer.Ordinal))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOrderPlacementThrottle();

        if (logger is not null)
        {
            services.AddSingleton(logger);
        }

        return services.BuildServiceProvider();
    }

    /// <summary>The throttle built exactly as the host builds it, with the provider that owns it.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly ServiceProvider _services;

        private Rig(ServiceProvider services)
        {
            _services = services;
            Throttle = services.GetRequiredService<IOrderPlacementThrottle>();
        }

        public IOrderPlacementThrottle Throttle { get; }

        public static Rig Create(
            int? permitsPerWindow = null,
            int? sharedAddressPermitsPerWindow = null,
            int? windowSeconds = null,
            ILogger<OrderPlacementThrottle>? logger = null)
        {
            var settings = new Dictionary<string, string?>(StringComparer.Ordinal);

            if (permitsPerWindow is { } permits)
            {
                settings[$"{Section}:PermitsPerWindow"] = permits.ToString(CultureInfo.InvariantCulture);
            }

            if (sharedAddressPermitsPerWindow is { } shared)
            {
                settings[$"{Section}:SharedAddressPermitsPerWindow"] = shared.ToString(CultureInfo.InvariantCulture);
            }

            if (windowSeconds is { } window)
            {
                settings[$"{Section}:WindowSeconds"] = window.ToString(CultureInfo.InvariantCulture);
            }

            return new Rig(Services(settings, logger));
        }

        public void Dispose() => _services.Dispose();
    }

    private sealed class RecordingLogger : ILogger<OrderPlacementThrottle>
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings => _warnings;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (logLevel == LogLevel.Warning)
            {
                _warnings.Add(formatter(state, exception));
            }
        }
    }
}
