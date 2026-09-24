using SpencerDenim.Domain.Common;

namespace SpencerDenim.Domain.Content;

/// <summary>
/// <c>content.HomeSections</c> (doc 08 §3.7) [A] [RV]. One block of the homepage (HOME-01..10).
/// <para>
/// The client owns every one of these rows: which blocks appear, in what order, with what heading
/// and for how long. Nothing the client can edit belongs in a component.
/// </para>
/// </summary>
public sealed class HomeSection : IAuditable
{
    public int Id { get; set; }

    public HomeSectionType SectionType { get; set; }

    public string? Title { get; set; }

    public string? Subtitle { get; set; }

    public bool IsEnabled { get; set; }

    public short SortOrder { get; set; }

    /// <summary>
    /// Per-type settings (doc 08 §3.7): mode auto/manual, how many products, the listing URL a
    /// product row is drawn from and links to, overlay and call to action. <c>nvarchar(max)</c>
    /// with an <c>ISJSON</c> check constraint (doc 08 §1.4, DB15).
    /// </summary>
    public string? SettingsJson { get; set; }

    public DateTime? StartsAtUtc { get; set; }

    public DateTime? EndsAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }

    /// <summary>Optimistic concurrency token exchanged with the admin API as an ETag.</summary>
    public byte[]? RowVersion { get; set; }

    public ICollection<HomeSectionItem> Items { get; } = [];

    /// <summary>Whether this section is on the storefront at <paramref name="utcNow"/>.</summary>
    public bool IsLiveAt(DateTime utcNow) =>
        ContentSchedule.IsLive(IsEnabled, StartsAtUtc, EndsAtUtc, utcNow);
}
