namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// <c>catalog.RelatedProducts</c> (doc 08 §3.2): the merchandiser's picks for PDP-06. The product
/// page tops the list up to four with same-category products.
/// </summary>
public sealed class RelatedProduct
{
    public int ProductId { get; set; }

    public Product? Product { get; set; }

    public int RelatedProductId { get; set; }

    public Product? Related { get; set; }

    public short SortOrder { get; set; }
}
