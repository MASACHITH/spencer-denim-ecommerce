namespace SpencerDenim.Application.Abstractions.Time;

/// <summary>Port for the current time, so use cases stay deterministic in tests.</summary>
public interface IClock
{
    /// <summary>The current instant in UTC.</summary>
    DateTimeOffset UtcNow { get; }
}
