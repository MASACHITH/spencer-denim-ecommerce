using SpencerDenim.Domain.Common;

namespace SpencerDenim.Domain.Marketing;

/// <summary><c>marketing.PromoCodes.DiscountType</c> (doc 08 §3.6; CK allows exactly these three).</summary>
public enum PromoDiscountType : byte
{
    /// <summary>A percentage off the eligible items, optionally capped (doc 08 §6 step 8).</summary>
    Percent = 1,

    /// <summary>A fixed LKR amount off the eligible items, never more than they are worth.</summary>
    FixedAmount = 2,

    /// <summary>
    /// The delivery fee discounted in full. It contributes <b>nothing</b> to step 8's items discount and
    /// everything to step 10's delivery discount, which is why <c>DiscountValue</c> is null for it.
    /// </summary>
    FreeDelivery = 3,
}

/// <summary>
/// <c>marketing.PromoCodes</c> (doc 08 §3.6; ADM-13, CART-01) [A] [SD] [RV]: one admin-managed code.
/// <para>
/// <b>It replaces the old demo's hard-coded <c>TUASL10</c> and <c>FREESHIP</c></b>, which doc 02 line 167
/// names explicitly as "replaced by admin-managed promo codes". Nothing is seeded: a demo code is the
/// client's decision, and a plausible-looking one in the database would be a discount the shop never
/// agreed to.
/// </para>
/// <para>
/// <b>What is deliberately not here: sale campaigns (ADM-14).</b> Doc 02 line 119 is a separate feature
/// with its own tables (<see cref="SaleCampaign"/>, built by W3-05b) and its own
/// <c>RefreshCampaignPricing</c> job. <see cref="ExcludeSaleItems"/> is the one place the two meet, and
/// it is a column this code reads rather than a campaign this code creates.
/// </para>
/// </summary>
public sealed class PromoCode : IAuditable, ISoftDeletable
{
    /// <summary>Doc 08 §3.6: <c>varchar(40)</c>, which <c>Orders.PromoCodeText</c> also uses.</summary>
    public const int MaxCodeLength = 40;

    /// <summary>Doc 08 §3.6: <c>nvarchar(250)</c>.</summary>
    public const int MaxDescriptionLength = 250;

    public int Id { get; set; }

    /// <summary>
    /// The code, stored upper-case (doc 08 §3.6, doc 11 P3 step 6) behind
    /// <c>UX_PromoCodes_Code WHERE IsDeleted = 0</c>.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Staff-facing note. Never shown to a shopper.</summary>
    public string? Description { get; set; }

    public PromoDiscountType DiscountType { get; set; }

    /// <summary>
    /// The percentage or the LKR amount, and <see langword="null"/> for
    /// <see cref="PromoDiscountType.FreeDelivery"/> — which <c>CK_PromoCodes_DiscountValue</c> enforces
    /// rather than leaves to the application.
    /// </summary>
    public decimal? DiscountValue { get; set; }

    /// <summary>The cap on a percentage discount (doc 08 §6 step 8). Only meaningful for Percent.</summary>
    public decimal? MaxDiscountAmount { get; set; }

    /// <summary>
    /// Compared with the <b>items subtotal after sale prices</b> (doc 08 §6 step 7, doc 11 P3), not with
    /// the eligible base and not with the grand total.
    /// </summary>
    public decimal? MinSpendAmount { get; set; }

    /// <summary>
    /// Doc 08 §6 step 7: lines a campaign priced are left out of the eligible base. Lines that only carry
    /// a markdown stay in (doc 11 §7 G-07).
    /// </summary>
    public bool ExcludeSaleItems { get; set; }

    /// <summary>Required (doc 11 P3 step 7). Inclusive.</summary>
    public DateTime StartsAtUtc { get; set; }

    /// <summary>Optional. Exclusive, like every other scheduled window in this schema.</summary>
    public DateTime? EndsAtUtc { get; set; }

    /// <summary>Across all customers. <see langword="null"/> is "no limit".</summary>
    public int? TotalUsageLimit { get; set; }

    /// <summary>Per normalised email (doc 11 line 468). <see langword="null"/> is "no limit".</summary>
    public int? PerCustomerLimit { get; set; }

    /// <summary>
    /// The cached count of <i>held</i> uses (doc 08 §3.6's usage model). Moved only by the conditional
    /// UPDATE at placement and the decrement when an unpaid order closes — never by application
    /// arithmetic, which is why there is no setter helper here.
    /// </summary>
    public int UsageCount { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }

    public byte[]? RowVersion { get; set; }

    /// <summary>Doc 08 §1.5 [SD]. Set only by <see cref="SoftDelete"/>.</summary>
    public bool IsDeleted { get; private set; }

    public DateTime? DeletedAtUtc { get; private set; }

    public int? DeletedByUserId { get; private set; }

    /// <summary>
    /// Removes the code. A soft delete, which releases the code string for reuse because
    /// <c>UX_PromoCodes_Code</c> is filtered on <c>IsDeleted = 0</c> — and leaves every order that already
    /// used it alone, because an order keeps <c>PromoCodeText</c> as its own snapshot (doc 08 §3.6 DB19:
    /// "Deactivating or ending a code doesn't affect orders already placed").
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
