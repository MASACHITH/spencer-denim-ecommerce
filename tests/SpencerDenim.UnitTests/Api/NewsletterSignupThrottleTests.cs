using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SpencerDenim.Api.Http;

namespace SpencerDenim.UnitTests.Api;

/// <summary>
/// The newsletter signup's rate limit (HOME-10, GLB-07).
/// <para>
/// The ceiling and the window are the adapter's own numbers, so these tests read them off the
/// behaviour rather than restating them: what matters is that one partition's budget is spent by
/// that partition alone, and that the deployment is told when every visitor is sharing one.
/// </para>
/// </summary>
public sealed class NewsletterSignupThrottleTests
{
    [Fact]
    public void A_caller_with_budget_left_gets_a_permit()
    {
        using var throttle = new NewsletterSignupThrottle(NullLogger<NewsletterSignupThrottle>.Instance);

        Assert.True(throttle.TryAcquire("203.0.113.7"));
    }

    /// <summary>
    /// The point of partitioning: one caller exhausting its budget must not refuse anybody else.
    /// Behind a reverse proxy every visitor shares one partition, which is why the ceiling is
    /// sized for that case and why W0-15's forwarded-headers work is a launch blocker.
    /// </summary>
    [Fact]
    public void One_caller_spending_its_whole_budget_does_not_refuse_another()
    {
        using var throttle = new NewsletterSignupThrottle(NullLogger<NewsletterSignupThrottle>.Instance);

        var spent = 0;
        while (throttle.TryAcquire("198.51.100.1") && spent < 10_000)
        {
            spent++;
        }

        Assert.InRange(spent, 1, 9_999);
        Assert.True(throttle.TryAcquire("203.0.113.9"));
    }

    [Fact]
    public void A_caller_that_runs_out_is_refused_until_the_window_turns()
    {
        using var throttle = new NewsletterSignupThrottle(NullLogger<NewsletterSignupThrottle>.Instance);

        while (throttle.TryAcquire("198.51.100.2"))
        {
            // Spend the partition.
        }

        Assert.False(throttle.TryAcquire("198.51.100.2"));
        Assert.True(throttle.Window > TimeSpan.Zero);
    }

    /// <summary>A caller with no address at all is still throttled, in a bucket of its own.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_caller_with_no_address_is_not_waved_through(string? address)
    {
        using var throttle = new NewsletterSignupThrottle(NullLogger<NewsletterSignupThrottle>.Instance);

        Assert.True(throttle.TryAcquire(address));
    }

    /// <summary>
    /// The signal that the deployment cannot see its visitors. A loopback or private address here
    /// means every caller is arriving through a proxy or a container network and the whole site is
    /// sharing one partition — which is exactly the state W0-15 has to fix, and which would
    /// otherwise only be noticed when a launch-day signup was refused.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.0.0.5")]
    [InlineData("172.17.0.3")]
    [InlineData("192.168.1.20")]
    [InlineData("169.254.7.7")]
    [InlineData("fd00::1")]
    [InlineData(null)]
    public void A_shared_partition_is_warned_about(string? address)
    {
        var logger = new RecordingLogger();
        using var throttle = new NewsletterSignupThrottle(logger);

        throttle.TryAcquire(address);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("forwarded headers", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("W0-15", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("2001:db8::1")]
    public void A_real_visitor_address_is_not_warned_about(string address)
    {
        var logger = new RecordingLogger();
        using var throttle = new NewsletterSignupThrottle(logger);

        throttle.TryAcquire(address);

        Assert.Empty(logger.Warnings);
    }

    /// <summary>
    /// The order the flag and the address test are applied in.
    ///
    /// <para>
    /// The once-only flag used to be spent first, so the very first call burned it whatever address
    /// it came from — one signup from a real visitor, a monitor or an uptime probe, arriving in any
    /// window before Caddy fronts the API, and the shared-bucket warning never fired again for the
    /// life of the process. The three tests above each use one kind of address on a fresh instance,
    /// so not one of them could see it: the bug lives in the *sequence*.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("203.0.113.7", "::1")]
    [InlineData("203.0.113.7", "127.0.0.1")]
    [InlineData("2001:db8::1", "10.0.0.5")]
    public void A_public_address_first_does_not_silence_the_warning_for_the_shared_one(
        string publicAddress,
        string sharedAddress)
    {
        var logger = new RecordingLogger();
        using var throttle = new NewsletterSignupThrottle(logger);

        throttle.TryAcquire(publicAddress);
        Assert.Empty(logger.Warnings);

        throttle.TryAcquire(sharedAddress);
        throttle.TryAcquire(sharedAddress);
        throttle.TryAcquire(sharedAddress);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(sharedAddress, warning, StringComparison.Ordinal);
    }

    /// <summary>Once, not on every signup: a warning on every request is a warning nobody reads.</summary>
    [Fact]
    public void The_warning_is_logged_once()
    {
        var logger = new RecordingLogger();
        using var throttle = new NewsletterSignupThrottle(logger);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            throttle.TryAcquire("127.0.0.1");
        }

        Assert.Single(logger.Warnings);
    }

    private sealed class RecordingLogger : ILogger<NewsletterSignupThrottle>
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
