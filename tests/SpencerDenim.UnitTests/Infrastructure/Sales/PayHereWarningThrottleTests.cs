using SpencerDenim.Infrastructure.Sales.Payments.PayHere;

namespace SpencerDenim.UnitTests.Infrastructure.Sales;

/// <summary>The "PayHere is hidden" warning is a signal, not a stream: once per window per process.</summary>
public sealed class PayHereWarningThrottleTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_first_call_warns_and_the_rest_of_the_window_does_not()
    {
        var throttle = new PayHereWarningThrottle(TimeSpan.FromMinutes(30));

        Assert.True(throttle.ShouldWarn(Start));
        Assert.False(throttle.ShouldWarn(Start));
        Assert.False(throttle.ShouldWarn(Start.AddMinutes(29)));
    }

    [Fact]
    public void A_fault_that_lasts_is_said_again_once_the_window_has_passed()
    {
        var throttle = new PayHereWarningThrottle(TimeSpan.FromMinutes(30));

        Assert.True(throttle.ShouldWarn(Start));
        Assert.True(throttle.ShouldWarn(Start.AddMinutes(30)));
        Assert.False(throttle.ShouldWarn(Start.AddMinutes(45)));
        Assert.True(throttle.ShouldWarn(Start.AddMinutes(61)));
    }

    [Fact]
    public async Task Two_requests_at_once_cannot_both_warn()
    {
        var throttle = new PayHereWarningThrottle();
        var gate = new TaskCompletionSource();

        var calls = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                return throttle.ShouldWarn(Start);
            }))
            .ToList();

        gate.SetResult();

        Assert.Equal(1, (await Task.WhenAll(calls)).Count(warned => warned));
    }

    [Fact]
    public void The_registered_window_is_half_an_hour()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), PayHereWarningThrottle.DefaultWindow);
    }
}
