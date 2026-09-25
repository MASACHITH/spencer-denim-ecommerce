using System.Globalization;
using System.Text;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Infrastructure.Catalog;

/// <summary>
/// A public image as a read projection returns it: enough to address the original <i>and</i> its
/// renditions, which is why a projection now selects these four columns rather than just the key.
/// </summary>
/// <param name="StorageKey">The original's key. Always present, and always the safe fallback.</param>
/// <param name="PublicId">The folder name in the rendition key convention (doc 08 §3.3).</param>
/// <param name="RenditionSetVersion">
/// Which set was generated, or <see langword="null"/> for an asset with no renditions — a video, a
/// private asset, an older row, or one whose processing run failed.
/// </param>
/// <param name="Width">
/// The original's pixel width, used to keep a <c>srcset</c> honest. Renditions are clamped, never
/// upscaled (<c>SkiaImageProcessor.EncodeRenditions</c>), so a 500 px photo's <c>large.webp</c> is
/// 500 px wide however much the set's target says 1200.
/// </param>
internal sealed record MediaRef(string StorageKey, Guid PublicId, byte? RenditionSetVersion, int? Width);

/// <summary>What one <c>&lt;img&gt;</c> needs: the <c>src</c>, and the candidates for <c>srcset</c>.</summary>
/// <param name="Url">Never null when the asset exists — a rendition, or the original as a fallback.</param>
/// <param name="SrcSet">
/// Null when there is nothing for the browser to choose between, so a client can bind it straight to the
/// attribute and get no attribute at all rather than an empty one.
/// </param>
internal readonly record struct ImageSources(string? Url, string? SrcSet)
{
    /// <summary>The slot has no asset at all.</summary>
    public static ImageSources None => new(null, null);
}

/// <summary>
/// Doc 08 §3.3: the database stores a relative storage key; the public URL is built from
/// configuration, so the same rows work behind Caddy today and behind Azure Blob or a CDN later
/// (doc 03 §5: "no code may depend on the host").
/// <para>
/// It is also the single place that decides <i>which file of an asset</i> a payload links to. Doc 03
/// §3b's reason for generating renditions is that the shopper downloads the size they will actually
/// see; a reader that linked the original would make the whole pipeline dead weight. Every consumer
/// therefore names the rendition its layout needs — <c>card</c> for a product card, <c>large</c> for
/// the product page stage, <c>thumb</c> for a swatch or a gallery rail, <c>zoom</c> for the
/// full-screen view — and is given the original only when the asset genuinely has no rendition.
/// </para>
/// </summary>
internal static class MediaUrlBuilder
{
    /// <summary>Joins the configured media base URL and a storage key, with exactly one slash between them.</summary>
    public static string? ToPublicUrl(string? baseUrl, string? storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            return null;
        }

        var key = storageKey.Trim().TrimStart('/');

        // An absolute URL in the key (a remote placeholder image) is already a public URL.
        if (Uri.TryCreate(storageKey, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return storageKey;
        }

        var prefix = (baseUrl ?? string.Empty).TrimEnd('/');
        return prefix.Length == 0 ? "/" + key : prefix + "/" + key;
    }

    /// <summary>
    /// The URL of one named rendition of <paramref name="media"/>, falling back to the original.
    /// <para>
    /// For a slot that wants exactly one size and leaves the browser no choice to make: a colour swatch,
    /// a gallery thumbnail, the file the full-screen viewer swaps in.
    /// </para>
    /// </summary>
    public static string? ToRenditionUrl(string? baseUrl, MediaRef? media, string code)
    {
        if (media is null)
        {
            return null;
        }

        var rendition = MediaRenditions.Find(MediaRenditions.ForVersion(media.RenditionSetVersion), code);

        // No rendition set this build understands, or a set without this code: the original always
        // exists, so the slot shows a heavier picture rather than a broken one.
        return rendition is null
            ? ToPublicUrl(baseUrl, media.StorageKey)
            : ToPublicUrl(baseUrl, MediaStorageKey.ForRendition(media.PublicId, rendition));
    }

    /// <summary>
    /// The <c>src</c> and <c>srcset</c> for one image slot.
    /// <para>
    /// <paramref name="code"/> is the size the slot is drawn at on a one-device-pixel screen, and is what
    /// <c>src</c> points at. <paramref name="widestCode"/> caps the candidate list at the largest size
    /// the slot could ever need, so a card on a retina laptop may reach for <c>large</c> while nothing on
    /// a listing page is allowed to pull <c>zoom</c>.
    /// </para>
    /// <para>
    /// The server deliberately does <b>not</b> emit a <c>sizes</c> attribute. <c>sizes</c> is a statement
    /// about the grid an image is laid out in, and only the component drawing that grid knows it — the
    /// same card DTO renders four-up on a listing and two-and-a-bit-up in a home carousel. The server
    /// supplies the candidates and their true widths; the client supplies the layout.
    /// </para>
    /// </summary>
    public static ImageSources ToImageSources(string? baseUrl, MediaRef? media, string code, string widestCode)
    {
        if (media is null)
        {
            return ImageSources.None;
        }

        return new ImageSources(ToRenditionUrl(baseUrl, media, code), BuildSrcSet(baseUrl, media, widestCode));
    }

    /// <summary>
    /// The candidate list, smallest first, as <c>url 160w, url 600w</c>.
    /// <para>
    /// Width descriptors rather than <c>1x</c>/<c>2x</c>: the browser then combines the candidate widths
    /// with the slot's <c>sizes</c> and the device pixel ratio itself, which is the only way one payload
    /// serves a 320 px phone at DPR 3 and a 1920 px desktop at DPR 1 correctly. That matters here more
    /// than on most sites — doc 02's audience is largely on Sri Lankan mobile data.
    /// </para>
    /// <para>
    /// Two honesty rules keep the browser's arithmetic right, and both follow from renditions being
    /// clamped to the source rather than upscaled:
    /// </para>
    /// <list type="number">
    /// <item>
    /// A candidate is advertised at its <i>actual</i> width, <c>min(target, source)</c>. A browser told a
    /// 500 px file is 1200 px wide will pick it for a slot it cannot fill, and render it soft.
    /// </item>
    /// <item>
    /// Once a candidate reaches the source width the list stops: every larger one is the same pixels
    /// re-encoded, so offering them only invites the browser to spend bytes on no extra detail.
    /// </item>
    /// </list>
    /// <para>
    /// A list that comes out with a single entry returns <see langword="null"/> — there is nothing to
    /// choose between, and <c>src</c> has already said it.
    /// </para>
    /// </summary>
    private static string? BuildSrcSet(string? baseUrl, MediaRef media, string widestCode)
    {
        var set = MediaRenditions.ForVersion(media.RenditionSetVersion);
        var widest = MediaRenditions.Find(set, widestCode);
        if (set is null || widest is null)
        {
            return null;
        }

        var builder = new StringBuilder();
        var count = 0;
        var lastWidth = 0;

        foreach (var rendition in set.OrderBy(candidate => candidate.Width))
        {
            if (rendition.Width > widest.Width)
            {
                break;
            }

            var actualWidth = media.Width is > 0
                ? Math.Min(rendition.Width, media.Width.Value)
                : rendition.Width;

            // Rule 2. The set is walked smallest-first, so once one candidate has matched the source
            // width the next can only repeat it; rule 1's clamp is what makes that visible here.
            if (actualWidth <= lastWidth)
            {
                break;
            }

            var url = ToPublicUrl(baseUrl, MediaStorageKey.ForRendition(media.PublicId, rendition));
            if (url is null)
            {
                continue;
            }

            if (count > 0)
            {
                builder.Append(", ");
            }

            builder.Append(url)
                .Append(' ')
                .Append(actualWidth.ToString(CultureInfo.InvariantCulture))
                .Append('w');

            lastWidth = actualWidth;
            count++;
        }

        return count > 1 ? builder.ToString() : null;
    }
}
