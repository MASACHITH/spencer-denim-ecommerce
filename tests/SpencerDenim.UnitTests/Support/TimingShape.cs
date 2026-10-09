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
    /// The fewest samples each side of a comparison takes, of which the <b>minimum</b> is kept.
    /// <para>
    /// The minimum is the right statistic for this, and not because it is optimistic: a garbage
    /// collection, a scheduler pre-emption or another test's thread landing inside a sample can only ever
    /// <i>add</i> time to it. Taking the smallest of several discards those. A mean would carry them
    /// straight into the result.
    /// </para>
    /// <para>
    /// <b>Seven is enough only when seven draws contain a clean one.</b> It was a fixed count and the
    /// tester found what that costs by running the suite pinned to two cores — the count
    /// <c>ubuntu-latest</c> gives the backend job — with those two cores also carrying spin loops.
    /// Exactly-quadratic work read anywhere from <b>2.84x</b> to <b>4.79x</b> against a theoretical 4,
    /// exactly-linear work read up to <b>5.38x</b> against a theoretical 2, and
    /// <c>TimingShapeTests</c>' quadratic canary went red against the shipped bound of 6 — <b>once in
    /// six contended full-suite runs</b>, and at the worst load applied, which stretched the suite from
    /// 23 s to 6 m 32 s. The mins were not wrong in one direction: a clean window landing on one side of
    /// a ratio and not the other moves the reading either way. So the count is now a floor rather than
    /// the whole budget: see <see cref="MeasureTogether"/>.
    /// </para>
    /// <para>
    /// <b>How much of that load the runner actually has is not known, and the honest answer matters.</b>
    /// A seventeen-fold slowdown is well past anything #134 recorded — there the failing case took 6.7 s
    /// and then 5.5 s against 1–2 s here, so roughly four-fold, with a fifth of that again as
    /// run-to-run spread. At loads up to <b>seven-fold</b> no canary failed in eight runs. So this is a
    /// margin that was thinner than it was documented to be rather than one that is known to be
    /// breached on CI.
    /// </para>
    /// </summary>
    private const int MinimumSamples = 7;

    /// <summary>
    /// The most samples a side will take while its minimum is still falling. On an idle machine a clean
    /// window turns up in the first few rounds and sampling stops at <see cref="MinimumSamples"/>, so
    /// this costs nothing there; on a loaded one it is the budget that buys a clean window instead of
    /// reporting a dirty one. Capped at three times <see cref="MinimumSamples"/>, because a machine that
    /// cannot produce a clean window in twenty-one tries will not produce one in two hundred, and a test
    /// must still finish. The cap is load-bearing: the tester also tried a 250 ms floor with a cap of
    /// forty, and on two oversubscribed cores not one full-suite run finished in the eleven minutes it
    /// was given, which is its own kind of CI failure.
    /// </summary>
    private const int MaximumSamples = 21;

    /// <summary>
    /// How many consecutive rounds must fail to improve <i>either</i> side's minimum before sampling
    /// stops. Both sides have to settle, because the error this is here to remove is one side getting a
    /// clean window and the other not.
    /// </summary>
    private const int RoundsWithoutImprovementToStop = 3;

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
    /// <para>
    /// <b>Matched durations are not enough either, and that is the fifth.</b> Equal samples are equally
    /// likely to be interrupted, which is the point — but on a loaded machine the likelihood is high
    /// enough that seven draws often contain no clean sample at all, and whether a clean one lands on
    /// one side or the other is luck. That is an error in <i>both</i> directions and it is large: the
    /// tester read exactly-linear work at <b>5.38x</b> against a theoretical 2 on two oversubscribed
    /// cores, and the same work against itself anywhere from <b>0.39x</b> to <b>2.37x</b>. So sampling
    /// does not stop at a fixed count. It stops when neither side's minimum has improved for
    /// <see cref="RoundsWithoutImprovementToStop"/> rounds, which on an idle machine is immediately
    /// after <see cref="MinimumSamples"/> and on a loaded one is however long it takes to see one clean
    /// window per side, up to <see cref="MaximumSamples"/>.
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
        var sinceImprovement = 0;

        for (var round = 0; round < MaximumSamples; round++)
        {
            var firstSample = TimeRepeats(first, firstRepeats) / firstRepeats;
            var secondSample = TimeRepeats(second, secondRepeats) / secondRepeats;

            // A full percent, not any improvement at all: floating-point noise on the last digit would
            // otherwise keep resetting the counter and spend the whole budget on every call.
            var improved = firstSample < bestFirst * 0.99 || secondSample < bestSecond * 0.99;

            bestFirst = Math.Min(bestFirst, firstSample);
            bestSecond = Math.Min(bestSecond, secondSample);

            sinceImprovement = improved ? 0 : sinceImprovement + 1;
            if (round + 1 >= MinimumSamples && sinceImprovement >= RoundsWithoutImprovementToStop)
            {
                break;
            }
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
