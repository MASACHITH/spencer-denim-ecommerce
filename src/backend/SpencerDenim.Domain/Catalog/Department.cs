using SpencerDenim.Domain.Common;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// <c>catalog.Departments</c> (doc 08 §3.2) [A] [RV]. The nav is fixed (Q5): admins edit the three
/// seeded departments, they never create or delete one.
/// </summary>
public sealed class Department : IAuditable
{
    private string _slug = string.Empty;

    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Storefront URL is <c>/{slug}</c>.
    /// <para>
    /// Guarded by <see cref="CatalogSlug"/> for the same reason <see cref="Product.Slug"/> is: the
    /// department page refuses a URL that is not a slug, so a row that is not one is a department the
    /// mega menu links to and nobody can open. EF reads and writes the backing field, so existing rows
    /// still materialise; only a write goes through the check. W1-12 added the guard because W1-12 added
    /// the write path — before it, only the seeder could set this column.
    /// </para>
    /// </summary>
    public string Slug
    {
        get => _slug;
        set => _slug = CatalogSlug.Require(value, nameof(Slug));
    }

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
