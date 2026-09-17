namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// <c>catalog.Sizes</c> (doc 08 §3.2; simplification 6 merged the old SizeScales table into
/// <see cref="ScaleName"/>). The admin picks an existing scale name or types a new one.
/// </summary>
public sealed class Size
{
    public int Id { get; set; }

    /// <summary>"Alpha", "Women waist", "Kids age", "Baby months", "Footwear EU", "One size".</summary>
    public string ScaleName { get; set; } = string.Empty;

    /// <summary>"M", "32", "4–5Y".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Order within the scale.</summary>
    public short SortOrder { get; set; }
}
