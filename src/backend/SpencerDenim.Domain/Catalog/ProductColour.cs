using SpencerDenim.Domain.Media;

namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// <c>catalog.ProductColours</c> (doc 08 §3.2): one colourway of a product. The marketing name
/// ("Indigo Rinse") is free text; <see cref="ColourFamilyId"/> is the bucket the PLP-02 filter uses.
/// </summary>
public sealed class ProductColour
{
    public int Id { get; set; }

    public int ProductId { get; set; }

    public Product? Product { get; set; }

    public string Name { get; set; } = string.Empty;

    public int ColourFamilyId { get; set; }

    public ColourFamily? ColourFamily { get; set; }

    /// <summary>Swatch colour, <c>#rrggbb</c>.</summary>
    public string? SwatchHex { get; set; }

    public int? SwatchAssetId { get; set; }

    public MediaAsset? SwatchAsset { get; set; }

    public short SortOrder { get; set; }

    public ICollection<ProductVariant> Variants { get; } = [];

    public ICollection<ProductMedia> Media { get; } = [];
}
