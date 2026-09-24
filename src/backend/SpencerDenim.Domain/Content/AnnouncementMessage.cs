using SpencerDenim.Domain.Common;

namespace SpencerDenim.Domain.Content;

/// <summary>
/// <c>content.AnnouncementMessages</c> (doc 08 §3.7, GLB-03) [A]. One line in the rotating strip
/// above the header, on every page of the storefront.
/// </summary>
public sealed class AnnouncementMessage : IAuditable
{
    public int Id { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? LinkUrl { get; set; }

    public short SortOrder { get; set; }

    public bool IsActive { get; set; }

    public DateTime? StartsAtUtc { get; set; }

    public DateTime? EndsAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }

    /// <summary>Whether this message is on the storefront at <paramref name="utcNow"/>.</summary>
    public bool IsLiveAt(DateTime utcNow) =>
        ContentSchedule.IsLive(IsActive, StartsAtUtc, EndsAtUtc, utcNow);
}
