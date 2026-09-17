using SpencerDenim.Domain.Common;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// <c>catalog.Products</c> (doc 08 §3.2) [A] [SD] [RV]: the merchandising record. Prices per size and
/// colour live on <see cref="ProductVariant"/>; everything a listing needs is denormalised into
/// <see cref="ProductStats"/> (doc 08 §7.4).
/// </summary>
public sealed class Product : IAuditable, ISoftDeletable
{
    public int Id { get; set; }

    public string ProductCode { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Storefront URL is <c>/p/{slug}</c>. Redirects are deferred, so a slug change breaks links.</summary>
    public string Slug { get; set; } = string.Empty;

    public int DepartmentId { get; set; }

    public Department? Department { get; set; }

    /// <summary>Must belong to <see cref="DepartmentId"/> (checked in the application, doc 08 §3.2).</summary>
    public int PrimaryCategoryId { get; set; }

    public Category? PrimaryCategory { get; set; }

    /// <summary>Required for Kids products (checked in the application).</summary>
    public int? AgeGroupId { get; set; }

    public AgeGroup? AgeGroup { get; set; }

    public string? ShortDescription { get; set; }

    /// <summary>Sanitised HTML.</summary>
    public string? Description { get; set; }

    public string? CareInstructions { get; set; }

    public int? MaterialId { get; set; }

    public AttributeOption? Material { get; set; }

    public int? FitId { get; set; }

    public AttributeOption? Fit { get; set; }

    public int? OccasionId { get; set; }

    public AttributeOption? Occasion { get; set; }

    /// <summary>List price before any variant override. CK &gt; 0.</summary>
    public decimal BasePrice { get; set; }

    /// <summary>Original ("was") price. CK NULL or greater than <see cref="BasePrice"/>.</summary>
    public decimal? CompareAtPrice { get; set; }

    /// <summary>Admin-assigned badge; it wins over every computed badge (doc 08 §3.2).</summary>
    public ProductBadge? BadgeOverride { get; set; }

    /// <summary>Drives the computed "New" badge and the New Arrivals listing.</summary>
    public DateTime? NewUntilUtc { get; set; }

    /// <summary>Global merchandising pin for the Recommended sort (doc 08 §7.3). NULLs sort last.</summary>
    public int? RecommendedRank { get; set; }

    public int? SizeGuideId { get; set; }

    public SizeGuide? SizeGuide { get; set; }

    /// <summary>Denormalised from <see cref="Media"/> on save (doc 08 §7.4).</summary>
    public int? CardImageAssetId { get; set; }

    public MediaAsset? CardImageAsset { get; set; }

    public int? HoverImageAssetId { get; set; }

    public MediaAsset? HoverImageAsset { get; set; }

    public bool IsPublished { get; set; }

    /// <summary>Set once, on first publish.</summary>
    public DateTime? FirstPublishedAtUtc { get; set; }

    public string? SeoTitle { get; set; }

    public string? SeoDescription { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }

    public byte[]? RowVersion { get; set; }

    public ProductStats? Stats { get; set; }

    public ProductSearchIndex? SearchIndex { get; set; }

    public ICollection<ProductCategory> CategoryLinks { get; } = [];

    public ICollection<ProductColour> Colours { get; } = [];

    public ICollection<ProductVariant> Variants { get; } = [];

    public ICollection<ProductMedia> Media { get; } = [];

    public ICollection<RelatedProduct> RelatedProducts { get; } = [];

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
    /// Publishes the product. <see cref="FirstPublishedAtUtc"/> is set once and
    /// <see cref="NewUntilUtc"/> is defaulted to publish date + <paramref name="newArrivalDays"/>
    /// (doc 08 §3.2).
    /// </summary>
    public void Publish(DateTime utcNow, int newArrivalDays)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(newArrivalDays);

        IsPublished = true;
        FirstPublishedAtUtc ??= utcNow;
        NewUntilUtc ??= FirstPublishedAtUtc.Value.AddDays(newArrivalDays);
    }
}
