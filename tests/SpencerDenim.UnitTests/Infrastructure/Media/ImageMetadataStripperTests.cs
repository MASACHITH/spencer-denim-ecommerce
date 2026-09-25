using System.Text;
using SkiaSharp;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Media;
using SpencerDenim.Tests.MediaFixtures;
using SpencerDenim.UnitTests.Support;

namespace SpencerDenim.UnitTests.Infrastructure.Media;

/// <summary>
/// The privacy rule: a photograph the client uploads must not publish where it was taken or which camera
/// took it. Each test first proves the metadata is genuinely there — Skia parses it, or the bytes contain
/// it — so a passing "it is gone" can never come from its never having been present.
/// </summary>
public sealed class ImageMetadataStripperTests
{
    [Fact]
    public void A_jpeg_with_gps_and_a_camera_serial_loses_both()
    {
        var source = TestImages.JpegWithExif();

        // The EXIF is real: it is in the bytes, and the GPS rationals for Colombo are in the TIFF block.
        Assert.True(Contains(source, "Exif\0\0"u8), "the fixture should carry an EXIF APP1 segment");
        Assert.True(Contains(source, Encoding.ASCII.GetBytes(TestImages.CameraSerial)));

        Assert.True(ImageMetadataStripper.TryStrip(source, MediaFileKind.Jpeg, out var stripped));

        Assert.False(Contains(stripped, "Exif\0\0"u8));
        Assert.False(Contains(stripped, Encoding.ASCII.GetBytes(TestImages.CameraSerial)));
        Assert.True(stripped.Length < source.Length);
    }

    [Fact]
    public void A_stripped_jpeg_is_still_a_decodable_jpeg_of_the_same_size()
    {
        var source = TestImages.JpegWithExif(orientation: 1, width: 300, height: 200);

        Assert.True(ImageMetadataStripper.TryStrip(source, MediaFileKind.Jpeg, out var stripped));

        using var codec = SKCodec.Create(new SKMemoryStream(stripped));
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
        Assert.Equal(300, codec.Info.Width);
        Assert.Equal(200, codec.Info.Height);
    }

    /// <summary>
    /// Lossless, not re-encoded: the compressed scan data must come through byte for byte, or the archive
    /// copy of every photograph the client ever uploads loses a generation of quality.
    /// </summary>
    [Fact]
    public void Stripping_a_jpeg_does_not_touch_a_byte_of_the_image_data()
    {
        var plain = TestImages.Jpeg(200, 150);
        var withExif = TestImages.JpegWithExif(orientation: 1, width: 200, height: 150);

        Assert.True(ImageMetadataStripper.TryStrip(withExif, MediaFileKind.Jpeg, out var stripped));

        var plainScan = ScanDataOf(plain);
        var strippedScan = ScanDataOf(stripped);

        Assert.True(plainScan.SequenceEqual(strippedScan), "the entropy-coded scan data should be unchanged");
    }

    /// <summary>
    /// Colour has to survive. Dropping the ICC profile or the Adobe transform segment would visibly shift a
    /// product photograph, which for a clothing retailer is the one unacceptable side effect.
    /// </summary>
    [Fact]
    public void A_colour_profile_segment_is_kept()
    {
        var source = WithSegment(TestImages.Jpeg(), marker: 0xE2, Encoding.ASCII.GetBytes("ICC_PROFILE\0\u0001\u0001"));

        Assert.True(ImageMetadataStripper.TryStrip(source, MediaFileKind.Jpeg, out var stripped));

        Assert.True(Contains(stripped, "ICC_PROFILE"u8), "APP2 (ICC profile) must survive");
    }

    [Fact]
    public void A_jpeg_comment_which_is_the_classic_hiding_place_is_removed()
    {
        var source = TestImages.JpegWithComment("<script>fetch('/api/admin/auth/whoami')</script>");

        Assert.True(Contains(source, "<script>"u8));
        Assert.True(ImageMetadataStripper.TryStrip(source, MediaFileKind.Jpeg, out var stripped));
        Assert.False(Contains(stripped, "<script>"u8));
    }

    [Fact]
    public void A_png_loses_its_exif_and_text_chunks_and_still_decodes()
    {
        var source = TestImages.PngWithMetadata();

        Assert.True(Contains(source, "eXIf"u8));
        Assert.True(Contains(source, Encoding.ASCII.GetBytes(TestImages.CameraSerial)));

        Assert.True(ImageMetadataStripper.TryStrip(source, MediaFileKind.Png, out var stripped));

        Assert.False(Contains(stripped, "eXIf"u8));
        Assert.False(Contains(stripped, "tEXt"u8));
        Assert.False(Contains(stripped, Encoding.ASCII.GetBytes(TestImages.CameraSerial)));

        using var codec = SKCodec.Create(new SKMemoryStream(stripped));
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedImageFormat.Png, codec.EncodedFormat);
    }

    [Fact]
    public void A_webp_loses_its_exif_chunk_and_the_header_stops_claiming_one()
    {
        var source = TestImages.WebpWithExif();

        Assert.True(Contains(source, "EXIF"u8));
        Assert.True(ImageMetadataStripper.TryStrip(source, MediaFileKind.Webp, out var stripped));

        Assert.False(Contains(stripped, "EXIF"u8));

        // The VP8X flags byte sits at offset 20: 12 bytes of RIFF header, 8 of chunk header. Bit 3 is the
        // "an EXIF chunk follows" flag, and a file that still claimed one after it had gone would be a file
        // contradicting itself.
        Assert.Equal(0, stripped[20] & 0x08);

        using var codec = SKCodec.Create(new SKMemoryStream(stripped));
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
    }

    [Fact]
    public void An_image_with_nothing_to_remove_still_comes_back_whole()
    {
        var source = TestImages.Jpeg(120, 90);

        Assert.True(ImageMetadataStripper.TryStrip(source, MediaFileKind.Jpeg, out var stripped));

        using var codec = SKCodec.Create(new SKMemoryStream(stripped));
        Assert.NotNull(codec);
        Assert.Equal(120, codec.Info.Width);
    }

    /// <summary>
    /// A truncated chunked container is refused rather than half-written: the upload fails instead of
    /// storing bytes nothing could read back.
    /// </summary>
    [Theory]
    [InlineData(MediaFileKind.Png)]
    [InlineData(MediaFileKind.Webp)]
    public void A_malformed_container_is_refused(MediaFileKind kind)
    {
        var whole = kind == MediaFileKind.Png ? TestImages.Png() : TestImages.Webp();
        var truncated = whole.AsSpan(0, whole.Length / 3).ToArray();

        Assert.False(ImageMetadataStripper.TryStrip(truncated, kind, out _));
    }

    /// <summary>
    /// A truncated <b>JPEG</b> is a different case, and the test says so rather than pretending otherwise:
    /// every metadata segment in a JPEG comes before the scan data, so a file cut off inside the scan has a
    /// perfectly readable header and the container walk succeeds on it. Skia will even open it. What
    /// refuses it is the decode, one step later in the pipeline — see
    /// <c>SkiaImageProcessorTests.A_truncated_image_is_refused_rather_than_published_half_decoded</c>.
    /// </summary>
    [Fact]
    public void A_truncated_jpeg_passes_the_container_walk_because_its_metadata_all_comes_first()
    {
        var whole = TestImages.Jpeg();
        var truncated = whole.AsSpan(0, whole.Length / 3).ToArray();

        Assert.True(ImageMetadataStripper.TryStrip(truncated, MediaFileKind.Jpeg, out _));
    }

    [Fact]
    public void A_format_the_stripper_does_not_handle_is_refused_rather_than_passed_through()
    {
        Assert.False(ImageMetadataStripper.TryStrip(TestVideos.Mp4(), MediaFileKind.Mp4, out _));
        Assert.False(ImageMetadataStripper.TryStrip([1, 2, 3], MediaFileKind.Unknown, out _));
    }

    // ---------------------------------------------------------------- helpers

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (var index = 0; index + needle.Length <= haystack.Length; index++)
        {
            if (haystack.Slice(index, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Everything from the start-of-scan marker to the end of the file.</summary>
    private static byte[] ScanDataOf(byte[] jpeg)
    {
        for (var index = 2; index + 1 < jpeg.Length; index++)
        {
            if (jpeg[index] == 0xFF && jpeg[index + 1] == 0xDA)
            {
                return jpeg.AsSpan(index).ToArray();
            }
        }

        throw new InvalidOperationException("No start-of-scan marker in the fixture.");
    }

    private static byte[] WithSegment(byte[] jpeg, byte marker, byte[] payload)
    {
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = marker;
        segment[2] = (byte)((payload.Length + 2) >> 8);
        segment[3] = (byte)((payload.Length + 2) & 0xFF);
        payload.CopyTo(segment.AsSpan(4));

        var result = new byte[jpeg.Length + segment.Length];
        jpeg.AsSpan(0, 2).CopyTo(result);
        segment.CopyTo(result.AsSpan(2));
        jpeg.AsSpan(2).CopyTo(result.AsSpan(2 + segment.Length));
        return result;
    }
}
