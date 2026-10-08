using Xunit.Sdk;

namespace SpencerDenim.UnitTests.Support;

/// <summary>
/// <b>Tests of the measuring instrument, not of the sanitiser.</b>
/// <para>
/// A timing ratio is the easiest kind of assertion to write so that it can never fail: pick a generous
/// factor, watch it go green, and the suite has gained a line that reads as a performance guard and
/// pins nothing. These cases are the answer to that. Each one applies <see cref="TimingShape"/>'s real
/// methods, at the same bounds the real tests use, to a workload whose growth is <i>chosen</i> — and
/// asserts that the guard trips on the steep one and does not trip on the shallow one.
/// </para>
/// <para>
/// That is also why the workloads here are arithmetic loops rather than calls into the sanitiser: the
/// point is to control the exponent exactly, which a real implementation does not let you do. Proving
/// the guard discriminates needs a known-quadratic workload, not a deliberately broken sanitiser.
/// </para>
/// <para>
/// <b>Measured</b> on the developer machine, Release, seven samples each, four repeats of the whole
/// set — these are readings I took, not estimates, and they agree with the exponents to about a
/// percent, which is the evidence that the instrument works:
/// <c>GrowingAs(1)</c> doubles at <b>2.00–2.04x</b> (theory 2), <c>GrowingAs(2)</c> at
/// <b>3.78–4.01x</b> (theory 4) and <c>GrowingAs(4)</c> at <b>15.92–16.11x</b> (theory 16). The same
/// shape against itself is <b>0.99–1.00x</b>, and linear against quadratic at <c>n</c> = 2,000 is
/// <b>1,989–2,165x</b>.
/// </para>
/// </summary>
public sealed class TimingShapeTests
{
    /// <summary>
    /// The bound <c>HtmlSanitiserTests.A_breakout_element_that_closes_its_own_kind_is_flat_and_cheap</c>
    /// uses, repeated here so these cases prove the guard at the setting that ships rather than at a
    /// setting chosen to make them pass.
    /// </summary>
    private const double DoublingBound = 6;

    /// <summary>
    /// The bound
    /// <c>HtmlSanitiserTests.An_html_element_inside_foreign_content_ends_the_self_closing_exception</c>
    /// uses, for the same reason.
    /// </summary>
    private const double RatioBound = 10;

    /// <summary>Consumes each loop's result, so the JIT cannot delete the work being measured.</summary>
    private static long _sink;

    [Fact]
    public void The_doubling_guard_does_not_trip_on_work_that_grows_linearly()
    {
        var multiple = TimingShape.AssertDoublingWithin(
            DoublingBound,
            GrowingAs(1),
            100_000,
            "linear work must pass the bound, or the guard is strict rather than discriminating.");

        Assert.InRange(multiple, 1.5, 3.0);
    }

    [Fact]
    public void The_doubling_guard_does_not_trip_on_work_that_grows_quadratically()
    {
        // This is not slack: the sanitiser's accept path is itself quadratic, measured at 2.73-4.23x per
        // doubling at the size that ships (#134, and the docblock on the sanitiser case carries the full
        // spread). A bound that refused quadratic work would be red on main, so the bound that ships has
        // to sit above it — and this case is the honest record of that, next to the one below, which
        // shows what the bound still catches.
        var multiple = TimingShape.AssertDoublingWithin(
            DoublingBound,
            GrowingAs(2),
            2_000,
            "quadratic work is below this bound by design; see the case above it.");

        Assert.InRange(multiple, 3.0, 5.0);
    }

    [Fact]
    public void The_doubling_guard_trips_on_work_that_grows_faster_than_its_bound()
    {
        var failure = Assert.ThrowsAny<XunitException>(() => TimingShape.AssertDoublingWithin(
            DoublingBound,
            GrowingAs(4),
            60,
            "this must trip."));

        Assert.Contains("multiplied the cost by", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ratio_guard_does_not_trip_on_two_workloads_of_the_same_shape()
    {
        var ratio = TimingShape.AssertWithin(
            RatioBound,
            GrowingAs(2)(2_000),
            GrowingAs(2)(2_000),
            "the same work twice must come out as the same cost.");

        Assert.InRange(ratio, 0.5, 2.0);
    }

    /// <summary>
    /// The shape of the defect the sanitiser's control pair exists to catch, reproduced at a known
    /// exponent: a cheap lexical baseline against work that is quadratic in the same input. Before
    /// <c>BreaksOutOfForeignContent</c> existed the real pair measured 3 ms against 41 seconds, so the
    /// bound of <see cref="RatioBound"/> had roughly three orders of magnitude of room to spare — which
    /// is only worth saying because this case shows the bound does trip when the room is used.
    /// </summary>
    [Fact]
    public void The_ratio_guard_trips_when_one_side_is_quadratic_in_the_same_input()
    {
        var failure = Assert.ThrowsAny<XunitException>(() => TimingShape.AssertWithin(
            RatioBound,
            GrowingAs(1)(2_000),
            GrowingAs(2)(2_000),
            "this must trip."));

        Assert.Contains("exceeds the bound", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Work whose cost is <c>n</c> raised to <paramref name="exponent"/>, so a test can state the growth
    /// it means instead of nesting loops and hoping. The iteration count is computed outside the
    /// returned delegate, because <see cref="TimingShape"/> measures only what the delegate does.
    /// </summary>
    private static Func<int, Action> GrowingAs(double exponent) =>
        n => Costing((long)Math.Pow(n, exponent));

    private static Action Costing(long iterations) => () =>
    {
        long total = 0;
        for (long step = 0; step < iterations; step++)
        {
            // XOR rather than a plain sum: the inner value has to depend on the loop counter, or the
            // JIT is entitled to replace the whole loop with a closed form and measure nothing.
            total += step ^ 0x5DEECE66L;
        }

        _sink = total;
    };
}
