using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Catalog.Products;
using SpencerDenim.Application.Content;

namespace SpencerDenim.UnitTests.Application.Content;

/// <summary>
/// A homepage product row is the head of the listing its link opens, and this is what makes that
/// true: the same URL that goes in the <c>href</c> is what the row is fetched with.
/// <para>
/// The assertions go through <see cref="ProductListQuery.Create"/> as well as through the criteria,
/// because a criteria record that the catalogue then refuses is a row that silently shows nothing.
/// </para>
/// </summary>
public sealed class ListingUrlTests
{
    private static readonly CatalogOptions Options = new();

    [Fact]
    public void No_url_means_no_query()
    {
        Assert.Null(ListingUrl.ToCriteria(null, 8));
        Assert.Null(ListingUrl.ToCriteria("   ", 8));
    }

    [Fact]
    public void The_new_arrivals_listing_carries_its_own_filter_and_no_department()
    {
        var criteria = ListingUrl.ToCriteria("/new-arrivals", 8);

        Assert.NotNull(criteria);
        Assert.Equal("true", criteria.NewArrivals);
        Assert.Null(criteria.Department);
        Assert.Null(criteria.Category);

        var query = ProductListQuery.Create(criteria, Options);
        Assert.True(query.IsSuccess);
        Assert.True(query.Value.NewArrivalsOnly);
        Assert.False(query.Value.OnSaleOnly);
    }

    [Fact]
    public void The_sale_listing_carries_its_own_filter()
    {
        var query = ProductListQuery.Create(ListingUrl.ToCriteria("/sale", 8)!, Options);

        Assert.True(query.IsSuccess);
        Assert.True(query.Value.OnSaleOnly);
        Assert.False(query.Value.NewArrivalsOnly);
    }

    [Fact]
    public void A_department_path_becomes_a_department_filter()
    {
        var query = ProductListQuery.Create(ListingUrl.ToCriteria("/women", 8)!, Options);

        Assert.True(query.IsSuccess);
        Assert.Equal("women", query.Value.DepartmentSlug);
        Assert.Null(query.Value.CategorySlug);
    }

    [Theory]
    [InlineData("/women/jeans", "women", "jeans")]
    // The catalogue filters on the deepest segment and returns its subtree, which is the same rule
    // the listing page's own three-level route follows.
    [InlineData("/kids/girls/girls-dresses", "kids", "girls-dresses")]
    public void A_category_path_becomes_a_department_and_the_deepest_category(
        string url,
        string department,
        string category)
    {
        var query = ProductListQuery.Create(ListingUrl.ToCriteria(url, 8)!, Options);

        Assert.True(query.IsSuccess);
        Assert.Equal(department, query.Value.DepartmentSlug);
        Assert.Equal(category, query.Value.CategorySlug);
    }

    [Fact]
    public void A_bare_slash_is_the_whole_catalogue()
    {
        var query = ProductListQuery.Create(ListingUrl.ToCriteria("/?sort=best-selling", 8)!, Options);

        Assert.True(query.IsSuccess);
        Assert.Null(query.Value.DepartmentSlug);
        Assert.Null(query.Value.CategorySlug);
        Assert.Equal(ProductSortOption.BestSelling, query.Value.Sort);
    }

    [Fact]
    public void Repeatable_filters_keep_every_value()
    {
        var query = ProductListQuery.Create(
            ListingUrl.ToCriteria("/women?colour=navy&colour=black&size=M&size=L", 8)!,
            Options);

        Assert.True(query.IsSuccess);
        Assert.Equal(["navy", "black"], query.Value.ColourFamilySlugs);
        Assert.Equal(["M", "L"], query.Value.SizeLabels);
    }

    [Fact]
    public void A_percent_encoded_value_is_decoded_before_the_catalogue_sees_it()
    {
        var criteria = ListingUrl.ToCriteria("/women?size=one%20size&colour=off%2Dwhite", 8);

        Assert.NotNull(criteria);
        Assert.Equal(["one size"], criteria.Sizes);
        Assert.Equal(["off-white"], criteria.Colours);
    }

    [Fact]
    public void A_plus_in_a_value_is_a_space_the_way_a_form_writes_one()
    {
        var criteria = ListingUrl.ToCriteria("/women?size=one+size", 8);

        Assert.Equal(["one size"], criteria!.Sizes);
    }

    /// <summary>
    /// A malformed escape must not throw inside a homepage render. It stays as typed, fails
    /// validation with a message naming the parameter, and the row shows nothing — which is far
    /// better than a 500 on the front page.
    /// </summary>
    [Fact]
    public void A_malformed_escape_does_not_throw()
    {
        var criteria = ListingUrl.ToCriteria("/women?colour=%zz", 8);

        Assert.NotNull(criteria);
        Assert.Equal(["%zz"], criteria.Colours);
        Assert.False(ProductListQuery.Create(criteria, Options).IsSuccess);
    }

    [Fact]
    public void The_row_always_asks_for_the_first_page_at_the_size_the_section_wants()
    {
        var query = ProductListQuery.Create(ListingUrl.ToCriteria("/sale?page=7&pageSize=96", 6)!, Options);

        Assert.True(query.IsSuccess);
        Assert.Equal(1, query.Value.Page);
        Assert.Equal(6, query.Value.PageSize);
        Assert.Equal(0, query.Value.Skip);
    }

    /// <summary>
    /// The path wins over a query parameter that contradicts it, so <c>/women?department=men</c>
    /// cannot serve Men's products under a link that goes to Women.
    /// </summary>
    [Fact]
    public void The_path_wins_over_a_contradicting_query_parameter()
    {
        var query = ProductListQuery.Create(
            ListingUrl.ToCriteria("/women/jeans?department=men&category=shirts", 8)!,
            Options);

        Assert.True(query.IsSuccess);
        Assert.Equal("women", query.Value.DepartmentSlug);
        Assert.Equal("jeans", query.Value.CategorySlug);
    }

    /// <summary>
    /// Nothing is validated here: the catalogue is the only judge of a filter, so a homepage row and
    /// the listing page it links to cannot disagree about what a bad value means.
    /// </summary>
    [Fact]
    public void A_value_the_catalogue_refuses_is_refused_here_too()
    {
        var result = ProductListQuery.Create(ListingUrl.ToCriteria("/women?minPrice=abc", 8)!, Options);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Field == "minPrice");
    }
}
