using System.Text;
using SpencerDenim.Domain.Media;
using SpencerDenim.Tests.MediaFixtures;
using SpencerDenim.UnitTests.Support;

namespace SpencerDenim.UnitTests.Domain.Media;

/// <summary>
/// Doc 03 §3b: "type and size checked by file signature (not just extension)". These prove the detector
/// reads the bytes and nothing else.
/// </summary>
public sealed class MediaFileSignatureTests
{
    [Fact]
    public void A_real_jpeg_is_detected_as_jpeg()
    {
        Assert.Equal(MediaFileKind.Jpeg, MediaFileSignature.Detect(TestImages.Jpeg()));
    }

    [Fact]
    public void A_real_png_is_detected_as_png()
    {
        Assert.Equal(MediaFileKind.Png, MediaFileSignature.Detect(TestImages.Png()));
    }

    [Fact]
    public void A_real_webp_is_detected_as_webp()
    {
        Assert.Equal(MediaFileKind.Webp, MediaFileSignature.Detect(TestImages.Webp()));
    }

    [Fact]
    public void A_real_mp4_is_detected_as_mp4()
    {
        Assert.Equal(MediaFileKind.Mp4, MediaFileSignature.Detect(TestVideos.Mp4()));
    }

    /// <summary>
    /// The case the rule exists for: a file called <c>lookbook.jpg</c>, sent as <c>image/jpeg</c>, whose
    /// contents are a shell script. Nothing about the name or the declared type reaches the detector, so it
    /// answers on the bytes.
    /// </summary>
    [Fact]
    public void A_script_named_as_an_image_is_detected_as_a_script()
    {
        var bytes = Encoding.ASCII.GetBytes("#!/bin/sh\nrm -rf /srv/spencer-denim\n");

        Assert.Equal(MediaFileKind.Script, MediaFileSignature.Detect(bytes));
        Assert.Null(MediaFileSignature.ContentTypeFor(MediaFileSignature.Detect(bytes)));
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><body><script>fetch('/api/admin')</script>", MediaFileKind.Html)]
    [InlineData("<html>", MediaFileKind.Html)]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>", MediaFileKind.Svg)]
    [InlineData("<?xml version=\"1.0\"?><svg/>", MediaFileKind.Svg)]
    public void Markup_is_recognised_and_is_not_an_image(string content, MediaFileKind expected)
    {
        var kind = MediaFileSignature.Detect(Encoding.UTF8.GetBytes(content));

        Assert.Equal(expected, kind);
        Assert.False(MediaFileSignature.IsImage(kind));
    }

    /// <summary>Leading whitespace and a byte-order mark do not hide markup.</summary>
    [Fact]
    public void Markup_behind_a_byte_order_mark_and_whitespace_is_still_markup()
    {
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("\r\n   <html><body>hello"))
            .ToArray();

        Assert.Equal(MediaFileKind.Html, MediaFileSignature.Detect(bytes));
    }

    [Theory]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0 }, MediaFileKind.Zip)]
    [InlineData(new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0, 0, 0, 0 }, MediaFileKind.Gzip)]
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0, 0, 0, 0 }, MediaFileKind.WindowsExecutable)]
    [InlineData(new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0, 0, 0, 0 }, MediaFileKind.ElfExecutable)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0, 0 }, MediaFileKind.Gif)]
    [InlineData(new byte[] { 0x42, 0x4D, 0x36, 0x00, 0, 0, 0, 0 }, MediaFileKind.Bmp)]
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00, 0, 0, 0, 0 }, MediaFileKind.Tiff)]
    public void Archives_executables_and_the_other_image_formats_are_recognised_and_refused(
        byte[] header,
        MediaFileKind expected)
    {
        var kind = MediaFileSignature.Detect(header);

        Assert.Equal(expected, kind);
        Assert.Null(MediaFileSignature.ContentTypeFor(kind));
        Assert.Null(MediaFileSignature.ExtensionFor(kind));
    }

    /// <summary>
    /// A zip bomb and a .docx have the same signature, so the rejection says so rather than guessing which
    /// mistake somebody made.
    /// </summary>
    [Fact]
    public void A_zip_rejection_names_both_things_a_zip_might_be()
    {
        Assert.Contains("Office", MediaFileSignature.Describe(MediaFileKind.Zip), StringComparison.Ordinal);
    }

    /// <summary>
    /// HEIC, AVIF, QuickTime and MP4 all open with the same <c>ftyp</c> box; only the brand tells them
    /// apart. Getting this wrong would let an iPhone photograph through as a video.
    /// </summary>
    [Theory]
    [InlineData("heic", MediaFileKind.Heic)]
    [InlineData("mif1", MediaFileKind.Heic)]
    [InlineData("avif", MediaFileKind.Avif)]
    [InlineData("qt  ", MediaFileKind.Quicktime)]
    [InlineData("mp42", MediaFileKind.Mp4)]
    [InlineData("isom", MediaFileKind.Mp4)]
    [InlineData("avc1", MediaFileKind.Mp4)]
    public void Iso_base_media_brands_are_told_apart(string brand, MediaFileKind expected)
    {
        var header = new byte[16];
        Encoding.ASCII.GetBytes("ftyp").CopyTo(header.AsSpan(4, 4));
        Encoding.ASCII.GetBytes(brand).CopyTo(header.AsSpan(8, 4));

        Assert.Equal(expected, MediaFileSignature.Detect(header));
    }

    /// <summary>The HEIC rejection tells the staff member what to do, because this one will happen weekly.</summary>
    [Fact]
    public void The_heic_rejection_says_how_to_fix_it()
    {
        Assert.Contains("JPEG", MediaFileSignature.Describe(MediaFileKind.Heic), StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_or_tiny_file_is_unknown()
    {
        Assert.Equal(MediaFileKind.Unknown, MediaFileSignature.Detect([]));
        Assert.Equal(MediaFileKind.Unknown, MediaFileSignature.Detect([0xFF, 0xD8]));
    }

    /// <summary>
    /// A polyglot still detects as the image it starts with, which is correct — the defence against it is
    /// that the image is re-encoded and the response says <c>nosniff</c>, not that the detector refuses it.
    /// This pins the behaviour so nobody later "fixes" it into a false sense of security.
    /// </summary>
    [Fact]
    public void A_jpeg_with_a_script_appended_is_still_a_jpeg_to_the_detector()
    {
        var bytes = TestImages.JpegWithAppendedScript("<script>alert('x')</script>");

        Assert.Equal(MediaFileKind.Jpeg, MediaFileSignature.Detect(bytes));
    }

    [Theory]
    [InlineData(MediaFileKind.Jpeg, "image/jpeg", ".jpg")]
    [InlineData(MediaFileKind.Png, "image/png", ".png")]
    [InlineData(MediaFileKind.Webp, "image/webp", ".webp")]
    [InlineData(MediaFileKind.Mp4, "video/mp4", ".mp4")]
    [InlineData(MediaFileKind.Pdf, "application/pdf", ".pdf")]
    public void An_accepted_kind_has_a_content_type_and_an_extension_this_server_chose(
        MediaFileKind kind,
        string contentType,
        string extension)
    {
        Assert.Equal(contentType, MediaFileSignature.ContentTypeFor(kind));
        Assert.Equal(extension, MediaFileSignature.ExtensionFor(kind));
        Assert.True(MediaStorageKey.IsSafeExtension(extension));
    }
}
