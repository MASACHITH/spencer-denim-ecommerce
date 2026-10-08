using System.Diagnostics;
using System.Globalization;

namespace SpencerDenim.UnitTests.Support;

/// <summary>
/// Measures the <b>shape</b> of a cost — how it grows — rather than its wall clock, so that a cost
/// assertion means the same thing on a developer machine and on the slower of GitHub's hosted runners.
/// <para>
/// <b>Why this exists (#134).</b> Four tests asserted <c>clock.Elapsed &lt; TimeSpan.FromSeconds(5)</c>.
/// One of them, <c>A_breakout_element_that_closes_its_own_kind_is_flat_and_cheap</c>, began failing the
/// backend job consistently on branches touching no sanitiser file: 6.7 s and 5.5 s on CI against 1–2 s
/// on the developer machine. An absolute budget cannot say what those tests exist to say. It fails when
/// the machine is slow and it passes a real blow-up on a machine that is fast enough, so it is a test
/// that can fail without a defect and pass with one. A ratio between two measurements taken in the same
/// process cancels the machine out: both halves get slower together.
/// </para>
/// <para>
/// <b>The guards here are proved able to fail.</b> <c>TimingShapeTests</c> applies these same two
/// methods to workloads written to be deliberately quadratic and deliberately cubic and asserts that
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
    /// (measured, 7 samples, Release), where a single scheduler pre-emption is a large share of the
    /// reading and the ratio becomes meaningless. Repeating the call until the sample reaches 25 ms puts
    /// per-call jitter and the <see cref="Stopwatch"/>'s own resolution well under a percent of it.
    /// Both halves of a ratio are calibrated the same way, so the repeat count cancels out.
    /// </para>
    /// </summary>
    private const double MinimumSampleMilliseconds = 25;

    /// <summary>
    /// How many samples each measurement takes, of which the <b>minimum</b> is kept.
    /// <para>
    /// The minimum is the right statistic for this, and not because it is optimistic: a garbage
    /// collection, a scheduler pre-emption or another test's thread landing inside a sample can only ever
    /// <i>add</i> time to it. Taking the smallest of several discards those, which is what stops a
    /// collection landing in one half of a ratio and not the other from being read as a change in shape.
    /// A mean would carry it straight into the result.
    /// </para>
    /// </summary>
    private const int Samples = 7;

    /// <summary>
    /// The cost of one call to <paramref name="work"/>, in milliseconds — the smallest of
    /// <see cref="Samples"/> samples, each repeated up to <see cref="MinimumSampleMilliseconds"/>.
    /// <para>
    /// <paramref name="work"/> must do only the thing being measured: build payloads and fixtures before
    /// the delegate, never inside it.
    /// </para>
    /// </summary>
    internal static double PerCallMilliseconds(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        // The warm-up pass. The first call to anything pays for JIT compilation of the whole call tree
        // and for whatever one-time setup it does — AngleSharp's parser tables, in the case this was
        // written for — and charging that to the first measurement is how a ratio ends up reporting the
        // order the two halves happened to run in.
        work();

        var repeats = 1;
        double sample;
        while ((sample = TimeRepeats(work, repeats)) < MinimumSampleMilliseconds && repeats < 1 << 22)
        {
            var needed = repeats * MinimumSampleMilliseconds / Math.Max(sample, 0.0005);
            repeats = (int)Math.Min(1 << 22, Math.Max(repeats * 2L, Math.Ceiling(needed)));
        }

        var best = sample / repeats;
        for (var remaining = Samples - 1; remaining > 0; remaining--)
        {
            best = Math.Min(best, TimeRepeats(work, repeats) / repeats);
        }

        return best;
    }

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

    /// <summary>
    /// Asserts that <paramref name="candidate"/> costs no more than <paramref name="factor"/> times
    /// <paramref name="baseline"/>, both measured in this process, and returns the measured ratio.
    /// </summary>
    internal static double AssertWithin(double factor, Action baseline, Action candidate, string because)
    {
        var baselineCost = PerCallMilliseconds(baseline);
        var candidateCost = PerCallMilliseconds(candidate);
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

        var single = workFor(n);
        var doubled = workFor(n * 2);

        var singleCost = PerCallMilliseconds(single);
        var doubledCost = PerCallMilliseconds(doubled);
        var multiple = doubledCost / singleCost;

        Assert.True(
            multiple <= factor,
            string.Create(
                CultureInfo.InvariantCulture,
                $"doubling {n:N0} to {n * 2:N0} multiplied the cost by {multiple:N2}x, more than the "
                + $"bound of {factor:N2}x ({singleCost:N3} ms then {doubledCost:N3} ms). {because}"));

        return multiple;
    }
}
