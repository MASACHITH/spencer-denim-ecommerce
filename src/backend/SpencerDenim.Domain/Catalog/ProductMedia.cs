using SpencerDenim.Domain.Media;

namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// <c>catalog.ProductMedia</c> (doc 08 §3.2): the PDP-01 gallery. A row may be tied to one colourway,
/// so selecting a colour swaps the gallery.
/// </summary>
public sealed class ProductMedia
{
    public int Id { get; set; }

    public int ProductId { get; set; }

    public Product? Product { get; set; }

    public int MediaAssetId { get; set; }

    public MediaAsset? MediaAsset { get; set; }

    /// <summary>NULL means the image belongs to every colourway.</summary>
    public int? ProductColourId { get; set; }

    public ProductColour? ProductColour { get; set; }

    public ProductMediaRole MediaRole { get; set; } = ProductMediaRole.Gallery;

    public short SortOrder { get; set; }

    public string? AltTextOverride { get; set; }
}
