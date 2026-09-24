namespace SpencerDenim.UnitTests.Support;

/// <summary>
/// A <see cref="TimeProvider"/> a test drives by hand.
/// <para>
/// Written here rather than taken from <c>Microsoft.Extensions.TimeProvider.Testing</c>: the two
/// things needed are a clock that only moves when a test moves it and a record of what was asked
/// to be waited for, and a package would be a new dependency for thirty lines.
/// </para>
/// <para>
/// <see cref="Delay"/> does not wait. It records the requested delay and returns, which is what
/// lets a test assert "it would have waited exactly this long" without the suite sleeping through
/// it — a padding test that really slept would be a test nobody runs twice.
/// </para>
/// </summary>
internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    private readonly List<TimeSpan> _delays = [];

    private DateTimeOffset _now = now;

    /// <summary>Every delay that was asked for, in order.</summary>
    public IReadOnlyList<TimeSpan> Delays => _delays;

    /// <summary>One tick per 100 ns, which is what <see cref="DateTimeOffset"/> counts in.</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _now.UtcTicks;

    /// <summary>Moves the clock, standing in for work that took time.</summary>
    public void Advance(TimeSpan by) => _now += by;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        _delays.Add(dueTime);

        // `Task.Delay(TimeSpan, TimeProvider, …)` creates a timer and awaits it firing, so firing
        // it immediately is what makes the awaited delay return without any real waiting. The
        // requested duration is already recorded above, which is what the test reads.
        return new ImmediateTimer(callback, state);
    }

    private sealed class ImmediateTimer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ImmediateTimer(TimerCallback callback, object? state)
        {
            _callback = callback;
            _state = state;

            // After construction, so the caller has its timer reference before the callback runs.
            ThreadPool.QueueUserWorkItem(_ => _callback(_state));
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
