namespace SpencerDenim.Application.Catalog.Products;

/// <summary>
/// The raw, unvalidated query string of <c>GET /api/catalog/products</c>, exactly as it arrives.
/// <see cref="ProductListQuery.Create"/> turns it into a validated query or a list of 400 errors.
/// </summary>
/// <param name="Department">Department slug, e.g. <c>women</c>.</param>
/// <param name="Category">Category slug. Matches the category and its whole subtree.</param>
/// <param name="AgeGroups">Age-group slugs (repeatable), e.g. <c>baby</c>.</param>
/// <param name="MinPrice">Lowest current price, in LKR.</param>
/// <param name="MaxPrice">Highest current price, in LKR.</param>
/// <param name="Sizes">Size labels (repeatable), e.g. <c>M</c> or <c>32</c>.</param>
/// <param name="Colours">Colour-family slugs (repeatable), e.g. <c>navy</c>.</param>
/// <param name="Materials">Material attribute slugs (repeatable).</param>
/// <param name="Fits">Fit attribute slugs (repeatable).</param>
/// <param name="Occasions">Occasion attribute slugs (repeatable).</param>
/// <param name="MinRating">Lowest average rating, 0–5.</param>
/// <param name="InStock">Only products with at least one available active variant.</param>
/// <param name="OnSale">Only products that currently show a discount.</param>
/// <param name="MinDiscountPercent">Lowest displayed discount, 0–100.</param>
/// <param name="NewArrivals">Only products still inside their "New" window (DEPT-02).</param>
/// <param name="Sort">One of <see cref="ProductSortOptions.All"/>.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Rows per page.</param>
public sealed record ProductListCriteria(
    string? Department = null,
    string? Category = null,
    IReadOnlyList<string>? AgeGroups = null,
    decimal? MinPrice = null,
    decimal? MaxPrice = null,
    IReadOnlyList<string>? Sizes = null,
    IReadOnlyList<string>? Colours = null,
    IReadOnlyList<string>? Materials = null,
    IReadOnlyList<string>? Fits = null,
    IReadOnlyList<string>? Occasions = null,
    decimal? MinRating = null,
    bool? InStock = null,
    bool? OnSale = null,
    decimal? MinDiscountPercent = null,
    bool? NewArrivals = null,
    string? Sort = null,
    int? Page = null,
    int? PageSize = null);
