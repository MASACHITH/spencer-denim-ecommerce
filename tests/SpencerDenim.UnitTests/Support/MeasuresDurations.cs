namespace SpencerDenim.UnitTests.Support;

/// <summary>
/// The collection every test that measures a <i>ratio of durations</i> belongs to, and it does not run
/// in parallel with anything else.
/// <para>
/// <b>Why (#134, found by the tester on this branch).</b> <see cref="TimingShape"/> cancels the machine
/// out of a cost assertion, and it does that well when each of its samples gets a core. xunit's default
/// is to run collections in parallel on as many threads as there are processors, and the backend job's
/// runner is <c>ubuntu-latest</c> — <b>two</b> vCPUs. So on CI a ratio was being measured on one core
/// while the other ran whatever came next in the suite, which for this assembly includes the sanitiser
/// parsing forty thousand siblings and allocating hundreds of megabytes doing it. Pinned to two cores,
/// that cost <c>TimingShapeTests</c>' quadratic canary one red run in seven: it read above its bound of
/// 6 on work that is exactly quadratic.
/// </para>
/// <para>
/// This is not the measurement being made tolerant of noise — it removes a noise source the suite was
/// creating for itself, which is the one source that is certainly present on the runner. A noisy
/// neighbour on a shared runner is not fixed by this and cannot be; what answers that is the sampling
/// budget on <see cref="TimingShape"/>.
/// </para>
/// <para>
/// <b>It costs the wall clock almost nothing</b>, because these are a handful of cases in a suite of
/// 1,819 and the rest of the assembly still runs in parallel around them.
/// </para>
/// </summary>
[CollectionDefinition(DisableParallelization = true)]
public sealed class MeasuresDurations;
