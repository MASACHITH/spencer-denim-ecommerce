namespace SpencerDenim.Domain.Media;

/// <summary>One WebP rendition: its code, which is also its filename, and its target width in pixels.</summary>
/// <param name="Code">The filename stem, e.g. <c>card</c>.</param>
/// <param name="Width">The target width. The aspect ratio is kept and the source is never upscaled.</param>
public sealed record MediaRendition(string Code, int Width);

/// <summary>
/// A rendition as it is actually written for one particular source image: the set entry, and the width
/// it really has once clamped to that source.
/// </summary>
/// <param name="Width">
/// <c>min(target, source)</c>. This is the number a <c>srcset</c> has to advertise — the target would be
/// a lie for any source narrower than it.
/// </param>
public sealed record ProducedRendition(MediaRendition Rendition, int Width);

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
    /// The renditions a source <paramref name="sourceWidth"/> pixels wide actually produces, smallest
    /// first, each with the width it really has.
    /// <para>
    /// Renditions are clamped to the source and never upscaled, so a 1200 px photograph renders
    /// <c>large</c> and <c>zoom</c> as the same 1200 px image twice. This is the one rule that decides
    /// where that stops, and <b>both</b> ends of the pipeline ask it: the encoder, so the duplicate is
    /// never written, and the URL builder, so nothing ever links a file the encoder did not write.
    /// Keeping the rule in one place is the point — when they disagreed, a quarter of the seeded
    /// renditions were byte-identical copies and the product page made the shopper download the stage
    /// image a second time to open the viewer.
    /// </para>
    /// <para>
    /// This is deliberately <b>not</b> a change of <see cref="CurrentSetVersion"/>. The codes and their
    /// target widths are untouched; what changed is only that a redundant file is no longer written.
    /// The files this returns are a prefix of the set, so they are present on an asset rendered under
    /// either rule, and an older asset simply carries a spare file nothing links.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ProducedRendition> ProducedFor(
        IReadOnlyList<MediaRendition>? set,
        int? sourceWidth)
    {
        // An unknown set and an unknown source width are the same kind of ignorance and get the same
        // answer. Without a width there is nothing to clamp against, so every code would look like a
        // file that was written — and for any source at or below the `large` target, `zoom.webp` is not
        // there. Nothing in this build can reach that (the pipeline, the seeder and the placeholder
        // generator all record Width), but an importer or a restore from a backup taken before the
        // column was populated could, and the failure would be a broken image on a shopper's screen.
        // Claiming nothing costs a heavier picture; claiming wrongly costs a 404.
        if (set is null || sourceWidth is not > 0)
        {
            return [];
        }

        var produced = new List<ProducedRendition>(set.Count);
        var lastWidth = 0;

        foreach (var rendition in set.OrderBy(candidate => candidate.Width))
        {
            var width = Math.Min(rendition.Width, sourceWidth.Value);

            // The set is walked smallest-first, so once one has matched the source width every larger
            // one can only repeat it.
            if (width <= lastWidth)
            {
                break;
            }

            produced.Add(new ProducedRendition(rendition, width));
            lastWidth = width;
        }

        return produced;
    }

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
