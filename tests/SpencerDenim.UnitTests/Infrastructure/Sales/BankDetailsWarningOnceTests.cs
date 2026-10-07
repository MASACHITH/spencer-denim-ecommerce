using System.Collections.Concurrent;

using SpencerDenim.Infrastructure.Sales.Payments;

namespace SpencerDenim.UnitTests.Infrastructure.Sales;

/// <summary>
/// The gate behind "the shop's bank details are not set", which must be a signal rather than noise.
/// </summary>
/// <remarks>
/// <b>Why this type exists at all is the thing worth testing.</b> The warning's only code path is
/// <c>GET /api/orders/{token}</c> for an unpaid bank order — unauthenticated, unthrottled (doc 11 §7 G-41
/// is owed) and re-read on every refresh. The first version logged on every one of those, so one shopper
/// refreshing produced one Warning per refresh and a shop live without bank details would bury its own log.
/// </remarks>
public sealed class BankDetailsWarningOnceTests
{
    [Fact]
    public void The_first_caller_warns_and_nobody_else_does()
    {
        var gate = new BankDetailsWarningOnce();

        Assert.True(gate.ShouldWarn());

        // A hundred page views, which is one shopper being patient.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            Assert.False(gate.ShouldWarn());
        }
    }

    [Fact]
    public void Exactly_one_of_many_simultaneous_callers_warns()
    {
        var gate = new BankDetailsWarningOnce();
        var answers = new ConcurrentBag<bool>();

        // The reason it is Interlocked and not a bool: the provider is resolved per request, and two
        // requests for the same order page can arrive at the same moment.
        Parallel.For(0, 64, _ => answers.Add(gate.ShouldWarn()));

        Assert.Single(answers, answer => answer);
        Assert.Equal(64, answers.Count);
    }

    [Fact]
    public void Each_instance_has_its_own_turn()
    {
        // One per process in production, and one per test here — which is the whole reason this is an
        // object rather than a static field: a static one could not be reset, so the throttle would be
        // either untested or order-dependent.
        Assert.True(new BankDetailsWarningOnce().ShouldWarn());
        Assert.True(new BankDetailsWarningOnce().ShouldWarn());
    }
}
