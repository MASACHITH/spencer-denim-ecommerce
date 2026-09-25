using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace SpencerDenim.Tests.MediaFixtures;

/// <summary>
/// Builds the files the media tests need: real images, images carrying real EXIF, and the hostile ones.
/// <para>
/// Everything is generated rather than checked in, so a reader can see exactly what is in each file and a
/// reviewer does not have to trust a binary fixture. The EXIF blocks below are genuine TIFF structures —
/// the tests assert that <c>SKCodec</c> parses them before asserting that the stripper removes them, so a
/// "the EXIF is gone" result can never come from the EXIF never having been there.
/// </para>
/// </summary>
public static class TestImages
{
    /// <summary>The camera serial written into the EXIF <c>Make</c> tag, so a test can look for it.</summary>
    public const string CameraSerial = "SPENCER-CAM-SERIAL-12345";

    /// <summary>
    /// A JPEG whose bytes no other call will produce.
    /// <para>
    /// The library deduplicates on the SHA-256 of the upload, and the integration tests share one database,
    /// so two tests uploading "a JPEG" would be uploading <i>the same</i> JPEG and the second would be
    /// answered from the checksum index. A test that means to create an asset asks for a unique one.
    /// </para>
    /// </summary>
    public static byte[] UniqueJpeg(int width = 640, int height = 480) => Jpeg(width, height, UniqueColour());

    /// <summary>A PNG whose bytes no other call will produce. See <see cref="UniqueJpeg"/>.</summary>
    public static byte[] UniquePng(int width = 320, int height = 240) => Png(width, height, UniqueColour());

    /// <summary>A colour drawn from a fresh GUID, so two images differ in every block of the encoding.</summary>
    public static SKColor UniqueColour()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        return new SKColor(bytes[0], bytes[1], bytes[2]);
    }

    /// <summary>A plain JPEG with no metadata beyond what the encoder writes.</summary>
    public static byte[] Jpeg(int width = 640, int height = 480, SKColor? colour = null) =>
        Encode(width, height, SKEncodedImageFormat.Jpeg, 85, colour);

    /// <summary>A plain PNG.</summary>
    public static byte[] Png(int width = 320, int height = 240, SKColor? colour = null) =>
        Encode(width, height, SKEncodedImageFormat.Png, 100, colour);

    /// <summary>A plain WebP.</summary>
    public static byte[] Webp(int width = 320, int height = 240, SKColor? colour = null) =>
        Encode(width, height, SKEncodedImageFormat.Webp, 85, colour);

    /// <summary>
    /// A JPEG carrying a real EXIF <c>APP1</c> segment: an orientation tag, a camera "Make" string standing
    /// in for a serial number, and a GPS IFD with a latitude and longitude. This is what comes off a phone.
    /// </summary>
    /// <param name="orientation">The EXIF orientation value, 1–8.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    public static byte[] JpegWithExif(int orientation = 1, int width = 640, int height = 480)
    {
        var jpeg = Jpeg(width, height);
        var exif = BuildExifApp1(orientation);

        // Straight after SOI, which is where a camera writes it.
        var result = new byte[jpeg.Length + exif.Length];
        jpeg.AsSpan(0, 2).CopyTo(result);
        exif.CopyTo(result.AsSpan(2));
        jpeg.AsSpan(2).CopyTo(result.AsSpan(2 + exif.Length));
        return result;
    }

    /// <summary>A JPEG with a <c>COM</c> comment segment carrying a payload, the classic hiding place.</summary>
    public static byte[] JpegWithComment(string comment)
    {
        var jpeg = Jpeg();
        var text = Encoding.ASCII.GetBytes(comment);
        var segment = new byte[4 + text.Length];
        segment[0] = 0xFF;
        segment[1] = 0xFE;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2, 2), (ushort)(text.Length + 2));
        text.CopyTo(segment.AsSpan(4));

        var result = new byte[jpeg.Length + segment.Length];
        jpeg.AsSpan(0, 2).CopyTo(result);
        segment.CopyTo(result.AsSpan(2));
        jpeg.AsSpan(2).CopyTo(result.AsSpan(2 + segment.Length));
        return result;
    }

    /// <summary>A PNG with an <c>eXIf</c> chunk and a <c>tEXt</c> chunk inserted after <c>IHDR</c>.</summary>
    public static byte[] PngWithMetadata()
    {
        var png = Png();
        var exif = PngChunk("eXIf", BuildTiff(orientation: 1));
        var text = PngChunk("tEXt", Encoding.ASCII.GetBytes("Author\0" + CameraSerial));

        // IHDR is always the first chunk: 8 signature bytes + 4 length + 4 type + 13 data + 4 CRC = 33.
        const int afterHeader = 8 + 4 + 4 + 13 + 4;

        var result = new byte[png.Length + exif.Length + text.Length];
        png.AsSpan(0, afterHeader).CopyTo(result);
        exif.CopyTo(result.AsSpan(afterHeader));
        text.CopyTo(result.AsSpan(afterHeader + exif.Length));
        png.AsSpan(afterHeader).CopyTo(result.AsSpan(afterHeader + exif.Length + text.Length));
        return result;
    }

    /// <summary>
    /// A WebP with an <c>EXIF</c> chunk appended and a <c>VP8X</c> header in front declaring it, which is
    /// the only legal way to carry metadata in a WebP.
    /// </summary>
    public static byte[] WebpWithExif()
    {
        var simple = Webp();

        // A "simple" WebP is RIFF/WEBP/VP8 or VP8L. Wrap it in an extended file: VP8X first, then the
        // original image chunk, then EXIF.
        var imageChunk = simple.AsSpan(12).ToArray();
        var exifPayload = BuildTiff(orientation: 1);

        using var body = new MemoryStream();

        // VP8X: flags (1) + reserved (3) + canvas width-1 (3) + canvas height-1 (3).
        var vp8x = new byte[10];
        vp8x[0] = 0x08; // EXIF present.
        WriteUInt24LittleEndian(vp8x.AsSpan(4, 3), 320 - 1);
        WriteUInt24LittleEndian(vp8x.AsSpan(7, 3), 240 - 1);
        body.Write(RiffChunk("VP8X", vp8x));

        body.Write(imageChunk);
        body.Write(RiffChunk("EXIF", exifPayload));

        var bodyBytes = body.ToArray();
        var result = new byte[12 + bodyBytes.Length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(result.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)(result.Length - 8));
        Encoding.ASCII.GetBytes("WEBP").CopyTo(result.AsSpan(8, 4));
        bodyBytes.CopyTo(result.AsSpan(12));
        return result;
    }

    /// <summary>
    /// A "polyglot": a valid JPEG with an HTML script appended past the end-of-image marker. Browsers
    /// render the picture; a naive server that sniffed the type or let the filename choose it would serve
    /// the script.
    /// </summary>
    public static byte[] JpegWithAppendedScript(string script)
    {
        var jpeg = Jpeg();
        var payload = Encoding.ASCII.GetBytes(script);
        var result = new byte[jpeg.Length + payload.Length];
        jpeg.CopyTo(result.AsSpan());
        payload.CopyTo(result.AsSpan(jpeg.Length));
        return result;
    }

    /// <summary>
    /// A PNG header that declares an enormous canvas with almost no data behind it: the decompression bomb.
    /// Nothing decodes it, which is the point — the dimensions are read from the header.
    /// </summary>
    public static byte[] PngDeclaring(int width, int height)
    {
        var png = Png(8, 8);

        // IHDR data starts 16 bytes in: 8 signature + 4 length + 4 type.
        var result = png.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(20, 4), height);
        FixPngCrc(result, chunkStart: 8);
        return result;
    }

    private static byte[] Encode(int width, int height, SKEncodedImageFormat format, int quality, SKColor? colour)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(colour ?? new SKColor(0x9A, 0xB5, 0xC8));

            // Something other than a flat field, so a resize has work to do and two colours differ.
            using var paint = new SKPaint { Color = new SKColor(0x22, 0x33, 0x44), IsAntialias = true };
            canvas.DrawCircle(width / 3f, height / 3f, Math.Min(width, height) / 4f, paint);
            canvas.DrawRect(new SKRect(width / 2f, height / 2f, width * 0.9f, height * 0.9f), paint);
        }

        using var data = bitmap.Encode(format, quality);
        return data.ToArray();
    }

    // -------------------------------------------------------------- EXIF/TIFF

    private static byte[] BuildExifApp1(int orientation)
    {
        var tiff = BuildTiff(orientation);
        var payload = new byte[6 + tiff.Length];
        Encoding.ASCII.GetBytes("Exif\0\0").CopyTo(payload.AsSpan(0, 6));
        tiff.CopyTo(payload.AsSpan(6));

        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2, 2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment.AsSpan(4));
        return segment;
    }

    /// <summary>
    /// A little-endian TIFF block: IFD0 with Orientation, Make and a GPS IFD pointer, then the GPS IFD with
    /// a real latitude and longitude. Offsets are laid out by hand, which is what makes it a real structure
    /// rather than a blob that happens to start with "Exif".
    /// </summary>
    private static byte[] BuildTiff(int orientation)
    {
        var make = Encoding.ASCII.GetBytes(CameraSerial + "\0");

        const int ifd0Offset = 8;
        const int ifd0Length = 2 + (3 * 12) + 4;
        const int makeOffset = ifd0Offset + ifd0Length;
        var gpsIfdOffset = makeOffset + ((make.Length + 1) & ~1);
        const int gpsIfdLength = 2 + (4 * 12) + 4;
        var latitudeOffset = gpsIfdOffset + gpsIfdLength;
        var longitudeOffset = latitudeOffset + 24;
        var total = longitudeOffset + 24;

        var tiff = new byte[total];
        var span = tiff.AsSpan();

        // Header: "II", 42, offset of IFD0.
        span[0] = (byte)'I';
        span[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2, 2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4, 4), ifd0Offset);

        // IFD0.
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(ifd0Offset, 2), 3);
        WriteEntry(span, ifd0Offset + 2, tag: 0x0112, type: 3, count: 1, value: (uint)orientation);
        WriteEntry(span, ifd0Offset + 14, tag: 0x010F, type: 2, count: (uint)make.Length, value: (uint)makeOffset);
        WriteEntry(span, ifd0Offset + 26, tag: 0x8825, type: 4, count: 1, value: (uint)gpsIfdOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(ifd0Offset + 38, 4), 0);

        make.CopyTo(span[makeOffset..]);

        // GPS IFD: 6°55'37"N, 79°51'40"E — Colombo.
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(gpsIfdOffset, 2), 4);
        WriteEntry(span, gpsIfdOffset + 2, tag: 0x0001, type: 2, count: 2, value: (uint)'N');
        WriteEntry(span, gpsIfdOffset + 14, tag: 0x0002, type: 5, count: 3, value: (uint)latitudeOffset);
        WriteEntry(span, gpsIfdOffset + 26, tag: 0x0003, type: 2, count: 2, value: (uint)'E');
        WriteEntry(span, gpsIfdOffset + 38, tag: 0x0004, type: 5, count: 3, value: (uint)longitudeOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(gpsIfdOffset + 50, 4), 0);

        WriteRational(span, latitudeOffset, 6, 1);
        WriteRational(span, latitudeOffset + 8, 55, 1);
        WriteRational(span, latitudeOffset + 16, 37, 1);
        WriteRational(span, longitudeOffset, 79, 1);
        WriteRational(span, longitudeOffset + 8, 51, 1);
        WriteRational(span, longitudeOffset + 16, 40, 1);

        return tiff;
    }

    private static void WriteEntry(Span<byte> span, int offset, ushort tag, ushort type, uint count, uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, 2), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset + 2, 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset + 4, 4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset + 8, 4), value);
    }

    private static void WriteRational(Span<byte> span, int offset, uint numerator, uint denominator)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, 4), numerator);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset + 4, 4), denominator);
    }

    // --------------------------------------------------------------- PNG/RIFF

    private static byte[] PngChunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk.AsSpan(4, 4));
        data.CopyTo(chunk.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length, 4), Crc32(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    private static void FixPngCrc(byte[] png, int chunkStart)
    {
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(chunkStart, 4));
        BinaryPrimitives.WriteUInt32BigEndian(
            png.AsSpan(chunkStart + 8 + length, 4),
            Crc32(png.AsSpan(chunkStart + 4, 4 + length)));
    }

    private static byte[] RiffChunk(string fourCc, byte[] data)
    {
        var padded = data.Length + (data.Length % 2);
        var chunk = new byte[8 + padded];
        Encoding.ASCII.GetBytes(fourCc).CopyTo(chunk.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4, 4), (uint)data.Length);
        data.CopyTo(chunk.AsSpan(8));
        return chunk;
    }

    private static void WriteUInt24LittleEndian(Span<byte> span, int value)
    {
        span[0] = (byte)(value & 0xFF);
        span[1] = (byte)((value >> 8) & 0xFF);
        span[2] = (byte)((value >> 16) & 0xFF);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
