using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Media.Abstractions;
using SpencerDenim.Application.Media.ClientAssets;
using SpencerDenim.Application.Media.Contracts;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.Tests.MediaFixtures;

namespace SpencerDenim.IntegrationTests.Media;

/// <summary>
/// The kids stand-in photographs: a kids product borrowing the delivered photograph of an adult
/// garment, until the real ones arrive (owner decision, 2026-10-02; `tools/kids-stand-in-photos.csv`).
/// <para>
/// <b>What can go wrong here, and why each one is a test.</b> The library deduplicates by SHA-256 under
/// a real unique index, so the same bytes under a second filename <i>cannot</i> become a second asset.
/// The right outcome is therefore one asset with two usages — and the three ways that goes wrong are
/// all silent. The second placement could <i>move</i> the photograph instead of sharing it, which would
/// leave the adult product showing a placeholder and nobody looking at the Kids page would notice. The
/// shared asset's alt text could end up naming the kids garment, which is a false sentence read out to
/// a screen-reader user on <i>both</i> products. And the run could stop being idempotent, which is the
/// one property that makes it safe to leave in a setup script.
/// </para>
/// <para>
/// The bytes here are fixtures, not the client's photographs. What is under test is the sharing, not
/// the pictures: whether a stand-in is a <i>good</i> stand-in is a judgement, and it lives in the CSV
/// with its reasons, checked by <c>KidsStandInMappingTests</c>.
/// </para>
/// </summary>
public sealed class KidsStandInPhotoTests(SqlServerDatabaseFixture sqlServer)
{
    /// <summary>
    /// One pair from the real mapping, and the one that matters most: <c>court-canvas-sneakers</c> is the
    /// exact match, and its hover image is the file that arrived with a character missing off the front
    /// (<c>ourt-canvas-sneakers-2.jpg.jpg</c>) — so this pair exercises the borrowing and the filename
    /// repair at once.
    /// </summary>
    private const string Donor = "court-canvas-sneakers";

    private const string Kid = "kids-canvas-sneakers";

    /// <summary>
    /// The delivery: the donor's two photographs, under the two shapes they really arrived in.
    /// </summary>
    private static readonly string[] DeliveredDonorFiles =
    [
        "court-canvas-sneakers-1.jpg.jpg",
        "ourt-canvas-sneakers-2.jpg.jpg",
    ];

    /// <summary>
    /// The whole point: one asset, two products. The kids product's card is the <b>same asset row</b> as
    /// the donor's, not a copy of it — which is what the checksum index guarantees and what two usage
    /// rows record.
    /// </summary>
    [Fact]
    public async Task A_kids_product_and_its_donor_share_one_asset_rather_than_duplicating_it()
    {
        await using var world = await CreateAsync();
        var assetsAfterDelivery = await ImportTheDeliveryAsync(world);

        await StandInAsync(world);
        var report = await world.ImportAsync(Token, root: world.StandInRoot);
        Assert.True(report.Succeeded, string.Join("; ", report.Failures));

        await using var scope = world.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var donor = await ProductAsync(dbContext, Donor);
        var kid = await ProductAsync(dbContext, Kid);

        Assert.NotNull(kid.CardImageAssetId);
        Assert.NotNull(kid.HoverImageAssetId);
        Assert.Equal(donor.CardImageAssetId, kid.CardImageAssetId);
        Assert.Equal(donor.HoverImageAssetId, kid.HoverImageAssetId);

        // Not one new asset and not one new file: the bytes were already in the library.
        Assert.Equal(assetsAfterDelivery, await dbContext.MediaAssets.CountAsync(Token));

        // Two owners of the one asset, which is what doc 08 §3.3's UX_MediaUsages_Asset_Owner_Role is
        // for, and what keeps the delete guard shut while either product still shows it.
        var owners = await dbContext.MediaUsages
            .Where(usage => usage.MediaAssetId == kid.CardImageAssetId!.Value && usage.OwnerType == "Product")
            .Select(usage => usage.OwnerId)
            .ToListAsync(Token);

        Assert.Equal(2, owners.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(donor.Id.ToString(CultureInfo.InvariantCulture), owners);
        Assert.Contains(kid.Id.ToString(CultureInfo.InvariantCulture), owners);

        // And the report says the sentence the library kept, not the one the file offered. It used to
        // print the offered one unconditionally, so the operator was told the kids card now read "Kids
        // Canvas Sneakers, front view" when nothing of the sort had been written — a report that
        // contradicts the database is worse than no report.
        var entry = report.Entries.Single(line => line.FileName == $"{Kid}-1.jpg");

        Assert.Contains("Court Canvas Sneakers, front view", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("kept rather than overwritten", entry.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The defect this is really guarding: a <b>swapped</b> photograph rather than a shared one. Sharing
    /// is the point; the donor losing its own card to the product that borrowed it would be a regression
    /// on a page the client has already seen signed off.
    /// </summary>
    [Fact]
    public async Task The_donor_keeps_its_own_photographs_and_its_own_alt_text()
    {
        await using var world = await CreateAsync();
        await ImportTheDeliveryAsync(world);

        var before = await CardAndHoverAsync(world, Donor);

        await StandInAsync(world);
        Assert.True((await world.ImportAsync(Token, root: world.StandInRoot)).Succeeded);

        var after = await CardAndHoverAsync(world, Donor);

        Assert.Equal(before, after);

        // And the sentence on the shared asset still describes the garment that is in the picture. The
        // alt text lives on the asset, so this is the *only* sentence either product can read out:
        // writing "Kids Canvas Sneakers" here would be a false statement on the men's product too.
        await using var scope = world.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var kid = await ProductAsync(dbContext, Kid);

        var card = await dbContext.MediaAssets.SingleAsync(asset => asset.Id == kid.CardImageAssetId!.Value, Token);
        var hover = await dbContext.MediaAssets.SingleAsync(asset => asset.Id == kid.HoverImageAssetId!.Value, Token);

        Assert.Equal("Court Canvas Sneakers, front view", card.AltText);
        Assert.Equal("Court Canvas Sneakers, alternate view", hover.AltText);

        // No per-product override either. **Not** because the listing would disagree — it cannot: a
        // product card's image is decorative (`alt=""` under `aria-hidden="true"`, because it repeats
        // the link on the product name), so no alt text of any kind reaches the listing. The reason is
        // that `PlaceProductMedia` does not clear an override when a photograph replaces the one before
        // it, so an override written for a stand-in would outlive it and go on describing the wrong
        // garment after the real photographs had landed.
        var overrides = await dbContext.ProductMedia
            .Where(row => row.ProductId == kid.Id && row.SortOrder < 0)
            .Select(row => row.AltTextOverride)
            .ToListAsync(Token);

        Assert.NotEmpty(overrides);
        Assert.All(overrides, text => Assert.Null(text));
    }

    /// <summary>
    /// Running the stand-in import again changes nothing — the property that makes it safe to leave in
    /// a setup script somebody runs twice.
    /// </summary>
    [Fact]
    public async Task A_second_stand_in_run_changes_nothing()
    {
        await using var world = await CreateAsync();
        await ImportTheDeliveryAsync(world);
        await StandInAsync(world);

        Assert.True((await world.ImportAsync(Token, root: world.StandInRoot)).Succeeded);

        var files = world.PublicFiles();
        var before = await CountsAsync(world);
        var card = await CardAndHoverAsync(world, Kid);

        var second = await world.ImportAsync(Token, root: world.StandInRoot);

        Assert.True(second.Succeeded, string.Join("; ", second.Failures));
        Assert.Equal(0, second.Count(ClientAssetOutcome.Imported));
        Assert.Equal(0, second.Count(ClientAssetOutcome.Replaced));
        Assert.Equal(before, await CountsAsync(world));
        Assert.Equal(card, await CardAndHoverAsync(world, Kid));
        Assert.Equal(files, world.PublicFiles());
    }

    /// <summary>
    /// The reversal, which is the whole reason this is done through the importer rather than by hand:
    /// the real kids photographs arrive in the client's own drop and <c>--replace</c> puts them in.
    /// <para>
    /// Without <c>--replace</c> the importer refuses and says so (exit 3), because a stand-in is a
    /// picture somebody chose and the importer cannot tell it from an admin's own upload. That refusal
    /// is asserted here too: a reversal that silently did nothing would be worse than one that stops.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_real_photographs_replace_a_stand_in_and_leave_the_donor_alone()
    {
        await using var world = await CreateAsync();
        await ImportTheDeliveryAsync(world);
        await StandInAsync(world);
        Assert.True((await world.ImportAsync(Token, root: world.StandInRoot)).Succeeded);

        var donorBefore = await CardAndHoverAsync(world, Donor);
        var borrowed = await CardAndHoverAsync(world, Kid);

        // The client finally sends them, into the delivery where every other photograph lives.
        await world.Drop("products", $"{Kid}-1.jpg", TestImages.UniqueJpeg(1024, 1024), Token);
        await world.Drop("products", $"{Kid}-2.jpg", TestImages.UniqueJpeg(1024, 1024), Token);

        var beforeRefusal = await CountsAsync(world);
        var filesBeforeRefusal = world.PublicFiles().Count;

        var refused = await world.ImportAsync(Token);

        Assert.True(refused.Succeeded, string.Join("; ", refused.Failures));
        Assert.NotEmpty(refused.Conflicts);
        Assert.Equal(borrowed, await CardAndHoverAsync(world, Kid));

        // **A conflict is not a no-op for the library, only for the slots.** The bytes went through the
        // pipeline before the slot was consulted, so the photographs and their renditions are stored and
        // referenced by nothing. Litter rather than loss, and nothing purges it on its own — asserted
        // here because the README used to say the run "changes nothing", which was true of what a
        // shopper sees and false of the library.
        var afterRefusal = await CountsAsync(world);

        Assert.True(
            afterRefusal.Assets > beforeRefusal.Assets,
            "A refused run was expected to have stored the photographs it could not apply.");
        Assert.Equal(beforeRefusal.Media, afterRefusal.Media);
        Assert.True(world.PublicFiles().Count > filesBeforeRefusal);

        var applied = await world.ImportAsync(Token, replace: true);

        Assert.True(applied.Succeeded, string.Join("; ", applied.Failures));

        var own = await CardAndHoverAsync(world, Kid);

        Assert.NotEqual(borrowed, own);
        Assert.Equal(donorBefore, await CardAndHoverAsync(world, Donor));

        await using var scope = world.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var kid = await ProductAsync(dbContext, Kid);
        var card = await dbContext.MediaAssets.SingleAsync(asset => asset.Id == kid.CardImageAssetId!.Value, Token);

        // Its own photograph, and now its own honest sentence.
        Assert.Equal("Kids Canvas Sneakers, front view", card.AltText);

        // The borrowed photograph is gone from the kids gallery rather than left behind beside the real
        // one. One asset is one row per product, and a superseded row pointing at a stand-in nobody
        // asked for any more is exactly the archaeology this task exists to avoid.
        var galleryAssets = await dbContext.ProductMedia
            .Where(row => row.ProductId == kid.Id)
            .Select(row => row.MediaAssetId)
            .ToListAsync(Token);

        Assert.DoesNotContain(borrowed.Card, galleryAssets);
    }

    /// <summary>
    /// <b>The first dry run cannot name the sentence, and must not pretend to.</b>
    /// <para>
    /// The documented order is "the delivery, then the stand-ins", and <c>-DryRun</c> passes
    /// <c>--dry-run</c> to both — so step 1 commits nothing, the donor's bytes are still absent when
    /// step 2 plans, and the only sentence available is the one the kids file offered. On a freshly
    /// seeded database, which is precisely where an operator checking before writing starts, every line
    /// used to read <c>alt text "Girls Cotton Sundress, front view"</c> as a statement of fact: the one
    /// false sentence this whole arrangement exists to avoid.
    /// </para>
    /// <para>
    /// A plan that cannot know has to say it cannot know. This is the same rule the command already
    /// states about refusals — a dry run's "0 refused" means NOT CHECKED, not none.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_dry_run_against_a_library_without_the_bytes_marks_the_alt_text_provisional()
    {
        await using var world = await CreateAsync();

        // No delivery imported: exactly what a first `-DryRun` leaves behind, because step 1 is a dry
        // run too and commits nothing.
        await StandInFromFreshBytesAsync(world);

        var plan = await world.ImportAsync(Token, dryRun: true, root: world.StandInRoot);
        Assert.True(plan.Succeeded, string.Join("; ", plan.Failures));

        var entry = plan.Entries.Single(line => line.FileName == $"{Kid}-1.jpg");

        Assert.Contains("PROVISIONAL", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("were not in the library", entry.Detail, StringComparison.Ordinal);

        // It may quote the sentence it would write, but never as the sentence the shop will carry.
        Assert.DoesNotContain(
            "Kids Canvas Sneakers, front view\" — the sentence already",
            entry.Detail,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// And once the delivery really is in, the same plan names the donor's sentence with no hedge — so
    /// the warning is specific to the case that cannot be known, not noise on every line.
    /// </summary>
    [Fact]
    public async Task A_dry_run_after_the_delivery_names_the_donors_sentence_without_hedging()
    {
        await using var world = await CreateAsync();
        await ImportTheDeliveryAsync(world);
        await StandInAsync(world);

        var plan = await world.ImportAsync(Token, dryRun: true, root: world.StandInRoot);
        Assert.True(plan.Succeeded, string.Join("; ", plan.Failures));

        var entry = plan.Entries.Single(line => line.FileName == $"{Kid}-1.jpg");

        Assert.Contains("Court Canvas Sneakers, front view", entry.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("PROVISIONAL", entry.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>"The bytes are not in the library" and "they are, on an asset with no sentence" are different
    /// facts, and a null <c>AltText</c> is both.</b> Deciding on the sentence alone made the second case
    /// report the first one's message — telling the operator bytes were absent when they were present.
    /// <para>
    /// Unreachable on today's data: nothing in the library carries a null sentence. It becomes reachable
    /// the first time an admin uploads without alt text, because <c>IMediaService</c> stores
    /// <c>Trim(altText)</c> and <c>Trim</c> yields null for whitespace — which is how this test
    /// manufactures the case.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Bytes_in_the_library_with_no_sentence_are_not_reported_as_bytes_that_are_absent()
    {
        await using var world = await CreateAsync();
        await StandInFromFreshBytesAsync(world);

        var bytes = await File.ReadAllBytesAsync(
            Path.Combine(world.StandInRoot, "products", $"{Kid}-1.jpg"),
            Token);

        // Put exactly those bytes in the library with no alt text at all, the way an admin upload with
        // an empty field would.
        await using (var scope = world.CreateScope())
        {
            var media = scope.ServiceProvider.GetRequiredService<IMediaService>();
            using var content = new MemoryStream(bytes);

            var outcome = await media.UploadImageAsync(
                new UploadImageRequest(
                    new MediaUploadFile(content, "an-admin-upload.jpg"),
                    MediaVisibility.Public,
                    "   ",
                    "An admin upload",
                    UploadedByUserId: null),
                Token);

            Assert.IsType<MediaUploadOutcome.Created>(outcome);
        }

        var plan = await world.ImportAsync(Token, dryRun: true, root: world.StandInRoot);
        var entry = plan.Entries.Single(line => line.FileName == $"{Kid}-1.jpg");

        Assert.Contains("already in the library", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("carries", entry.Detail, StringComparison.Ordinal);

        // And emphatically not the other message, which would be a false statement about the library.
        Assert.DoesNotContain("were not in the library", entry.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("PROVISIONAL", entry.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refused file is not a file whose alt text was "kept rather than overwritten" — nothing was
    /// kept, because nothing was applied. The sharing clause was built before the outcome was consulted
    /// and appended to conflicts too, so the operator was told a sentence had been preserved on a slot
    /// the run had just declined to touch.
    /// <para>
    /// The scenario is the one the README warns about and the likeliest way anybody meets it: the real
    /// kids photographs have landed, and somebody runs the stand-in script again out of habit. The
    /// stand-in's bytes are already in the library under the donor's name, so the sentence differs from
    /// the one offered — which is what used to trigger the clause — while the slot now holds the
    /// client's own photograph and is rightly refused.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_conflict_does_not_claim_a_sentence_was_kept()
    {
        await using var world = await CreateAsync();
        await ImportTheDeliveryAsync(world);
        await StandInAsync(world);
        Assert.True((await world.ImportAsync(Token, root: world.StandInRoot)).Succeeded);

        // The real photographs land and take the slots.
        await world.Drop("products", $"{Kid}-1.jpg", TestImages.UniqueJpeg(1024, 1024), Token);
        await world.Drop("products", $"{Kid}-2.jpg", TestImages.UniqueJpeg(1024, 1024), Token);
        Assert.True((await world.ImportAsync(Token, replace: true)).Succeeded);

        // And now the stand-in drop is run again by mistake.
        var refused = await world.ImportAsync(Token, root: world.StandInRoot);

        Assert.True(refused.Succeeded, string.Join("; ", refused.Failures));

        var conflict = refused.Conflicts.Single(line => line.FileName == $"{Kid}-1.jpg");

        Assert.Contains("already holds a different picture", conflict.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("kept rather than overwritten", conflict.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Court Canvas Sneakers", conflict.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The order matters, and this is the proof. Run the stand-ins <b>first</b>, on a library that does
    /// not yet hold the donor's bytes, and the kids product creates the asset — so the shared asset
    /// carries the <i>kids</i> product's alt text, and when the delivery arrives the men's sneaker reads
    /// it out too.
    /// <para>
    /// This test asserts the wrong outcome on purpose. It is the reason
    /// <c>tools/kids-stand-in-photos.ps1</c> imports the client's delivery before the stand-ins instead
    /// of asking an operator to remember to, and if the importer ever stops depending on order this test
    /// fails and the script's second step can go.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Importing_the_stand_ins_first_puts_the_kids_name_on_the_donors_photograph()
    {
        await using var world = await CreateAsync();

        ClientAssetWorld.MakeTheEmptyFolders(world.StandInRoot);
        var bytes = TestImages.UniqueJpeg(1024, 1024);
        await ClientAssetWorld.DropInto(world.StandInRoot, "products", $"{Kid}-1.jpg", bytes, Token);

        Assert.True((await world.ImportAsync(Token, root: world.StandInRoot)).Succeeded);

        // Now the delivery, carrying the same bytes under the donor's name.
        await world.DropTheHomeAsync(Token);
        await world.Drop("products", DeliveredDonorFiles[0], bytes, Token);
        Assert.True((await world.ImportAsync(Token)).Succeeded);

        await using var scope = world.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var donor = await ProductAsync(dbContext, Donor);
        var card = await dbContext.MediaAssets.SingleAsync(asset => asset.Id == donor.CardImageAssetId!.Value, Token);

        Assert.Equal("Kids Canvas Sneakers, front view", card.AltText);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<ClientAssetWorld> CreateAsync()
    {
        sqlServer.SkipIfUnavailable();
        return await ClientAssetWorld.CreateAsync(sqlServer.ConnectionString, withPlaceholderFiles: false, Token);
    }

    /// <summary>The client's delivery, with the donor's two photographs in it. Returns the asset count.</summary>
    private static async Task<int> ImportTheDeliveryAsync(ClientAssetWorld world)
    {
        await world.DropTheUsualAsync(DeliveredDonorFiles, Token);

        var report = await world.ImportAsync(Token);
        Assert.True(report.Succeeded, string.Join("; ", report.Failures));

        await using var scope = world.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().MediaAssets.CountAsync(Token);
    }

    /// <summary>
    /// A stand-in drop whose bytes are in no library yet, which is what a first <c>-DryRun</c> plans
    /// against: both of the script's steps are dry runs, so step 1 stores nothing for step 2 to find.
    /// </summary>
    private static async Task StandInFromFreshBytesAsync(ClientAssetWorld world)
    {
        ClientAssetWorld.MakeTheEmptyFolders(world.StandInRoot);

        for (var index = 1; index <= 2; index++)
        {
            await ClientAssetWorld.DropInto(
                world.StandInRoot,
                "products",
                $"{Kid}-{index}.jpg",
                TestImages.UniqueJpeg(1024, 1024),
                Token);
        }
    }

    /// <summary>
    /// The stand-in drop: the donor's delivered files, byte for byte, under the kids product's slug —
    /// which is what <c>tools/kids-stand-in-photos.ps1</c> copies.
    /// </summary>
    private static async Task StandInAsync(ClientAssetWorld world)
    {
        ClientAssetWorld.MakeTheEmptyFolders(world.StandInRoot);

        for (var index = 0; index < DeliveredDonorFiles.Length; index++)
        {
            var source = Path.Combine(world.DropRoot, "products", DeliveredDonorFiles[index]);
            await ClientAssetWorld.DropInto(
                world.StandInRoot,
                "products",
                $"{Kid}-{index + 1}.jpg",
                await File.ReadAllBytesAsync(source, Token),
                Token);
        }
    }

    private static async Task<Product> ProductAsync(AppDbContext dbContext, string slug) =>
        await dbContext.Products.SingleAsync(product => product.Slug == slug, Token);

    private static async Task<(int Card, int Hover)> CardAndHoverAsync(ClientAssetWorld world, string slug)
    {
        await using var scope = world.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var product = await ProductAsync(dbContext, slug);

        return (product.CardImageAssetId!.Value, product.HoverImageAssetId!.Value);
    }

    private static async Task<(int Assets, int Media, int Usages)> CountsAsync(ClientAssetWorld world)
    {
        await using var scope = world.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return (
            await dbContext.MediaAssets.CountAsync(Token),
            await dbContext.ProductMedia.CountAsync(Token),
            await dbContext.MediaUsages.CountAsync(Token));
    }
}
