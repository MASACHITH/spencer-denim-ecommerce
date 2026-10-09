using System.Diagnostics;
using System.Globalization;

namespace SpencerDenim.UnitTests.Support;

/// <summary>
/// Measures the <b>shape</b> of a cost — how it grows — rather than its wall clock, so that a cost
/// assertion means the same thing on a developer machine and on the slower of GitHub's hosted runners.
/// <para>
/// <b>Why this exists (#134).</b> Four tests asserted <c>clock.Elapsed &lt; TimeSpan.FromSeconds(5)</c>.
/// One of them, <c>A_breakout_element_that_closes_its_own_kind_is_flat_and_cheap</c>, began failing the
/// backend job consistently: 6.7 s and 5.5 s on CI against 1–2 s on the developer machine, on branches
/// touching no sanitiser file. An absolute budget cannot say what those tests exist to say. It fails
/// when the machine is slow and it passes a real blow-up on a machine that is fast enough, so it is a
/// test that can fail without a defect and pass with one. A ratio between two measurements taken in the
/// same process cancels the machine out: both halves get slower together.
/// </para>
/// <para>
/// <b>The guards here are proved able to fail.</b> <c>TimingShapeTests</c> applies these same two
/// methods to workloads written to be deliberately quadratic and deliberately quartic and asserts that
/// they trip. That is what keeps a timing ratio from becoming the easiest kind of assertion to write
/// and never fail.
/// </para>
/// </summary>
internal static class TimingShape
{
    /// <summary>
    /// Each measured sample repeats the work until it has run for at least this long.
    /// <para>
    /// This is the fix for the trap that breaks a naive ratio: a denominator small enough for noise to
    /// dominate it. The cheap half of the sanitiser pair is a <b>0.1 ms</b> refusal on this machine
    /// (measured), where a single scheduler pre-emption is a large share of the reading.
    /// </para>
    /// <para>
    /// <b>50 ms rather than the 25 ms this started at, and the reason is a measurement I got wrong.</b>
    /// 25 ms was chosen against <see cref="Stopwatch"/> resolution and per-call jitter, which was the
    /// incomplete half of the problem: a Windows scheduler quantum is on the order of 15 ms, so on a
    /// saturated machine a 25 ms sample is a couple of quanta and losing one of them moves it by half.
    /// Under eight spin loops on eight cores, work that is exactly quadratic read <b>6.79x</b> per
    /// doubling against a theoretical 4. A sample has to span many quanta, not a few.
    /// </para>
    /// <para>
    /// It is a floor and not the target, because clearing it is not sufficient on its own — see
    /// <see cref="MeasureTogether"/>.
    /// </para>
    /// </summary>
    private const double MinimumSampleMilliseconds = 50;

    /// <summary>
    /// How many samples each side of a comparison takes, of which the <b>minimum</b> is kept.
    /// <para>
    /// The minimum is the right statistic for this, and not because it is optimistic: a garbage
    /// collection, a scheduler pre-emption or another test's thread landing inside a sample can only ever
    /// <i>add</i> time to it. Taking the smallest of several discards those. A mean would carry them
    /// straight into the result.
    /// </para>
    /// </summary>
    private const int Samples = 7;

    /// <summary>
    /// Asserts that <paramref name="candidate"/> costs no more than <paramref name="factor"/> times
    /// <paramref name="baseline"/>, both measured in this process, and returns the measured ratio.
    /// <para>
    /// Neither delegate may build its own payload: whatever they need is built before they are passed in,
    /// because everything they do is measured.
    /// </para>
    /// </summary>
    internal static double AssertWithin(double factor, Action baseline, Action candidate, string because)
    {
        var (baselineCost, candidateCost) = MeasureTogether(baseline, candidate);
        var ratio = candidateCost / baselineCost;

        Assert.True(
            ratio <= factor,
            string.Create(
                CultureInfo.InvariantCulture,
                $"cost ratio {ratio:N2}x exceeds the bound of {factor:N2}x "
                + $"({candidateCost:N3} ms against a baseline of {baselineCost:N3} ms). {because}"));

        return ratio;
    }

    /// <summary>
    /// Asserts that doubling the input multiplies the cost by no more than <paramref name="factor"/>,
    /// and returns the measured multiple.
    /// <para>
    /// <paramref name="workFor"/> is called twice, with <paramref name="n"/> and <c>2n</c>, to build the
    /// delegate for each size; whatever it does before returning that delegate is not measured.
    /// </para>
    /// </summary>
    internal static double AssertDoublingWithin(
        double factor,
        Func<int, Action> workFor,
        int n,
        string because)
    {
        ArgumentNullException.ThrowIfNull(workFor);

        var (singleCost, doubledCost) = MeasureTogether(workFor(n), workFor(n * 2));
        var multiple = doubledCost / singleCost;

        Assert.True(
            multiple <= factor,
            string.Create(
                CultureInfo.InvariantCulture,
                $"doubling {n:N0} to {n * 2:N0} multiplied the cost by {multiple:N2}x, more than the "
                + $"bound of {factor:N2}x ({singleCost:N3} ms then {doubledCost:N3} ms). {because}"));

        return multiple;
    }

    /// <summary>
    /// The per-call cost of both delegates in milliseconds, sampled <b>alternately</b> and in samples of
    /// <b>matched duration</b>.
    /// <para>
    /// <b>Alternating is the third measurement trap.</b> Measuring one side to completion and then the
    /// other makes the result depend on how the machine behaved during each half: a collection, another
    /// test finishing, or a runner's noisy neighbour arriving between them lands entirely on one side and
    /// is read as a change in shape. Taking a sample of each per round, and the minimum per side, means
    /// both sides saw the same few hundred milliseconds of whatever the machine was doing.
    /// </para>
    /// <para>
    /// <b>Matching the durations is the fourth, and it is the one that cost two attempts to find.</b>
    /// Giving each side just enough repeats to clear
    /// <see cref="MinimumSampleMilliseconds"/> leaves the expensive side with the longer samples — 62 ms
    /// against 133 ms, in the case that exposed this — and a longer sample is less likely to escape
    /// pre-emption, so the minimum of seven is systematically less clean on that side. The bias is
    /// one-directional and it inflates the ratio: work that is exactly quadratic read <b>6.41x</b> per
    /// doubling under saturation where the theory is 4. So the repeat counts are chosen to make both
    /// samples last about as long as the slower side's single call, rather than merely to clear the
    /// floor. Both sides are still divided by their own repeat count, so the counts cancel out of the
    /// ratio.
    /// </para>
    /// </summary>
    private static (double First, double Second) MeasureTogether(Action first, Action second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        // The warm-up pass, and both sides before either is timed. The first call to anything pays for
        // JIT compilation of the whole call tree and for whatever one-time setup it does — AngleSharp's
        // parser tables, in the case this was written for. Charging that to the first measurement is how
        // a ratio ends up reporting which of the two happened to run first.
        first();
        second();

        var firstOnce = CostOfOneCall(first);
        var secondOnce = CostOfOneCall(second);
        var sampleTarget = Math.Max(MinimumSampleMilliseconds, Math.Max(firstOnce, secondOnce));

        var firstRepeats = RepeatsFor(firstOnce, sampleTarget);
        var secondRepeats = RepeatsFor(secondOnce, sampleTarget);

        var bestFirst = double.MaxValue;
        var bestSecond = double.MaxValue;
        for (var round = 0; round < Samples; round++)
        {
            bestFirst = Math.Min(bestFirst, TimeRepeats(first, firstRepeats) / firstRepeats);
            bestSecond = Math.Min(bestSecond, TimeRepeats(second, secondRepeats) / secondRepeats);
        }

        return (bestFirst, bestSecond);
    }

    /// <summary>
    /// A rough per-call cost, enough to size the real samples with. Five milliseconds is plenty for an
    /// estimate that is only ever fed to <see cref="RepeatsFor"/> and rounded.
    /// </summary>
    private static double CostOfOneCall(Action work)
    {
        var repeats = 1;
        double sample;
        while ((sample = TimeRepeats(work, repeats)) < 5 && repeats < 1 << 22)
        {
            var needed = repeats * 5 / Math.Max(sample, 0.0005);
            repeats = (int)Math.Min(1 << 22, Math.Max(repeats * 2L, Math.Ceiling(needed)));
        }

        return sample / repeats;
    }

    private static int RepeatsFor(double costOfOneCall, double sampleTarget) =>
        (int)Math.Min(1 << 22, Math.Max(1, Math.Round(sampleTarget / Math.Max(costOfOneCall, 0.0005))));

    private static double TimeRepeats(Action work, int repeats)
    {
        // Collect before the sample rather than hoping none lands in it. Two passes because the first
        // can promote objects whose finalisers then queue more work.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var clock = Stopwatch.StartNew();
        for (var repeat = 0; repeat < repeats; repeat++)
        {
            work();
        }

        clock.Stop();
        return clock.Elapsed.TotalMilliseconds;
    }
}
