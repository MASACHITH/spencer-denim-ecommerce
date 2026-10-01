using SpencerDenim.Domain.Media;

namespace SpencerDenim.Infrastructure.Media.ClientAssets;

/// <summary>
/// Whether an image file can carry a transparent background, read from its header alone.
/// <para>
/// This exists for one sentence in the import report. <c>client-assets/README.md</c> asks for the
/// wordmarks as "PNG with a transparent background", and the three files the client delivered are
/// JPEGs with a <c>.png</c> name — which the media pipeline handled correctly, because it reads the
/// signature and ignores the extension, and which therefore left no trace in the report the operator
/// actually reads. A logo on an opaque rectangle cannot be drawn over a photograph or a dark section,
/// so it is worth saying out loud at import time rather than discovering in a design review.
/// </para>
/// <para>
/// <b>It reads bytes, it does not decode.</b> A file is opened for a few dozen bytes; nothing is
/// decompressed, so this costs nothing even on a 15 MB image. It answers <see langword="null"/> — "I do
/// not know" — for anything it cannot read confidently, because a wrong claim in a report is worse than
/// no claim.
/// </para>
/// </summary>
public static class ImageTransparency
{
    /// <summary>PNG's eight-byte signature.</summary>
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>How many bytes are needed to reach a PNG's colour-type byte, or a RIFF/WebP header.</summary>
    private const int HeaderBytes = 32;

    /// <summary>
    /// Whether the file can hold per-pixel transparency: <see langword="true"/> yes,
    /// <see langword="false"/> definitely not, <see langword="null"/> unknown.
    /// </summary>
    public static bool? Of(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var header = ReadHeader(path);
        return header is null ? null : Of(header);
    }

    /// <summary>The same, from a header already in hand.</summary>
    public static bool? Of(ReadOnlySpan<byte> header)
    {
        var kind = MediaFileSignature.Detect(header);

        return kind switch
        {
            // JPEG has no alpha channel. There is no variant of it that does.
            MediaFileKind.Jpeg => false,

            // PNG's IHDR colour type is byte 25: 4 is grey+alpha and 6 is RGBA, so both carry alpha.
            // 3 is a palette, which can only be transparent through a tRNS chunk further in — not read
            // here, so it is reported as unknown rather than guessed at.
            MediaFileKind.Png when header.Length > 25 => header[25] switch
            {
                4 or 6 => true,
                0 or 2 => false,
                _ => null,
            },

            // A WebP with alpha is either lossless (VP8L) or extended (VP8X with the alpha flag set in
            // the first byte of its chunk payload). A plain VP8 chunk is lossy and opaque.
            MediaFileKind.Webp when header.Length > 20 => Webp(header),

            _ => null,
        };
    }

    /// <summary>A phrase for the report: what the file turned out to be.</summary>
    public static string Describe(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var header = ReadHeader(path);
        var kind = header is null ? MediaFileKind.Unknown : MediaFileSignature.Detect(header);

        return kind switch
        {
            MediaFileKind.Jpeg => "a JPEG, which has no alpha channel at all",
            MediaFileKind.Png => "a PNG with no alpha channel",
            MediaFileKind.Webp => "a WebP with no alpha channel",
            _ => "an image with no alpha channel",
        };
    }

    private static bool? Webp(ReadOnlySpan<byte> header)
    {
        var fourCc = header[12..16];

        if (fourCc.SequenceEqual("VP8L"u8))
        {
            return true;
        }

        if (fourCc.SequenceEqual("VP8X"u8))
        {
            // The flags byte follows the chunk's four-byte size; bit 4 (0x10) is ALPHA.
            return header.Length > 20 && (header[20] & 0x10) != 0;
        }

        return fourCc.SequenceEqual("VP8 "u8) ? false : null;
    }

    private static byte[]? ReadHeader(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[HeaderBytes];
            var read = stream.ReadAtLeast(buffer, HeaderBytes, throwOnEndOfStream: false);
            return read == 0 ? null : buffer[..read];
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Kept so the PNG signature constant is used, and so a reader can see what it is for.</summary>
    internal static bool LooksLikePng(ReadOnlySpan<byte> header) =>
        header.Length >= PngSignature.Length && header[..PngSignature.Length].SequenceEqual(PngSignature);
}
