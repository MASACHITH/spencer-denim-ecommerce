using SpencerDenim.Application.Abstractions.Time;

namespace SpencerDenim.Infrastructure.Time;

/// <summary><see cref="IClock"/> backed by <see cref="TimeProvider"/>.</summary>
internal sealed class SystemClock(TimeProvider timeProvider) : IClock
{
    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
}
