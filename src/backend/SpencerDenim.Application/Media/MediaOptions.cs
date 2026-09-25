using SpencerDenim.Domain.Media;

namespace SpencerDenim.Application.Media;

/// <summary>
/// Media settings, bound from the <c>Media</c> configuration section (environment variables
/// <c>Media__*</c>). Doc 03 §7: "Paths, URLs, connection strings and storage all come from
/// configuration" — there is no absolute path, root, host name or URL anywhere in the code.
/// </summary>
public sealed class MediaOptions
{
    public const string SectionName = "Media";

    /// <summary>
    /// Root of the <b>public</b> uploads volume. Empty means media is switched off (see
    /// <c>IFileStorage.IsConfigured</c>). A relative value is resolved against the application's
    /// content root, so a developer needs no absolute path either.
    /// </summary>
    public string PublicRoot { get; set; } = string.Empty;

    /// <summary>
    /// Root of the <b>private</b> uploads volume (customer bank slips). Doc 03 §4: a different volume,
    /// not mounted by the reverse proxy. It must not be the public root or inside it, and startup
    /// validation refuses that.
    /// </summary>
    public string PrivateRoot { get; set; } = string.Empty;

    /// <summary>
    /// Largest image upload accepted, in bytes. Default 15 MB: a 6000 × 4000 JPEG out of a camera is
    /// about 8 MB, so this leaves room without letting a 50 MB TIFF-sized file through.
    /// </summary>
    public long MaxImageBytes { get; set; } = 15 * 1024 * 1024;

    /// <summary>
    /// Largest video upload accepted. Capped at <see cref="MediaLimits.MaxVideoBytes"/> (50 MB) by
    /// startup validation, because the database check constraint rejects anything larger.
    /// </summary>
    public long MaxVideoBytes { get; set; } = MediaLimits.MaxVideoBytes;

    /// <summary>
    /// Largest private upload accepted (bank slips). Capped at <see cref="MediaLimits.MaxPrivateBytes"/>
    /// (5 MB) by startup validation, for the same reason.
    /// </summary>
    public long MaxPrivateBytes { get; set; } = MediaLimits.MaxPrivateBytes;

    /// <summary>
    /// Largest image the decoder is allowed to open, in megapixels. This is the decompression-bomb
    /// guard: a 200 KB PNG can declare 40 000 × 40 000 pixels and ask for 6 GB of memory when it is
    /// decoded. The dimensions are read from the header and checked <b>before</b> any pixel is decoded.
    /// Default 60 MP, which is past any camera the client is likely to use.
    /// </summary>
    public int MaxImageMegapixels { get; set; } = 60;

    /// <summary>WebP quality for renditions. 82 is the usual "indistinguishable at 1×" point.</summary>
    public int RenditionQuality { get; set; } = 82;

    /// <summary>
    /// JPEG quality used only when an image has to be re-encoded rather than metadata-stripped in
    /// place — that is, when it carries a non-default EXIF orientation, which cannot be honoured
    /// losslessly once the EXIF is gone. High, because this is the archive copy.
    /// </summary>
    public int OriginalReencodeQuality { get; set; } = 92;

    /// <summary>
    /// <c>Cache-Control: max-age</c> for a served media file, in seconds. Default one year: every key
    /// contains the asset's <c>PublicId</c>, so a key's content never changes.
    /// </summary>
    public int CacheMaxAgeSeconds { get; set; } = 31_536_000;

    /// <summary>
    /// Whether two uploads of the same bytes share one stored file. See <c>IMediaService</c> for the
    /// reasoning; off would mean a second copy of every re-uploaded photograph on a 100 GB VPS disk.
    /// </summary>
    public bool DeduplicateByChecksum { get; set; } = true;

    /// <summary>The effective ceiling for a visibility and media type, in bytes.</summary>
    public long MaxBytesFor(MediaVisibility visibility, MediaAssetType mediaType)
    {
        if (visibility == MediaVisibility.Private)
        {
            return Math.Min(MaxPrivateBytes, MediaLimits.MaxPrivateBytes);
        }

        return mediaType == MediaAssetType.Video
            ? Math.Min(MaxVideoBytes, MediaLimits.MaxVideoBytes)
            : MaxImageBytes;
    }

    /// <summary>
    /// The largest body any media endpoint will read. A video upload carries its poster image in the
    /// same request (doc 08 §3.3: a video row cannot exist without one), so the ceiling is both files
    /// plus a megabyte for the multipart framing and the text fields. Used for the endpoint's
    /// request-size limit, which is the outermost of the three size defences.
    /// </summary>
    public long MaxRequestBytes =>
        Math.Min(MaxVideoBytes, MediaLimits.MaxVideoBytes) + MaxImageBytes + (1024 * 1024);
}
