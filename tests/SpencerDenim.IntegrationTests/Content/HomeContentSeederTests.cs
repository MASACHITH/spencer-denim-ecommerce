using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Content;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Seeding;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Content;

/// <summary>
/// The seed of doc 08 §9: ten sections in proposal order, six items, two announcements, and the
/// media-dependent sections switched off until there is media.
/// </summary>
public sealed class HomeContentSeederTests(SqlServerDatabaseFixture sqlServer)
{
    /// <summary>
    /// Doc 08 §9's table says "media-dependent sections disabled until content exists". These four
    /// are nothing but artwork, and W1-04 has not landed.
    /// </summary>
    private static readonly HomeSectionType[] SeededDisabled =
    [
        HomeSectionType.HeroVideo,
        HomeSectionType.CampaignBanner,
        HomeSectionType.ActivewearFeature,
        HomeSectionType.SocialGallery,
    ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_ten_sections_are_seeded_in_the_proposal_order()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var sections = await dbContext.HomeSections
            .OrderBy(section => section.SortOrder)
            .Select(section => new { section.SectionType, section.SortOrder, section.IsEnabled })
            .ToListAsync(Token);

        Assert.Equal(10, sections.Count);
        Assert.Equal(
            Enum.GetValues<HomeSectionType>(),
            sections.Select(section => section.SectionType));
        Assert.Equal(Enumerable.Range(1, 10), sections.Select(section => (int)section.SortOrder));
    }

    [Fact]
    public async Task The_media_dependent_sections_are_seeded_switched_off_and_the_rest_are_on()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var disabled = await dbContext.HomeSections
            .Where(section => !section.IsEnabled)
            .Select(section => section.SectionType)
            .OrderBy(type => type)
            .ToListAsync(Token);

        Assert.Equal(SeededDisabled.Order(), disabled);
    }

    [Fact]
    public async Task Six_items_are_seeded_three_department_tiles_and_three_benefits()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var items = await dbContext.HomeSectionItems
            .Include(item => item.HomeSection)
            .OrderBy(item => item.HomeSectionId)
            .ThenBy(item => item.SortOrder)
            .ToListAsync(Token);

        Assert.Equal(6, items.Count);

        var tiles = items.Where(item => item.HomeSection!.SectionType == HomeSectionType.ShopByDepartment).ToList();
        var benefits = items.Where(item => item.HomeSection!.SectionType == HomeSectionType.Benefits).ToList();

        Assert.Equal(3, tiles.Count);
        Assert.Equal(3, benefits.Count);

        // Every tile points at a real department and carries the link that opens it.
        Assert.All(tiles, tile => Assert.NotNull(tile.DepartmentId));
        Assert.Equal(["/women", "/men", "/kids"], tiles.Select(tile => tile.LinkUrl));

        // Doc 08 §3.7: an icon key names an icon in the storefront's set, never a file, so the
        // benefits block needs no media either.
        Assert.Equal(["delivery", "returns", "quality"], benefits.Select(benefit => benefit.IconKey));
        Assert.All(benefits, benefit => Assert.Null(benefit.ImageAssetId));
        Assert.All(benefits, benefit => Assert.False(string.IsNullOrWhiteSpace(benefit.Body)));
    }

    [Fact]
    public async Task Two_announcement_lines_are_seeded_and_active()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var messages = await dbContext.AnnouncementMessages
            .OrderBy(message => message.SortOrder)
            .ToListAsync(Token);

        Assert.Equal(2, messages.Count);
        Assert.All(messages, message => Assert.True(message.IsActive));
        Assert.All(messages, message => Assert.False(string.IsNullOrWhiteSpace(message.Message)));
    }

    /// <summary>
    /// Doc 08 §9 (N13): the seeder inserts only what is missing. Running it again on a seeded
    /// database inserts nothing.
    /// </summary>
    [Fact]
    public async Task Running_the_seeder_again_inserts_nothing()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<HomeContentSeeder>();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var before = await CountsAsync(dbContext);

        var first = await seeder.SeedAsync(Token);
        var second = await seeder.SeedAsync(Token);

        Assert.Equal(0, first.Sections);
        Assert.Equal(0, first.SectionItems);
        Assert.Equal(0, first.Announcements);
        Assert.Equal(0, second.Sections);
        Assert.Equal(0, second.SectionItems);
        Assert.Equal(0, second.Announcements);

        Assert.Equal(before, await CountsAsync(dbContext));
    }

    /// <summary>
    /// The point of "insert only what is missing": the client rewrites a heading, switches a section
    /// on, reorders the page — and the next deployment's seed run leaves all of it alone.
    /// </summary>
    [Fact]
    public async Task An_admin_edit_survives_a_reseed()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seeder = scope.ServiceProvider.GetRequiredService<HomeContentSeeder>();

        var section = await dbContext.HomeSections
            .SingleAsync(candidate => candidate.SectionType == HomeSectionType.Benefits, Token);

        var originalTitle = section.Title;
        var originalOrder = section.SortOrder;

        section.Title = "Why shop with us";
        section.SortOrder = 4;
        await dbContext.SaveChangesAsync(Token);

        try
        {
            await seeder.SeedAsync(Token);

            var reread = await dbContext.HomeSections
                .AsNoTracking()
                .SingleAsync(candidate => candidate.SectionType == HomeSectionType.Benefits, Token);

            Assert.Equal("Why shop with us", reread.Title);
            Assert.Equal(4, reread.SortOrder);
            Assert.Equal(10, await dbContext.HomeSections.CountAsync(Token));
        }
        finally
        {
            section.Title = originalTitle;
            section.SortOrder = originalOrder;
            await dbContext.SaveChangesAsync(Token);
        }
    }

    /// <summary>
    /// A benefit the client has rewritten keeps its copy, because the seeder keys on the icon —
    /// the one field the copy does not change.
    /// </summary>
    [Fact]
    public async Task A_rewritten_benefit_is_not_duplicated_by_a_reseed()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seeder = scope.ServiceProvider.GetRequiredService<HomeContentSeeder>();

        var benefit = await dbContext.HomeSectionItems.SingleAsync(item => item.IconKey == "delivery", Token);
        var originalTitle = benefit.Title;
        var originalBody = benefit.Body;

        benefit.Title = "Delivery";
        benefit.Body = "Rs. 400 in Colombo.";
        await dbContext.SaveChangesAsync(Token);

        try
        {
            await seeder.SeedAsync(Token);

            var benefits = await dbContext.HomeSectionItems
                .AsNoTracking()
                .Where(item => item.IconKey == "delivery")
                .ToListAsync(Token);

            var only = Assert.Single(benefits);
            Assert.Equal("Delivery", only.Title);
            Assert.Equal("Rs. 400 in Colombo.", only.Body);
        }
        finally
        {
            benefit.Title = originalTitle;
            benefit.Body = originalBody;
            await dbContext.SaveChangesAsync(Token);
        }
    }

    /// <summary>
    /// The three product rows are configured with a listing URL the catalogue can answer. A typo
    /// here would be an empty row on the front page with nothing to say why.
    /// </summary>
    [Theory]
    [InlineData(HomeSectionType.NewArrivals, "/new-arrivals")]
    [InlineData(HomeSectionType.TrendingItems, "/?sort=best-selling")]
    [InlineData(HomeSectionType.SaleSection, "/sale")]
    public async Task A_product_row_is_seeded_with_the_listing_it_draws_from(
        HomeSectionType type,
        string expectedListingUrl)
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var settingsJson = await dbContext.HomeSections
            .Where(section => section.SectionType == type)
            .Select(section => section.SettingsJson)
            .SingleAsync(Token);

        var settings = SpencerDenim.Application.Content.HomeSectionSettings.Parse(settingsJson);

        Assert.Equal(expectedListingUrl, settings.ListingUrl);
        Assert.Equal(SpencerDenim.Application.Content.HomeSectionMode.Auto, settings.Mode);
        Assert.Equal(8, settings.Count);
    }

    private static async Task<(int Sections, int Items, int Announcements)> CountsAsync(AppDbContext dbContext) =>
        (await dbContext.HomeSections.CountAsync(Token),
            await dbContext.HomeSectionItems.CountAsync(Token),
            await dbContext.AnnouncementMessages.CountAsync(Token));
}
