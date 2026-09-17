using SpencerDenim.Domain.Common;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// <c>catalog.Departments</c> (doc 08 §3.2) [A] [RV]. The nav is fixed (Q5): admins edit the three
/// seeded departments, they never create or delete one.
/// </summary>
public sealed class Department : IAuditable
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int? HeroImageAssetId { get; set; }

    public MediaAsset? HeroImageAsset { get; set; }

    public short SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public string? SeoTitle { get; set; }

    public string? SeoDescription { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }

    /// <summary>Optimistic concurrency token exchanged with the admin API as an ETag.</summary>
    public byte[]? RowVersion { get; set; }

    public ICollection<Category> Categories { get; } = [];

    public ICollection<Product> Products { get; } = [];
}
