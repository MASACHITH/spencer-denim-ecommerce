using SpencerDenim.Domain.Common;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// <c>catalog.Categories</c> (doc 08 §3.2) [A] [SD] [RV]. A hierarchy of at most three levels inside
/// one department, ordered per parent, with a materialised <see cref="Path"/> so a subtree is one
/// range predicate instead of a recursive query.
/// </summary>
public sealed class Category : IAuditable, ISoftDeletable
{
    /// <summary>Doc 08 §3.2: <c>CK_Categories_Depth</c> allows at most three levels.</summary>
    public const byte MaxDepth = 3;

    private string _slug = string.Empty;

    public int Id { get; set; }

    public int DepartmentId { get; set; }

    public Department? Department { get; set; }

    public int? ParentCategoryId { get; set; }

    public Category? ParentCategory { get; set; }

    /// <summary>Materialised ID path, e.g. <c>/12/34/</c>. Maintained by the move use case.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>1 for a top-level category, up to <see cref="MaxDepth"/>.</summary>
    public byte Depth { get; set; } = 1;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// URL segment, unique among live siblings (<c>UX_Categories_Dept_Parent_Slug</c>).
    /// <para>
    /// Guarded by <see cref="CatalogSlug"/> for the same reason <see cref="Product.Slug"/> is: the
    /// listing refuses a <c>?category=</c> value that is not a slug, so a row that is not one is a
    /// category the mega menu links to and nobody can open. EF reads and writes the backing field, so
    /// existing rows still materialise; only a write goes through the check. W1-12 added the guard
    /// because W1-12 added the write path — before it, only the seeder could set this column.
    /// </para>
    /// </summary>
    public string Slug
    {
        get => _slug;
        set => _slug = CatalogSlug.Require(value, nameof(Slug));
    }

    public string? Description { get; set; }

    public int? ImageAssetId { get; set; }

    public MediaAsset? ImageAsset { get; set; }

    public int? BannerAssetId { get; set; }

    public MediaAsset? BannerAsset { get; set; }

    public string? BannerLinkUrl { get; set; }

    public int? AgeGroupId { get; set; }

    public AgeGroup? AgeGroup { get; set; }

    /// <summary>NULL inherits the nearest ancestor's guide.</summary>
    public int? SizeGuideId { get; set; }

    public SizeGuide? SizeGuide { get; set; }

    /// <summary>Sibling order.</summary>
    public int SortOrder { get; set; }

    public bool ShowInMegaMenu { get; set; } = true;

    /// <summary>1–4 when shown in the mega menu.</summary>
    public byte? MegaMenuColumn { get; set; }

    public bool IsActive { get; set; } = true;

    public string? SeoTitle { get; set; }

    public string? SeoDescription { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }

    public byte[]? RowVersion { get; set; }

    public ICollection<Category> ChildCategories { get; } = [];

    public ICollection<ProductCategory> ProductLinks { get; } = [];

    /// <summary>Doc 08 §1.5 [SD]. Set only by <see cref="SoftDelete"/>; a filtered unique index and the
    /// global query filter both rely on it.</summary>
    public bool IsDeleted { get; private set; }

    public DateTime? DeletedAtUtc { get; private set; }

    public int? DeletedByUserId { get; private set; }

    public void SoftDelete(DateTime utcNow, int? userId)
    {
        if (IsDeleted)
        {
            return;
        }

        IsDeleted = true;
        DeletedAtUtc = utcNow;
        DeletedByUserId = userId;
    }

    public void Restore()
    {
        IsDeleted = false;
        DeletedAtUtc = null;
        DeletedByUserId = null;
    }

    /// <summary>
    /// Builds the materialised path for a category, given its parent's path (doc 08 §3.2).
    /// Always <c>/id/</c> segments with leading and trailing slashes, so <c>LIKE parentPath + '%'</c>
    /// selects exactly the subtree.
    /// </summary>
    public static string BuildPath(string? parentPath, int categoryId) =>
        CategoryPath.Build(parentPath, categoryId);
}
