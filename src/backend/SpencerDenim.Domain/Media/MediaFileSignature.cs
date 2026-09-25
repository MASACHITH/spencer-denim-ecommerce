namespace SpencerDenim.Domain.Media;

/// <summary>
/// What the first bytes of an uploaded file actually are. The ones outside the allow-list exist so a
/// rejection can say what was recognised instead of "unsupported file" — a staff member who uploaded
/// a HEIC straight off an iPhone, or a .zip of photographs, gets told which mistake they made.
/// </summary>
public enum MediaFileKind
{
    /// <summary>Nothing recognised.</summary>
    Unknown = 0,

    Jpeg,
    Png,
    Webp,
    Mp4,
    Pdf,

    // Recognised and refused.
    Gif,
    Bmp,
    Tiff,
    Heic,
    Avif,
    Svg,
    Zip,
    Gzip,
    Rar,
    SevenZip,
    WindowsExecutable,
    ElfExecutable,
    Html,
    Script,
    Quicktime,
    Webm,
    Avi,
}

/// <summary>
/// Type detection by file signature, which is the only kind doc 03 §3b accepts: "type and size checked
/// by file signature (not just extension)".
/// <para>
/// A browser's <c>Content-Type</c> on a multipart part and the extension on the filename are both
/// attacker-controlled, so neither is consulted. Everything downstream — the stored extension, the
/// stored <c>ContentType</c>, which pipeline runs — comes from what this returns.
/// </para>
/// <para>
/// A signature match is a claim about the first bytes, not a promise about the rest of the file, so it
/// is only the first of the defences. An image is decoded and re-encoded for every rendition, and the
/// original has its metadata stripped, so a script hidden in an EXIF comment or appended after the
/// image data does not survive; and everything is served with <c>X-Content-Type-Options: nosniff</c>
/// under a generated filename, so a polyglot is never interpreted as the other thing it also is.
/// </para>
/// </summary>
public static class MediaFileSignature
{
    /// <summary>
    /// How many leading bytes <see cref="Detect"/> needs. 32 covers every signature below, including
    /// the ISO base-media <c>ftyp</c> brand at offset 8 and the WebP form type at offset 8.
    /// </summary>
    public const int RequiredBytes = 32;

    /// <summary>Identifies the content from its leading bytes.</summary>
    /// <param name="header">The first bytes of the file; at least <see cref="RequiredBytes"/> where possible.</param>
    public static MediaFileKind Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
        {
            return MediaFileKind.Unknown;
        }

        // Images we accept.
        if (StartsWith(header, [0xFF, 0xD8, 0xFF]))
        {
            return MediaFileKind.Jpeg;
        }

        if (StartsWith(header, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return MediaFileKind.Png;
        }

        if (IsRiff(header, "WEBP"))
        {
            return MediaFileKind.Webp;
        }

        if (StartsWith(header, "%PDF-"u8))
        {
            return MediaFileKind.Pdf;
        }

        // ISO base media container. The brand at offset 8 separates MP4 from QuickTime and HEIC/AVIF,
        // which share the same box structure and would otherwise all look like "an MP4".
        if (header.Length >= 12 && header.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return FromIsoBrand(header.Slice(8, 4));
        }

        // Recognised and refused.
        if (StartsWith(header, "GIF87a"u8) || StartsWith(header, "GIF89a"u8))
        {
            return MediaFileKind.Gif;
        }

        if (StartsWith(header, "BM"u8))
        {
            return MediaFileKind.Bmp;
        }

        if (StartsWith(header, [0x49, 0x49, 0x2A, 0x00]) || StartsWith(header, [0x4D, 0x4D, 0x00, 0x2A]))
        {
            return MediaFileKind.Tiff;
        }

        if (StartsWith(header, [0x50, 0x4B, 0x03, 0x04])
            || StartsWith(header, [0x50, 0x4B, 0x05, 0x06])
            || StartsWith(header, [0x50, 0x4B, 0x07, 0x08]))
        {
            // Also every .docx, .xlsx and .jar: a zip bomb and an Office file are the same signature.
            return MediaFileKind.Zip;
        }

        if (StartsWith(header, [0x1F, 0x8B]))
        {
            return MediaFileKind.Gzip;
        }

        if (StartsWith(header, "Rar!"u8))
        {
            return MediaFileKind.Rar;
        }

        if (StartsWith(header, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]))
        {
            return MediaFileKind.SevenZip;
        }

        if (StartsWith(header, "MZ"u8))
        {
            return MediaFileKind.WindowsExecutable;
        }

        if (StartsWith(header, [0x7F, 0x45, 0x4C, 0x46]))
        {
            return MediaFileKind.ElfExecutable;
        }

        if (StartsWith(header, [0x1A, 0x45, 0xDF, 0xA3]))
        {
            return MediaFileKind.Webm;
        }

        if (IsRiff(header, "AVI "))
        {
            return MediaFileKind.Avi;
        }

        return DetectText(header);
    }

    /// <summary>The media type to store for a kind the library accepts, or <see langword="null"/>.</summary>
    public static string? ContentTypeFor(MediaFileKind kind) => kind switch
    {
        MediaFileKind.Jpeg => "image/jpeg",
        MediaFileKind.Png => "image/png",
        MediaFileKind.Webp => "image/webp",
        MediaFileKind.Mp4 => "video/mp4",
        MediaFileKind.Pdf => "application/pdf",
        _ => null,
    };

    /// <summary>The extension to store a kind under, chosen here rather than taken from the upload.</summary>
    public static string? ExtensionFor(MediaFileKind kind) => kind switch
    {
        MediaFileKind.Jpeg => ".jpg",
        MediaFileKind.Png => ".png",
        MediaFileKind.Webp => ".webp",
        MediaFileKind.Mp4 => ".mp4",
        MediaFileKind.Pdf => ".pdf",
        _ => null,
    };

    /// <summary>Whether the kind is one of the three still-image formats the pipeline can decode.</summary>
    public static bool IsImage(MediaFileKind kind) =>
        kind is MediaFileKind.Jpeg or MediaFileKind.Png or MediaFileKind.Webp;

    /// <summary>A phrase naming the detected content, for a rejection a person has to act on.</summary>
    public static string Describe(MediaFileKind kind) => kind switch
    {
        MediaFileKind.Jpeg => "a JPEG image",
        MediaFileKind.Png => "a PNG image",
        MediaFileKind.Webp => "a WebP image",
        MediaFileKind.Mp4 => "an MP4 video",
        MediaFileKind.Pdf => "a PDF document",
        MediaFileKind.Gif => "a GIF image",
        MediaFileKind.Bmp => "a BMP image",
        MediaFileKind.Tiff => "a TIFF image",
        MediaFileKind.Heic => "a HEIC/HEIF image (an iPhone photograph: export it as JPEG first)",
        MediaFileKind.Avif => "an AVIF image",
        MediaFileKind.Svg => "an SVG file",
        MediaFileKind.Zip => "a ZIP archive (or an Office file, which uses the same format)",
        MediaFileKind.Gzip => "a gzip archive",
        MediaFileKind.Rar => "a RAR archive",
        MediaFileKind.SevenZip => "a 7-Zip archive",
        MediaFileKind.WindowsExecutable => "a Windows executable",
        MediaFileKind.ElfExecutable => "a Linux executable",
        MediaFileKind.Html => "an HTML or XML document",
        MediaFileKind.Script => "a script",
        MediaFileKind.Quicktime => "a QuickTime (.mov) video",
        MediaFileKind.Webm => "a WebM or Matroska video",
        MediaFileKind.Avi => "an AVI video",
        _ => "not a file type this library recognises",
    };

    private static MediaFileKind FromIsoBrand(ReadOnlySpan<byte> brand)
    {
        if (brand.SequenceEqual("qt  "u8))
        {
            return MediaFileKind.Quicktime;
        }

        if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8)
            || brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("mif1"u8)
            || brand.SequenceEqual("msf1"u8) || brand.SequenceEqual("heim"u8))
        {
            return MediaFileKind.Heic;
        }

        if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
        {
            return MediaFileKind.Avif;
        }

        // The MP4 brands, plus the ones a phone or an editor writes for an MP4 (3GPP shares the codecs).
        if (brand.SequenceEqual("isom"u8) || brand.SequenceEqual("iso2"u8) || brand.SequenceEqual("iso4"u8)
            || brand.SequenceEqual("iso5"u8) || brand.SequenceEqual("iso6"u8) || brand.SequenceEqual("mp41"u8)
            || brand.SequenceEqual("mp42"u8) || brand.SequenceEqual("avc1"u8) || brand.SequenceEqual("M4V "u8)
            || brand.SequenceEqual("dash"u8) || brand.SequenceEqual("3gp4"u8) || brand.SequenceEqual("3gp5"u8))
        {
            return MediaFileKind.Mp4;
        }

        return MediaFileKind.Unknown;
    }

    private static MediaFileKind DetectText(ReadOnlySpan<byte> header)
    {
        // A leading UTF-8 BOM, which an uploader's editor may have added.
        var text = StartsWith(header, [0xEF, 0xBB, 0xBF]) ? header[3..] : header;

        var start = 0;
        while (start < text.Length && text[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            start++;
        }

        text = text[start..];

        if (text.Length == 0)
        {
            return MediaFileKind.Unknown;
        }

        if (StartsWith(text, "#!"u8))
        {
            return MediaFileKind.Script;
        }

        if (text[0] != (byte)'<')
        {
            return MediaFileKind.Unknown;
        }

        // "<svg", "<?xml" (which an SVG usually opens with) and "<!DOCTYPE html" / "<html" / "<script".
        return StartsWithIgnoringCase(text, "<svg"u8) || StartsWithIgnoringCase(text, "<?xml"u8)
            ? MediaFileKind.Svg
            : MediaFileKind.Html;
    }

    private static bool IsRiff(ReadOnlySpan<byte> header, string formType)
    {
        if (header.Length < 12 || !StartsWith(header, "RIFF"u8))
        {
            return false;
        }

        var form = header.Slice(8, 4);
        for (var index = 0; index < 4; index++)
        {
            if (form[index] != (byte)formType[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool StartsWith(ReadOnlySpan<byte> header, ReadOnlySpan<byte> prefix) =>
        header.Length >= prefix.Length && header[..prefix.Length].SequenceEqual(prefix);

    private static bool StartsWithIgnoringCase(ReadOnlySpan<byte> header, ReadOnlySpan<byte> prefix)
    {
        if (header.Length < prefix.Length)
        {
            return false;
        }

        for (var index = 0; index < prefix.Length; index++)
        {
            if (ToLowerAscii(header[index]) != ToLowerAscii(prefix[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static byte ToLowerAscii(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;
}
