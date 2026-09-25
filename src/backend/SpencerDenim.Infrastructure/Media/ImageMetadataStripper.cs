using System.Buffers.Binary;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Infrastructure.Media;

/// <summary>
/// Removes the metadata blocks from a JPEG, PNG or WebP <b>without touching the pixels</b>.
/// <para>
/// This matters because the file stored under <c>public/originals/…</c> is served (that key is what
/// <c>MediaAssets.StorageKey</c> holds and what the storefront's image URL is built from), and a
/// photograph out of a camera or a phone carries EXIF: GPS coordinates, the camera's serial number, the
/// owner's name, the shoot's date and time, and sometimes an embedded thumbnail of an earlier edit.
/// Publishing a client's product photograph should not publish where it was taken.
/// </para>
/// <para>
/// <b>Lossless, deliberately.</b> Re-encoding would cost a generation of quality on the archive copy of
/// every photograph the client ever uploads. So the container is rewritten and the compressed image data
/// is copied through byte for byte: the JPEG scan data, the PNG <c>IDAT</c> chunks and the WebP
/// <c>VP8</c>/<c>VP8L</c> payload are never decoded.
/// </para>
/// <para>
/// <b>What is kept.</b> Anything that changes how the pixels look: the JPEG JFIF (<c>APP0</c>), ICC
/// profile (<c>APP2</c>) and Adobe colour-transform (<c>APP14</c>) segments, and the PNG colour and
/// transparency chunks. Dropping an ICC profile would visibly shift the colour of a product photograph,
/// which is the one thing a fashion retailer cannot have.
/// </para>
/// <para>
/// <b>What this does not solve.</b> EXIF also carries <i>orientation</i>, and a JPEG that relies on it
/// would display rotated once the EXIF is gone. The caller checks the orientation first and only asks for
/// a strip when it is the default; a rotated source is re-encoded upright instead (see
/// <c>SkiaImageProcessor</c>).
/// </para>
/// </summary>
public static class ImageMetadataStripper
{
    private const int PngSignatureLength = 8;
    private const int PngChunkHeaderLength = 8;
    private const int PngChunkCrcLength = 4;

    /// <summary>The WebP <c>VP8X</c> flag bit that says an EXIF chunk is present.</summary>
    private const byte WebpExifFlag = 0x08;

    /// <summary>The WebP <c>VP8X</c> flag bit that says an XMP chunk is present.</summary>
    private const byte WebpXmpFlag = 0x04;

    /// <summary>
    /// PNG chunks that hold text or metadata. Everything else is copied, so an unknown chunk that affects
    /// rendering survives; the privacy-carrying ones are all here.
    /// </summary>
    private static readonly string[] PngChunksToDrop = ["eXIf", "tEXt", "zTXt", "iTXt", "tIME", "dSIG"];

    /// <summary>
    /// Strips metadata from <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The whole file.</param>
    /// <param name="kind">The signature-detected kind; only JPEG, PNG and WebP are handled.</param>
    /// <param name="stripped">
    /// The rewritten file. When nothing needed removing this is <paramref name="source"/> copied, so the
    /// caller always has one thing to store.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when the container could not be parsed — a truncated or malformed file that
    /// happens to carry a valid signature. The caller then refuses the upload rather than storing bytes it
    /// could not read.
    /// </returns>
    public static bool TryStrip(ReadOnlySpan<byte> source, MediaFileKind kind, out byte[] stripped)
    {
        stripped = [];

        return kind switch
        {
            MediaFileKind.Jpeg => TryStripJpeg(source, out stripped),
            MediaFileKind.Png => TryStripPng(source, out stripped),
            MediaFileKind.Webp => TryStripWebp(source, out stripped),
            _ => false,
        };
    }

    // ------------------------------------------------------------------ JPEG

    private static bool TryStripJpeg(ReadOnlySpan<byte> source, out byte[] stripped)
    {
        stripped = [];

        if (source.Length < 4 || source[0] != 0xFF || source[1] != 0xD8)
        {
            return false;
        }

        var output = new MemoryStream(source.Length);
        output.WriteByte(0xFF);
        output.WriteByte(0xD8);

        var position = 2;

        while (true)
        {
            // Fill bytes: a conforming encoder writes one 0xFF before a marker, but padding is legal.
            while (position < source.Length && source[position] == 0xFF
                   && position + 1 < source.Length && source[position + 1] == 0xFF)
            {
                position++;
            }

            if (position + 1 >= source.Length)
            {
                // Ran out before a terminating marker. A truncated file.
                return false;
            }

            if (source[position] != 0xFF)
            {
                return false;
            }

            var marker = source[position + 1];
            position += 2;

            // Markers that carry no payload.
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                output.WriteByte(0xFF);
                output.WriteByte(marker);
                continue;
            }

            if (marker == 0xD9)
            {
                // End of image.
                output.WriteByte(0xFF);
                output.WriteByte(marker);
                stripped = output.ToArray();
                return true;
            }

            if (position + 2 > source.Length)
            {
                return false;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(source.Slice(position, 2));
            if (segmentLength < 2 || position + segmentLength > source.Length)
            {
                return false;
            }

            var segment = source.Slice(position, segmentLength);

            if (marker == 0xDA)
            {
                // Start of scan: the entropy-coded data runs to the end of the file (a progressive JPEG
                // has several scans, and copying the remainder verbatim keeps all of them, together with
                // any trailing EOI). Nothing after this point can be a metadata segment we would drop,
                // so there is no reason to walk the scan data.
                output.WriteByte(0xFF);
                output.WriteByte(marker);
                output.Write(segment);
                output.Write(source[(position + segmentLength)..]);
                stripped = output.ToArray();
                return true;
            }

            if (!IsJpegSegmentToDrop(marker))
            {
                output.WriteByte(0xFF);
                output.WriteByte(marker);
                output.Write(segment);
            }

            position += segmentLength;
        }
    }

    /// <summary>
    /// Whether a JPEG marker segment carries metadata rather than image or colour information.
    /// <list type="bullet">
    /// <item><c>APP1</c> — EXIF (GPS, camera serial, owner) and XMP.</item>
    /// <item><c>APP3</c>–<c>APP12</c> — Meta, maker notes, Picture Info, Ducky, and the rest.</item>
    /// <item><c>APP13</c> — Photoshop image resources, which carry IPTC (author, location, credit).</item>
    /// <item><c>APP15</c> — unspecified.</item>
    /// <item><c>COM</c> — a free-text comment, the classic place to hide a payload.</item>
    /// </list>
    /// <c>APP0</c> (JFIF density), <c>APP2</c> (ICC profile) and <c>APP14</c> (Adobe colour transform) are
    /// kept: dropping them changes how the image is displayed.
    /// </summary>
    private static bool IsJpegSegmentToDrop(byte marker) =>
        marker == 0xE1 || (marker >= 0xE3 && marker <= 0xED) || marker == 0xEF || marker == 0xFE;

    // ------------------------------------------------------------------- PNG

    private static bool TryStripPng(ReadOnlySpan<byte> source, out byte[] stripped)
    {
        stripped = [];

        if (source.Length < PngSignatureLength + PngChunkHeaderLength + PngChunkCrcLength)
        {
            return false;
        }

        var output = new MemoryStream(source.Length);
        output.Write(source[..PngSignatureLength]);

        var position = PngSignatureLength;
        var sawEnd = false;

        while (position + PngChunkHeaderLength + PngChunkCrcLength <= source.Length)
        {
            var dataLength = BinaryPrimitives.ReadUInt32BigEndian(source.Slice(position, 4));

            // A PNG chunk length is capped at 2^31 - 1 by the specification. The cast below has to be
            // safe, and a declared length past the end of the file is a malformed chunk, not a chunk to
            // skip: a reader that trusted it would read out of bounds.
            if (dataLength > int.MaxValue)
            {
                return false;
            }

            var totalLength = PngChunkHeaderLength + (int)dataLength + PngChunkCrcLength;
            if (position + totalLength > source.Length)
            {
                return false;
            }

            var type = GetAsciiType(source.Slice(position + 4, 4));

            if (!ShouldDropPngChunk(type))
            {
                output.Write(source.Slice(position, totalLength));
            }

            position += totalLength;

            if (string.Equals(type, "IEND", StringComparison.Ordinal))
            {
                sawEnd = true;
                break;
            }
        }

        if (!sawEnd)
        {
            return false;
        }

        stripped = output.ToArray();
        return true;
    }

    private static bool ShouldDropPngChunk(string type)
    {
        foreach (var candidate in PngChunksToDrop)
        {
            if (string.Equals(type, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ WebP

    private static bool TryStripWebp(ReadOnlySpan<byte> source, out byte[] stripped)
    {
        stripped = [];

        const int riffHeaderLength = 12;
        if (source.Length < riffHeaderLength + PngChunkHeaderLength)
        {
            return false;
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4, 4));
        if (declared > int.MaxValue)
        {
            return false;
        }

        // The RIFF size counts everything after the size field itself. A file may be padded, so accept a
        // declared size that is not longer than what we have.
        var end = Math.Min(source.Length, 8 + (int)declared);

        var body = new MemoryStream(source.Length);
        var position = riffHeaderLength;
        int? vp8xFlagsOffset = null;

        while (position + 8 <= end)
        {
            var fourCc = GetAsciiType(source.Slice(position, 4));
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(position + 4, 4));

            if (chunkSize > int.MaxValue)
            {
                return false;
            }

            // Chunk payloads are padded to an even length.
            var padded = (int)chunkSize + ((chunkSize % 2 == 1) ? 1 : 0);
            if (position + 8 + padded > end)
            {
                return false;
            }

            var drop = string.Equals(fourCc, "EXIF", StringComparison.Ordinal)
                || string.Equals(fourCc, "XMP ", StringComparison.Ordinal);

            if (!drop)
            {
                var start = (int)body.Length;
                body.Write(source.Slice(position, 8 + padded));

                if (string.Equals(fourCc, "VP8X", StringComparison.Ordinal) && chunkSize >= 1)
                {
                    // Remember where the flags byte landed, so the EXIF/XMP bits can be cleared once the
                    // chunks themselves are gone. A decoder that trusted the flag and found no chunk
                    // would be reading a file that contradicts itself.
                    vp8xFlagsOffset = start + 8;
                }
            }

            position += 8 + padded;
        }

        var bytes = body.ToArray();

        if (vp8xFlagsOffset is { } flagsOffset && flagsOffset < bytes.Length)
        {
            bytes[flagsOffset] &= unchecked((byte)~(WebpExifFlag | WebpXmpFlag));
        }

        var result = new byte[riffHeaderLength + bytes.Length];
        source[..riffHeaderLength].CopyTo(result);
        bytes.CopyTo(result.AsSpan(riffHeaderLength));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)(result.Length - 8));

        stripped = result;
        return true;
    }

    private static string GetAsciiType(ReadOnlySpan<byte> four)
    {
        Span<char> characters = stackalloc char[4];
        for (var index = 0; index < 4; index++)
        {
            characters[index] = (char)four[index];
        }

        return new string(characters);
    }
}
