using SpencerDenim.Application.Media.Abstractions;
using System.Security.Cryptography;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Media.ClientAssets;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Media.ClientAssets;
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
    /// <summary>The five blocks W2-01 seeded switched off, because each one is nothing but artwork.</summary>
    private static readonly HomeSectionType[] ArtworkSections =
    [
        HomeSectionType.HeroVideo,
        HomeSectionType.TrendingItems,
        HomeSectionType.CampaignBanner,
        HomeSectionType.ActivewearFeature,
        HomeSectionType.SocialGallery,
    ];

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
    /// <b>A drop that stops sending a photograph must not leave the site showing it.</b>
    /// <para>
    /// The real case: the client's <c>poplin-formal-shirt-1</c> was the wrong garment, so they moved the
    /// right one into <c>-1</c> and withdrew the other. The product has one photograph now — and the
    /// hover slot still held the withdrawn one, pointer and gallery row both, because the import only
    /// ever visited slots a file was delivered for. The card showed the shirt and flipped, on hover, to
    /// the picture the client had just rejected.
    /// </para>
    /// <para>
    /// <b>The whole correction needs <c>--replace</c>, and the plain run says nothing has changed rather
    /// than half-changing it.</b> The card slot holds a picture somebody chose, so the first run reports
    /// a conflict and leaves the product exactly as it was — which is the right answer, because a card
    /// that moved while the hover did not would be the same contradiction in a new place.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_photograph_the_drop_withdraws_stops_being_the_hover_image()
    {
        await using var world = await CreateAsync();

        var wrong = TestImages.UniqueJpeg(1024, 1024);
        var right = TestImages.UniqueJpeg(1024, 1024);

        // The first delivery: the wrong garment on the card, the right one as the hover.
        await world.DropTheHomeAsync(Token);
        await world.Drop("products", "chino-shorts-1.jpg.jpg", wrong, Token);
        await world.Drop("products", "chino-shorts-2.jpg.jpg", right, Token);

        Assert.True((await world.ImportAsync(Token)).Succeeded);
        var withdrawn = await CardStateAsync(world, "chino-shorts");

        // The correction: the right photograph is -1 now and there is no -2 at all.
        File.Delete(Path.Combine(world.DropRoot, "products", "chino-shorts-2.jpg.jpg"));
        await world.Drop("products", "chino-shorts-1.jpg.jpg", right, Token);

        var reported = await world.ImportAsync(Token);
        Assert.True(reported.Succeeded, string.Join("; ", reported.Failures));

        Assert.Contains(
            reported.Entries,
            entry => entry.Outcome == ClientAssetOutcome.Conflict
                && entry.FileName == "chino-shorts-1.jpg.jpg");

        await using (var scope = world.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var untouched = await ProductAsync(dbContext, "chino-shorts");

            Assert.Equal(withdrawn.CardAssetId, untouched.CardImageAssetId);
            Assert.NotNull(untouched.HoverImageAssetId);
        }

        var applied = await world.ImportAsync(Token, replace: true);
        Assert.True(applied.Succeeded, string.Join("; ", applied.Failures));

        Assert.Contains(
            applied.Notes,
            note => note.Contains("chino-shorts", StringComparison.Ordinal)
                && note.Contains("no longer sends", StringComparison.Ordinal));

        await using (var scope = world.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var product = await ProductAsync(dbContext, "chino-shorts");

            var checksum = SHA256.HashData(right);
            var correct = await dbContext.MediaAssets.SingleAsync(asset => asset.Checksum == checksum, Token);

            Assert.Equal(correct.Id, product.CardImageAssetId);
            Assert.Null(product.HoverImageAssetId);

            // **And the photograph is still in the gallery.** It was sitting in the hover slot — one
            // asset is one row — so emptying that slot without moving it first deleted the client's
            // only picture out of the product's gallery and left a card above three placeholders.
            var rows = await dbContext.ProductMedia
                .Where(row => row.ProductId == product.Id && row.SortOrder < 0)
                .ToListAsync(Token);

            var kept = Assert.Single(rows);
            Assert.Equal(correct.Id, kept.MediaAssetId);
            Assert.Equal(-2, kept.SortOrder);

            Assert.Contains(
                await dbContext.MediaUsages
                    .Where(usage => usage.MediaAssetId == correct.Id)
                    .Select(usage => usage.UsageRole)
                    .ToListAsync(Token),
                role => role == MediaUsageRoles.Gallery);
        }
    }

    /// <summary>
    /// And the rule that keeps that from becoming a silent delete: a hover image the drop no longer
    /// sends is <b>reported</b>, not removed, while it might be somebody's own choice.
    /// <para>
    /// The importer cannot tell its own earlier work from an upload an admin made in ADM-11, which is
    /// the wall every slot in this class runs into. A note would be the wrong answer — the command would
    /// exit 0 over a product still showing a withdrawn garment — so it is a conflict, named after the
    /// file the drop does not contain, and <c>--replace</c> is what applies it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_hover_image_the_drop_no_longer_sends_is_reported_before_it_is_cleared()
    {
        await using var world = await CreateAsync();
        await world.DropTheHomeAsync(Token);
        await world.Drop("products", "chino-shorts-1.jpg.jpg", TestImages.UniqueJpeg(1024, 1024), Token);
        await world.Drop("products", "washed-linen-camp-shirt-1.jpg.jpg", TestImages.UniqueJpeg(1024, 1024), Token);

        Assert.True((await world.ImportAsync(Token)).Succeeded);

        // Somebody puts a picture in the hover slot that is not a placeholder and not from this drop.
        int chosen;
        await using (var scope = world.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var shirt = await ProductAsync(dbContext, "washed-linen-camp-shirt");
            var shorts = await ProductAsync(dbContext, "chino-shorts");

            chosen = shirt.CardImageAssetId!.Value;
            shorts.HoverImageAssetId = chosen;
            await dbContext.SaveChangesAsync(Token);
        }

        var reported = await world.ImportAsync(Token);
        Assert.True(reported.Succeeded, string.Join("; ", reported.Failures));

        var conflict = Assert.Single(
            reported.Entries,
            entry => entry.Outcome == ClientAssetOutcome.Conflict && entry.FileName == "chino-shorts-2");

        Assert.Contains("--replace", conflict.Detail, StringComparison.Ordinal);

        await using (var scope = world.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(chosen, (await ProductAsync(dbContext, "chino-shorts")).HoverImageAssetId);
        }

        Assert.True((await world.ImportAsync(Token, replace: true)).Succeeded);

        await using (var scope = world.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Null((await ProductAsync(dbContext, "chino-shorts")).HoverImageAssetId);
        }
    }

    /// <summary>
    /// #63: the HOME-06 picture lands under the words <b>seeded</b> for it — the importer writes no copy at
    /// all. The row said "Linen and denim" before the photograph arrived; the import attaches the picture,
    /// switches the row on, and switches the section on, and the words, the link and the picture agree
    /// because nothing in the importer gets a chance to disagree with the seeder.
    /// </summary>
    [Fact]
    public async Task The_activewear_picture_lands_under_the_words_seeded_for_it()
    {
        await using var world = await CreateAsync();

        await using (var scope = world.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var waiting = Assert.Single(await ItemsAsync(dbContext, HomeSectionType.ActivewearFeature));

            Assert.Equal("Linen and denim", waiting.Title);
            Assert.False(waiting.IsEnabled);
            Assert.Null(waiting.ImageAssetId);
        }

        await world.DropTheUsualAsync(TheDeliveredShapes, Token);
        var report = await world.ImportAsync(Token);
        Assert.True(report.Succeeded, string.Join("; ", report.Failures));

        await using (var scope = world.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var item = Assert.Single(await ItemsAsync(dbContext, HomeSectionType.ActivewearFeature));

            Assert.Equal("Linen and denim", item.Title);
            Assert.Equal("Breathable linen for the heat, washed indigo for everything else.", item.Subtitle);
            Assert.Equal("Shop linen and denim", item.ButtonLabel);
            Assert.Equal("/women?material=linen&material=linen-blend&material=denim", item.LinkUrl);
            Assert.NotNull(item.ImageAssetId);
            Assert.True(item.IsEnabled);
        }

        Assert.True(Assert.Single(await EnabledAsync(world, [HomeSectionType.ActivewearFeature])).IsEnabled);
    }

    /// <summary>
    /// #63: an import never touches words. A heading written in ADM-17 — even one that happens to say
    /// "Activewear" — and a button label of "Activewear" both survive a re-import exactly; the retraction of
    /// the old importer's words is the seeder's job now, done once (HomeContentSeedVersionTests).
    /// </summary>
    [Fact]
    public async Task An_import_never_rewrites_the_words_on_a_row()
    {
        await using var world = await CreateAsync();
        await world.DropTheUsualAsync(TheDeliveredShapes, Token);

        Assert.True((await world.ImportAsync(Token)).Succeeded);

        await using (var scope = world.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var item = Assert.Single(await ItemsAsync(dbContext, HomeSectionType.ActivewearFeature));

            item.Title = "Activewear";
            item.ButtonLabel = "Activewear";
            item.LinkUrl = "/women/activewear";
            await dbContext.SaveChangesAsync(Token);

            var tile = (await ItemsAsync(dbContext, HomeSectionType.TrendingItems))[0];
            tile.Title = "The hot-weather edit";
            await dbContext.SaveChangesAsync(Token);
        }

        var report = await world.ImportAsync(Token);
        Assert.True(report.Succeeded, string.Join("; ", report.Failures));

        await using (var scope = world.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var item = Assert.Single(await ItemsAsync(dbContext, HomeSectionType.ActivewearFeature));

            Assert.Equal("Activewear", item.Title);
            Assert.Equal("Activewear", item.ButtonLabel);
            Assert.Equal("/women/activewear", item.LinkUrl);
            Assert.Equal("The hot-weather edit", (await ItemsAsync(dbContext, HomeSectionType.TrendingItems))[0].Title);
        }
    }

    /// <summary>
    /// #63: the importer does not invent a row. A picture whose row was deleted in ADM-17 fails the run with
    /// the way to fix it named, and a trending picture beyond the rows that exist is reported as outside the
    /// contract rather than given a tile nobody wrote.
    /// </summary>
    [Fact]
    public async Task A_picture_with_no_row_waiting_for_it_is_not_given_one()
    {
        await using var world = await CreateAsync();

        await using (var scope = world.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await dbContext.HomeSectionItems
                .Where(item => item.HomeSection!.SectionType == HomeSectionType.CampaignBanner)
                .ExecuteDeleteAsync(Token);
            await dbContext.HomeSectionItems
                .Where(item => item.HomeSection!.SectionType == HomeSectionType.TrendingItems && item.SortOrder == 4)
                .ExecuteDeleteAsync(Token);
        }

        await world.DropTheUsualAsync(TheDeliveredShapes, Token);
        var report = await world.ImportAsync(Token);

        Assert.False(report.Succeeded);
        Assert.Contains(
            report.Failures,
            failure => failure.Contains("CampaignBanner", StringComparison.Ordinal)
                && failure.Contains("content seeder", StringComparison.Ordinal));

        await using (var scope = world.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // The run rolled back, and in any case no banner row was conjured up.
            Assert.Empty(await ItemsAsync(dbContext, HomeSectionType.CampaignBanner));
            Assert.Equal(3, (await ItemsAsync(dbContext, HomeSectionType.TrendingItems)).Count);
        }
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

        Assert.All(
            await EnabledAsync(world, ArtworkSections),
            state => Assert.False(state.IsEnabled, state.Type.ToString()));

        await world.DropTheUsualAsync(TheDeliveredShapes, Token);
        var report = await world.ImportAsync(Token);
        Assert.True(report.Succeeded, string.Join("; ", report.Failures));

        var after = await EnabledAsync(world, ArtworkSections);
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


    /// <summary>
    /// A run that fails leaves <b>nothing</b> behind — not a row, not a file, not a section switched on.
    /// <para>
    /// <see cref="IMediaService"/> saves on the same scoped context for every upload, so before the run
    /// became one transaction a failure in the hero left everything before it committed: measured once
    /// at 88 assets, 358 files, 70 gallery rows and four homepage sections live, under a report that
    /// said FAILED. The failure is put at the end on purpose — the hero is imported after every
    /// product — because a failure at the start proves nothing about rolling back.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_run_that_fails_at_the_end_rolls_back_everything_before_it()
    {
        await using var world = await CreateAsync();
        await world.DropTheUsualAsync(TheDeliveredShapes, Token);

        // Everything is fine except the hero, which is HEVC and which the pipeline will refuse.
        await world.Drop("home", "hero.mp4", TestVideos.Mp4(codec: "hvc1", payloadBytes: 2048), Token);

        var before = await CountsAsync(world);
        var filesBefore = world.PublicFiles();

        var report = await world.ImportAsync(Token);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Failures, failure => failure.Contains("hvc1", StringComparison.Ordinal));

        // Nothing survived it.
        Assert.Equal(before, await CountsAsync(world));
        Assert.Equal(filesBefore, world.PublicFiles());

        var sections = await EnabledAsync(world, ArtworkSections);
        Assert.All(sections, state => Assert.False(state.IsEnabled, state.Type.ToString()));

        // And the catalogue still points where it did: no product kept a card from the rolled-back run.
        await using var scope = world.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var shirt = await ProductAsync(dbContext, "washed-linen-camp-shirt");
        var card = await dbContext.MediaAssets.SingleAsync(asset => asset.Id == shirt.CardImageAssetId!.Value, Token);
        Assert.StartsWith("Placeholder image", card.AltText!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A corrected photograph <b>replaces</b> the one it supersedes; it is not added beside it.
    /// <para>
    /// Keying the gallery row on the asset meant a re-encoded file was a different asset and therefore
    /// a new row: two rows at sort order -2 both claiming to be the card, the superseded photograph
    /// still in the gallery, and its usage row keeping the delete guard shut for ever. The slot —
    /// product, role, sort order — is the key now.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_corrected_photograph_replaces_the_one_it_supersedes_rather_than_joining_it()
    {
        await using var world = await CreateAsync();
        await world.DropTheUsualAsync(TheDeliveredShapes, Token);
        Assert.True((await world.ImportAsync(Token)).Succeeded);

        var (originalCardId, rowsBefore) = await CardStateAsync(world, "washed-linen-camp-shirt");

        // The client sends a corrected card image under the same name: same slot, different bytes.
        await world.Drop("products", "washed-linen-camp-shirt-1.jpg.jpg", TestImages.UniqueJpeg(1024, 1024), Token);

        // Without --replace it is reported and left alone, because the importer cannot tell a corrected
        // photograph from an admin's own upload.
        var refused = await world.ImportAsync(Token);
        Assert.True(refused.Succeeded, string.Join("; ", refused.Failures));
        Assert.Contains(refused.Conflicts, entry => entry.FileName == "washed-linen-camp-shirt-1.jpg.jpg");

        var (unchangedCardId, rowsUnchanged) = await CardStateAsync(world, "washed-linen-camp-shirt");
        Assert.Equal(originalCardId, unchangedCardId);
        Assert.Equal(rowsBefore, rowsUnchanged);

        // With it, the slot takes the new picture and the old one leaves the gallery.
        var replaced = await world.ImportAsync(Token, replace: true);
        Assert.True(replaced.Succeeded, string.Join("; ", replaced.Failures));

        var (newCardId, rowsAfter) = await CardStateAsync(world, "washed-linen-camp-shirt");

        Assert.NotEqual(originalCardId, newCardId);
        Assert.Equal(1, rowsAfter);
        Assert.Equal(rowsBefore, rowsAfter);

        await using var scope = world.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The superseded photograph keeps no claim on the product, so the library can let go of it.
        var stale = await dbContext.MediaUsages
            .Where(usage => usage.MediaAssetId == originalCardId
                && usage.OwnerType == MediaUsageOwnerTypes.Product)
            .ToListAsync(Token);

        Assert.Empty(stale);
    }

    /// <summary>
    /// A picture an admin chose survives the import. A placeholder does not, because it was never a
    /// choice — it is what the seeder put there while there was nothing better.
    /// </summary>
    [Fact]
    public async Task An_admin_s_own_card_image_is_not_reverted_by_a_re_run()
    {
        await using var world = await CreateAsync();
        await world.DropTheUsualAsync(TheDeliveredShapes, Token);
        Assert.True((await world.ImportAsync(Token)).Succeeded);

        int chosenId;

        await using (var scope = world.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Something a person picked in ADM-18: another product's photograph, deliberately.
            var other = await ProductAsync(dbContext, "chino-shorts");
            chosenId = other.CardImageAssetId!.Value;

            var shirt = await ProductAsync(dbContext, "washed-linen-camp-shirt");
            shirt.CardImageAssetId = chosenId;
            await dbContext.SaveChangesAsync(Token);
        }

        Assert.True((await world.ImportAsync(Token)).Succeeded);

        await using (var scope = world.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var shirt = await ProductAsync(dbContext, "washed-linen-camp-shirt");

            Assert.Equal(chosenId, shirt.CardImageAssetId);
        }
    }

    /// <summary>
    /// A homepage asset is recorded against the <b>item</b> it fills, which is what doc 08 section 3.3
    /// enumerates — a section id would name "the homepage" for thirteen pictures, and the "used in…"
    /// panel could not tell an operator which tile to go and change.
    /// </summary>
    [Fact]
    public async Task Homepage_media_is_recorded_against_the_item_it_fills_with_a_label_a_person_reads()
    {
        await using var world = await CreateAsync();
        await world.DropTheUsualAsync(TheDeliveredShapes, Token);
        Assert.True((await world.ImportAsync(Token)).Succeeded);

        await using var scope = world.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mediaService = scope.ServiceProvider.GetRequiredService<IMediaService>();

        var tiles = await ItemsAsync(dbContext, HomeSectionType.SocialGallery);
        var ids = tiles
            .Select(tile => tile.Id.ToString(CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.Ordinal);

        var usages = await dbContext.MediaUsages
            .Where(usage => usage.OwnerType == MediaUsageOwnerTypes.HomeSectionItem)
            .ToListAsync(Token);

        Assert.NotEmpty(usages);
        Assert.Equal(6, usages.Count(usage => ids.Contains(usage.OwnerId)));

        // Nothing is filed under the section any more.
        var ownerTypes = await dbContext.MediaUsages
            .Select(usage => usage.OwnerType)
            .Distinct()
            .ToListAsync(Token);

        Assert.DoesNotContain("HomeSection", ownerTypes);

        // And the library's "used in…" panel names the tile rather than printing an id.
        var tile = tiles[0];
        var asset = await dbContext.MediaAssets.SingleAsync(candidate => candidate.Id == tile.ImageAssetId!.Value, Token);
        var detail = await mediaService.GetAsync(asset.PublicId, Token);

        var usage = Assert.Single(
            detail!.Usages,
            candidate => candidate.OwnerType == MediaUsageOwnerTypes.HomeSectionItem);

        Assert.Equal(tile.Title, usage.OwnerLabel);
    }

    /// <summary>
    /// A picture that does not fit the slot its filename claims keeps the section switched off, and the
    /// hold is keyed on the bytes so it releases itself when a replacement arrives.
    /// </summary>
    [Fact]
    public async Task A_held_back_picture_keeps_its_section_off_until_different_bytes_arrive()
    {
        var banner = TestImages.UniqueJpeg(1376, 768);
        var checksum = Convert.ToHexStringLower(SHA256.HashData(banner));

        var holds = new ClientAssetHolds(new Dictionary<string, HeldBackArtwork>(StringComparer.OrdinalIgnoreCase)
        {
            [checksum] = new(HomeSectionType.CampaignBanner, "this one is a flat-lay of the wrong thing"),
        });

        await using var world = await ClientAssetWorld.CreateAsync(
            sqlServer.ConnectionString, withPlaceholderFiles: false, Token, holds);

        await world.DropTheUsualAsync(TheDeliveredShapes, Token);
        await world.Drop("home", "campaign.jpg", banner, Token);

        var first = await world.ImportAsync(Token);
        Assert.True(first.Succeeded, string.Join("; ", first.Failures));

        // Imported and attached, so the client can look at it — and not switched on.
        var held = Assert.Single(await EnabledAsync(world, [HomeSectionType.CampaignBanner]));
        Assert.False(held.IsEnabled);
        Assert.Contains(first.Notes, note => note.Contains("flat-lay of the wrong thing", StringComparison.Ordinal));

        await using (var scope = world.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var item = Assert.Single(await ItemsAsync(dbContext, HomeSectionType.CampaignBanner));
            Assert.NotNull(item.ImageAssetId);
        }

        // The others were not held, so they are on: a hold is about one picture, not a blanket off.
        var others = await EnabledAsync(
            world,
            [HomeSectionType.HeroVideo, HomeSectionType.TrendingItems, HomeSectionType.SocialGallery]);
        Assert.All(others, state => Assert.True(state.IsEnabled, state.Type.ToString()));

        // A replacement has different bytes, so the hold simply does not apply to it.
        await world.Drop("home", "campaign.jpg", TestImages.UniqueJpeg(1376, 768), Token);
        var second = await world.ImportAsync(Token, replace: true);

        Assert.True(second.Succeeded, string.Join("; ", second.Failures));
        Assert.True(Assert.Single(await EnabledAsync(world, [HomeSectionType.CampaignBanner])).IsEnabled);
    }

    /// <summary>
    /// The repair that fixes a truncated filename is only safe while no two slugs differ by a short
    /// prefix. Nothing re-checks that when the client adds a product, so this does — against the real
    /// catalogue, not against a list written down beside it.
    /// </summary>
    [Fact]
    public async Task No_two_product_slugs_differ_only_by_a_prefix_the_repair_could_reach_across()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var slugs = await dbContext.Products.Select(product => product.Slug).ToListAsync(Token);

        Assert.NotEmpty(slugs);
        Assert.Empty(ClientAssetNaming.AmbiguousPrefixPairs(slugs));

        // And they are unique ignoring case, which is what lets the importer match a filename's
        // capitalisation without creating an ambiguity.
        Assert.Equal(slugs.Count, slugs.ToHashSet(StringComparer.OrdinalIgnoreCase).Count);
    }


    /// <summary>
    /// Two files carrying the same photograph do not crash the import.
    /// <para>
    /// <c>catalog.ProductMedia</c>'s unique index is <c>(ProductId, MediaAssetId, ProductColourId)</c>.
    /// The library deduplicates by checksum, so two byte-identical files are <b>one</b> asset — and a
    /// slot key of (product, role, sort order) happily asked for it to be inserted twice. SQL Server
    /// refused the second row and the import died: <c>Cannot insert duplicate key row … The duplicate
    /// key value is (37, 198, &lt;NULL&gt;)</c>, on the plain documented command with no flags.
    /// </para>
    /// <para>
    /// One photograph fills one gallery row, both pointers name it, and the report says which file it
    /// is the same as — because a drop that sends one picture twice is usually a mistake somebody wants
    /// to know about.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_files_carrying_the_same_photograph_fill_one_gallery_row_and_say_so()
    {
        await using var world = await CreateAsync();

        var picture = TestImages.UniqueJpeg(1024, 1024);
        await world.Drop("products", "chino-shorts-1.jpg.jpg", picture, Token);
        await world.Drop("products", "chino-shorts-2.jpg.jpg", picture, Token);
        await world.DropTheHomeAsync(Token);

        var report = await world.ImportAsync(Token);

        Assert.True(report.Succeeded, string.Join("; ", report.Failures));

        var same = Assert.Single(report.SamePictures);
        Assert.Equal("chino-shorts-2.jpg.jpg", same.FileName);
        Assert.Contains("chino-shorts-1.jpg.jpg", same.Detail, StringComparison.Ordinal);

        await using var scope = world.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var product = await ProductAsync(dbContext, "chino-shorts");

        // One row, and the card and hover both name the one asset the two files became.
        var rows = await dbContext.ProductMedia
            .Where(row => row.ProductId == product.Id && row.SortOrder < 0)
            .ToListAsync(Token);

        var row = Assert.Single(rows);
        Assert.Equal(product.CardImageAssetId, row.MediaAssetId);
        Assert.Equal(product.CardImageAssetId, product.HoverImageAssetId);

        // And a second run still writes nothing: the outcome is stable, not alternating.
        var again = await world.ImportAsync(Token);
        Assert.True(again.Succeeded, string.Join("; ", again.Failures));
        Assert.Equal(0, again.Count(ClientAssetOutcome.Imported));
        Assert.Single(again.SamePictures);
    }

    /// <summary>
    /// The same input under <c>--replace</c>, where a drop sends one photograph for two roles. It used
    /// to hit the same index from the replace branch instead of the insert branch.
    /// </summary>
    [Fact]
    public async Task One_photograph_for_two_roles_survives_replace_as_well()
    {
        await using var world = await CreateAsync();

        await world.Drop("products", "chino-shorts-1.jpg.jpg", TestImages.UniqueJpeg(1024, 1024), Token);
        await world.Drop("products", "chino-shorts-2.jpg.jpg", TestImages.UniqueJpeg(1024, 1024), Token);
        await world.DropTheHomeAsync(Token);
        Assert.True((await world.ImportAsync(Token)).Succeeded);

        // Now the client re-sends one picture under both names.
        var corrected = TestImages.UniqueJpeg(1024, 1024);
        await world.Drop("products", "chino-shorts-1.jpg.jpg", corrected, Token);
        await world.Drop("products", "chino-shorts-2.jpg.jpg", corrected, Token);

        var report = await world.ImportAsync(Token, replace: true);

        Assert.True(report.Succeeded, string.Join("; ", report.Failures));
        Assert.Single(report.SamePictures);

        await using var scope = world.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var product = await ProductAsync(dbContext, "chino-shorts");

        var rows = await dbContext.ProductMedia
            .Where(row => row.ProductId == product.Id && row.SortOrder < 0)
            .ToListAsync(Token);

        Assert.Single(rows);
    }

    /// <summary>
    /// A dry run tells the operator what the real run will do.
    /// <para>
    /// It used to report the duplicate-photograph drop above as a clean green plan, exit 0, and then the
    /// real run crashed on it — so somebody who checked first was told it was fine. The plan resolves
    /// identity the same way the pipeline does, by SHA-256 over the same bytes, so the two agree by
    /// construction rather than by coincidence.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_dry_run_predicts_what_the_real_run_does()
    {
        await using var world = await CreateAsync();

        var picture = TestImages.UniqueJpeg(1024, 1024);
        await world.Drop("products", "chino-shorts-1.jpg.jpg", picture, Token);
        await world.Drop("products", "chino-shorts-2.jpg.jpg", picture, Token);
        await world.DropTheHomeAsync(Token);

        var planned = await world.ImportAsync(Token, dryRun: true);
        var real = await world.ImportAsync(Token);

        Assert.True(planned.Succeeded, string.Join("; ", planned.Failures));
        Assert.True(real.Succeeded, string.Join("; ", real.Failures));

        // The plan named the same file as the duplicate, not just the same count.
        Assert.Equal(
            planned.SamePictures.Select(entry => entry.FileName).Order(StringComparer.Ordinal),
            real.SamePictures.Select(entry => entry.FileName).Order(StringComparer.Ordinal));

        Assert.Equal(
            planned.Count(ClientAssetOutcome.SamePicture),
            real.Count(ClientAssetOutcome.SamePicture));
    }

    /// <summary>
    /// A dry run that finds a slot already taken says so, instead of promising the file will land.
    /// </summary>
    [Fact]
    public async Task A_dry_run_predicts_a_conflict_the_real_run_would_report()
    {
        await using var world = await CreateAsync();
        await world.DropTheUsualAsync(TheDeliveredShapes, Token);
        Assert.True((await world.ImportAsync(Token)).Succeeded);

        await world.Drop("products", "washed-linen-camp-shirt-1.jpg.jpg", TestImages.UniqueJpeg(1024, 1024), Token);

        var planned = await world.ImportAsync(Token, dryRun: true);
        var real = await world.ImportAsync(Token);

        Assert.Contains(planned.Conflicts, entry => entry.FileName == "washed-linen-camp-shirt-1.jpg.jpg");
        Assert.Equal(
            planned.Conflicts.Select(entry => entry.FileName).Order(StringComparer.Ordinal),
            real.Conflicts.Select(entry => entry.FileName).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A dry run writes nothing at all — not a row, not a file — even now that it walks the same
    /// placement logic as the real run against the same tracked entities.
    /// </summary>
    [Fact]
    public async Task A_dry_run_against_the_real_rows_still_writes_nothing()
    {
        await using var world = await CreateAsync();
        await world.DropTheUsualAsync(TheDeliveredShapes, Token);

        var before = await CountsAsync(world);
        var filesBefore = world.PublicFiles();

        var report = await world.ImportAsync(Token, dryRun: true);

        Assert.True(report.Succeeded, string.Join("; ", report.Failures));
        Assert.Equal(before, await CountsAsync(world));
        Assert.Equal(filesBefore, world.PublicFiles());

        Assert.All(
            await EnabledAsync(world, ArtworkSections),
            state => Assert.False(state.IsEnabled, state.Type.ToString()));
    }

    /// <summary>
    /// A rolled-back run leaves the <b>hero poster's</b> files behind too.
    /// <para>
    /// <c>UploadVideoAsync</c> stores the poster as a second asset that never surfaces as its own
    /// outcome, so a discard list built from the outcomes could not see it: a failing run left the
    /// database perfect and the volume five files heavier — one original and four renditions, with no
    /// row anywhere. The list is a diff of the asset table now, which cannot miss an asset however it
    /// came to exist.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_rollback_removes_the_hero_posters_files_as_well_as_the_videos()
    {
        await using var world = await CreateAsync();
        await world.DropTheUsualAsync(TheDeliveredShapes, Token);

        // The hero itself must SUCCEED — a refused video has its poster rolled back by the pipeline, so
        // it never reaches the state this is about. The run fails afterwards instead, on a social tile
        // the pipeline will not take, leaving the video and its poster both created and both orphaned.
        await world.Drop("social", "social-1.jpg.jpg", "#!/bin/sh echo not an image"u8.ToArray(), Token);

        var filesBefore = world.PublicFiles();
        var report = await world.ImportAsync(Token);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Failures, failure => failure.Contains("social-1", StringComparison.Ordinal));

        // The hero really was stored before the failure, so this is not a vacuous assertion.
        Assert.Contains(
            report.Entries,
            entry => entry.FileName == "hero.mp4" && entry.Outcome == ClientAssetOutcome.Imported);

        Assert.Equal(filesBefore, world.PublicFiles());
    }


    /// <summary>
    /// Ctrl+C rolls the run back and takes its files with it.
    /// <para>
    /// The catch filter used to exclude <see cref="OperationCanceledException"/>, so the rows went (the
    /// transaction disposes) and every file written before the interrupt stayed — a few hundred of them
    /// — and the exception then left the command as a stack trace with an exit code outside the
    /// documented set.
    /// </para>
    /// <para>
    /// The interrupt is fired by the storage adapter at a known file rather than on a timer: a test that
    /// races the clock passes or fails on how fast the machine is, which is not a property of the code.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_interrupted_run_rolls_back_and_removes_the_files_it_had_written()
    {
        using var interrupt = new CancellationTokenSource();

        await using var world = await ClientAssetWorld.CreateAsync(
            sqlServer.ConnectionString,
            withPlaceholderFiles: false,
            Token,
            holds: null,
            decorateStorage: inner => new OnNthWrite(inner, writes: 12, () => interrupt.CancelAsync()));

        await world.DropTheUsualAsync(TheDeliveredShapes, Token);

        var before = await CountsAsync(world);
        var filesBefore = world.PublicFiles();

        var report = await world.ImportAsync(interrupt.Token);

        Assert.True(report.Cancelled);
        Assert.False(report.Succeeded);
        Assert.Contains(report.Failures, failure => failure.Contains("cancelled", StringComparison.OrdinalIgnoreCase));

        // Not one row survives.
        Assert.Equal(before, await CountsAsync(world));

        // Every *completed* upload's files are gone. The one that was still in flight when the token
        // was cancelled may leave its own behind, and deliberately does: the only way to find it would
        // be to sweep the asset table for Processing rows, and the admin upload path has no transaction
        // — it commits a Processing row and flips it to Ready afterwards — so a concurrent upload would
        // be swept up with it. A missed file is litter with no row pointing at it; a deleted file is
        // somebody else's data. At most one asset's worth, and this asserts that bound rather than
        // waving at it.
        var leftover = world.PublicFiles().Except(filesBefore, StringComparer.Ordinal).ToList();
        // public/originals/<id>.jpg and public/renditions/<id>/card.webp are the same asset.
        var strandedAssets = leftover
            .Select(key => Path.GetFileNameWithoutExtension(key.Split('/')[2]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(
            strandedAssets.Count <= 1,
            $"an interrupt should strand at most the one upload in flight, not {strandedAssets.Count}: "
                + string.Join(", ", leftover));

        Assert.Contains(
            report.Notes,
            note => note.Contains("part way through", StringComparison.Ordinal));

        var sections = await EnabledAsync(world, ArtworkSections);
        Assert.All(sections, state => Assert.False(state.IsEnabled, state.Type.ToString()));
    }


    /// <summary>
    /// A rolled-back run does not touch a file somebody else wrote while it was running.
    /// <para>
    /// The discard list used to be "every asset row now, minus a snapshot from the start of the
    /// attempt". That is right about this attempt's own uncommitted inserts and wrong about everybody
    /// else's: a row another connection commits mid-run is committed and was not in the snapshot, so a
    /// failing import deleted a stranger's file. <b>The end state was the worst available</b> — the row
    /// survives, the bytes do not, there is nothing to restore from and nothing reports it; an admin
    /// finds a broken image days later next to an import that said FAILED for an unrelated reason.
    /// </para>
    /// <para>
    /// A missed file is litter. A deleted file is data loss. The list is built from what the pipeline
    /// told this attempt it created, so nothing else can enter it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_rollback_leaves_alone_a_file_another_connection_wrote_while_it_ran()
    {
        var strangerId = Guid.NewGuid();
        var strangerKey = MediaStorageKey.ForOriginal(MediaVisibility.Public, strangerId, ".jpg");
        var strangerBytes = TestImages.UniqueJpeg(64, 64);

        ClientAssetWorld? world = null;

        // Exactly what an ADM-18 upload does from another connection: the bytes on the volume, then a
        // committed row naming them. Fired part way through the import rather than on a timer.
        async Task Stranger()
        {
            var path = Path.Combine(world!.PublicRoot, strangerKey.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, strangerBytes, Token);

            await world.OnAnotherConnectionAsync(async dbContext =>
            {
                dbContext.MediaAssets.Add(new MediaAsset
                {
                    PublicId = strangerId,
                    MediaType = MediaAssetType.Image,
                    Visibility = MediaVisibility.Public,
                    Status = MediaAssetStatus.Ready,
                    OriginalFileName = "somebody-elses-upload.jpg",
                    StorageKey = strangerKey,
                    ContentType = "image/jpeg",
                    SizeBytes = strangerBytes.LongLength,
                    Checksum = SHA256.HashData(strangerBytes),
                    AltText = "An upload that has nothing to do with this import",
                });

                await dbContext.SaveChangesAsync(Token);
            });
        }

        world = await ClientAssetWorld.CreateAsync(
            sqlServer.ConnectionString,
            withPlaceholderFiles: false,
            Token,
            holds: null,
            decorateStorage: inner => new OnNthWrite(inner, writes: 12, Stranger));

        await using (world)
        {
            await world.DropTheUsualAsync(TheDeliveredShapes, Token);

            // The run fails late, after the stranger has arrived.
            await world.Drop("social", "social-1.jpg.jpg", "#!/bin/sh echo not an image"u8.ToArray(), Token);

            var report = await world.ImportAsync(Token);
            Assert.False(report.Succeeded);

            var path = Path.Combine(world.PublicRoot, strangerKey.Replace('/', Path.DirectorySeparatorChar));

            // Their bytes and their row both survive, and they still agree with each other.
            Assert.True(File.Exists(path), "the other connection's file was deleted by this import's rollback");
            Assert.Equal(strangerBytes, await File.ReadAllBytesAsync(path, Token));

            await using var scope = world.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await dbContext.MediaAssets.AnyAsync(asset => asset.PublicId == strangerId, Token));

            // And the import still cleaned up after itself: nothing of its own is left behind.
            var mine = world.PublicFiles().Where(key => !key.Equals(strangerKey, StringComparison.Ordinal)).ToList();
            Assert.Empty(mine);
        }
    }

    /// <summary>Storage that runs something once a given number of files have been written.</summary>
    private sealed class OnNthWrite(IFileStorage inner, int writes, Func<Task> action) : IFileStorage
    {
        private int _written;

        public bool IsConfigured => inner.IsConfigured;

        public async Task<StoredFileInfo> WriteAsync(
            string storageKey,
            Stream content,
            CancellationToken cancellationToken)
        {
            var result = await inner.WriteAsync(storageKey, content, cancellationToken);

            if (Interlocked.Increment(ref _written) == writes)
            {
                await action();
            }

            return result;
        }

        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(storageKey, cancellationToken);

        public Task<StoredFileInfo?> GetInfoAsync(string storageKey, CancellationToken cancellationToken) =>
            inner.GetInfoAsync(storageKey, cancellationToken);

        public Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken) =>
            inner.DeleteAsync(storageKey, cancellationToken);

        public Task<int> DeletePrefixAsync(string keyPrefix, CancellationToken cancellationToken) =>
            inner.DeletePrefixAsync(keyPrefix, cancellationToken);
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

    /// <summary>The product's card asset, and how many rows sit in the card slot.</summary>
    private static async Task<(int CardAssetId, int Rows)> CardStateAsync(ClientAssetWorld world, string slug)
    {
        await using var scope = world.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var product = await ProductAsync(dbContext, slug);
        var rows = await dbContext.ProductMedia
            .CountAsync(row => row.ProductId == product.Id && row.SortOrder == -2, Token);

        return (product.CardImageAssetId!.Value, rows);
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
