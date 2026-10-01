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
    /// One tile, banner or hero of the homepage (<c>content.HomeSectionItems</c>).
    /// <para>
    /// The <b>item</b>, which is what doc 08 §3.3 enumerates, not the section it sits in. A section id
    /// would name "the trending row" for all four of its pictures and "the homepage" for thirteen, so
    /// the "used in…" panel could not tell an operator which tile to go and change.
    /// </para>
    /// </summary>
    public const string HomeSectionItem = "HomeSectionItem";
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
}
