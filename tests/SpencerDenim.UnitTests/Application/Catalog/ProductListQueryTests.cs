using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Catalog.Products;

namespace SpencerDenim.UnitTests.Application.Catalog;

/// <summary>
/// Validation and normalisation of the listing query (PLP-02, PLP-04, PLP-07). Everything here is pure,
/// so the filter, sort and paging rules are covered without a database.
/// </summary>
public sealed class ProductListQueryTests
{
    private static readonly CatalogOptions Options = new() { DefaultPageSize = 24, MaxPageSize = 96 };

    private static ProductListQuery Create(ProductListCriteria criteria)
    {
        var result = ProductListQuery.Create(criteria, Options);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors.Select(error => $"{error.Field}: {error.Message}")));
        return result.Value;
    }

    private static List<string> FailureFields(ProductListCriteria criteria)
    {
        var result = ProductListQuery.Create(criteria, Options);
        Assert.False(result.IsSuccess);
        return result.Errors.Select(error => error.Field).ToList();
    }

    [Fact]
    public void An_empty_query_uses_the_defaults()
    {
        var query = Create(new ProductListCriteria());

        Assert.Null(query.DepartmentSlug);
        Assert.Null(query.CategorySlug);
        Assert.Empty(query.SizeLabels);
        Assert.False(query.InStockOnly);
        Assert.False(query.OnSaleOnly);
        Assert.False(query.NewArrivalsOnly);
        Assert.Equal(ProductSortOption.Recommended, query.Sort);
        Assert.Equal(1, query.Page);
        Assert.Equal(24, query.PageSize);
        Assert.Equal(0, query.Skip);
    }

    [Fact]
    public void Slugs_are_trimmed_and_lower_cased()
    {
        var query = Create(new ProductListCriteria(Department: "  Women  ", Category: "Shirts-Blouses"));

        Assert.Equal("women", query.DepartmentSlug);
        Assert.Equal("shirts-blouses", query.CategorySlug);
    }

    [Fact]
    public void Repeated_filter_values_are_de_duplicated_and_blanks_dropped()
    {
        var query = Create(new ProductListCriteria(
            Colours: ["navy", "NAVY", " navy ", "cream"],
            Sizes: ["M", "m", "  ", "L"]));

        Assert.Equal(["navy", "cream"], query.ColourFamilySlugs);
        Assert.Equal(["M", "L"], query.SizeLabels);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("Trailing-")]
    [InlineData("double--hyphen")]
    [InlineData("under_score")]
    [InlineData("semi;colon")]
    public void A_malformed_slug_is_rejected(string slug)
    {
        Assert.Contains("department", FailureFields(new ProductListCriteria(Department: slug)));
    }

    [Fact]
    public void A_slug_longer_than_the_column_is_rejected()
    {
        var slug = new string('a', ProductListQuery.MaxSlugLength + 1);

        Assert.Contains("category", FailureFields(new ProductListCriteria(Category: slug)));
    }

    [Fact]
    public void Too_many_values_for_one_filter_are_rejected()
    {
        var colours = Enumerable.Range(0, ProductListQuery.MaxFilterValues + 1)
            .Select(index => $"colour-{index}")
            .ToArray();

        Assert.Contains("colour", FailureFields(new ProductListCriteria(Colours: colours)));
    }

    [Fact]
    public void A_size_label_longer_than_the_column_is_rejected()
    {
        var label = new string('X', ProductListQuery.MaxSizeLabelLength + 1);

        Assert.Contains("size", FailureFields(new ProductListCriteria(Sizes: [label])));
    }

    [Fact]
    public void Prices_are_rounded_to_two_decimals()
    {
        var query = Create(new ProductListCriteria(MinPrice: 1000.005m, MaxPrice: 2000.004m));

        Assert.Equal(1000.01m, query.MinPrice);
        Assert.Equal(2000.00m, query.MaxPrice);
    }

    [Fact]
    public void A_negative_price_is_rejected()
    {
        Assert.Contains("minPrice", FailureFields(new ProductListCriteria(MinPrice: -1m)));
    }

    [Fact]
    public void A_price_above_the_cap_is_rejected()
    {
        Assert.Contains("maxPrice", FailureFields(new ProductListCriteria(MaxPrice: ProductListQuery.MaxPriceValue + 1m)));
    }

    [Fact]
    public void An_inverted_price_range_is_rejected()
    {
        Assert.Contains("minPrice", FailureFields(new ProductListCriteria(MinPrice: 5000m, MaxPrice: 1000m)));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(5.1)]
    public void A_rating_outside_zero_to_five_is_rejected(double rating)
    {
        Assert.Contains("minRating", FailureFields(new ProductListCriteria(MinRating: (decimal)rating)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void A_discount_outside_zero_to_one_hundred_is_rejected(int discount)
    {
        Assert.Contains("minDiscountPercent", FailureFields(new ProductListCriteria(MinDiscountPercent: discount)));
    }

    [Fact]
    public void Asking_for_a_minimum_discount_implies_the_on_sale_filter()
    {
        var query = Create(new ProductListCriteria(MinDiscountPercent: 20m));

        Assert.True(query.OnSaleOnly);
        Assert.Equal(20m, query.MinDiscountPercent);
    }

    [Fact]
    public void A_zero_minimum_discount_does_not_imply_the_on_sale_filter()
    {
        var query = Create(new ProductListCriteria(MinDiscountPercent: 0m));

        Assert.False(query.OnSaleOnly);
    }

    [Theory]
    [InlineData("recommended", ProductSortOption.Recommended)]
    [InlineData("newest", ProductSortOption.Newest)]
    [InlineData("best-selling", ProductSortOption.BestSelling)]
    [InlineData("price-asc", ProductSortOption.PriceAscending)]
    [InlineData("price-desc", ProductSortOption.PriceDescending)]
    [InlineData("rating", ProductSortOption.HighestRated)]
    [InlineData("discount", ProductSortOption.BiggestDiscount)]
    [InlineData("PRICE-ASC", ProductSortOption.PriceAscending)]
    public void Every_plp_04_sort_is_accepted(string value, ProductSortOption expected)
    {
        Assert.Equal(expected, Create(new ProductListCriteria(Sort: value)).Sort);
    }

    [Fact]
    public void An_unknown_sort_is_rejected_and_the_message_lists_the_valid_values()
    {
        var result = ProductListQuery.Create(new ProductListCriteria(Sort: "cheapest"), Options);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors, candidate => candidate.Field == "sort");
        Assert.Contains("price-asc", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Paging_skips_the_pages_before_the_requested_one()
    {
        var query = Create(new ProductListCriteria(Page: 4, PageSize: 12));

        Assert.Equal(36, query.Skip);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ProductListQuery.MaxPage + 1)]
    public void An_out_of_range_page_is_rejected(int page)
    {
        Assert.Contains("page", FailureFields(new ProductListCriteria(Page: page)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(97)]
    public void A_page_size_outside_the_configured_bounds_is_rejected(int pageSize)
    {
        Assert.Contains("pageSize", FailureFields(new ProductListCriteria(PageSize: pageSize)));
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var fields = FailureFields(new ProductListCriteria(
            Department: "not a slug",
            Sort: "nonsense",
            Page: 0,
            PageSize: 500,
            MinRating: 9m));

        Assert.Contains("department", fields);
        Assert.Contains("sort", fields);
        Assert.Contains("page", fields);
        Assert.Contains("pageSize", fields);
        Assert.Contains("minRating", fields);
    }

    [Fact]
    public void The_category_merchandising_order_applies_only_to_recommended_with_one_category()
    {
        Assert.True(Create(new ProductListCriteria(Category: "jeans")).UsesCategoryMerchandisingOrder);
        Assert.False(Create(new ProductListCriteria()).UsesCategoryMerchandisingOrder);
        Assert.False(Create(new ProductListCriteria(Category: "jeans", Sort: "newest")).UsesCategoryMerchandisingOrder);
    }

    [Fact]
    public void The_cache_key_is_stable_for_equivalent_queries_and_differs_otherwise()
    {
        var first = Create(new ProductListCriteria(Department: "Women", Colours: ["navy", "NAVY"], Sort: "newest"));
        var second = Create(new ProductListCriteria(Department: "women", Colours: [" navy "], Sort: "newest"));
        var third = Create(new ProductListCriteria(Department: "women", Colours: ["navy"], Sort: "price-asc"));

        Assert.Equal(first.ToCacheKey(), second.ToCacheKey());
        Assert.NotEqual(first.ToCacheKey(), third.ToCacheKey());
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => ProductListQuery.Create(null!, Options));
        Assert.Throws<ArgumentNullException>(() => ProductListQuery.Create(new ProductListCriteria(), null!));
    }
}
