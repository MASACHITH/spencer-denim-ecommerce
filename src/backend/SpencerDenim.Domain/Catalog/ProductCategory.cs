namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// <c>catalog.ProductCategories</c> (doc 08 §3.2): which categories list a product. The primary
/// category is stored here too, so one EXISTS predicate answers every category filter.
/// </summary>
public sealed class ProductCategory
{
    public int CategoryId { get; set; }

    public Category? Category { get; set; }

    public int ProductId { get; set; }

    public Product? Product { get; set; }

    /// <summary>Manual merchandising order inside this category (doc 08 §7.3). NULLs sort last.</summary>
    public int? SortOrder { get; set; }
}
