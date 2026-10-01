using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Media.ClientAssets;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.Tests.MediaFixtures;

namespace SpencerDenim.IntegrationTests.Media;

/// <summary>
/// The client's own photographs, video and logo going into the library and coming out attached to the
/// right things (W1-13).
/// <para>
/// These tests are about the <b>contract</b>, not the pixels. The pixels are the client's and nothing
/// here can assert anything useful about them; what can go silently wrong is a photograph of a polo
/// shirt landing on a belt, a second run quietly doubling the library, a card that flips to a grey
/// placeholder on hover, or a homepage section switched on over an empty frame. Each of those is a
/// test below.
/// </para>
/// </summary>
public sealed class ClientAssetImportTests(SqlServerDatabaseFixture sqlServer)
{
    /// <summary>
    /// Four products, chosen because between them they carry every shape the delivered batch has: a
    /// clean pair, a name with a character lost off the front, a name with the folder run into it, and a
    /// garment with only one photograph.
    /// </summary>
    private static readonly string[] TheDeliveredShapes =
    [
        "washed-linen-camp-shirt-1.jpg.jpg",
        "washed-linen-camp-shirt-2.jpg.jpg",
        "court-canvas-sneakers-1.jpg.jpg",
        "ourt-canvas-sneakers-2.jpg.jpg",
        "productsleather-look-belt-1.jpg.jpg",
        "leather-look-belt-2.jpg.jpg",
        "chino-shorts-1.jpg.jpg",
    ];

    [Fact]
    public async Task A_photograph_lands_on_the_product_it_names_in_the_role_its_number_means()
    {
        await using var world = await CreateAsync();
        await world.DropTheUsualAsync(TheDeliveredShapes, Token);

        var report = await world.ImportAsync(Token);
        Assert.True(report.Succeeded, string.Join("; ", report.Failures));

        await using var scope = world.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var product = await ProductAsync(dbContext, "washed-linen-camp-shirt");

        var card = await dbContext.MediaAssets.SingleAsync(
            asset => asset.Id == product.CardImageAssetId!.Value, Token);
        var hover = await dbContext.MediaAssets.SingleAsync(
            asset => asset.Id == product.HoverImageAssetId!.Value, Token);

        // Real alt text from the product's name, not the placeholder wording the seeder wrote.
        Assert.Equal("Washed Linen Camp Shirt, front view", card.AltText);
        Assert.Equal("Washed Linen Camp Shirt, alternate view", hover.AltText);
        Assert.DoesNotContain("laceholder", card.AltText!, StringComparison.Ordinal);

        // Through the pipeline, not around it: Ready, checksummed, with the WebP rendition set.
        Assert.Equal(MediaAssetStatus.Ready, card.Status);
        Assert.Equal(MediaRenditions.CurrentSetVersion, card.RenditionSetVersion);
        Assert.Contains(MediaStorageKey.ForRendition(card.PublicId, MediaRenditions.Card), world.PublicFiles());

        // And the gallery rows carry the roles ProductMedia already defines, the client's photographs
        // ahead of the placeholder frames.
        var gallery = await dbContext.ProductMedia
            .Where(row => row.ProductId == product.Id)
            .OrderBy(row => row.SortOrder)
            .ThenBy(row => row.Id)
            .ToListAsync(Token);

        Assert.Equal(card.Id, gallery[0].MediaAssetId);
        Assert.Equal(ProductMediaRole.Gallery, gallery[0].MediaRole);
        Assert.Equal(hover.Id, gallery[1].MediaAssetId);
        Assert.Equal(ProductMediaRole.Hover, gallery[1].MediaRole);

        // Doc 08 §3.3: every structured reference is mirrored for the "used in…" panel and the delete guard.
        var usages = await dbContext.MediaUsages
            .Where(usage => usage.MediaAssetId == card.Id || usage.MediaAssetId == hover.Id)
            .ToListAsync(Token);

        Assert.Contains(usages, usage => usage.MediaAssetId == card.Id && usage.UsageRole == MediaUsageRoles.Card);
        Assert.Contains(usages, usage => usage.MediaAssetId == hover.Id && usage.UsageRole == MediaUsageRoles.Hover);
        Assert.All(usages, usage => Assert.Equal(MediaUsageOwnerTypes.Product, usage.OwnerType));
    }

    /// <summary>
    /// The two corruptions in the delivered batch reach the garment they were meant for, and the report
    /// names every rename rather than making it quietly.
    /// </summary>
    [Fact]
    public async Task A_corrupted_filename_reaches_the_intended_product_and_the_rename_is_reported()
    {
        await using var world = await CreateAsync();
        await world.DropTheUsualAsync(TheDeliveredShapes, Token);

        var report = await world.ImportAsync(Token);
        Assert.True(report.Succeeded, string.Join("; ", report.Failures));

        var renames = report.InferredRenames;
        Assert.Equal(2, renames.Count);
        Assert.Contains(renames, entry => entry.RepairedFrom == "ourt-canvas-sneakers");
        Assert.Contains(renames, entry => entry.RepairedFrom == "productsleather-look-belt");

        await using var scope = world.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var sneakers = await ProductAsync(dbContext, "court-canvas-sneakers");
        var belt = await ProductAsync(dbContext, "leather-look-belt");

        // Both ends of each pair: the repaired name filled the slot its number names, and the clean name
        // filled the other — so the pair really did meet on the same product.
        Assert.NotNull(sneakers.CardImageAssetId);
        Assert.NotNull(sneakers.HoverImageAssetId);
        Assert.NotNull(belt.CardImageAssetId);
        Assert.NotNull(belt.HoverImageAssetId);
    }

    /// <summary>
    /// A photograph nobody can place stops the run. It is not guessed at and it is not skipped quietly:
    /// a garment silently missing its picture on opening day is the failure this prevents.
    /// </summary>
    [Fact]
    public async Task A_photograph_that_names_no_product_fails_the_run_and_stores_nothing()
    {
        await using var world = await CreateAsync();
        await world.Drop("products", "washed-linen-camp-shirt-1.jpg.jpg", TestImages.UniqueJpeg(), Token);
        await world.Drop("products", "a-garment-we-do-not-sell-1.jpg.jpg", TestImages.UniqueJpeg(), Token);
        await world.Drop("departments", "women.jpg", TestImages.UniqueJpeg(), Token);
        Directory.CreateDirectory(Path.Combine(world.DropRoot, "home"));
        Directory.CreateDirectory(Path.Combine(world.DropRoot, "social"));

        var before = world.PublicFiles().Count;
        var report = await world.ImportAsync(Token);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Failures, failure => failure.Contains("a-garment-we-do-not-sell", StringComparison.Ordinal));
        Assert.Equal(1, report.Count(ClientAssetOutcome.Unmappable));

        // Nothing at all was stored, not even the photograph that *was* placeable: the names are all
        // resolved before a byte is written, so a batch with a hole in it does not half-import.
        Assert.Equal(before, world.PublicFiles().Count);
        Assert.Equal(0, report.Count(ClientAssetOutcome.Imported));
    }

    /// <summary>
    /// A product with one photograph gets a card and <b>no hover</b>. The alternative is a real garment
    /// on the card flipping, on hover, to a grey frame that says no photograph was supplied.
    /// </summary>
    [Fact]
    public async Task A_product_with_one_photograph_gets_a_card_and_no_hover()
    {
        await using var world = await CreateAsync();
        await world.DropTheUsualAsync(TheDeliveredShapes, Token);

        var report = await world.ImportAsync(Token);
        Assert.True(report.Succeeded, string.Join("; ", report.Failures));

        await using var scope = world.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var shorts = await ProductAsync(dbContext, "chino-shorts");

        Assert.NotNull(shorts.CardImageAssetId);
        Assert.Null(shorts.HoverImageAssetId);

        var card = await dbContext.MediaAssets.SingleAsync(asset => asset.Id == shorts.CardImageAssetId!.Value, Token);
        Assert.Equal("Chino Shorts, front view", card.AltText);

        Assert.Contains(report.Notes, note => note.Contains("chino-shorts", StringComparison.Ordinal));
    }

    /// <summary>
    /// The ten kids' products keep their placeholders. No photograph of an adult is put in front of a
    /// children's garment to fill a gap, however convincing the gap looks.
    /// </summary>
    [Fact]
    public async Task Kids_products_keep_their_placeholders_rather_than_borrowing_an_adult_photograph()
    {
        await using var world = await CreateAsync();
        await world.DropTheUsualAsync(TheDeliveredShapes, Token);

        var report = await world.ImportAsync(Token);
        Assert.True(report.Succeeded, string.Join("; ", report.Failures));

        await using var scope = world.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var kids = await dbContext.Products
            .Where(product => product.Department!.Slug == "kids")
            .Select(product => new { product.Slug, Alt = product.CardImageAsset!.AltText })
            .ToListAsync(Token);

        Assert.Equal(10, kids.Count);
        Assert.All(kids, entry => Assert.StartsWith("Placeholder image", entry.Alt!, StringComparison.Ordinal));
    }

    /// <summary>
    /// Running it again is a no-op: the pipeline answers the same bytes with the same asset, and every
    /// reference row is written only when it is not already there.
    /// </summary>
    [Fact]
    public async Task Running_the_import_a_second_time_writes_nothing()
    {
        await using var world = await CreateAsync();
        await world.DropTheUsualAsync(TheDeliveredShapes, Token);

        var first = await world.ImportAsync(Token);
        Assert.True(first.Succeeded, string.Join("; ", first.Failures));
        Assert.True(first.Count(ClientAssetOutcome.Imported) > 0);

        var filesAfterFirst = world.PublicFiles();
        var (assetsAfterFirst, mediaAfterFirst, usagesAfterFirst, itemsAfterFirst) = await CountsAsync(world);

        var second = await world.ImportAsync(Token);

        Assert.True(second.Succeeded, string.Join("; ", second.Failures));
        Assert.Equal(0, second.Count(ClientAssetOutcome.Imported));
        Assert.Equal(first.Entries.Count, second.Count(ClientAssetOutcome.AlreadyPresent) + second.Count(ClientAssetOutcome.NotInContract));

        var (assets, media, usages, items) = await CountsAsync(world);

        Assert.Equal(filesAfterFirst, world.PublicFiles());
        Assert.Equal(assetsAfterFirst, assets);
        Assert.Equal(mediaAfterFirst, media);
        Assert.Equal(usagesAfterFirst, usages);
        Assert.Equal(itemsAfterFirst, items);
    }

    /// <summary>
    /// The five artwork sections are seeded switched off and come on only once they have something to
    /// show — which is the storefront's own <c>hasContent</c> rule, enforced on the way in.
    /// </summary>
    [Fact]
    public async Task The_five_dark_sections_come_on_only_once_their_media_exists()
    {
        await using var world = await CreateAsync();

        HomeSectionType[] artwork =
        [
            HomeSectionType.HeroVideo,
            HomeSectionType.TrendingItems,
            HomeSectionType.CampaignBanner,
            HomeSectionType.ActivewearFeature,
            HomeSectionType.SocialGallery,
        ];

        Assert.All(await EnabledAsync(world, artwork), state => Assert.False(state.IsEnabled, state.Type.ToString()));

        await world.DropTheUsualAsync(TheDeliveredShapes, Token);
        var report = await world.ImportAsync(Token);
        Assert.True(report.Succeeded, string.Join("; ", report.Failures));

        var after = await EnabledAsync(world, artwork);
        Assert.All(after, state => Assert.True(state.IsEnabled, state.Type.ToString()));

        // Switched on, and each one really does carry the picture that justifies it.
        await using var scope = world.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var hero = await ItemsAsync(dbContext, HomeSectionType.HeroVideo);
        Assert.NotNull(hero[0].VideoAssetId);

        // Doc 08 §3.7's check constraint: a video item must carry a still, and the poster is it.
        Assert.NotNull(hero[0].ImageAssetId);
        Assert.True(hero[0].HasStaticFallback);

        Assert.Equal(4, (await ItemsAsync(dbContext, HomeSectionType.TrendingItems)).Count);
        Assert.Equal(6, (await ItemsAsync(dbContext, HomeSectionType.SocialGallery)).Count);
    }

    /// <summary>
    /// With no homepage media in the drop, the five stay off. The gate is "has it got a picture", not
    /// "did the import run".
    /// </summary>
    [Fact]
    public async Task Without_its_media_a_section_is_left_switched_off()
    {
        await using var world = await CreateAsync();

        await world.Drop("products", "chino-shorts-1.jpg.jpg", TestImages.UniqueJpeg(), Token);
        await world.Drop("departments", "women.jpg", TestImages.UniqueJpeg(), Token);
        Directory.CreateDirectory(Path.Combine(world.DropRoot, "home"));
        Directory.CreateDirectory(Path.Combine(world.DropRoot, "social"));

        var report = await world.ImportAsync(Token);
        Assert.True(report.Succeeded, string.Join("; ", report.Failures));

        var states = await EnabledAsync(
            world,
            [
                HomeSectionType.HeroVideo,
                HomeSectionType.TrendingItems,
                HomeSectionType.CampaignBanner,
                HomeSectionType.ActivewearFeature,
                HomeSectionType.SocialGallery,
            ]);

        Assert.All(states, state => Assert.False(state.IsEnabled, state.Type.ToString()));

        // And HOME-02, which was already on, got its photographs.
        await using var scope = world.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var women = await dbContext.Departments.SingleAsync(department => department.Slug == "women", Token);
        Assert.NotNull(women.HeroImageAssetId);
    }

    /// <summary>
    /// The pipeline takes MP4/H.264 only and converts nothing (doc 03 §3b). A file that is not H.264 is
    /// refused with the real reason, the hero stays off, and no workaround is attempted.
    /// </summary>
    [Fact]
    public async Task A_hero_video_that_is_not_h264_is_refused_with_the_real_reason()
    {
        await using var world = await CreateAsync();
        await world.DropTheUsualAsync(TheDeliveredShapes, Token);

        // The same container, the same everything, with an HEVC sample entry instead of avc1.
        await world.Drop("home", "hero.mp4", TestVideos.Mp4(codec: "hvc1", payloadBytes: 2048), Token);

        var report = await world.ImportAsync(Token);

        Assert.False(report.Succeeded);
        Assert.Contains(
            report.Failures,
            failure => failure.Contains("hvc1", StringComparison.Ordinal)
                && failure.Contains("H.264", StringComparison.Ordinal));

        var refused = Assert.Single(report.Entries, entry => entry.Outcome == ClientAssetOutcome.Refused);
        Assert.Equal("hero.mp4", refused.FileName);

        var hero = Assert.Single(await EnabledAsync(world, [HomeSectionType.HeroVideo]));
        Assert.False(hero.IsEnabled);
    }

    /// <summary>
    /// The social tiles are ordinary admin-editable rows, and the destinations they carry until the
    /// client sends real ones cannot be mistaken for real ones — <c>example.invalid</c> is a domain RFC
    /// 2606 reserves so it can never resolve, and the accessible name says PLACEHOLDER out loud.
    /// </summary>
    [Fact]
    public async Task The_social_tiles_carry_links_that_could_never_be_mistaken_for_real_ones()
    {
        await using var world = await CreateAsync();
        await world.DropTheUsualAsync(TheDeliveredShapes, Token);

        var report = await world.ImportAsync(Token);
        Assert.True(report.Succeeded, string.Join("; ", report.Failures));

        await using var scope = world.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tiles = await ItemsAsync(dbContext, HomeSectionType.SocialGallery);

        Assert.Equal(6, tiles.Count);
        Assert.All(tiles, tile =>
        {
            // Absolute and external, which is what the gallery renders with target="_blank",
            // rel="noopener noreferrer" and an accessible name saying it opens a new tab.
            Assert.StartsWith("https://", tile.LinkUrl!, StringComparison.Ordinal);
            Assert.Contains("example.invalid", tile.LinkUrl!, StringComparison.Ordinal);
            Assert.Contains("PLACEHOLDER", tile.Title!, StringComparison.Ordinal);
            Assert.NotNull(tile.ImageAssetId);
        });

        // Nothing that looks like a live account.
        Assert.DoesNotContain(tiles, tile => tile.LinkUrl!.Contains("instagram.com", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A dry run resolves every name and reports what would happen, and writes nothing — which is how
    /// the naming can be checked against a real catalogue before the library is touched.
    /// </summary>
    [Fact]
    public async Task A_dry_run_reports_the_whole_plan_and_stores_nothing()
    {
        await using var world = await CreateAsync();
        await world.DropTheUsualAsync(TheDeliveredShapes, Token);

        var before = world.PublicFiles();
        var report = await world.ImportAsync(Token, dryRun: true);

        Assert.True(report.Succeeded, string.Join("; ", report.Failures));
        Assert.Equal(2, report.InferredRenames.Count);
        Assert.Equal(before, world.PublicFiles());

        var sections = await EnabledAsync(world, [HomeSectionType.HeroVideo, HomeSectionType.SocialGallery]);
        Assert.All(sections, state => Assert.False(state.IsEnabled, state.Type.ToString()));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<ClientAssetWorld> CreateAsync()
    {
        sqlServer.SkipIfUnavailable();
        return await ClientAssetWorld.CreateAsync(sqlServer.ConnectionString, withPlaceholderFiles: false, Token);
    }

    private static async Task<Product> ProductAsync(AppDbContext dbContext, string slug) =>
        await dbContext.Products.SingleAsync(product => product.Slug == slug, Token);

    private static async Task<List<HomeSectionItem>> ItemsAsync(AppDbContext dbContext, HomeSectionType type) =>
        await dbContext.HomeSectionItems
            .Where(item => item.HomeSection!.SectionType == type)
            .OrderBy(item => item.SortOrder)
            .ToListAsync(Token);

    private static async Task<List<(HomeSectionType Type, bool IsEnabled)>> EnabledAsync(
        ClientAssetWorld world,
        IReadOnlyList<HomeSectionType> types)
    {
        await using var scope = world.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await dbContext.HomeSections
            .Where(section => types.Contains(section.SectionType))
            .Select(section => new { section.SectionType, section.IsEnabled })
            .ToListAsync(Token);

        return rows.Select(row => (row.SectionType, row.IsEnabled)).ToList();
    }

    private static async Task<(int Assets, int Media, int Usages, int Items)> CountsAsync(ClientAssetWorld world)
    {
        await using var scope = world.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return (
            await dbContext.MediaAssets.CountAsync(Token),
            await dbContext.ProductMedia.CountAsync(Token),
            await dbContext.MediaUsages.CountAsync(Token),
            await dbContext.HomeSectionItems.CountAsync(Token));
    }
}
