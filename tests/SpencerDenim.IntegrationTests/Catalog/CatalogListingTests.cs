using System.Net;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Inventory;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Catalog;

/// <summary>
/// PLP-01..07 and CARD-01 against the seeded catalogue on a real SQL Server: every filter, every sort,
/// paging, and the empty state.
/// </summary>
public sealed class CatalogListingTests(SqlServerDatabaseFixture sqlServer) : CatalogTestBase(sqlServer)
{
    [Fact]
    public async Task Departments_return_the_three_departments_with_their_category_tree()
    {
        var api = CreateApi();

        var departments = await api.GetDepartmentsAsync(Token);

        Assert.Equal(["women", "men", "kids"], departments.Select(department => department.Slug));

        var kids = departments.Single(department => department.Slug == "kids");
        var girls = kids.Categories.Single(category => category.Slug == "girls");

        Assert.Equal(1, girls.Depth);
        Assert.Contains(girls.Children, child => child.Slug == "girls-dresses");
        Assert.All(girls.Children, child => Assert.Equal(2, child.Depth));
        Assert.Equal("kids", girls.AgeGroupSlug);
    }

    [Fact]
    public async Task An_unfiltered_listing_returns_every_published_product_one_page_at_a_time()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?pageSize=10", Token);

        Assert.Equal(45, page.TotalCount);
        Assert.Equal(10, page.Items.Count);
        Assert.Equal(1, page.Page);
        Assert.Equal(5, page.TotalPages);
        Assert.True(page.HasNextPage);
        Assert.False(page.HasPreviousPage);
    }

    [Fact]
    public async Task Paging_walks_the_whole_catalogue_without_gaps_or_repeats()
    {
        var api = CreateApi();

        var seen = new List<int>();
        for (var pageNumber = 1; pageNumber <= 5; pageNumber++)
        {
            var page = await api.ListAsync($"?pageSize=10&page={pageNumber}&sort=price-asc", Token);
            seen.AddRange(page.Items.Select(item => item.Id));
        }

        Assert.Equal(45, seen.Count);
        Assert.Equal(45, seen.Distinct().Count());

        var lastPage = await api.ListAsync("?pageSize=10&page=5&sort=price-asc", Token);
        Assert.Equal(5, lastPage.Items.Count);
        Assert.False(lastPage.HasNextPage);
        Assert.True(lastPage.HasPreviousPage);
    }

    [Fact]
    public async Task A_page_past_the_end_is_an_empty_page_not_an_error()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?pageSize=10&page=99", Token);

        Assert.Empty(page.Items);
        Assert.Equal(45, page.TotalCount);
    }

    [Fact]
    public async Task The_department_filter_narrows_the_listing()
    {
        var api = CreateApi();

        var women = await api.ListAsync("?department=women&pageSize=96", Token);
        var men = await api.ListAsync("?department=men&pageSize=96", Token);
        var kids = await api.ListAsync("?department=kids&pageSize=96", Token);

        Assert.Equal(19, women.TotalCount);
        Assert.Equal(16, men.TotalCount);
        Assert.Equal(10, kids.TotalCount);
        Assert.All(women.Items, card => Assert.Equal("women", card.DepartmentSlug));
    }

    [Fact]
    public async Task The_category_filter_includes_the_whole_subtree()
    {
        var api = CreateApi();

        var girls = await api.ListAsync("?department=kids&category=girls&pageSize=96", Token);
        var girlsDresses = await api.ListAsync("?department=kids&category=girls-dresses&pageSize=96", Token);

        // "Girls" has no products of its own; everything comes from its children.
        Assert.True(girls.TotalCount >= girlsDresses.TotalCount);
        Assert.Equal(2, girlsDresses.TotalCount);
        Assert.Contains(girls.Items, card => card.CategorySlug == "girls-dresses");
        Assert.Contains(girls.Items, card => card.CategorySlug == "girls-tops");
    }

    [Fact]
    public async Task The_age_group_filter_narrows_to_baby_products()
    {
        var api = CreateApi();

        var baby = await api.ListAsync("?ageGroup=baby&pageSize=96", Token);

        Assert.Equal(2, baby.TotalCount);
        Assert.Contains(baby.Items, card => card.Slug == "baby-ribbed-bodysuit-set");
    }

    [Fact]
    public async Task The_price_range_filter_uses_the_current_price()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?minPrice=2000&maxPrice=4000&pageSize=96", Token);

        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, card =>
        {
            Assert.True(card.Price.Current >= 2000m, $"{card.Slug} is {card.Price.Current}");
            Assert.True(card.Price.Current <= 4000m, $"{card.Slug} is {card.Price.Current}");
        });
    }

    [Fact]
    public async Task The_size_filter_returns_only_products_offered_in_that_size()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?size=XXL&pageSize=96", Token);

        Assert.NotEmpty(page.Items);

        // Every returned product must really offer the size.
        foreach (var card in page.Items)
        {
            var product = await api.GetProductAsync(card.Slug, Token);
            Assert.Contains(product.Sizes, size => size.Label == "XXL");
        }
    }

    [Fact]
    public async Task Two_size_values_are_combined_with_or()
    {
        var api = CreateApi();

        var alpha = await api.ListAsync("?size=XXL&pageSize=96", Token);
        var waist = await api.ListAsync("?size=38&pageSize=96", Token);
        var both = await api.ListAsync("?size=XXL&size=38&pageSize=96", Token);

        Assert.True(both.TotalCount >= Math.Max(alpha.TotalCount, waist.TotalCount));
        Assert.True(both.TotalCount <= alpha.TotalCount + waist.TotalCount);
    }

    [Fact]
    public async Task The_colour_filter_matches_the_colour_family_not_the_marketing_name()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?colour=navy&pageSize=96", Token);

        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, card => Assert.Contains(card.Colours, colour => colour.FamilySlug == "navy"));

        // "Deep Navy" is the marketing name behind the Navy family.
        Assert.Contains(page.Items, card => card.Colours.Any(colour => colour.Name == "Deep Navy"));
    }

    [Theory]
    [InlineData("material=denim")]
    [InlineData("fit=relaxed")]
    [InlineData("occasion=traditional")]
    public async Task The_attribute_filters_narrow_the_listing(string filter)
    {
        var api = CreateApi();

        var all = await api.ListAsync("?pageSize=96", Token);
        var filtered = await api.ListAsync($"?{filter}&pageSize=96", Token);

        Assert.NotEmpty(filtered.Items);
        Assert.True(filtered.TotalCount < all.TotalCount);
    }

    [Fact]
    public async Task The_material_filter_matches_the_product_page_attribute()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?material=denim&pageSize=96", Token);

        foreach (var card in page.Items)
        {
            var product = await api.GetProductAsync(card.Slug, Token);
            Assert.Equal("denim", product.Attributes.MaterialSlug);
        }
    }

    [Fact]
    public async Task The_rating_filter_excludes_lower_rated_and_unrated_products()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?minRating=4.5&pageSize=96", Token);

        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, card =>
        {
            Assert.NotNull(card.Rating.Average);
            Assert.True(card.Rating.Average >= 4.5m, $"{card.Slug} is rated {card.Rating.Average}");
        });
    }

    [Fact]
    public async Task The_availability_filter_removes_sold_out_products()
    {
        var api = CreateApi();

        var all = await api.ListAsync("?pageSize=96", Token);
        var inStock = await api.ListAsync("?inStock=true&pageSize=96", Token);

        Assert.Equal(2, all.TotalCount - inStock.TotalCount);
        Assert.All(inStock.Items, card =>
        {
            Assert.True(card.IsInStock);
            Assert.NotEqual(StockStatus.OutOfStock, card.StockStatus);
        });
        Assert.Contains(all.Items, card => card.StockStatus == StockStatus.OutOfStock);
    }

    [Fact]
    public async Task The_discount_filters_return_only_reduced_products()
    {
        var api = CreateApi();

        var onSale = await api.ListAsync("?onSale=true&pageSize=96", Token);
        var deepDiscount = await api.ListAsync("?minDiscountPercent=20&pageSize=96", Token);

        Assert.Equal(11, onSale.TotalCount);
        Assert.All(onSale.Items, card =>
        {
            Assert.True(card.Price.HasDiscount);
            Assert.True(card.Price.Original > card.Price.Current);
        });

        Assert.NotEmpty(deepDiscount.Items);
        Assert.All(deepDiscount.Items, card => Assert.True(card.Price.DiscountPercent >= 20m));
        Assert.True(deepDiscount.TotalCount <= onSale.TotalCount);
    }

    [Fact]
    public async Task The_new_arrivals_filter_returns_products_inside_their_new_window()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?newArrivals=true&pageSize=96", Token);

        Assert.NotEmpty(page.Items);
        Assert.Contains(page.Items, card => card.Slug == "baby-ribbed-bodysuit-set");
    }

    [Fact]
    public async Task Filters_combine_with_and()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?department=men&material=denim&inStock=true&minPrice=7000&pageSize=96", Token);

        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, card =>
        {
            Assert.Equal("men", card.DepartmentSlug);
            Assert.True(card.IsInStock);
            Assert.True(card.Price.Current >= 7000m);
        });
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_returns_the_empty_state()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?department=women&colour=orange&material=denim&pageSize=96", Token);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(0, page.TotalPages);
        Assert.False(page.HasNextPage);
    }

    [Fact]
    public async Task An_unknown_slug_in_a_filter_returns_the_empty_state_not_an_error()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?colour=chartreuse", Token);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task Price_ascending_and_descending_are_mirror_images()
    {
        var api = CreateApi();

        var ascending = await api.ListAsync("?sort=price-asc&pageSize=96", Token);
        var descending = await api.ListAsync("?sort=price-desc&pageSize=96", Token);

        AssertOrdered(ascending.Items, card => card.Price.Current, ascending: true);
        AssertOrdered(descending.Items, card => card.Price.Current, ascending: false);
        Assert.Equal(ascending.Items[0].Id, descending.Items[^1].Id);
    }

    [Fact]
    public async Task Best_selling_orders_by_the_last_thirty_days()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?sort=best-selling&pageSize=96", Token);

        // SoldQty30d is not on the card, so the order is checked through the seeded leader
        // (SD-M-0004 "Heavyweight Crew Tee" has the highest SoldQty30d at 106).
        Assert.Equal("heavyweight-crew-tee", page.Items[0].Slug);
        Assert.Equal("washed-linen-camp-shirt", page.Items[1].Slug);
    }

    [Fact]
    public async Task Highest_rated_puts_unrated_products_last()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?sort=rating&pageSize=96", Token);

        var rated = page.Items.TakeWhile(card => card.Rating.Average is not null).ToList();
        var unrated = page.Items.Skip(rated.Count).ToList();

        Assert.NotEmpty(rated);
        Assert.NotEmpty(unrated);
        Assert.All(unrated, card => Assert.Null(card.Rating.Average));
        AssertOrdered(rated, card => card.Rating.Average!.Value, ascending: false);
    }

    [Fact]
    public async Task Biggest_discount_leads_with_the_deepest_reduction()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?sort=discount&pageSize=96", Token);

        AssertOrdered(page.Items, card => card.Price.DiscountPercent, ascending: false);
        Assert.True(page.Items[0].Price.DiscountPercent > 0m);
    }

    [Fact]
    public async Task Newest_leads_with_the_most_recently_published_product()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?sort=newest&pageSize=96", Token);

        Assert.Equal("baby-ribbed-bodysuit-set", page.Items[0].Slug);
    }

    [Fact]
    public async Task Recommended_puts_pinned_products_first()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?department=women&sort=recommended&pageSize=96", Token);

        // RecommendedRank 1, 2 and 3 are pinned on these three products.
        Assert.Equal(
            ["relaxed-linen-blend-shirt", "tiered-midi-dress", "high-rise-slim-fit-jeans"],
            page.Items.Take(3).Select(card => card.Slug));
    }

    [Fact]
    public async Task Recommended_on_a_category_page_uses_the_manual_order_in_that_category()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?department=women&category=dresses&sort=recommended&pageSize=96", Token);

        // The seeder pins the first three products in each category with ProductCategories.SortOrder.
        Assert.Equal(
            ["tiered-midi-dress", "belted-shirt-dress", "sleeveless-column-dress"],
            page.Items.Select(card => card.Slug));
    }

    [Fact]
    public async Task A_product_card_carries_everything_card_01_renders()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?department=women&category=shirts-blouses&sort=price-desc&pageSize=96", Token);
        var card = page.Items.Single(item => item.Slug == "relaxed-linen-blend-shirt");

        Assert.Equal("SD-W-0001", card.ProductCode);
        Assert.Equal("Relaxed Linen Blend Shirt", card.Name);
        Assert.Equal("Women", card.DepartmentName);
        Assert.Equal("Shirts & Blouses", card.CategoryName);
        Assert.StartsWith("/media/public/originals/demo/relaxed-linen-blend-shirt-", card.ImageUrl, StringComparison.Ordinal);
        Assert.NotNull(card.HoverImageUrl);
        Assert.NotEqual(card.ImageUrl, card.HoverImageUrl);
        Assert.False(string.IsNullOrWhiteSpace(card.ImageAltText));
        Assert.Equal("LKR", card.Price.Currency);
        Assert.Equal(5490m, card.Price.Current);
        Assert.Equal(6990m, card.Price.Original);
        Assert.Equal(21m, card.Price.DiscountPercent);
        Assert.True(card.Price.HasDiscount);
        Assert.Equal(ProductBadge.Sale, card.Badge);
        Assert.Equal(4.6m, card.Rating.Average);
        Assert.Equal(128, card.Rating.Count);
        Assert.Equal(3, card.Colours.Count);
        Assert.Contains(card.Colours, colour => colour.Name == "Sandstone Beige" && colour.FamilySlug == "beige");
        Assert.All(card.Colours, colour => Assert.StartsWith("#", colour.HexCode!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_admin_badge_override_wins_on_the_card()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?department=women&category=traditional-wear&pageSize=96", Token);
        var exclusive = page.Items.Single(card => card.Slug == "kanjivaram-silk-blend-saree");

        Assert.Equal(ProductBadge.OnlineExclusive, exclusive.Badge);
    }

    [Fact]
    public async Task A_low_stock_product_gets_the_low_stock_badge_and_a_sold_out_one_does_not()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?pageSize=96", Token);

        var lowStock = page.Items.Single(card => card.Slug == "girls-printed-tee");
        Assert.Equal(StockStatus.LowStock, lowStock.StockStatus);
        Assert.True(lowStock.IsInStock);

        var soldOut = page.Items.Single(card => card.Slug == "oversized-graphic-tee");
        Assert.Equal(StockStatus.OutOfStock, soldOut.StockStatus);
        Assert.False(soldOut.IsInStock);
        Assert.Null(soldOut.Badge);
    }

    [Theory]
    [InlineData("?sort=cheapest", "sort")]
    [InlineData("?page=0", "page")]
    [InlineData("?pageSize=500", "pageSize")]
    [InlineData("?minPrice=5000&maxPrice=100", "minPrice")]
    [InlineData("?minRating=9", "minRating")]
    [InlineData("?department=not%20a%20slug", "department")]
    public async Task An_invalid_parameter_is_a_400_naming_the_parameter(string query, string field)
    {
        var api = CreateApi();

        var (status, body) = await api.GetRawAsync("/api/catalog/products" + query, Token);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out var messages), body.ToString());
        Assert.NotEmpty(messages.EnumerateArray().ToList());
    }

    [Fact]
    public async Task Several_invalid_parameters_are_reported_in_one_response()
    {
        var api = CreateApi();

        var (status, body) = await api.GetRawAsync("/api/catalog/products?sort=nope&page=0&minRating=9", Token);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var errors = body.GetProperty("errors");
        Assert.True(errors.TryGetProperty("sort", out _));
        Assert.True(errors.TryGetProperty("page", out _));
        Assert.True(errors.TryGetProperty("minRating", out _));
    }

    private static void AssertOrdered<TKey>(
        IReadOnlyList<ProductCardDto> items,
        Func<ProductCardDto, TKey> selector,
        bool ascending)
        where TKey : IComparable<TKey>
    {
        for (var index = 1; index < items.Count; index++)
        {
            var previous = selector(items[index - 1]);
            var current = selector(items[index]);
            var comparison = previous.CompareTo(current);

            Assert.True(
                ascending ? comparison <= 0 : comparison >= 0,
                $"{items[index - 1].Slug} ({previous}) and {items[index].Slug} ({current}) are out of order.");
        }
    }
}
