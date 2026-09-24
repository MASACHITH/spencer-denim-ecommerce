namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// <c>catalog.ColourFamilies</c> (doc 08 §3.2): the colour buckets the PLP-02 filter offers, so
/// "Floral Blue" and "Indigo" both answer a "Blue" filter. Not audited in doc 08.
/// </summary>
public sealed class ColourFamily
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    /// <summary>Swatch colour, <c>#rrggbb</c>.</summary>
    public string HexCode { get; set; } = "#000000";

    public short SortOrder { get; set; }
}
