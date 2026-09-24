using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Content;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Content;

/// <summary>
/// <c>GET /api/content/home</c> against the real API and the seeded database (HOME-01..10).
/// <para>
/// Several of these change the seeded content on purpose and restore it in a <c>finally</c>; the
/// assembly runs its test classes sequentially, so nothing else sees the half-applied state
/// (<c>AssemblyInfo.cs</c>).
/// </para>
/// </summary>
public sealed class HomeEndpointTests(SqlServerDatabaseFixture sqlServer) : ContentTestBase(sqlServer)
{
    [Fact]
    public async Task Only_the_sections_that_are_switched_on_are_returned_and_in_order()
    {
        var page = await CreateApi().GetHomeAsync(Token);

        Assert.Equal(
            [
                HomeSectionType.ShopByDepartment,
                HomeSectionType.NewArrivals,
                HomeSectionType.TrendingItems,
                HomeSectionType.SaleSection,
                HomeSectionType.Benefits,
                HomeSectionType.Newsletter,
            ],
            page.Sections.Select(section => section.Type));

        // Ascending, and the same order the client sees in admin.
        Assert.Equal(
            page.Sections.Select(section => section.SortOrder).Order(),
            page.Sections.Select(section => section.SortOrder));
    }

    /// <summary>
    /// The four media-only sections are seeded off. The storefront must not receive them at all —
    /// deciding whether to draw a section is the API's job, not the page's.
    /// </summary>
    [Theory]
    [InlineData(HomeSectionType.HeroVideo)]
    [InlineData(HomeSectionType.CampaignBanner)]
    [InlineData(HomeSectionType.ActivewearFeature)]
    [InlineData(HomeSectionType.SocialGallery)]
    public async Task A_section_that_is_switched_off_is_absent(HomeSectionType type)
    {
        var page = await CreateApi().GetHomeAsync(Token);

        Assert.DoesNotContain(page.Sections, section => section.Type == type);
    }

    [Fact]
    public async Task Switching_a_section_on_puts_it_on_the_page_in_its_own_place()
    {
        var api = CreateApi();

        await SetEnabledAsync(HomeSectionType.HeroVideo, true);
        try
        {
            var page = await api.GetHomeAsync(Token);

            Assert.Equal(HomeSectionType.HeroVideo, page.Sections[0].Type);
            Assert.Equal(7, page.Sections.Count);
        }
        finally
        {
            await SetEnabledAsync(HomeSectionType.HeroVideo, false);
        }

        Assert.Equal(6, (await api.GetHomeAsync(Token)).Sections.Count);
    }

    [Fact]
    public async Task A_section_whose_window_has_closed_is_absent()
    {
        var api = CreateApi();

        await ScheduleAsync(HomeSectionType.Benefits, startsAtUtc: null, endsAtUtc: DateTime.UtcNow.AddMinutes(-1));
        try
        {
            Assert.DoesNotContain(
                (await api.GetHomeAsync(Token)).Sections,
                section => section.Type == HomeSectionType.Benefits);
        }
        finally
        {
            await ScheduleAsync(HomeSectionType.Benefits, null, null);
        }

        Assert.Contains(
            (await api.GetHomeAsync(Token)).Sections,
            section => section.Type == HomeSectionType.Benefits);
    }

    [Fact]
    public async Task A_section_whose_window_has_not_opened_is_absent()
    {
        var api = CreateApi();

        await ScheduleAsync(HomeSectionType.Benefits, DateTime.UtcNow.AddMinutes(5), null);
        try
        {
            Assert.DoesNotContain(
                (await api.GetHomeAsync(Token)).Sections,
                section => section.Type == HomeSectionType.Benefits);
        }
        finally
        {
            await ScheduleAsync(HomeSectionType.Benefits, null, null);
        }
    }

    /// <summary>
    /// A section that is on but has nothing in it. It comes back with an empty list rather than
    /// being dropped, and the storefront draws nothing for it — tested in the browser suite. The
    /// distinction matters: "on and empty" is a state the client can see and fix in admin.
    /// </summary>
    [Fact]
    public async Task A_section_whose_items_are_all_switched_off_comes_back_empty()
    {
        var api = CreateApi();

        await SetItemsEnabledAsync(HomeSectionType.ShopByDepartment, false);
        try
        {
            var section = Assert.Single(
                (await api.GetHomeAsync(Token)).Sections,
                candidate => candidate.Type == HomeSectionType.ShopByDepartment);

            Assert.Empty(section.Items);
            Assert.Empty(section.Products);
        }
        finally
        {
            await SetItemsEnabledAsync(HomeSectionType.ShopByDepartment, true);
        }

        var restored = Assert.Single(
            (await api.GetHomeAsync(Token)).Sections,
            candidate => candidate.Type == HomeSectionType.ShopByDepartment);

        Assert.Equal(3, restored.Items.Count);
    }

    [Fact]
    public async Task The_department_tiles_carry_the_department_they_open()
    {
        var page = await CreateApi().GetHomeAsync(Token);

        var tiles = Assert
            .Single(page.Sections, section => section.Type == HomeSectionType.ShopByDepartment)
            .Items;

        Assert.Equal(["women", "men", "kids"], tiles.Select(tile => tile.DepartmentSlug));
        Assert.Equal(["Women", "Men", "Kids"], tiles.Select(tile => tile.DepartmentName));
        Assert.Equal(["/women", "/men", "/kids"], tiles.Select(tile => tile.LinkUrl));

        // W1-04 has not landed, so there is no artwork and the payload says so plainly rather than
        // carrying a URL that would 404 on a shopper's screen.
        Assert.All(tiles, tile => Assert.Null(tile.ImageUrl));
    }

    [Fact]
    public async Task The_benefits_carry_an_icon_key_and_their_copy()
    {
        var page = await CreateApi().GetHomeAsync(Token);

        var benefits = Assert
            .Single(page.Sections, section => section.Type == HomeSectionType.Benefits)
            .Items;

        Assert.Equal(["delivery", "returns", "quality"], benefits.Select(benefit => benefit.IconKey));
        Assert.All(benefits, benefit => Assert.False(string.IsNullOrWhiteSpace(benefit.Title)));
        Assert.All(benefits, benefit => Assert.False(string.IsNullOrWhiteSpace(benefit.Body)));
    }

    /// <summary>
    /// The invariant the whole server-side resolution exists for: a product row <b>is</b> the head
    /// of the listing it links to. Asserted against the catalogue endpoint itself, so the row and
    /// the click-through cannot drift apart.
    /// </summary>
    [Theory]
    [InlineData(HomeSectionType.NewArrivals, "?newArrivals=true&page=1&pageSize=8")]
    [InlineData(HomeSectionType.SaleSection, "?onSale=true&page=1&pageSize=8")]
    [InlineData(HomeSectionType.TrendingItems, "?sort=best-selling&page=1&pageSize=8")]
    public async Task A_product_row_is_the_head_of_the_listing_it_is_drawn_from(
        HomeSectionType type,
        string listingQuery)
    {
        var page = await CreateApi().GetHomeAsync(Token);
        var listing = await CreateCatalogApi().ListAsync(listingQuery, Token);

        var section = Assert.Single(page.Sections, candidate => candidate.Type == type);

        Assert.NotEmpty(section.Products);
        Assert.Equal(
            listing.Items.Select(card => card.Slug),
            section.Products.Select(card => card.Slug));
    }

    [Fact]
    public async Task A_product_row_carries_the_prices_and_badges_a_card_renders()
    {
        var page = await CreateApi().GetHomeAsync(Token);

        var card = Assert
            .Single(page.Sections, section => section.Type == HomeSectionType.NewArrivals)
            .Products[0];

        Assert.False(string.IsNullOrWhiteSpace(card.Slug));
        Assert.False(string.IsNullOrWhiteSpace(card.DepartmentSlug));
        Assert.True(card.Price.Current > 0);
        Assert.Equal("LKR", card.Price.Currency);
    }

    [Fact]
    public async Task The_sale_row_only_holds_discounted_products()
    {
        var page = await CreateApi().GetHomeAsync(Token);

        var sale = Assert.Single(page.Sections, section => section.Type == HomeSectionType.SaleSection);

        Assert.NotEmpty(sale.Products);
        Assert.All(sale.Products, card => Assert.True(card.Price.HasDiscount));
    }

    /// <summary>
    /// An admin typo in the listing URL must cost that row and nothing else. On a front page a 500
    /// is the worst possible answer.
    /// </summary>
    [Fact]
    public async Task A_listing_url_the_catalogue_refuses_empties_the_row_and_leaves_the_page_standing()
    {
        var api = CreateApi();

        await SetSettingsAsync(HomeSectionType.SaleSection, """{"listingUrl":"/women?minPrice=abc","count":8}""");
        try
        {
            var page = await api.GetHomeAsync(Token);
            var sale = Assert.Single(page.Sections, section => section.Type == HomeSectionType.SaleSection);

            Assert.Empty(sale.Products);
            Assert.Equal(6, page.Sections.Count);
            Assert.NotEmpty(
                Assert.Single(page.Sections, section => section.Type == HomeSectionType.NewArrivals).Products);
        }
        finally
        {
            await SetSettingsAsync(
                HomeSectionType.SaleSection,
                """{"mode":"auto","count":8,"listingUrl":"/sale","linkLabel":"Shop all sale"}""");
        }
    }

    /// <summary>
    /// HOME-04's own shape (doc 08 §3.7: "trending items store a filtered listing URL"). Switched to
    /// manual, the section shows the tiles the client arranged and asks the catalogue for nothing.
    /// </summary>
    [Fact]
    public async Task A_manual_trending_section_serves_its_tiles_and_their_filtered_listings()
    {
        var api = CreateApi();
        var added = await AddTrendingTilesAsync();

        try
        {
            var section = Assert.Single(
                (await api.GetHomeAsync(Token)).Sections,
                candidate => candidate.Type == HomeSectionType.TrendingItems);

            Assert.Empty(section.Products);
            Assert.Equal(
                ["/women/jeans?fit=wide-leg", "/men/shirts?material=linen"],
                section.Items.Select(item => item.LinkUrl));
            Assert.Equal(["Wide leg denim", "Linen shirts"], section.Items.Select(item => item.Title));
        }
        finally
        {
            await RemoveAsync(added);
            await SetSettingsAsync(
                HomeSectionType.TrendingItems,
                """{"mode":"auto","count":8,"listingUrl":"/?sort=best-selling"}""");
        }
    }

    [Fact]
    public async Task An_item_outside_its_own_window_is_left_out_while_the_section_stays()
    {
        var api = CreateApi();

        await ScheduleItemAsync("delivery", endsAtUtc: DateTime.UtcNow.AddMinutes(-1));
        try
        {
            var benefits = Assert
                .Single((await api.GetHomeAsync(Token)).Sections, section => section.Type == HomeSectionType.Benefits)
                .Items;

            Assert.Equal(2, benefits.Count);
            Assert.DoesNotContain(benefits, benefit => benefit.IconKey == "delivery");
        }
        finally
        {
            await ScheduleItemAsync("delivery", endsAtUtc: null);
        }
    }

    [Fact]
    public async Task The_endpoint_answers_head_as_well_as_get()
    {
        using var response = await CreateApi().HeadAsync("/api/content/home", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task SetEnabledAsync(HomeSectionType type, bool isEnabled)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.HomeSections
            .Where(section => section.SectionType == type)
            .ExecuteUpdateAsync(update => update.SetProperty(section => section.IsEnabled, isEnabled), Token);
    }

    private async Task ScheduleAsync(HomeSectionType type, DateTime? startsAtUtc, DateTime? endsAtUtc)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.HomeSections
            .Where(section => section.SectionType == type)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(section => section.StartsAtUtc, startsAtUtc)
                    .SetProperty(section => section.EndsAtUtc, endsAtUtc),
                Token);
    }

    private async Task SetSettingsAsync(HomeSectionType type, string settingsJson)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.HomeSections
            .Where(section => section.SectionType == type)
            .ExecuteUpdateAsync(
                update => update.SetProperty(section => section.SettingsJson, settingsJson),
                Token);
    }

    private async Task SetItemsEnabledAsync(HomeSectionType type, bool isEnabled)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.HomeSectionItems
            .Where(item => item.HomeSection!.SectionType == type)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.IsEnabled, isEnabled), Token);
    }

    private async Task ScheduleItemAsync(string iconKey, DateTime? endsAtUtc)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.HomeSectionItems
            .Where(item => item.IconKey == iconKey)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.EndsAtUtc, endsAtUtc), Token);
    }

    private async Task<IReadOnlyList<int>> AddTrendingTilesAsync()
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var sectionId = await dbContext.HomeSections
            .Where(section => section.SectionType == HomeSectionType.TrendingItems)
            .Select(section => section.Id)
            .SingleAsync(Token);

        await dbContext.HomeSections
            .Where(section => section.Id == sectionId)
            .ExecuteUpdateAsync(
                update => update.SetProperty(section => section.SettingsJson, """{"mode":"manual"}"""),
                Token);

        var tiles = new List<HomeSectionItem>
        {
            new()
            {
                HomeSectionId = sectionId,
                Title = "Wide leg denim",
                LinkUrl = "/women/jeans?fit=wide-leg",
                SortOrder = 1,
                IsEnabled = true,
            },
            new()
            {
                HomeSectionId = sectionId,
                Title = "Linen shirts",
                LinkUrl = "/men/shirts?material=linen",
                SortOrder = 2,
                IsEnabled = true,
            },
        };

        dbContext.HomeSectionItems.AddRange(tiles);
        await dbContext.SaveChangesAsync(Token);

        return tiles.Select(tile => tile.Id).ToList();
    }

    private async Task RemoveAsync(IReadOnlyList<int> itemIds)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.HomeSectionItems
            .Where(item => itemIds.Contains(item.Id))
            .ExecuteDeleteAsync(Token);
    }
}
