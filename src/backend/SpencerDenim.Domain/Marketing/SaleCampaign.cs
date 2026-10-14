using SpencerDenim.Domain.Common;

namespace SpencerDenim.Domain.Marketing;

/// <summary><c>marketing.SaleCampaigns.DiscountType</c> (doc 08 §3.6; CK allows exactly these two).</summary>
public enum SaleDiscountType : byte
{
    /// <summary>
    /// A percentage off the <b>list</b> price (doc 08 §6 step 3), capped at
    /// <see cref="SaleCampaign.MaxPercentDiscount"/> by <c>CK_SaleCampaigns_DiscountValue</c>.
    /// </summary>
    Percent = 1,

    /// <summary>A fixed LKR amount off the list price, floored at <see cref="Catalog.CatalogPricing.MinimumSalePrice"/>.</summary>
    FixedAmount = 2,
}

/// <summary><c>marketing.SaleCampaignTargets.TargetType</c> (doc 08 §3.6; CK allows exactly these three).</summary>
public enum SaleCampaignTargetType : byte
{
    /// <summary>Every product in the department.</summary>
    Department = 1,

    /// <summary>The category, and its subtree when <see cref="SaleCampaignTarget.IncludeSubcategories"/> is set.</summary>
    Category = 2,

    /// <summary>One product.</summary>
    Product = 3,
}

/// <summary>
/// <c>marketing.SaleCampaigns</c> (doc 08 §3.6; ADM-14, DEPT-03, HOME-07) [A] [SD] [RV]: a discount on
/// departments, categories or products for a date range (doc 02 line 119).
/// <para>
/// <b>Campaigns never stack.</b> Doc 08 §6 step 3 and doc 11 line 461 both say the <i>lowest resulting
/// price</i> wins — a rule about the result and not about the discount, and not about how specific the
/// target is. Two campaigns on one product are resolved by <see cref="CampaignPricing"/>, which is where
/// that rule lives and is tested; nothing in this entity ranks one target type above another, because
/// nothing in the specification does.
/// </para>
/// <para>
/// <b>Nothing is seeded.</b> A demo campaign is a discount the client never agreed to, and doc 09's
/// standing rule on invented data applies to money most of all.
/// </para>
/// </summary>
public sealed class SaleCampaign : IAuditable, ISoftDeletable
{
    /// <summary>Doc 08 §3.6: <c>nvarchar(100)</c>.</summary>
    public const int MaxNameLength = 100;

    /// <summary>Doc 08 §3.6: <c>nvarchar(500)</c>.</summary>
    public const int MaxDescriptionLength = 500;

    /// <summary>
    /// Doc 08 §3.6 "CK Percent ≤ 90", doc 11 P3 step 1 "percent up to 90%". The ceiling is the schema's,
    /// not the application's, so a data fix cannot write a 95% campaign either.
    /// </summary>
    public const decimal MaxPercentDiscount = 90m;

    public int Id { get; set; }

    /// <summary>Staff-facing and shopper-facing: DEPT-03's Sale page and HOME-07 can both print it.</summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public SaleDiscountType DiscountType { get; set; }

    /// <summary>
    /// The percentage (≤ <see cref="MaxPercentDiscount"/>) or the LKR amount (&gt; 0), which
    /// <c>CK_SaleCampaigns_DiscountValue</c> enforces over both columns at once.
    /// </summary>
    public decimal DiscountValue { get; set; }

    /// <summary>Inclusive, like every other scheduled window in this schema.</summary>
    public DateTime StartsAtUtc { get; set; }

    /// <summary>
    /// Exclusive. <b>Required</b>, unlike <see cref="PromoCode.EndsAtUtc"/>: doc 02 line 119 is "for a
    /// date range" and doc 08 §3.6 makes the column <c>NOT NULL</c> with <c>CK Ends &gt; Starts</c>.
    /// </summary>
    public DateTime EndsAtUtc { get; set; }

    public bool IsActive { get; set; }

    /// <summary>Doc 08 §3.6: optional banner, FK <c>media.MediaAssets</c> (doc 11 P3 step 1).</summary>
    public int? BannerAssetId { get; set; }

    public Media.MediaAsset? BannerAsset { get; set; }

    /// <summary>Departments, categories or products (doc 11 P3 step 2). Cascade children (doc 08 §1.6).</summary>
    public ICollection<SaleCampaignTarget> Targets { get; } = [];

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }

    public byte[]? RowVersion { get; set; }

    /// <summary>Doc 08 §1.5 [SD]. Set only by <see cref="SoftDelete"/>; <c>Remove()</c> throws (DB5).</summary>
    public bool IsDeleted { get; private set; }

    public DateTime? DeletedAtUtc { get; private set; }

    public int? DeletedByUserId { get; private set; }

    /// <summary>
    /// Doc 11 P3 step 5's third way to end a campaign. A soft delete, so the orders that were priced by it
    /// keep a resolvable <c>OrderLines.SaleCampaignId</c> — which is the reason the foreign key is
    /// <c>NO ACTION</c> and the delete is never hard.
    /// </summary>
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
}

/// <summary>
/// <c>marketing.SaleCampaignTargets</c> (doc 08 §3.6; ADM-14): one department, category or product a
/// campaign applies to. A pure child row — doc 08 §1.6 lists it in the <b>CASCADE</b> allow-list, so a
/// purge of a campaign takes its targets with it.
/// </summary>
/// <remarks>
/// <b>Three nullable id columns and not one, with a check constraint.</b> Doc 08 §3.6 spells the table out
/// that way ("CK: exactly the matching one is NOT NULL"), and the reason is that each column is a real
/// foreign key: a polymorphic <c>TargetId int</c> would point at three tables and be constrained to none,
/// so a category that was removed would leave a campaign silently targeting an id that means something
/// else now.
/// </remarks>
public sealed class SaleCampaignTarget
{
    public int Id { get; set; }

    public int SaleCampaignId { get; set; }

    public SaleCampaign? SaleCampaign { get; set; }

    public SaleCampaignTargetType TargetType { get; set; }

    public int? DepartmentId { get; set; }

    public Catalog.Department? Department { get; set; }

    public int? CategoryId { get; set; }

    public Catalog.Category? Category { get; set; }

    public int? ProductId { get; set; }

    public Catalog.Product? Product { get; set; }

    /// <summary>
    /// Doc 08 §3.6: <c>DF 1</c>. Only meaningful for <see cref="SaleCampaignTargetType.Category"/> — a
    /// department target already covers every category under it, and a product has no subtree.
    /// </summary>
    public bool IncludeSubcategories { get; set; } = true;
}
