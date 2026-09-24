using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Common;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Domain.Content;

/// <summary>
/// <c>content.HomeSectionItems</c> (doc 08 §3.7) [A]. One tile, benefit, gallery image or banner
/// inside a <see cref="HomeSection"/>. A pure child row: it dies with its section (doc 08 §1.6).
/// <para>
/// The columns are deliberately wide, because one table serves every block: a department tile uses
/// <see cref="DepartmentId"/> and an image, a trending tile uses <see cref="LinkUrl"/>, a benefit
/// uses <see cref="IconKey"/> and <see cref="Body"/>, and the hero uses
/// <see cref="VideoAssetId"/> with <see cref="MobileImageAssetId"/> as its still (HOME-01).
/// </para>
/// </summary>
public sealed class HomeSectionItem : IAuditable
{
    public int Id { get; set; }

    public int HomeSectionId { get; set; }

    public HomeSection? HomeSection { get; set; }

    public string? Title { get; set; }

    public string? Subtitle { get; set; }

    public string? Body { get; set; }

    public string? ButtonLabel { get; set; }

    /// <summary>Where the tile goes. Trending items store a filtered listing URL (HOME-04).</summary>
    public string? LinkUrl { get; set; }

    public int? ImageAssetId { get; set; }

    public MediaAsset? ImageAsset { get; set; }

    public int? MobileImageAssetId { get; set; }

    public MediaAsset? MobileImageAsset { get; set; }

    /// <summary>
    /// The hero video (HOME-01). Doc 08 §3.7 requires a still alongside it — <c>CK</c>
    /// <c>VideoAssetId IS NULL OR ImageAssetId IS NOT NULL OR MobileImageAssetId IS NOT NULL</c> —
    /// so a phone, a data-saver and <c>prefers-reduced-motion</c> always have something to show.
    /// </summary>
    public int? VideoAssetId { get; set; }

    public MediaAsset? VideoAsset { get; set; }

    public int? ProductId { get; set; }

    public Product? Product { get; set; }

    public int? DepartmentId { get; set; }

    public Department? Department { get; set; }

    /// <summary>Names an icon in the storefront's own set (HOME-09), never a file.</summary>
    public string? IconKey { get; set; }

    public short SortOrder { get; set; }

    public bool IsEnabled { get; set; }

    public DateTime? StartsAtUtc { get; set; }

    public DateTime? EndsAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }

    /// <summary>Whether this item is on the storefront at <paramref name="utcNow"/>.</summary>
    public bool IsLiveAt(DateTime utcNow) =>
        ContentSchedule.IsLive(IsEnabled, StartsAtUtc, EndsAtUtc, utcNow);

    /// <summary>
    /// Doc 08 §3.7's static-fallback rule, as the application sees it: a video without a still is
    /// not a hero anyone can render. The database enforces the same thing with a CK.
    /// </summary>
    public bool HasStaticFallback =>
        VideoAssetId is null || ImageAssetId is not null || MobileImageAssetId is not null;
}
