using SpencerDenim.Application.Analytics;
using SpencerDenim.Domain.Analytics;

namespace SpencerDenim.UnitTests.Application.Analytics;

/// <summary>
/// What a storefront address may be stored as (doc 08 §3.8: "Query string dropped except whitelisted filter
/// keys"), and above all what it may never be stored as: the guest order page's token, a reset or
/// confirmation token, a search term, an email address or a phone number.
/// </summary>
public sealed class PagePathTests
{
    private const string OrderToken = "3f2a6c1e-9b4d-4e8a-a7c2-5d1e0f9b8a76";

    [Theory]
    [InlineData("/order/" + OrderToken)]
    [InlineData("/order/" + OrderToken + "?paypal=return&token=EC-8AB12345CD678901E")]
    [InlineData("/order/" + OrderToken + "#top")]
    [InlineData("/order/3F2A6C1E-9B4D-4E8A-A7C2-5D1E0F9B8A76")]
    [InlineData("/order/not-a-guid-but-still-the-secret")]
    [InlineData("/order/%33f2a6c1e-9b4d-4e8a-a7c2-5d1e0f9b8a76")]
    public void The_order_page_is_stored_without_its_token(string raw)
    {
        var page = PagePath.Normalise(raw);

        Assert.NotNull(page);
        Assert.Equal("/order/*", page.Path);
        Assert.Equal(PageType.Order, page.PageType);
        Assert.DoesNotContain("3f2a", page.Path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", page.Path, StringComparison.Ordinal);
        Assert.DoesNotContain("EC-8AB", page.Path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/account/reset-password?token=CfDJ8Abc%2Bdef&email=jane%40example.com", "/account/reset-password")]
    [InlineData("/account/confirm-email?token=CfDJ8Abc&userId=42", "/account/confirm-email")]
    [InlineData("/account/sign-in?returnTo=%2Forder%2F" + OrderToken, "/account/sign-in")]
    [InlineData("/search?q=jane%40example.com", "/search")]
    [InlineData("/search?q=0771234567", "/search")]
    [InlineData("/search?q=linen+shirt", "/search")]
    [InlineData("/checkout?email=jane@example.com", "/checkout")]
    [InlineData("/bag?promo=SUMMER10", "/bag")]
    [InlineData("/?utm_source=newsletter&utm_campaign=x&gclid=abc", "/")]
    public void Query_strings_are_dropped_from_pages_that_take_no_filters(string raw, string expected)
    {
        var page = PagePath.Normalise(raw);

        Assert.NotNull(page);
        Assert.Equal(expected, page.Path);
    }

    [Fact]
    public void A_listing_keeps_its_whitelisted_filters_in_a_canonical_order_and_drops_the_rest()
    {
        var page = PagePath.Normalise(
            "/women/dresses?sort=price-asc&sizes=M&colours=red&colours=blue&colours=red&pages=3"
            + "&inStock=true&minPrice=1000&maxPrice=5000&q=hello&email=a%40b.com&utm_source=fb&fbclid=xyz#grid");

        Assert.NotNull(page);
        Assert.Equal(
            "/women/dresses?colours=blue&colours=red&inStock=true&maxPrice=5000&minPrice=1000&sizes=M&sort=price-asc",
            page.Path);
        Assert.Equal(PageType.Category, page.PageType);
    }

    [Theory]
    [InlineData("colours", "jane@example.com")]
    [InlineData("colours", "Red")]
    [InlineData("sizes", "0771234567")]
    [InlineData("sizes", "<script>")]
    [InlineData("minPrice", "771234567")]
    [InlineData("minPrice", "-5")]
    [InlineData("minDiscountPercent", "101")]
    [InlineData("minRating", "6")]
    [InlineData("inStock", "yes")]
    [InlineData("sort", "price asc; drop")]
    public void A_filter_value_of_the_wrong_shape_is_dropped(string key, string value)
    {
        var page = PagePath.Normalise($"/women?{key}={Uri.EscapeDataString(value)}");

        Assert.NotNull(page);
        Assert.Equal("/women", page.Path);
    }

    [Theory]
    [InlineData("/product/linen-shirt", "/product/linen-shirt", PageType.Product)]
    [InlineData("/product/jane%40example.com", "/product/*", PageType.Product)]
    [InlineData("/product/" + OrderToken, "/product/*", PageType.Product)]
    [InlineData("/product/call-0771234567", "/product/*", PageType.Product)]
    [InlineData("/product/Linen-Shirt", "/product/*", PageType.Product)]
    [InlineData("/jane@example.com", "/*", PageType.Other)]
    [InlineData("/women/" + OrderToken, "/women/*", PageType.Category)]
    [InlineData("/12345678", "/*", PageType.Other)]
    public void A_path_segment_that_is_not_a_safe_slug_is_starred(string raw, string expected, PageType type)
    {
        var page = PagePath.Normalise(raw);

        Assert.NotNull(page);
        Assert.Equal(expected, page.Path);
        Assert.Equal(type, page.PageType);
    }

    [Theory]
    [InlineData("/", "/", PageType.Home)]
    [InlineData("", null, null)]
    [InlineData("/women", "/women", PageType.Department)]
    [InlineData("/about", "/about", PageType.Department)]
    [InlineData("/women/", "/women", PageType.Department)]
    [InlineData("/kids/girls/girls-dresses", "/kids/girls/girls-dresses", PageType.Category)]
    [InlineData("/new-arrivals", "/new-arrivals", PageType.NewArrivals)]
    [InlineData("/sale?onSale=true", "/sale?onSale=true", PageType.Sale)]
    [InlineData("/search?q=x&sizes=M", "/search?sizes=M", PageType.Search)]
    [InlineData("/bag", "/bag", PageType.Bag)]
    [InlineData("/wishlist", "/wishlist", PageType.Wishlist)]
    [InlineData("/checkout", "/checkout", PageType.Checkout)]
    [InlineData("/account", "/account", PageType.Account)]
    [InlineData("/account/orders", "/account/orders", PageType.Account)]
    [InlineData("/a/b/c/d", "/a/b/c/d", PageType.Other)]
    [InlineData("/order", "/order", PageType.Other)]
    public void Each_route_family_gets_its_page_type(string raw, string? expected, PageType? type)
    {
        var page = PagePath.Normalise(raw);

        Assert.Equal(expected, page?.Path);
        Assert.Equal(type, page?.PageType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("women")]
    [InlineData("//evil.example/path")]
    [InlineData("https://evil.example/")]
    [InlineData("/a\\b")]
    [InlineData("/a/b/c/d/e/f")]
    public void Something_that_is_not_a_storefront_path_is_not_recorded(string? raw)
    {
        Assert.Null(PagePath.Normalise(raw));
    }

    [Fact]
    public void An_address_longer_than_the_raw_limit_is_not_recorded()
    {
        Assert.Null(PagePath.Normalise("/" + new string('a', PagePath.MaxRawLength)));
    }

    [Fact]
    public void Filters_that_would_overflow_the_column_are_dropped_whole_not_cut()
    {
        var colours = string.Join('&', Enumerable.Range(0, 25).Select(i => $"colours=colour-number-{i:D2}-long-slug"));

        var page = PagePath.Normalise("/women?" + colours);

        Assert.NotNull(page);
        Assert.Equal("/women", page.Path);
        Assert.True(page.Path.Length <= PageView.PathMaxLength);
    }

    [Fact]
    public void Utm_tags_are_read_before_the_query_is_dropped_and_click_ids_only_flag_the_channel()
    {
        var page = PagePath.Normalise("/?utm_source=Facebook&utm_medium=CPC&utm_campaign=Avurudu Sale&gclid=Cj0K&fbclid=IwAR");

        Assert.NotNull(page);
        Assert.Equal("/", page.Path);
        Assert.Equal(new CampaignTags("facebook", "cpc", "Avurudu Sale", true, true), page.Campaign);
    }

    [Theory]
    [InlineData("jane@example.com")]
    [InlineData("077 123 4567")]
    [InlineData("+94-77-123-4567")]
    [InlineData("   ")]
    public void A_utm_value_that_looks_personal_or_empty_is_not_kept(string value)
    {
        var page = PagePath.Normalise("/?utm_campaign=" + Uri.EscapeDataString(value) + "&utm_source=" + Uri.EscapeDataString(value));

        Assert.NotNull(page);
        Assert.Null(page.Campaign.Campaign);
        Assert.Null(page.Campaign.Source);
    }

    [Fact]
    public void A_utm_value_is_cut_to_the_column_width()
    {
        var page = PagePath.Normalise("/?utm_campaign=" + new string('c', 150));

        Assert.NotNull(page);
        Assert.Equal(PageView.UtmMaxLength, page.Campaign.Campaign!.Length);
    }

    /// <summary>
    /// Issue #201: in .NET a regex <c>$</c> also matches before a final line feed, so "women\n" (what a decoded
    /// <c>%0A</c> is) was a slug, was stored in <c>PageViews.Path</c> and would have written a line break into a
    /// log line once the log started printing the same segments.
    /// </summary>
    [Theory]
    [InlineData("women\n")]
    [InlineData("women\r\n")]
    [InlineData("\nwomen")]
    [InlineData("wo\nmen")]
    public void A_line_break_is_never_part_of_a_slug(string value)
    {
        Assert.False(PagePath.IsSafeSlug(value));
        Assert.Equal(PagePath.Redacted, PagePath.SafeSegment(Uri.EscapeDataString(value)));
    }

    [Fact]
    public void An_address_with_an_encoded_line_feed_is_stored_with_the_segment_starred()
    {
        var page = PagePath.Normalise("/women%0A");

        Assert.NotNull(page);
        Assert.Equal("/*", page.Path);
    }
}
