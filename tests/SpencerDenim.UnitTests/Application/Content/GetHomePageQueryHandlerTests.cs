using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Catalog.Abstractions;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Application.Catalog.Products;
using SpencerDenim.Application.Catalog.Search;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Contracts;
using SpencerDenim.Application.Content.Home;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Inventory;

namespace SpencerDenim.UnitTests.Application.Content;

/// <summary>
/// How the homepage is assembled (HOME-01..10): the content store's order is kept, and a product
/// row is filled from the listing its own settings name.
/// <para>
/// Both stubs answer <b>what they were asked</b>. The catalogue stub builds its cards out of the
/// query it received — its department, its fixed filter, its sort and its page size — so a handler
/// that asked for the wrong listing, or for the wrong number of rows, fails here rather than
/// passing against a fixed list that happens to look right.
/// </para>
/// </summary>
public sealed class GetHomePageQueryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Sections_are_returned_in_the_order_the_content_store_gives_them()
    {
        var content = new StubContentReader(
            Section(HomeSectionType.Benefits, sortOrder: 9),
            Section(HomeSectionType.ShopByDepartment, sortOrder: 2),
            Section(HomeSectionType.Newsletter, sortOrder: 10));

        var page = await HandleAsync(content, new StubProductReader());

        Assert.Equal(
            [HomeSectionType.Benefits, HomeSectionType.ShopByDepartment, HomeSectionType.Newsletter],
            page.Sections.Select(section => section.Type));
    }

    [Fact]
    public async Task The_content_store_is_asked_for_the_current_instant()
    {
        var content = new StubContentReader();

        await HandleAsync(content, new StubProductReader());

        Assert.Equal(Now.UtcDateTime, content.AskedFor);
    }

    [Fact]
    public async Task A_product_row_is_fetched_from_the_listing_its_settings_name()
    {
        var products = new StubProductReader();
        var content = new StubContentReader(
            Section(
                HomeSectionType.NewArrivals,
                sortOrder: 3,
                settingsJson: """{"mode":"auto","count":6,"listingUrl":"/women/jeans?sort=newest","linkLabel":"View all"}"""));

        var page = await HandleAsync(content, products);
        var section = Assert.Single(page.Sections);

        var asked = Assert.Single(products.Queries);
        Assert.Equal("women", asked.DepartmentSlug);
        Assert.Equal("jeans", asked.CategorySlug);
        Assert.Equal(ProductSortOption.Newest, asked.Sort);
        Assert.Equal(6, asked.PageSize);
        Assert.Equal(1, asked.Page);

        // The cards are built from that query, so this asserts the row is the listing's head and
        // not a list the stub decided on.
        Assert.Equal(6, section.Products.Count);
        Assert.All(section.Products, card => Assert.Equal("women", card.DepartmentSlug));
        Assert.Equal("/women/jeans?sort=newest", section.LinkUrl);
        Assert.Equal("View all", section.LinkLabel);
    }

    [Theory]
    [InlineData(HomeSectionType.NewArrivals)]
    [InlineData(HomeSectionType.TrendingItems)]
    [InlineData(HomeSectionType.SaleSection)]
    public async Task All_three_product_rows_are_resolved(HomeSectionType type)
    {
        var products = new StubProductReader();
        var content = new StubContentReader(
            Section(type, sortOrder: 1, settingsJson: """{"listingUrl":"/sale","count":4}"""));

        var page = await HandleAsync(content, products);

        Assert.Equal(4, Assert.Single(page.Sections).Products.Count);
    }

    [Theory]
    [InlineData(HomeSectionType.HeroVideo)]
    [InlineData(HomeSectionType.ShopByDepartment)]
    [InlineData(HomeSectionType.CampaignBanner)]
    [InlineData(HomeSectionType.Benefits)]
    [InlineData(HomeSectionType.Newsletter)]
    public async Task A_section_that_is_not_a_product_row_never_touches_the_catalogue(HomeSectionType type)
    {
        var products = new StubProductReader();
        var content = new StubContentReader(
            Section(type, sortOrder: 1, settingsJson: """{"listingUrl":"/sale"}"""));

        var page = await HandleAsync(content, products);

        Assert.Empty(products.Queries);
        Assert.Empty(Assert.Single(page.Sections).Products);
    }

    /// <summary>
    /// HOME-04's own shape: a trending section set to show the tiles the client arranged does not
    /// also pull a row of products behind them.
    /// </summary>
    [Fact]
    public async Task A_manual_row_shows_its_own_items_and_asks_for_no_products()
    {
        var products = new StubProductReader();
        var content = new StubContentReader(
            Section(
                HomeSectionType.TrendingItems,
                sortOrder: 4,
                settingsJson: """{"mode":"manual","listingUrl":"/sale"}""",
                items: [Item(1, "Wide leg denim", "/women/jeans?fit=wide-leg")]));

        var page = await HandleAsync(content, products);
        var section = Assert.Single(page.Sections);

        Assert.Empty(products.Queries);
        Assert.Empty(section.Products);
        Assert.Equal("/women/jeans?fit=wide-leg", Assert.Single(section.Items).LinkUrl);
    }

    [Fact]
    public async Task A_product_row_with_no_listing_url_shows_nothing_rather_than_everything()
    {
        var products = new StubProductReader();
        var content = new StubContentReader(Section(HomeSectionType.SaleSection, 7, settingsJson: null));

        var page = await HandleAsync(content, products);

        Assert.Empty(products.Queries);
        Assert.Empty(Assert.Single(page.Sections).Products);
    }

    /// <summary>
    /// The listing URL is typed by a person. A filter the catalogue refuses is a 400 on the listing
    /// page and must be an empty row here, never a 500 on the front page.
    /// </summary>
    [Fact]
    public async Task A_listing_url_the_catalogue_refuses_empties_the_row_instead_of_failing()
    {
        var products = new StubProductReader();
        var content = new StubContentReader(
            Section(HomeSectionType.SaleSection, 7, settingsJson: """{"listingUrl":"/women?minPrice=abc"}"""));

        var page = await HandleAsync(content, products);

        Assert.Empty(products.Queries);
        Assert.Empty(Assert.Single(page.Sections).Products);
    }

    [Fact]
    public async Task A_catalogue_that_refuses_the_query_empties_the_row_instead_of_failing()
    {
        var products = new StubProductReader { Refuse = true };
        var content = new StubContentReader(
            Section(HomeSectionType.NewArrivals, 3, settingsJson: """{"listingUrl":"/new-arrivals"}"""));

        var page = await HandleAsync(content, products);

        Assert.Single(products.Queries);
        Assert.Empty(Assert.Single(page.Sections).Products);
    }

    [Fact]
    public async Task An_empty_homepage_is_an_empty_list_and_not_a_null()
    {
        var page = await HandleAsync(new StubContentReader(), new StubProductReader());

        Assert.Empty(page.Sections);
    }

    private static async Task<HomePageDto> HandleAsync(
        StubContentReader content,
        StubProductReader products)
    {
        var handler = new GetHomePageQueryHandler(content, products, new CatalogOptions(), new FixedClock(Now));

        return await handler.HandleAsync(new GetHomePageQuery(), TestContext.Current.CancellationToken);
    }

    private static HomeSectionContent Section(
        HomeSectionType type,
        short sortOrder,
        string? settingsJson = null,
        IReadOnlyList<HomeSectionItemDto>? items = null) =>
        new((int)type, type, type.ToString(), null, sortOrder, settingsJson, items ?? []);

    private static HomeSectionItemDto Item(int id, string title, string linkUrl) =>
        new(id, title, null, null, null, linkUrl, null, null, null, null, null, null, null, null, null, null, null, null, 1);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class StubContentReader(params HomeSectionContent[] sections) : IHomeContentReader
    {
        public DateTime? AskedFor { get; private set; }

        public Task<IReadOnlyList<HomeSectionContent>> GetLiveSectionsAsync(
            DateTime utcNow,
            CancellationToken cancellationToken)
        {
            AskedFor = utcNow;
            return Task.FromResult<IReadOnlyList<HomeSectionContent>>(sections);
        }

        public Task<IReadOnlyList<AnnouncementDto>> GetLiveAnnouncementsAsync(
            DateTime utcNow,
            CancellationToken cancellationToken)
        {
            AskedFor = utcNow;
            return Task.FromResult<IReadOnlyList<AnnouncementDto>>([]);
        }
    }

    /// <summary>
    /// Answers from the query it is given, never from a fixture. A page of exactly
    /// <c>PageSize</c> cards, each stamped with the department, the fixed filter and the sort the
    /// caller asked for, so an assertion about the row is an assertion about the request.
    /// </summary>
    private sealed class StubProductReader : IProductCatalogReader
    {
        private readonly List<ProductListQuery> _queries = [];

        public IReadOnlyList<ProductListQuery> Queries => _queries;

        /// <summary>Makes the catalogue answer with a validation failure, as an unusable filter does.</summary>
        public bool Refuse { get; init; }

        public Task<Result<PagedResult<ProductCardDto>>> ListAsync(
            ProductListQuery query,
            CancellationToken cancellationToken)
        {
            _queries.Add(query);

            if (Refuse)
            {
                return Task.FromResult(
                    Result.Failure<PagedResult<ProductCardDto>>("category", "That category is ambiguous."));
            }

            var items = Enumerable
                .Range(1, query.PageSize)
                .Select(index => Card(index, query))
                .ToList();

            return Task.FromResult(Result.Success(
                new PagedResult<ProductCardDto>(items, query.Page, query.PageSize, 200)));
        }

        public Task<ProductDetailDto?> GetBySlugAsync(string slug, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The homepage never reads one product.");

        public Task<Result<PagedResult<ProductCardDto>>> SearchAsync(
            ProductSearchQuery query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The homepage never searches.");

        private static ProductCardDto Card(int index, ProductListQuery query)
        {
            var marker = string.Join(
                '-',
                new[]
                {
                    query.DepartmentSlug ?? "all",
                    query.CategorySlug ?? "any",
                    query.NewArrivalsOnly ? "new" : null,
                    query.OnSaleOnly ? "sale" : null,
                    ProductSortOptions.ToQueryValue(query.Sort),
                }.Where(part => part is not null));

            return new ProductCardDto(
                index,
                $"SD-{index:0000}",
                $"{marker} {index}",
                $"{marker}-{index}",
                "Women",
                query.DepartmentSlug ?? "women",
                "Jeans",
                query.CategorySlug ?? "jeans",
                null,
                null,
                null,
                null,
                null,
                new PriceDto(2490, 2490, 0, false, PriceDto.Lkr),
                new RatingDto(null, 0),
                null,
                StockStatus.InStock,
                true,
                []);
        }
    }
}
