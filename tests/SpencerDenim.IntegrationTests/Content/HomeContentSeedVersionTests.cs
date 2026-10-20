using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Seeding;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Content;

/// <summary>
/// Issue #63: the homepage's words and links moved out of the client-asset importer and into the content
/// seeder, as switched-off rows offered once by version (<c>Seed.HomeContentVersion</c>, the #152 rule).
/// <para>
/// <b>Each test owns a throwaway database</b> (<see cref="FreshDeployment"/>), because what is under test
/// is the marker row and rows going missing — exactly the state another test class on the shared fixture
/// must never see half-way through.
/// </para>
/// </summary>
public sealed class HomeContentSeedVersionTests(SqlServerDatabaseFixture sqlServer)
{
    private static readonly HomeSectionType[] ArtworkSections =
    [
        HomeSectionType.HeroVideo,
        HomeSectionType.TrendingItems,
        HomeSectionType.CampaignBanner,
        HomeSectionType.ActivewearFeature,
        HomeSectionType.SocialGallery,
    ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A fresh database: every artwork row exists, says what it is for, and is switched off — as is its
    /// section — so the shop looks exactly as it did before the move; the marker records version 2; a second
    /// run adds nothing; and a row the client deletes stays deleted.
    /// </summary>
    [Fact]
    public async Task A_fresh_database_gets_the_artwork_rows_switched_off_once_and_never_again()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var counts = await Seeder(dbContext).SeedAsync(Token);

            // Fresh: no departments are seeded in this database, so the three tiles are skipped, and every
            // item inserted is either a benefit or one of the artwork rows.
            Assert.Equal(HomeContentData.Benefits.Count + HomeContentData.ArtworkItems.Count, counts.SectionItems);
        }

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var items = await dbContext.HomeSectionItems
                .AsNoTracking()
                .Include(item => item.HomeSection)
                .Where(item => ArtworkSections.Contains(item.HomeSection!.SectionType))
                .ToListAsync(Token);

            Assert.Equal(HomeContentData.ArtworkItems.Count, items.Count);

            foreach (var seed in HomeContentData.ArtworkItems)
            {
                var item = Assert.Single(items, candidate =>
                    candidate.HomeSection!.SectionType == seed.SectionType && candidate.SortOrder == seed.SortOrder);

                Assert.Equal(seed.Title, item.Title);
                Assert.Equal(seed.Subtitle, item.Subtitle);
                Assert.Equal(seed.ButtonLabel, item.ButtonLabel);
                Assert.Equal(seed.LinkUrl, item.LinkUrl);
                Assert.False(item.IsEnabled, $"{seed.SectionType} {seed.SortOrder} was seeded switched on.");
                Assert.Null(item.ImageAssetId);
                Assert.Null(item.VideoAssetId);
            }

            // The words follow the photograph the client sent (W1-14), and say so before it arrives.
            var activewear = Assert.Single(items, item => item.HomeSection!.SectionType == HomeSectionType.ActivewearFeature);
            Assert.Equal("Linen and denim", activewear.Title);
            Assert.DoesNotContain("activewear", activewear.LinkUrl!, StringComparison.OrdinalIgnoreCase);

            // The sections stay off: a fresh shop's homepage is unchanged by any of this.
            Assert.All(
                await dbContext.HomeSections.AsNoTracking().Where(section => ArtworkSections.Contains(section.SectionType)).ToListAsync(Token),
                section => Assert.False(section.IsEnabled));

            Assert.Equal("2", await MarkerAsync(dbContext));
        }

        // Again: nothing. Then the client deletes a trending tile, and the next deployment leaves it gone.
        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Assert.Equal(0, (await Seeder(dbContext).SeedAsync(Token)).SectionItems);

            var removed = await dbContext.HomeSectionItems
                .Where(item => item.HomeSection!.SectionType == HomeSectionType.TrendingItems && item.SortOrder == 3)
                .ExecuteDeleteAsync(Token);
            Assert.Equal(1, removed);

            Assert.Equal(0, (await Seeder(dbContext).SeedAsync(Token)).SectionItems);
            Assert.False(await dbContext.HomeSectionItems.AnyAsync(
                item => item.HomeSection!.SectionType == HomeSectionType.TrendingItems && item.SortOrder == 3, Token));
        }
    }

    /// <summary>
    /// A database from before #63, on which the importer had already written its rows: nothing anybody wrote
    /// is overwritten, nothing is duplicated, a switched-on row stays on — and the one exception, the words
    /// the importer itself retracted for HOME-06, is replaced field by field and only on an exact match.
    /// </summary>
    [Fact]
    public async Task An_existing_database_keeps_what_is_there_and_only_loses_the_retracted_words()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);

        // The pre-#63 state: sections seeded, no marker, and the rows the old importer left behind.
        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await Seeder(dbContext).SeedAsync(Token);

            await dbContext.HomeSectionItems
                .Where(item => ArtworkSections.Contains(item.HomeSection!.SectionType))
                .ExecuteDeleteAsync(Token);
            await dbContext.Settings
                .Where(setting => setting.Key == SettingKeys.Seed.HomeContentVersion)
                .ExecuteDeleteAsync(Token);

            var activewear = await SectionIdAsync(dbContext, HomeSectionType.ActivewearFeature);
            var trending = await SectionIdAsync(dbContext, HomeSectionType.TrendingItems);
            var campaign = await SectionIdAsync(dbContext, HomeSectionType.CampaignBanner);

            dbContext.HomeSectionItems.AddRange(
                new HomeSectionItem
                {
                    HomeSectionId = activewear,
                    SortOrder = 1,
                    IsEnabled = true,
                    Title = "Activewear",                  // retracted, as a heading
                    Subtitle = "Pieces that move with you.", // retracted
                    LinkUrl = "/women/activewear",          // retracted
                    ButtonLabel = "Activewear",             // NOT retracted here: a person typed it into the button
                },
                new HomeSectionItem
                {
                    HomeSectionId = trending,
                    SortOrder = 2,
                    IsEnabled = true,
                    Title = "Our linen edit",               // somebody's own words
                    LinkUrl = "/men/shirts?material=linen",
                },
                new HomeSectionItem
                {
                    HomeSectionId = campaign,
                    SortOrder = 1,
                    IsEnabled = false,                      // switched off on purpose: still somebody's row
                    Title = "Monsoon edit",
                });
            await dbContext.SaveChangesAsync(Token);
        }

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var counts = await Seeder(dbContext).SeedAsync(Token);

            // Every artwork slot but the three that were already filled, switched on or not.
            Assert.Equal(HomeContentData.ArtworkItems.Count - 3, counts.SectionItems);
        }

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var activewear = await dbContext.HomeSectionItems.AsNoTracking()
                .SingleAsync(item => item.HomeSection!.SectionType == HomeSectionType.ActivewearFeature, Token);
            Assert.Equal("Linen and denim", activewear.Title);
            Assert.Equal("Breathable linen for the heat, washed indigo for everything else.", activewear.Subtitle);
            Assert.Equal("/women?material=linen&material=linen-blend&material=denim", activewear.LinkUrl);
            Assert.Equal("Activewear", activewear.ButtonLabel);
            Assert.True(activewear.IsEnabled);

            var trending = await dbContext.HomeSectionItems.AsNoTracking()
                .Where(item => item.HomeSection!.SectionType == HomeSectionType.TrendingItems)
                .OrderBy(item => item.SortOrder)
                .ToListAsync(Token);
            Assert.Equal([1, 2, 3, 4], trending.Select(item => (int)item.SortOrder));
            Assert.Equal("Our linen edit", trending[1].Title);
            Assert.Equal("/men/shirts?material=linen", trending[1].LinkUrl);
            Assert.True(trending[1].IsEnabled);
            Assert.False(trending[0].IsEnabled);

            var banner = Assert.Single(await dbContext.HomeSectionItems.AsNoTracking()
                .Where(item => item.HomeSection!.SectionType == HomeSectionType.CampaignBanner)
                .ToListAsync(Token));
            Assert.Equal("Monsoon edit", banner.Title);

            Assert.Equal("2", await MarkerAsync(dbContext));
        }
    }

    /// <summary>
    /// The retraction rides on the version too: once a database is at version 2, "Activewear" typed back into
    /// the heading by a person is that person's choice, and no later deployment touches it.
    /// </summary>
    [Fact]
    public async Task Once_recorded_the_retraction_never_touches_a_heading_again()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);

        await using var scope = deployment.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await Seeder(dbContext).SeedAsync(Token);

        var item = await dbContext.HomeSectionItems
            .SingleAsync(candidate => candidate.HomeSection!.SectionType == HomeSectionType.ActivewearFeature, Token);
        item.Title = "Activewear";
        await dbContext.SaveChangesAsync(Token);

        await Seeder(dbContext).SeedAsync(Token);

        var reread = await dbContext.HomeSectionItems.AsNoTracking()
            .SingleAsync(candidate => candidate.HomeSection!.SectionType == HomeSectionType.ActivewearFeature, Token);
        Assert.Equal("Activewear", reread.Title);
    }

    private static HomeContentSeeder Seeder(AppDbContext dbContext) => new(dbContext, new SystemTestClock());

    private static Task<int> SectionIdAsync(AppDbContext dbContext, HomeSectionType type) =>
        dbContext.HomeSections.Where(section => section.SectionType == type).Select(section => section.Id).SingleAsync(Token);

    private static Task<string?> MarkerAsync(AppDbContext dbContext) =>
        dbContext.Settings.AsNoTracking()
            .Where(setting => setting.Key == SettingKeys.Seed.HomeContentVersion)
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(Token);

    private sealed class SystemTestClock : IClock
    {
        public DateTimeOffset UtcNow => TimeProvider.System.GetUtcNow();
    }
}
