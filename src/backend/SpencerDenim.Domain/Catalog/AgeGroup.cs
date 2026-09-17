using SpencerDenim.Domain.Common;

namespace SpencerDenim.Domain.Catalog;

/// <summary><c>catalog.AgeGroups</c> (doc 08 §3.2) [A]. Drives the Kids filters (PLP-02, DEPT-01).</summary>
public sealed class AgeGroup : IAuditable
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public short? MinMonths { get; set; }

    public short? MaxMonths { get; set; }

    public short SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }
}
