using SpencerDenim.Infrastructure.Media;
using SpencerDenim.Tests.MediaFixtures;
using SpencerDenim.UnitTests.Support;

namespace SpencerDenim.UnitTests.Infrastructure.Media;

/// <summary>
/// Doc 03 §3b: "MP4 (H.264) only … No server-side conversion." The inspector is the only thing standing
/// between a 2-vCPU VPS and whatever file somebody drags onto the upload box, so half of these are the
/// files no encoder would write.
/// </summary>
public sealed class Mp4InspectorTests
{
    [Fact]
    public void A_well_formed_h264_mp4_gives_its_dimensions_duration_and_codec()
    {
        var info = Inspect(TestVideos.Mp4(width: 1920, height: 1080, durationSeconds: 12.5));

        Assert.NotNull(info);
        Assert.Equal(1920, info.Width);
        Assert.Equal(1080, info.Height);
        Assert.Equal(12.50m, info.DurationSeconds);
        Assert.Equal("avc1", info.Codec);
        Assert.True(info.IsH264);
    }

    [Fact]
    public void An_avc3_track_also_counts_as_h264()
    {
        var info = Inspect(TestVideos.Mp4(codec: "avc3"));

        Assert.NotNull(info);
        Assert.True(info.IsH264);
    }

    /// <summary>
    /// HEVC plays nowhere reliable and nothing here converts it, so it has to be named rather than
    /// accepted as "an MP4".
    /// </summary>
    [Fact]
    public void An_hevc_track_is_read_but_is_not_h264()
    {
        var info = Inspect(TestVideos.Mp4(codec: "hvc1"));

        Assert.NotNull(info);
        Assert.Equal("hvc1", info.Codec);
        Assert.False(info.IsH264);
    }

    [Fact]
    public void An_mp4_with_only_an_audio_track_has_no_video_to_report()
    {
        Assert.Null(Inspect(TestVideos.Mp4(handler: "soun")));
    }

    [Fact]
    public void The_duration_is_rounded_to_the_two_decimals_the_column_holds()
    {
        var info = Inspect(TestVideos.Mp4(durationSeconds: 7.126));

        Assert.NotNull(info);
        Assert.Equal(7.13m, info.DurationSeconds);
    }

    // ------------------------------------------------------- hostile and broken

    [Fact]
    public void A_truncated_file_is_refused_rather_than_read_past_its_end()
    {
        Assert.Null(Inspect(TestVideos.TruncatedMp4()));
    }

    /// <summary>A box that claims to be longer than its parent would walk off the end of the file.</summary>
    [Fact]
    public void A_box_longer_than_the_file_is_refused()
    {
        Assert.Null(Inspect(TestVideos.Mp4WithOversizedBox()));
    }

    /// <summary>
    /// A zero-length box is the classic hang: a walker that advances by the declared size never moves.
    /// This has to return, and quickly.
    /// </summary>
    [Fact]
    public async Task A_zero_length_box_does_not_hang_the_walk()
    {
        var work = Task.Run(() => Inspect(TestVideos.Mp4WithZeroLengthBox()), TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.Same(work, finished);
        Assert.Null(await work);
    }

    /// <summary>
    /// Ten thousand eight-byte boxes: the walk is capped, so this costs a bounded amount of work instead
    /// of turning one request into a CPU spike on a two-core machine.
    /// <para>
    /// <b>The cap that fires here is <c>MaxChildrenPerBox</c> (512), not <c>BoxBudget</c> (4,096)</b>,
    /// which the name of this case reads as. The file has no <c>moov</c> at all, so the top-level
    /// <c>ReadBoxes</c> loop stops on its child cap after 512 <c>free</c> boxes with 3,584 of the budget
    /// still unspent, and <c>TryInspect</c> returns null for want of a <c>moov</c>. Checked twice over:
    /// in the loop condition on <c>ReadBoxes</c>, and by measuring — 10,000 boxes against 40,000 costs
    /// <b>0.98–1.02x</b>, and 1,000 against 4,000 costs <b>1.00x</b>, so the cost does not move with the
    /// box count in either range.
    /// </para>
    /// <para>
    /// <b>This is a liveness check and it is deliberately not a ratio (#134.)</b> Every other absolute
    /// timing bound in this suite became a ratio, because each was asserting a cost. This one cannot:
    /// the work is bounded by a <i>counter</i> in production code, so the cost is flat in the input by
    /// construction and a scaling ratio here would be 1.00x for any pair of counts above 512 — an
    /// assertion that cannot fail, which is the defect being fixed rather than a fix for it. What this
    /// case is for is what <c>A_zero_length_box_does_not_hang_the_walk</c> one case up is for: proving
    /// the walk <i>returns at all</i>. Liveness is not a budget, and it has no shape to measure.
    /// </para>
    /// <para>
    /// <b>So it is written the way that case is written, and it used to be a <c>Stopwatch</c>, which
    /// could not do the job it was credited with.</b> A clock read after a synchronous call proves
    /// nothing about returning: if <c>ReadBoxes</c> ever stopped advancing, the call would not come
    /// back, the assertion after it would never run, and the job would sit until the thirty-minute
    /// timeout killed it — the test would hang rather than fail. Raced against a <c>Task.Delay</c> it
    /// fails in five seconds and names the reason, which is what the case was always claiming to do.
    /// The clock it replaces was also the last absolute timing budget left in this suite, and it had a
    /// margin no regression could ever use: the call costs <b>0.021 ms</b> measured here and
    /// <b>0.017 ms</b> measured by the tester, so the five seconds stood some <b>250,000x</b> clear of
    /// it. A budget that loose can only ever go red for the reason #134 is about.
    /// </para>
    /// <para>
    /// <b>The flatness above is the tester's reading too</b>, taken against the real
    /// <c>TryInspect</c> over a min-of-nine of 500 calls: 1,000 boxes cost 0.0257 ms, 4,000 cost
    /// 0.0259 ms, 10,000 cost 0.0172 ms and 40,000 cost 0.0162 ms, with <see langword="null"/> returned
    /// at every count. The cost does not rise with the box count — it falls slightly, as the larger
    /// buffer changes how it is allocated — so a scaling ratio here would read <i>below</i> 1.00x and
    /// pass any upper bound that could be written. That is the case for a liveness check rather than a
    /// looser number, and it is why this one case kept a five-second deadline when the other four lost
    /// theirs.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Thousands_of_tiny_boxes_are_bounded_by_the_budget()
    {
        var file = TestVideos.Mp4WithManyTinyBoxes(10_000);

        var work = Task.Run(() => Inspect(file), TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(
            work,
            Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.Same(work, finished);
        Assert.Null(await work);
    }

    [Fact]
    public void A_file_that_is_not_an_mp4_at_all_is_refused()
    {
        Assert.Null(Inspect(TestImages.Jpeg()));
        Assert.Null(Inspect([]));
    }

    [Fact]
    public void A_non_seekable_stream_is_a_programming_error_not_a_refusal()
    {
        using var source = new MemoryStream(TestVideos.Mp4());
        using var forwardOnly = new ForwardOnlyStream(source);

        Assert.Throws<ArgumentException>(() => Mp4Inspector.TryInspect(forwardOnly));
    }

    private static Mp4VideoInfo? Inspect(byte[] file)
    {
        using var stream = new MemoryStream(file);
        return Mp4Inspector.TryInspect(stream);
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
