using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SkiaSharp;
using SpencerDenim.Application.Media;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Media;
using SpencerDenim.Tests.MediaFixtures;
using SpencerDenim.UnitTests.Support;

namespace SpencerDenim.UnitTests.Infrastructure.Media;

/// <summary>
/// The imaging half of the pipeline: the decompression-bomb guard, orientation, and the WebP rendition set
/// of doc 03 §3b and doc 08 §3.3.
/// </summary>
public sealed class SkiaImageProcessorTests
{
    private static SkiaImageProcessor Processor(Action<MediaOptions>? configure = null)
    {
        var options = new MediaOptions();
        configure?.Invoke(options);
        return new SkiaImageProcessor(Options.Create(options));
    }

    [Fact]
    public void The_header_gives_the_dimensions_without_decoding()
    {
        var header = Processor().ReadHeader(TestImages.Jpeg(640, 480));

        Assert.NotNull(header);
        Assert.Equal(640, header.Width);
        Assert.Equal(480, header.Height);
        Assert.False(header.HasNonDefaultOrientation);
    }

    /// <summary>
    /// The decompression bomb: a tiny PNG declaring an enormous canvas. Decoding it would ask for gigabytes
    /// of RGBA, so the dimensions are read from the header and refused before anything is allocated.
    /// </summary>
    [Fact]
    public void An_image_declaring_more_pixels_than_the_ceiling_is_refused_before_it_is_decoded()
    {
        var bomb = TestImages.PngDeclaring(40_000, 40_000);
        var processor = Processor(options => options.MaxImageMegapixels = 60);

        Assert.Null(processor.ReadHeader(bomb));
        Assert.Null(processor.DecodeUpright(bomb));
    }

    [Fact]
    public void An_image_inside_the_ceiling_is_accepted()
    {
        var processor = Processor(options => options.MaxImageMegapixels = 1);

        Assert.NotNull(processor.ReadHeader(TestImages.Jpeg(800, 600)));
        Assert.Null(processor.ReadHeader(TestImages.Jpeg(1400, 1000)));
    }

    /// <summary>
    /// An upload that dropped part way through leaves a JPEG whose header parses and whose scan data stops
    /// mid-row. Skia will decode what it has and leave the rest grey, so the decode result is checked and
    /// the file refused: half a product photograph must never reach the library.
    /// </summary>
    [Fact]
    public void A_truncated_image_is_refused_rather_than_published_half_decoded()
    {
        var whole = TestImages.Jpeg(640, 480);
        var truncated = whole.AsSpan(0, whole.Length / 3).ToArray();

        // The header is intact — this is not a "Skia cannot open it" case.
        using (var codec = SKCodec.Create(new SKMemoryStream(truncated)))
        {
            Assert.NotNull(codec);
            Assert.Equal(640, codec.Info.Width);
        }

        Assert.NotNull(Processor().ReadHeader(truncated));
        Assert.Null(Processor().DecodeUpright(truncated));
    }

    [Fact]
    public void Something_that_is_not_an_image_has_no_header_and_does_not_decode()
    {
        var processor = Processor();

        Assert.Null(processor.ReadHeader(TestVideos.Mp4()));
        Assert.Null(processor.DecodeUpright([1, 2, 3, 4, 5, 6, 7, 8]));
    }

    /// <summary>
    /// A phone photograph taken sideways carries EXIF orientation 6 and is 4:3 in the file but 3:4 on
    /// screen. Skia reads the tag; the processor has to act on it, because the tag is about to be stripped.
    /// </summary>
    [Fact]
    public void A_rotated_photograph_is_reported_and_decoded_upright()
    {
        var source = TestImages.JpegWithExif(orientation: 6, width: 640, height: 480);

        // The fixture's EXIF is genuine: Skia itself reads the orientation out of it.
        using (var codec = SKCodec.Create(new SKMemoryStream(source)))
        {
            Assert.NotNull(codec);
            Assert.Equal(SKEncodedOrigin.RightTop, codec.EncodedOrigin);
        }

        var processor = Processor();
        var header = processor.ReadHeader(source);

        Assert.NotNull(header);
        Assert.True(header.HasNonDefaultOrientation);

        // Axes swapped: 640x480 in the file is 480x640 on screen.
        Assert.Equal(480, header.Width);
        Assert.Equal(640, header.Height);

        using var upright = processor.DecodeUpright(source);
        Assert.NotNull(upright);
        Assert.Equal(480, upright.Width);
        Assert.Equal(640, upright.Height);
    }

    [Theory]
    [InlineData(1, 640, 480)]
    [InlineData(3, 640, 480)]
    [InlineData(6, 480, 640)]
    [InlineData(8, 480, 640)]
    public void Every_orientation_decodes_to_the_dimensions_a_browser_would_show(
        int orientation,
        int expectedWidth,
        int expectedHeight)
    {
        using var upright = Processor().DecodeUpright(TestImages.JpegWithExif(orientation, 640, 480));

        Assert.NotNull(upright);
        Assert.Equal(expectedWidth, upright.Width);
        Assert.Equal(expectedHeight, upright.Height);
    }

    [Fact]
    public void The_rendition_set_is_the_four_documented_codes_as_webp()
    {
        using var upright = Processor().DecodeUpright(TestImages.Jpeg(2400, 3200))!;
        var renditions = Processor().EncodeRenditions(upright);

        Assert.Equal(
            ["thumb", "card", "large", "zoom"],
            renditions.Select(rendition => rendition.Rendition.Code));

        foreach (var rendition in renditions)
        {
            using var codec = SKCodec.Create(new SKMemoryStream(rendition.Bytes));
            Assert.NotNull(codec);
            Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
        }
    }

    [Fact]
    public void A_rendition_keeps_the_aspect_ratio_and_hits_its_target_width()
    {
        using var upright = Processor().DecodeUpright(TestImages.Jpeg(2400, 3200))!;

        foreach (var rendition in Processor().EncodeRenditions(upright))
        {
            using var codec = SKCodec.Create(new SKMemoryStream(rendition.Bytes));
            Assert.NotNull(codec);
            Assert.Equal(rendition.Rendition.Width, codec.Info.Width);

            // 3:4 in, 3:4 out, within a pixel of rounding.
            Assert.InRange(codec.Info.Height, (rendition.Rendition.Width * 4 / 3) - 1, (rendition.Rendition.Width * 4 / 3) + 1);
        }
    }

    /// <summary>
    /// A small logo must not come back as a blurry 2000 px "zoom". Every code still exists, so no consumer
    /// has to ask which ones were generated — they are just capped at the source width.
    /// </summary>
    [Fact]
    public void A_small_source_is_never_upscaled_and_never_written_twice()
    {
        using var upright = Processor().DecodeUpright(TestImages.Png(400, 300))!;
        var renditions = Processor().EncodeRenditions(upright);

        // thumb clamps to 160 and card to 400; large and zoom would clamp to 400 as well, so they
        // would be byte-identical copies of card and are not written at all.
        Assert.Equal([MediaRenditions.Thumb, MediaRenditions.Card], renditions.Select(r => r.Rendition.Code));

        foreach (var rendition in renditions)
        {
            using var codec = SKCodec.Create(new SKMemoryStream(rendition.Bytes));
            Assert.NotNull(codec);
            Assert.Equal(Math.Min(rendition.Rendition.Width, 400), codec.Info.Width);
        }

        // No two renditions of one source are the same picture.
        Assert.Equal(
            renditions.Count,
            renditions.Select(rendition => Convert.ToHexString(SHA256.HashData(rendition.Bytes))).Distinct().Count());
    }

    /// <summary>
    /// Renditions are drawn from decoded pixels, so no metadata can survive into one — which is what makes
    /// the delivered images safe even when the stripper is not involved.
    /// </summary>
    [Fact]
    public void A_rendition_of_a_photograph_with_gps_carries_no_metadata()
    {
        var source = TestImages.JpegWithExif(orientation: 1, width: 1200, height: 900);
        using var upright = Processor().DecodeUpright(source)!;

        foreach (var rendition in Processor().EncodeRenditions(upright))
        {
            Assert.False(
                ContainsAscii(rendition.Bytes, TestImages.CameraSerial),
                $"the '{rendition.Rendition.Code}' rendition should carry no camera serial");

            Assert.False(
                ContainsAscii(rendition.Bytes, "Exif"),
                $"the '{rendition.Rendition.Code}' rendition should carry no EXIF block");
        }
    }

    /// <summary>
    /// A polyglot's appended payload does not survive re-encoding either — which is the actual defence
    /// against one, rather than trying to detect it.
    /// </summary>
    [Fact]
    public void An_appended_script_does_not_survive_into_a_rendition()
    {
        const string script = "<script>alert('spencer')</script>";
        var source = TestImages.JpegWithAppendedScript(script);

        using var upright = Processor().DecodeUpright(source)!;

        foreach (var rendition in Processor().EncodeRenditions(upright))
        {
            Assert.False(ContainsAscii(rendition.Bytes, script));
        }
    }

    [Fact]
    public void Re_encoding_an_original_keeps_its_format()
    {
        var processor = Processor();

        foreach (var (kind, format, source) in new[]
                 {
                     (MediaFileKind.Jpeg, SKEncodedImageFormat.Jpeg, TestImages.Jpeg(200, 200)),
                     (MediaFileKind.Png, SKEncodedImageFormat.Png, TestImages.Png(200, 200)),
                     (MediaFileKind.Webp, SKEncodedImageFormat.Webp, TestImages.Webp(200, 200)),
                 })
        {
            using var upright = processor.DecodeUpright(source)!;
            var bytes = processor.ReencodeOriginal(upright, kind);

            using var codec = SKCodec.Create(new SKMemoryStream(bytes));
            Assert.NotNull(codec);
            Assert.Equal(format, codec.EncodedFormat);
        }
    }

    private static bool ContainsAscii(byte[] haystack, string needle)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(needle);

        for (var index = 0; index + bytes.Length <= haystack.Length; index++)
        {
            if (haystack.AsSpan(index, bytes.Length).SequenceEqual(bytes))
            {
                return true;
            }
        }

        return false;
    }
}
