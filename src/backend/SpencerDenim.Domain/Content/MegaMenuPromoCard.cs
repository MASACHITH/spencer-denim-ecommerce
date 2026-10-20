using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Common;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Domain.Content;

/// <summary>
/// <c>content.MegaMenuPromoCards</c> (doc 08 §3.7, GLB-05, ADM-17) [A] [RV]. The picture card at the end of a
/// department's desktop mega menu: an image, a heading, an optional line under it, a button and where it goes.
/// <para>
/// <b>Several per department, one on the shop at a time.</b> Doc 08 keys the table to a department with a sort
/// order and a schedule, so a client can line up next season's card behind this one; the storefront draws the
/// first card that is switched on and inside its schedule, by position. With none, the menu keeps the card it
/// has always drawn from the department itself (#215), so a fresh database looks exactly as before.
/// </para>
/// <para>
/// <b>The image may be empty only while the card is switched off</b> (<c>CK_MegaMenuPromoCards_ActiveImage</c>):
/// the seeder offers each department a switched-off card in today's words before anyone has a photograph for
/// it, the arrangement #192 made for the hero and the banners.
/// </para>
/// </summary>
public sealed class MegaMenuPromoCard : IAuditable
{
    public int Id { get; set; }

    public int DepartmentId { get; set; }

    public Department? Department { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Subtitle { get; set; }

    public string ButtonLabel { get; set; } = string.Empty;

    /// <summary>A path on this site or a full http(s) address (ADM-17's link rules).</summary>
    public string LinkUrl { get; set; } = string.Empty;

    public int? ImageAssetId { get; set; }

    public MediaAsset? ImageAsset { get; set; }

    public short SortOrder { get; set; }

    public bool IsActive { get; set; }

    public DateTime? StartsAtUtc { get; set; }

    public DateTime? EndsAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }

    /// <summary>Optimistic concurrency token exchanged with the admin API.</summary>
    public byte[]? RowVersion { get; set; }

    /// <summary>Whether this card may be on the storefront at <paramref name="utcNow"/> (switch and schedule).</summary>
    public bool IsLiveAt(DateTime utcNow) =>
        ContentSchedule.IsLive(IsActive, StartsAtUtc, EndsAtUtc, utcNow);
}
