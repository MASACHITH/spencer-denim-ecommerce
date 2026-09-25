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
