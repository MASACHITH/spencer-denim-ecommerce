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
/// <b>Every assertion here is one-sided, and that was a correction rather than a choice.</b> Three of
/// these cases first also pinned the measured multiple inside a narrow band —
/// <c>Assert.InRange(multiple, 3.0, 5.0)</c> on the quadratic one. Running the suite under eight spin
/// loops on eight cores read <b>2.88</b> and it went red, which is the #134 defect reproduced inside the
/// fix for it: a two-sided bound on a timing measurement, tight enough that a busy machine trips it. The
/// bands are gone. What is left is what each guard is for — it trips, or it does not. The exponents
/// the instrument reads belong below as measurements, which is the right place for a number nobody
/// should fail a build over.
/// </para>
/// <para>
/// <b>Then the quadratic case went red again at 6.79x</b>, and that one was a real fault in
/// <see cref="TimingShape"/> rather than in the assertion: a 25 ms sample floor is only a couple of
/// scheduler quanta, and the two sides of a ratio were measured one after the other. Both are fixed
/// there — a 50 ms floor and alternating samples — and the sizes here are chosen so that a single
/// call is tens of milliseconds rather than two, which is the part that belonged in the test. Worth
/// recording because it is the same mistake twice: a timing measurement that is too short to be stable
/// is the thing that makes timing assertions look unreliable.
/// </para>
/// <para>
/// <b>Measured</b> on the developer machine, Release, seven samples each, five repeats of the whole set
/// — these are readings I took, not estimates, and they agree with the exponents to about a percent,
/// which is the evidence that the instrument works. Idle, then with all eight cores saturated by spin
/// loops:
/// <list type="bullet">
/// <item><description><c>GrowingAs(1)</c>, theory 2: <b>1.98–2.01x</b>, then <b>1.97–2.19x</b>.</description></item>
/// <item><description><c>GrowingAs(2)</c>, theory 4: <b>3.99–4.04x</b>, then <b>3.99–4.10x</b>.</description></item>
/// <item><description><c>GrowingAs(4)</c>, theory 16: <b>15.93–15.99x</b>, then <b>15.97–16.68x</b>.</description></item>
/// <item><description>The same shape against itself: <b>1.00–1.01x</b>, then <b>1.00–1.08x</b>.</description></item>
/// <item><description>Linear against quadratic at <c>n</c> = 2,000: <b>1,980–1,998x</b>, then <b>1,659–1,998x</b>.</description></item>
/// </list>
/// Saturation costs about a tenth on the worst of them, which is the whole claim being made for a ratio
/// over a clock. None of them is asserted as a range all the same.
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
        TimingShape.AssertDoublingWithin(
            DoublingBound,
            GrowingAs(1),
            50_000_000,
            "linear work must pass the bound, or the guard is strict rather than discriminating.");
    }

    [Fact]
    public void The_doubling_guard_does_not_trip_on_work_that_grows_quadratically()
    {
        // This is not slack: the sanitiser's accept path is itself quadratic, measured at 3.04-3.35x per
        // doubling at the size that ships (#134, and the docblock on the sanitiser case carries the
        // spread). A bound that refused quadratic work would be red on main, so the bound that ships has
        // to sit above it — and this case is the honest record of that, next to the one below, which
        // shows what the bound still catches.
        TimingShape.AssertDoublingWithin(
            DoublingBound,
            GrowingAs(2),
            6_000,
            "quadratic work is below this bound by design; see the case above it.");
    }

    [Fact]
    public void The_doubling_guard_trips_on_work_that_grows_faster_than_its_bound()
    {
        var failure = Assert.ThrowsAny<XunitException>(() => TimingShape.AssertDoublingWithin(
            DoublingBound,
            GrowingAs(4),
            50,
            "this must trip."));

        Assert.Contains("multiplied the cost by", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ratio_guard_does_not_trip_on_two_workloads_of_the_same_shape()
    {
        TimingShape.AssertWithin(
            RatioBound,
            GrowingAs(2)(2_000),
            GrowingAs(2)(2_000),
            "the same work twice must come out as the same cost.");
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
