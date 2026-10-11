using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Infrastructure.Identity.Seeding;

namespace SpencerDenim.UnitTests.Infrastructure.Identity;

/// <summary>
/// What the start-up entry point does when it cannot finish (issue #122). The reference data it follows is what
/// lets the shop take an order; a permissions reconciliation that fails must say so loudly and leave the host
/// alone — and a shutdown must still be a shutdown.
/// </summary>
public sealed class DefaultRolePermissionSyncFailureTests
{
    [Fact]
    public async Task A_failure_is_logged_at_error_and_does_not_reach_the_host()
    {
        var logger = new RecordingLogger();
        var sync = new DefaultRolePermissionSync(
            new FailingScopeFactory(new InvalidOperationException("the database is on fire")),
            new FixedClock(),
            logger);

        var result = await sync.TryRunAsync(TestContext.Current.CancellationToken);

        Assert.Null(result);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(1510, entry.EventId);

        // It names the consequence for staff and that the shop is unaffected; the cause travels as the entry's
        // exception, not in the message.
        Assert.Contains("lack permissions", entry.Message, StringComparison.Ordinal);
        Assert.Contains("shop is not affected", entry.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    [Fact]
    public async Task A_shutdown_is_not_swallowed()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        var logger = new RecordingLogger();
        var sync = new DefaultRolePermissionSync(
            new FailingScopeFactory(new OperationCanceledException(stopping.Token)),
            new FixedClock(),
            logger);

        await Assert.ThrowsAsync<OperationCanceledException>(() => sync.TryRunAsync(stopping.Token));

        Assert.Empty(logger.Entries);
    }

    /// <summary>
    /// An <see cref="OperationCanceledException"/> that is <i>not</i> the host stopping — a command timeout that
    /// surfaces as one — is a failure like any other and is reported.
    /// </summary>
    [Fact]
    public async Task A_cancellation_that_is_not_a_shutdown_is_a_failure()
    {
        var logger = new RecordingLogger();
        var sync = new DefaultRolePermissionSync(
            new FailingScopeFactory(new OperationCanceledException()),
            new FixedClock(),
            logger);

        Assert.Null(await sync.TryRunAsync(TestContext.Current.CancellationToken));
        Assert.Equal(LogLevel.Error, Assert.Single(logger.Entries).Level);
    }

    private sealed class FailingScopeFactory(Exception failure) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw failure;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
    }

    private sealed record Entry(LogLevel Level, int EventId, string Message, Exception? Exception);

    private sealed class RecordingLogger : ILogger<DefaultRolePermissionSync>
    {
        private readonly List<Entry> _entries = [];

        public IReadOnlyList<Entry> Entries => _entries;

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

            _entries.Add(new Entry(logLevel, eventId.Id, formatter(state, exception), exception));
        }
    }
}
