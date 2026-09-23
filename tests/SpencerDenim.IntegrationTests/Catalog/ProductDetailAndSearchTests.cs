using System.Net;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Inventory;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Catalog;

/// <summary>PDP-01..06 and SRCH-02 against the seeded catalogue.</summary>
public sealed class ProductDetailAndSearchTests(SqlServerDatabaseFixture sqlServer) : CatalogTestBase(sqlServer)
{
    [Fact]
    public async Task The_product_page_payload_carries_pdp_01_to_06()
    {
        var api = CreateApi();

        var product = await api.GetProductAsync("relaxed-linen-blend-shirt", Token);

        // PDP-02: identity, price and stock.
        Assert.Equal("SD-W-0001", product.ProductCode);
        Assert.Equal("Relaxed Linen Blend Shirt", product.Name);
        Assert.Equal("women", product.Department.Slug);
        Assert.Equal(5490m, product.Price.Current);
        Assert.Equal(6990m, product.Price.Original);
        Assert.True(product.Price.HasDiscount);
        Assert.Equal(ProductBadge.Sale, product.Badge);
        Assert.True(product.IsInStock);
        Assert.Equal(4.6m, product.Rating.Average);

        // PLP-01 breadcrumb.
        Assert.Equal(["shirts-blouses"], product.Breadcrumb.Select(step => step.Slug));

        // PDP-01: gallery with a hover shot.
        Assert.Equal(4, product.Images.Count);
        Assert.Contains(product.Images, image => image.Role == ProductMediaRole.Hover);
        Assert.All(product.Images, image =>
        {
            Assert.StartsWith("/media/public/originals/demo/", image.Url, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(image.AltText));
            Assert.Equal(1200, image.Width);
        });

        // PDP-03: colours, sizes and variants.
        Assert.Equal(3, product.Colours.Count);
        Assert.Equal(["XS", "S", "M", "L", "XL"], product.Sizes.Select(size => size.Label));
        Assert.Equal(15, product.Variants.Count);
        Assert.All(product.Variants, variant =>
        {
            Assert.StartsWith("SD-W-0001-", variant.Sku, StringComparison.Ordinal);
            Assert.Equal("LKR", variant.Price.Currency);
            Assert.Contains(product.Colours, colour => colour.Id == variant.ColourId);
        });

        // PDP-04 and PDP-05: size guide and details.
        Assert.Equal("Women's apparel", product.SizeGuide?.Name);
        Assert.Equal("linen-blend", product.Attributes.MaterialSlug);
        Assert.Equal("relaxed", product.Attributes.FitSlug);
        Assert.Equal("smart-casual", product.Attributes.OccasionSlug);
        Assert.False(string.IsNullOrWhiteSpace(product.CareInstructions));
        Assert.StartsWith("<p>", product.Description, StringComparison.Ordinal);

        // GLB-11.
        Assert.Equal("Relaxed Linen Blend Shirt | Spencer Denim", product.Seo.Title);
        Assert.False(string.IsNullOrWhiteSpace(product.Seo.Description));

        // PDP-06.
        Assert.Equal(4, product.RelatedProducts.Count);
        Assert.DoesNotContain(product.RelatedProducts, related => related.Id == product.Id);
    }

    [Fact]
    public async Task A_variant_price_override_is_reflected_on_the_variant_not_the_product()
    {
        var api = CreateApi();

        var product = await api.GetProductAsync("heavyweight-crew-tee", Token);

        var medium = product.Variants.First(variant => variant.SizeLabel == "M");
        var extraLarge = product.Variants.First(variant => variant.SizeLabel == "XXL");

        Assert.Equal(2490m, medium.Price.Current);
        Assert.Equal(2790m, extraLarge.Price.Current);
        Assert.Equal(2490m, product.MinPrice);
        Assert.Equal(2790m, product.MaxPrice);
    }

    [Fact]
    public async Task A_sold_out_product_reports_out_of_stock_on_every_variant()
    {
        var api = CreateApi();

        var product = await api.GetProductAsync("oversized-graphic-tee", Token);

        Assert.False(product.IsInStock);
        Assert.Equal(StockStatus.OutOfStock, product.StockStatus);
        Assert.All(product.Variants, variant =>
        {
            Assert.Equal(StockStatus.OutOfStock, variant.StockStatus);
            Assert.Equal(0, variant.AvailableQuantity);
        });
        Assert.All(product.Sizes, size => Assert.False(size.IsInStock));
    }

    [Fact]
    public async Task A_partly_sold_out_product_marks_only_the_missing_sizes()
    {
        var api = CreateApi();

        var product = await api.GetProductAsync("poplin-formal-shirt", Token);

        Assert.True(product.IsInStock);
        Assert.Contains(product.Variants, variant => variant.StockStatus == StockStatus.OutOfStock);
        Assert.Contains(product.Variants, variant => variant.IsInStock);
    }

    [Fact]
    public async Task An_unknown_slug_is_a_404_problem_response()
    {
        var api = CreateApi();

        var (status, body) = await api.GetRawAsync("/api/catalog/products/no-such-product", Token);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("Product not found", body.GetProperty("title").GetString());
        Assert.Contains("no-such-product", body.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_slug_longer_than_the_column_is_a_400()
    {
        var api = CreateApi();

        var (status, _) = await api.GetRawAsync("/api/catalog/products/" + new string('a', 300), Token);

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    /// <summary>
    /// A slug that is not a slug is a 400 naming the parameter, and nothing the URL carried is echoed
    /// back: the 404 path used to reflect the raw text into <c>detail</c>.
    /// </summary>
    [Theory]
    [InlineData("%3Cscript%3E")]
    [InlineData("under_score")]
    [InlineData("double--hyphen")]
    [InlineData("100%25")]
    public async Task A_malformed_slug_is_a_400_naming_the_parameter(string slug)
    {
        var api = CreateApi();

        var (status, body) = await api.GetRawAsync("/api/catalog/products/" + slug, Token);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("slug", out _), body.ToString());
        Assert.DoesNotContain("script", body.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_matches_the_product_name()
    {
        var api = CreateApi();

        var response = await api.SearchAsync("?q=linen", Token);

        Assert.Equal("linen", response.Term);
        Assert.Equal(["linen"], response.Tokens);
        Assert.NotEmpty(response.Results.Items);
        Assert.Contains(response.Results.Items, card => card.Slug == "relaxed-linen-blend-shirt");
    }

    [Fact]
    public async Task Search_requires_every_token_to_match()
    {
        var api = CreateApi();

        var linen = await api.SearchAsync("?q=linen", Token);
        var linenShorts = await api.SearchAsync("?q=linen%20shorts", Token);

        Assert.True(linenShorts.Results.TotalCount < linen.Results.TotalCount);
        Assert.All(linenShorts.Results.Items, card => Assert.Contains("linen", card.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Search_also_matches_the_category_colour_and_attribute_text()
    {
        var api = CreateApi();

        var byCategory = await api.SearchAsync("?q=trousers", Token);
        var byColour = await api.SearchAsync("?q=terracotta", Token);
        var byCode = await api.SearchAsync("?q=SD-M-0007", Token);

        Assert.NotEmpty(byCategory.Results.Items);
        Assert.NotEmpty(byColour.Results.Items);
        Assert.Equal("straight-fit-rigid-jeans", Assert.Single(byCode.Results.Items).Slug);
    }

    [Fact]
    public async Task Search_ranks_a_name_match_above_a_category_match()
    {
        var api = CreateApi();

        var response = await api.SearchAsync("?q=jeans", Token);

        Assert.Contains("Jeans", response.Results.Items[0].Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Doc 08 §7.6 bands: name prefix 100, name contains 50, category or colour 20, anything else 10.
    /// The popularity term is bounded, so the best seller among the "anything else" matches cannot
    /// climb past a name match.
    /// </summary>
    [Fact]
    public async Task Search_ranks_a_name_prefix_above_a_best_seller_that_matches_no_name()
    {
        var api = CreateApi();

        // "denim" is the leading word of one product name, appears inside another, and is the material
        // of every pair of jeans — including SD-W-0009, the highest-selling product that matches.
        var response = await api.SearchAsync("?q=denim&pageSize=96", Token);
        var order = response.Results.Items.Select(card => card.Slug).ToList();

        Assert.Equal("denim-wrap-mini-skirt", order[0]);
        Assert.Equal("girls-denim-pinafore", order[1]);
        Assert.Contains("high-rise-slim-fit-jeans", order);
        Assert.True(
            order.IndexOf("girls-denim-pinafore") < order.IndexOf("high-rise-slim-fit-jeans"),
            string.Join(", ", order));
    }

    /// <summary>
    /// Doc 08 §7.6: a name match (50) outranks a category match (20), and the bounded popularity term
    /// cannot close that gap. "Cotton Twill Chinos" sits in the Trousers category and outsells two of
    /// the three products with "Trousers" in the name, so an unbounded <c>SoldQty30d</c> would put it
    /// above them.
    /// </summary>
    [Fact]
    public async Task Search_keeps_a_category_match_below_the_name_matches_however_well_it_sells()
    {
        var api = CreateApi();

        var response = await api.SearchAsync("?q=trousers&pageSize=96", Token);
        var order = response.Results.Items.Select(card => card.Slug).ToList();

        Assert.Equal(4, order.Count);
        Assert.Equal("cotton-twill-chinos", order[^1]);
        Assert.All(
            order[..^1],
            slug => Assert.EndsWith("-trousers", slug, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_finds_a_sinhala_term_under_the_platform_collation()
    {
        var api = CreateApi();

        var response = await api.SearchAsync("?q=%E0%B6%94%E0%B7%83%E0%B6%BB%E0%B7%92%E0%B6%BA", Token);

        var card = Assert.Single(response.Results.Items);
        Assert.Equal("kandyan-osariya-handloom-saree", card.Slug);
    }

    [Fact]
    public async Task Search_finds_a_tamil_term_under_the_platform_collation()
    {
        var api = CreateApi();

        // காஞ்சிபுரம்
        var response = await api.SearchAsync(
            "?q=%E0%AE%95%E0%AE%BE%E0%AE%9E%E0%AF%8D%E0%AE%9A%E0%AE%BF%E0%AE%AA%E0%AF%81%E0%AE%B0%E0%AE%AE%E0%AF%8D",
            Token);

        var card = Assert.Single(response.Results.Items);
        Assert.Equal("kanjivaram-silk-blend-saree", card.Slug);
    }

    [Fact]
    public async Task Search_is_case_insensitive_because_the_collation_is_ci()
    {
        var api = CreateApi();

        var lower = await api.SearchAsync("?q=linen", Token);
        var upper = await api.SearchAsync("?q=LINEN", Token);

        Assert.Equal(lower.Results.TotalCount, upper.Results.TotalCount);
        Assert.NotEqual(0, lower.Results.TotalCount);
    }

    [Fact]
    public async Task Search_applies_the_listing_filters_and_sorts()
    {
        var api = CreateApi();

        var all = await api.SearchAsync("?q=jeans&pageSize=96", Token);
        var women = await api.SearchAsync("?q=jeans&department=women&pageSize=96", Token);
        var cheapestFirst = await api.SearchAsync("?q=jeans&sort=price-asc&pageSize=96", Token);

        Assert.True(women.Results.TotalCount < all.Results.TotalCount);
        Assert.All(women.Results.Items, card => Assert.Equal("women", card.DepartmentSlug));
        Assert.True(cheapestFirst.Results.Items[0].Price.Current <= cheapestFirst.Results.Items[^1].Price.Current);
    }

    [Fact]
    public async Task Search_wildcards_are_escaped_rather_than_matching_everything()
    {
        var api = CreateApi();

        var response = await api.SearchAsync("?q=%25%25", Token);

        Assert.Equal(0, response.Results.TotalCount);
    }

    [Fact]
    public async Task Search_with_no_hits_returns_the_empty_state()
    {
        var api = CreateApi();

        var response = await api.SearchAsync("?q=zzzznothing", Token);

        Assert.Empty(response.Results.Items);
        Assert.Equal(0, response.Results.TotalCount);
    }

    [Theory]
    [InlineData("?q=")]
    [InlineData("?q=a")]
    [InlineData("")]
    public async Task A_term_that_is_too_short_is_a_400(string query)
    {
        var api = CreateApi();

        var (status, body) = await api.GetRawAsync("/api/catalog/search" + query, Token);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("q", out _));
    }
}
