namespace SpencerDenim.Domain.Media;

/// <summary>One WebP rendition: its code, which is also its filename, and its target width in pixels.</summary>
/// <param name="Code">The filename stem, e.g. <c>card</c>.</param>
/// <param name="Width">The target width. The aspect ratio is kept and the source is never upscaled.</param>
public sealed record MediaRendition(string Code, int Width);

/// <summary>
/// The rendition set of doc 08 §3.3 ("the codes and target widths are code constants") and doc 03 §3b
/// ("resized to WebP renditions (thumb / card / large / zoom)"). There is no table: the key convention
/// <c>public/renditions/{PublicId}/{code}.webp</c> is the index.
/// <para>
/// <see cref="CurrentSetVersion"/> is written to <c>MediaAssets.RenditionSetVersion</c>. Changing the
/// codes or the widths means bumping it, so the regeneration job can find the assets that still carry
/// an older set (doc 08 §3.3; see <see cref="MediaRetention"/>).
/// </para>
/// </summary>
public static class MediaRenditions
{
    /// <summary>The version recorded for an asset rendered with <see cref="Current"/>.</summary>
    public const byte CurrentSetVersion = 1;

    /// <summary>Thumbnail: the gallery rail.</summary>
    public const string Thumb = "thumb";

    /// <summary>Card: a product card on the listing and the homepage.</summary>
    public const string Card = "card";

    /// <summary>Large: the product page stage.</summary>
    public const string Large = "large";

    /// <summary>Zoom: the full-screen view.</summary>
    public const string Zoom = "zoom";

    /// <summary>The media type every rendition is encoded as.</summary>
    public const string ContentType = "image/webp";

    /// <summary>The set generated for every public image, smallest first.</summary>
    public static IReadOnlyList<MediaRendition> Current { get; } =
    [
        new(Thumb, 160),
        new(Card, 600),
        new(Large, 1200),
        new(Zoom, 2000),
    ];

    /// <summary>
    /// The rendition set an asset recorded as <paramref name="setVersion"/> was rendered with, or
    /// <see langword="null"/> when this build cannot name that set's files.
    /// <para>
    /// This is what <c>MediaAssets.RenditionSetVersion</c> is <i>for</i>. The key convention
    /// (<c>public/renditions/{PublicId}/{code}.webp</c>) carries no version, so the recorded version is
    /// the only thing that says which codes are actually on disk for a given asset. A URL builder must
    /// ask here rather than assume <see cref="Current"/>: when the set is changed — a code renamed, a
    /// width altered, a size added — <see cref="CurrentSetVersion"/> is bumped, and until the
    /// regeneration job has caught up the database holds assets of both versions at once. Linking a
    /// v2 code on a v1 asset would be a 404 on a shopper's screen.
    /// </para>
    /// <para>
    /// <b>When you bump the version:</b> keep the old set here as its own case, so URLs for the assets
    /// that have not been regenerated yet stay correct while the job runs. Drop a case only once no row
    /// carries that version any more.
    /// </para>
    /// <para>
    /// An unrecognised version — a row written by a newer deployment than this one, or a
    /// <see langword="null"/> meaning the asset was never rendered — returns <see langword="null"/>.
    /// The caller's contract is to fall back to the original file, which always exists, so an asset
    /// this build does not understand degrades to a larger image rather than to a broken one.
    /// </para>
    /// </summary>
    public static IReadOnlyList<MediaRendition>? ForVersion(byte? setVersion) => setVersion switch
    {
        CurrentSetVersion => Current,
        _ => null,
    };

    /// <summary>
    /// The rendition named <paramref name="code"/> within <paramref name="set"/>, or
    /// <see langword="null"/> when that set does not contain it.
    /// </summary>
    public static MediaRendition? Find(IReadOnlyList<MediaRendition>? set, string? code)
    {
        if (set is null || code is null)
        {
            return null;
        }

        foreach (var rendition in set)
        {
            if (string.Equals(rendition.Code, code, StringComparison.Ordinal))
            {
                return rendition;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="code"/> is one of <see cref="Current"/>'s codes.</summary>
    public static bool IsKnownCode(string? code)
    {
        if (code is null)
        {
            return false;
        }

        foreach (var rendition in Current)
        {
            if (string.Equals(rendition.Code, code, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
