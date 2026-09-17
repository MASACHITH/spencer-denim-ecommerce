using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Diagnostics.Ping;

namespace SpencerDenim.UnitTests.Application;

public sealed class PingQueryHandlerTests
{
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    [Fact]
    public async Task HandleAsync_returns_ok_status_service_name_and_clock_time_in_utc()
    {
        // Colombo is UTC+05:30: the handler must still report UTC.
        var colomboTime = new DateTimeOffset(2026, 9, 17, 15, 30, 0, TimeSpan.FromMinutes(330));
        var handler = new PingQueryHandler(new FixedClock(colomboTime));

        var response = await handler.HandleAsync(new PingQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(PingQueryHandler.OkStatus, response.Status);
        Assert.Equal("SpencerDenim.Api", response.Service);
        Assert.False(string.IsNullOrWhiteSpace(response.Version));
        Assert.DoesNotContain("+", response.Version, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.Zero, response.ServerTimeUtc.Offset);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero), response.ServerTimeUtc);
    }

    [Fact]
    public async Task HandleAsync_throws_when_query_is_null()
    {
        var handler = new PingQueryHandler(new FixedClock(DateTimeOffset.UnixEpoch));

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.HandleAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_honours_cancellation()
    {
        var handler = new PingQueryHandler(new FixedClock(DateTimeOffset.UnixEpoch));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => handler.HandleAsync(new PingQuery(), cancellation.Token));
    }
}
