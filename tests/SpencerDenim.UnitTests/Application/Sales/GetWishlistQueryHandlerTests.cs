using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Application.Sales.Wishlist;
using SpencerDenim.Application.Sales.Wishlist.Abstractions;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Inventory;

namespace SpencerDenim.UnitTests.Application.Sales;

/// <summary>
/// The wishlist read (W2-08, WISH-01): what reaches the catalogue, and what comes back.
/// <para>
/// The whole of this handler's job is to be careful with a list the <b>client</b> sent. Doc 02's WISH-01
/// line puts a guest's wishlist in the browser, so these slugs were written by `localStorage` — possibly
/// by an older build of the site, another tab or a curious visitor (lesson L1) — and none of it is
/// something a shopper can see or correct. So nothing here is a 400, and nothing is dropped from the
/// <i>answer</i> either.
/// </para>
/// </summary>
public sealed class GetWishlistQueryHandlerTests
{
    private sealed class RecordingReader(params string[] sellable) : IWishlistCatalogReader
    {
        private readonly HashSet<string> _sellable = new(sellable, StringComparer.Ordinal);

        public IReadOnlyList<string>? Asked { get; private set; }

        public int Calls { get; private set; }

        public Task<IReadOnlyList<(string Slug, ProductCardDto? Product)>> GetCardsBySlugAsync(
            IReadOnlyList<string> slugs,
            CancellationToken cancellationToken)
        {
            Calls++;
            Asked = slugs;

            IReadOnlyList<(string, ProductCardDto?)> answer = slugs
                .Select(slug => (slug, _sellable.Contains(slug) ? Card(slug) : null))
                .ToList();

            return Task.FromResult(answer);
        }
    }

    private static ProductCardDto Card(string slug) => new(
        Id: 1,
        ProductCode: "SD-0001",
        Name: slug,
        Slug: slug,
        DepartmentName: "Women",
        DepartmentSlug: "women",
        CategoryName: "Shirts",
        CategorySlug: "shirts",
        ImageUrl: null,
        ImageSrcSet: null,
        HoverImageUrl: null,
        HoverImageSrcSet: null,
        ImageAltText: null,
        Price: new PriceDto(4392m, 4392m, 0m, false, PriceDto.Lkr),
        Rating: new RatingDto(null, 0),
        Badge: null,
        StockStatus: StockStatus.InStock,
        IsInStock: true,
        Colours: []);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_slugs_reach_the_catalogue_in_the_order_the_browser_sent_them()
    {
        var reader = new RecordingReader("b", "a");
        var handler = new GetWishlistQueryHandler(reader);

        var result = await handler.HandleAsync(new GetWishlistQuery(["b", "a", "c"]), Token);

        Assert.Equal(["b", "a", "c"], reader.Asked);
        Assert.Equal(["b", "a", "c"], result.Items.Select(item => item.Slug));
    }

    /// <summary>
    /// A slug the shop no longer offers is an <i>answer</i>, not an omission. Doc 11 §575: the item "is
    /// shown as unavailable; can be removed" — and a shopper whose entry simply vanished from the
    /// response would read it as the server having lost their item.
    /// </summary>
    [Fact]
    public async Task A_slug_the_shop_no_longer_offers_comes_back_with_no_product()
    {
        var handler = new GetWishlistQueryHandler(new RecordingReader("still-sold"));

        var result = await handler.HandleAsync(new GetWishlistQuery(["still-sold", "withdrawn"]), Token);

        Assert.Equal(2, result.Items.Count);
        Assert.NotNull(result.Items[0].Product);
        Assert.Null(result.Items[1].Product);
    }

    [Fact]
    public async Task An_empty_list_asks_the_catalogue_nothing()
    {
        var reader = new RecordingReader();
        var handler = new GetWishlistQueryHandler(reader);

        var result = await handler.HandleAsync(new GetWishlistQuery([]), Token);

        Assert.Empty(result.Items);
        Assert.Equal(0, reader.Calls);
    }

    /// <summary>
    /// Doc 11 §553: one entry per product. A duplicate is one slug to ask about and one entry to answer
    /// with — asking twice would double the <c>IN</c> list and answering twice would draw the card twice.
    /// </summary>
    [Fact]
    public async Task A_duplicated_slug_is_asked_about_once_and_answered_once()
    {
        var reader = new RecordingReader("a");
        var handler = new GetWishlistQueryHandler(reader);

        var result = await handler.HandleAsync(new GetWishlistQuery(["a", "a", "b", "a"]), Token);

        Assert.Equal(["a", "b"], reader.Asked);
        Assert.Equal(["a", "b"], result.Items.Select(item => item.Slug));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task A_blank_slug_is_dropped_rather_than_refused(string slug)
    {
        var reader = new RecordingReader("real");
        var handler = new GetWishlistQueryHandler(reader);

        var result = await handler.HandleAsync(new GetWishlistQuery([slug, "real"]), Token);

        Assert.Equal(["real"], reader.Asked);
        Assert.Single(result.Items);
    }

    /// <summary>
    /// <c>catalog.Products.Slug</c> is <c>varchar(220)</c>, so nothing longer can match a product. It is
    /// dropped before it reaches the database rather than sent and missed.
    /// </summary>
    [Fact]
    public async Task A_slug_longer_than_the_column_never_reaches_the_catalogue()
    {
        var reader = new RecordingReader("real");
        var handler = new GetWishlistQueryHandler(reader);
        var tooLong = new string('a', GetWishlistQueryHandler.MaxSlugLength + 1);
        var exactly = new string('b', GetWishlistQueryHandler.MaxSlugLength);

        await handler.HandleAsync(new GetWishlistQuery([tooLong, exactly, "real"]), Token);

        Assert.Equal([exactly, "real"], reader.Asked);
    }

    /// <summary>
    /// The cap is a bound on what a client can make the catalogue project in one query. Truncating is the
    /// least bad of the three options — see the constant's remarks — and the storefront says so rather
    /// than appearing to have lost the rest.
    /// </summary>
    [Fact]
    public async Task A_list_over_the_cap_is_truncated_to_the_most_recent()
    {
        var reader = new RecordingReader();
        var handler = new GetWishlistQueryHandler(reader);
        var many = Enumerable
            .Range(0, GetWishlistQueryHandler.MaxSlugs + 25)
            .Select(index => $"product-{index}")
            .ToList();

        var result = await handler.HandleAsync(new GetWishlistQuery(many), Token);

        Assert.Equal(GetWishlistQueryHandler.MaxSlugs, reader.Asked?.Count);
        Assert.Equal(GetWishlistQueryHandler.MaxSlugs, result.Items.Count);

        // The browser sends the most recently saved first, so the ones that survive are the ones a
        // shopper is most likely to be looking for.
        Assert.Equal("product-0", reader.Asked?[0]);
    }

    /// <summary>
    /// The cap counts <i>usable</i> slugs, not raw ones: a list padded with blanks must not push real
    /// entries out of the request. A naive `Take(MaxSlugs)` before filtering would have done exactly that.
    /// </summary>
    [Fact]
    public async Task Blanks_do_not_use_up_the_cap()
    {
        var reader = new RecordingReader();
        var handler = new GetWishlistQueryHandler(reader);

        var padded = Enumerable
            .Range(0, GetWishlistQueryHandler.MaxSlugs)
            .SelectMany(index => new[] { "  ", $"product-{index}" })
            .ToList();

        await handler.HandleAsync(new GetWishlistQuery(padded), Token);

        Assert.Equal(GetWishlistQueryHandler.MaxSlugs, reader.Asked?.Count);
        Assert.Equal($"product-{GetWishlistQueryHandler.MaxSlugs - 1}", reader.Asked?[^1]);
    }

    /// <summary>
    /// Two spellings that differ in case are two slugs here: a slug is an address, the database's unique
    /// index decides identity, and folding them would mean echoing back a slug the browser never sent —
    /// which is the one thing <c>WishlistEntryDto</c> promises not to do, because the browser matches its
    /// stored entry on it.
    /// </summary>
    [Fact]
    public async Task Case_is_not_folded_when_de_duplicating()
    {
        var reader = new RecordingReader();
        var handler = new GetWishlistQueryHandler(reader);

        var result = await handler.HandleAsync(new GetWishlistQuery(["linen-shirt", "Linen-Shirt"]), Token);

        Assert.Equal(["linen-shirt", "Linen-Shirt"], result.Items.Select(item => item.Slug));
    }
}
