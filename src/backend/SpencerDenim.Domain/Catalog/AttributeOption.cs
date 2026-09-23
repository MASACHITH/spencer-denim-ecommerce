using SpencerDenim.Domain.Common;

namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// <c>catalog.AttributeOptions</c> (doc 08 §3.2) [A]: the material, fit and occasion lookups behind
/// the PLP-02 filters and the PDP-05 details block.
/// </summary>
public sealed class AttributeOption : IAuditable
{
    public int Id { get; set; }

    public AttributeOptionType AttributeType { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public short SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }
}
