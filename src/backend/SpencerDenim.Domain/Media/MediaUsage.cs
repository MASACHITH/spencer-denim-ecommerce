namespace SpencerDenim.Domain.Media;

/// <summary>
/// <c>media.MediaUsages</c> (doc 08 §3.3): the "where used" panel and the delete guard. Real FKs
/// protect structured references; this table covers references that have no FK (settings, inline
/// HTML) and gives the library one uniform list.
/// </summary>
public sealed class MediaUsage
{
    public long Id { get; set; }

    public int MediaAssetId { get; set; }

    public MediaAsset? MediaAsset { get; set; }

    /// <summary>
    /// <c>Product</c>, <c>ProductColour</c>, <c>Category</c>, <c>Department</c>, <c>SizeGuide</c>,
    /// <c>MediaAsset</c> (poster), <c>Setting</c>, … (doc 08 §3.3).
    /// </summary>
    public string OwnerType { get; set; } = string.Empty;

    /// <summary>The owning entity's ID, or the setting key.</summary>
    public string OwnerId { get; set; } = string.Empty;

    public string UsageRole { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>The <see cref="MediaUsage.OwnerType"/> values this milestone writes.</summary>
public static class MediaUsageOwnerTypes
{
    public const string Product = "Product";
    public const string ProductColour = "ProductColour";
    public const string Category = "Category";
    public const string Department = "Department";
    public const string SizeGuide = "SizeGuide";
    public const string MediaAsset = "MediaAsset";

    /// <summary>
    /// A customer's payment slip (<c>sales.Payments.ProofAssetId</c>, PAY-01, doc 11 P7 step 3).
    /// </summary>
    /// <remarks>
    /// <b>Never persisted, and not one of doc 08 §3.3's owner types.</b> Every other constant here names a
    /// value that is written to <c>media.MediaUsages</c>; this one is <i>synthesised</i> at the moment a
    /// delete is refused, so the 409's "used in…" list can say what is holding the file in the same shape
    /// every other owner uses. No row with this <c>OwnerType</c> exists in the database, and nothing
    /// queries for one — a reader who assumes otherwise, as the first version of this comment invited them
    /// to, would go looking for rows that are not there.
    /// <para>
    /// The reference is checked directly instead, for the reason <c>MediaService.DeleteAsync</c> gives
    /// about a video's poster: <c>sales.Payments.ProofAssetId</c> is a real foreign key, so reading it is
    /// cheaper and safer than trusting that a usage row was written, and it also covers the slips attached
    /// before the check existed. <see cref="MediaUsage.OwnerId"/> carries the payment's id.
    /// </para>
    /// </remarks>
    public const string Payment = "Payment";

    /// <summary>
    /// One tile, banner or hero of the homepage (<c>content.HomeSectionItems</c>).
    /// <para>
    /// The <b>item</b>, which is what doc 08 §3.3 enumerates, not the section it sits in. A section id
    /// would name "the trending row" for all four of its pictures and "the homepage" for thirteen, so
    /// the "used in…" panel could not tell an operator which tile to go and change.
    /// </para>
    /// </summary>
    public const string HomeSectionItem = "HomeSectionItem";

    /// <summary>
    /// A department's mega-menu card (<c>content.MegaMenuPromoCards.ImageAssetId</c>, GLB-05, #215), one of doc
    /// 08 §3.3's owner types. The column has a real foreign key, but the media library's delete is a soft
    /// delete, so this row is what refuses deleting the picture a menu is showing. Rewritten in the same
    /// transaction as the card's save, and removed with the card.
    /// </summary>
    public const string MegaMenuPromoCard = "MegaMenuPromoCard";

    /// <summary>
    /// An <c>ops.Settings</c> row whose <c>ValueType</c> is <c>MediaAssetRef</c> (doc 08 §3.3, §3.8;
    /// ADM-22). <see cref="MediaUsage.OwnerId"/> is the setting <b>key</b> rather than a number — the
    /// one owner type whose id is not an integer, which is why the library's label lookup has to handle
    /// it separately.
    /// <para>
    /// This is what makes the media library refuse to delete the shop's logo while it is the shop's
    /// logo: <c>Settings.Value</c> has no foreign key to <c>MediaAssets</c> (it is an
    /// <c>nvarchar(max)</c> holding a number), so the usage row is the <i>only</i> thing standing
    /// between a staff user and a header with a broken image in it.
    /// </para>
    /// </summary>
    public const string Setting = "Setting";
}

/// <summary>The <see cref="MediaUsage.UsageRole"/> values this milestone writes.</summary>
public static class MediaUsageRoles
{
    public const string Gallery = "Gallery";
    public const string Hover = "Hover";
    public const string Video = "Video";
    public const string Card = "Card";
    public const string Swatch = "Swatch";
    public const string Hero = "Hero";
    public const string Image = "Image";
    public const string Banner = "Banner";
    public const string Poster = "Poster";

    /// <summary>
    /// A homepage item's phone artwork (<c>HomeSectionItems.MobileImageAssetId</c>, ADM-17). Its own role
    /// rather than <see cref="Image"/>, so one photograph used as both the desktop and the phone image is
    /// two usage rows the "used in…" panel can tell apart, not one that hides the other.
    /// </summary>
    public const string MobileImage = "MobileImage";

    /// <summary>The shop's logo (GLB-02, ADM-22): <c>Store.LogoAssetId</c>'s image.</summary>
    public const string Logo = "Logo";

    /// <summary>
    /// The customer's proof of a bank transfer (PAY-01): <c>sales.Payments.ProofAssetId</c>'s image or PDF.
    /// </summary>
    public const string PaymentSlip = "PaymentSlip";
}
