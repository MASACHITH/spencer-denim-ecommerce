namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// <c>catalog.ProductSearchIndex</c> (doc 08 §3.2, §7.6): one denormalised row per product that the
/// <c>LIKE</c> search scans. Full-text is deferred (Appendix A).
/// </summary>
public sealed class ProductSearchIndex
{
    public int ProductId { get; set; }

    public Product? Product { get; set; }

    /// <summary>Lower-cased product name, used for prefix relevance and suggestions.</summary>
    public string NameText { get; set; } = string.Empty;

    /// <summary>
    /// Name, category path names, colour names and families, material, fit, occasion, product code
    /// and SKUs, lower-cased and space separated (tags are deferred).
    /// </summary>
    public string SearchText { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; }
}
