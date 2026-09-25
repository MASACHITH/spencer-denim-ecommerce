using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Catalog;

namespace SpencerDenim.UnitTests.Infrastructure.Catalog;

/// <summary>
/// Which file of an asset a catalogue payload links to (doc 03 §3b, W1-04).
/// <para>
/// Renditions were generated, stored and cached from the first day of W1-04, and linked nowhere: every
/// card, row and gallery served the full original, five times the bytes, to an audience doc 02 puts
/// largely on Sri Lankan mobile data. These tests pin the three things that stopped being true when
/// that was fixed — a slot asks for the rendition it needs, a slot with no rendition still gets a file
/// that displays, and a <c>srcset</c> never lies to the browser about how wide a candidate is.
/// </para>
/// </summary>
public sealed class MediaUrlBuilderTests
{
    private const string BaseUrl = "/media";

    private static readonly Guid AssetId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>A public image the pipeline rendered the current set for, 3000 px wide.</summary>
    private static MediaRef Rendered(int? width = 3000) =>
        new("public/originals/demo/photo.jpg", AssetId, MediaRenditions.CurrentSetVersion, width);

    [Fact]
    public void A_named_rendition_resolves_to_the_rendition_key_not_the_original()
    {
        var url = MediaUrlBuilder.ToRenditionUrl(BaseUrl, Rendered(), MediaRenditions.Card);

        Assert.Equal($"/media/public/renditions/{AssetId:D}/card.webp", url);
        Assert.DoesNotContain("originals", url, StringComparison.Ordinal);
    }

    [Fact]
    public void An_asset_with_no_rendition_set_falls_back_to_the_original()
    {
        // An older row, a video, or a processing run that failed. The original always exists, so the
        // slot must show a heavier picture rather than a broken one.
        var neverRendered = new MediaRef("public/originals/demo/photo.jpg", AssetId, null, 3000);

        var url = MediaUrlBuilder.ToRenditionUrl(BaseUrl, neverRendered, MediaRenditions.Card);

        Assert.Equal("/media/public/originals/demo/photo.jpg", url);
    }

    [Fact]
    public void An_asset_recorded_at_an_unknown_set_version_falls_back_to_the_original()
    {
        // The rendition key convention carries no version, so a row this build cannot interpret must
        // not be guessed at: linking a code that is not on disk would be a 404 on a shopper's screen.
        var futureSet = new MediaRef("public/originals/demo/photo.jpg", AssetId, 99, 3000);

        var sources = MediaUrlBuilder.ToImageSources(
            BaseUrl,
            futureSet,
            MediaRenditions.Card,
            MediaRenditions.Large);

        Assert.Equal("/media/public/originals/demo/photo.jpg", sources.Url);
        Assert.Null(sources.SrcSet);
    }

    [Fact]
    public void A_missing_asset_produces_no_url_at_all()
    {
        var sources = MediaUrlBuilder.ToImageSources(BaseUrl, null, MediaRenditions.Card, MediaRenditions.Large);

        Assert.Null(sources.Url);
        Assert.Null(sources.SrcSet);
    }

    [Fact]
    public void A_card_slot_offers_the_smaller_candidates_and_is_capped_below_zoom()
    {
        var sources = MediaUrlBuilder.ToImageSources(
            BaseUrl,
            Rendered(),
            MediaRenditions.Card,
            MediaRenditions.Large);

        Assert.Equal($"/media/public/renditions/{AssetId:D}/card.webp", sources.Url);
        Assert.Equal(
            $"/media/public/renditions/{AssetId:D}/thumb.webp 160w, "
            + $"/media/public/renditions/{AssetId:D}/card.webp 600w, "
            + $"/media/public/renditions/{AssetId:D}/large.webp 1200w",
            sources.SrcSet);

        // A card is a grid cell, never a full screen: there is no layout in which zoom is the answer.
        Assert.DoesNotContain("zoom", sources.SrcSet, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stage_slot_may_run_all_the_way_to_zoom()
    {
        var sources = MediaUrlBuilder.ToImageSources(
            BaseUrl,
            Rendered(),
            MediaRenditions.Large,
            MediaRenditions.Zoom);

        Assert.Equal($"/media/public/renditions/{AssetId:D}/large.webp", sources.Url);
        Assert.Contains($"/media/public/renditions/{AssetId:D}/zoom.webp 2000w", sources.SrcSet, StringComparison.Ordinal);
    }

    [Fact]
    public void A_candidate_is_advertised_at_the_width_it_really_has()
    {
        // Renditions are clamped to the source, never upscaled, so an 800 px original's large.webp is
        // 800 px wide. Advertising it as 1200w would have the browser pick it for a slot it cannot
        // fill and then render it soft.
        var sources = MediaUrlBuilder.ToImageSources(
            BaseUrl,
            Rendered(width: 800),
            MediaRenditions.Card,
            MediaRenditions.Large);

        Assert.Equal(
            $"/media/public/renditions/{AssetId:D}/thumb.webp 160w, "
            + $"/media/public/renditions/{AssetId:D}/card.webp 600w, "
            + $"/media/public/renditions/{AssetId:D}/large.webp 800w",
            sources.SrcSet);
    }

    [Fact]
    public void Candidates_stop_once_one_has_reached_the_source_width()
    {
        // A 500 px original renders card, large and zoom as the same 500 px image. Offering all three
        // invites the browser to spend bytes on no extra detail.
        var sources = MediaUrlBuilder.ToImageSources(
            BaseUrl,
            Rendered(width: 500),
            MediaRenditions.Large,
            MediaRenditions.Zoom);

        Assert.Equal(
            $"/media/public/renditions/{AssetId:D}/thumb.webp 160w, "
            + $"/media/public/renditions/{AssetId:D}/card.webp 500w",
            sources.SrcSet);
    }

    [Fact]
    public void A_source_smaller_than_every_rendition_gets_no_srcset_to_choose_from()
    {
        // One candidate is not a choice, and an empty or single-entry srcset is noise in the payload.
        var sources = MediaUrlBuilder.ToImageSources(
            BaseUrl,
            Rendered(width: 120),
            MediaRenditions.Card,
            MediaRenditions.Large);

        Assert.Equal($"/media/public/renditions/{AssetId:D}/card.webp", sources.Url);
        Assert.Null(sources.SrcSet);
    }

    [Fact]
    public void An_unknown_width_falls_back_to_the_set_targets()
    {
        // Width is nullable on the asset row; without it the target widths are the best claim there is.
        var sources = MediaUrlBuilder.ToImageSources(
            BaseUrl,
            Rendered(width: null),
            MediaRenditions.Card,
            MediaRenditions.Large);

        Assert.Equal(
            $"/media/public/renditions/{AssetId:D}/thumb.webp 160w, "
            + $"/media/public/renditions/{AssetId:D}/card.webp 600w, "
            + $"/media/public/renditions/{AssetId:D}/large.webp 1200w",
            sources.SrcSet);
    }

    [Fact]
    public void A_configured_base_url_is_honoured_so_a_cdn_can_take_over()
    {
        // Doc 03 §5: no code may depend on the host. A rendition URL is built the same way the
        // original's is, so moving media behind a CDN stays a configuration change.
        var url = MediaUrlBuilder.ToRenditionUrl("https://cdn.example.com/media/", Rendered(), MediaRenditions.Thumb);

        Assert.Equal($"https://cdn.example.com/media/public/renditions/{AssetId:D}/thumb.webp", url);
    }
}
