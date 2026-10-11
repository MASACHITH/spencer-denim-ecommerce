using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>One line a component logged: its level, its category and the fully rendered message.</summary>
public sealed record CapturedLogEntry(LogLevel Level, string Category, int EventId, string Message);

/// <summary>
/// Keeps every log line in memory so a test can assert on what an operator would read. Thread-safe, because the
/// code under test logs from whatever thread its <c>await</c> resumed on.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    public IReadOnlyList<CapturedLogEntry> Entries => [.. _entries];

    public void Clear() => _entries.Clear();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            entries.Enqueue(new CapturedLogEntry(logLevel, category, eventId.Id, formatter(state, exception)));
        }
    }
}
