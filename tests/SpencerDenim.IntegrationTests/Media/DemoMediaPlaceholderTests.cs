using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Seeding;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Media;

/// <summary>
/// The point of the demo placeholders: the seeded catalogue's image URLs stop being grey boxes, without a
/// single invented product photograph reaching a screen.
/// <para>
/// The generator is driven one asset at a time here rather than over the whole catalogue, because these
/// tests are about what one asset's generation produces. The whole set <i>is</i> now drawn once per run,
/// by <see cref="SqlServerDatabaseFixture"/> during seeding — without it the catalogue's assets carry no
/// <c>RenditionSetVersion</c> and no test can tell a reader that picks the right rendition from one that
/// ignores renditions altogether, which is how doc 03 §3b's renditions came to be generated and linked
/// nowhere. The cost that was feared when this class was written turned out not to bite at this size:
/// about 600 encodes of a 1200x1600 placeholder add a couple of seconds to a suite that runs in over a
/// minute.
/// </para>
/// </summary>
public sealed class DemoMediaPlaceholderTests(SqlServerDatabaseFixture sqlServer)
{
    [Fact]
    public async Task A_seeded_demo_asset_gets_a_placeholder_that_the_storefront_url_then_serves()
    {
        await using var host = await StartAsync();

        var (publicId, storageKey) = await GenerateOneAsync(host);

        // Original and every rendition are on disk.
        Assert.True(host.PublicFileExists(storageKey), storageKey);

        // The placeholder is PlaceholderImageRenderer.Width wide, which decides how many of the set's
        // codes produce distinct pixels: anything at or below the `large` target makes no `zoom`.
        var produced = MediaRenditions.ProducedFor(MediaRenditions.Current, PlaceholderImageRenderer.Width);
        Assert.NotEmpty(produced);

        foreach (var (rendition, _) in produced)
        {
            var key = MediaStorageKey.ForRendition(publicId, rendition);
            Assert.True(host.PublicFileExists(key), key);
        }

        // And nothing beyond them: a file written here would be a byte-for-byte copy of the one before.
        foreach (var rendition in MediaRenditions.Current.Where(candidate =>
            !produced.Any(entry => entry.Rendition.Code == candidate.Code)))
        {
            var key = MediaStorageKey.ForRendition(publicId, rendition.Code);
            Assert.False(host.PublicFileExists(key), key);
        }

        // And the URL the catalogue API builds for that key answers with a real image.
        using var client = host.CreateAnonymousClient();
        using var response = await client.GetAsync(new Uri("/media/" + storageKey, UriKind.Relative), TestContext.Current.CancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);

        using var codec = SKCodec.Create(new SKMemoryStream(bytes));
        Assert.NotNull(codec);
        Assert.Equal(PlaceholderImageRenderer.Width, codec.Info.Width);
        Assert.Equal(PlaceholderImageRenderer.Height, codec.Info.Height);
    }

    /// <summary>
    /// The row is brought in line with the file that was written: a size and a checksum that describe the
    /// bytes, not the placeholders the catalogue seeder had to invent when there was no file.
    /// </summary>
    [Fact]
    public async Task Generating_a_placeholder_makes_the_row_describe_the_file_that_now_exists()
    {
        await using var host = await StartAsync();
        var (publicId, storageKey) = await GenerateOneAsync(host);

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var asset = await dbContext.MediaAssets
            .AsNoTracking()
            .SingleAsync(candidate => candidate.PublicId == publicId, TestContext.Current.CancellationToken);

        var onDisk = await File.ReadAllBytesAsync(
            Path.Combine(host.PublicRoot, storageKey.Replace('/', Path.DirectorySeparatorChar)),
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaAssetStatus.Ready, asset.Status);
        Assert.Equal(onDisk.LongLength, asset.SizeBytes);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(onDisk), asset.Checksum);
        Assert.Equal(PlaceholderImageRenderer.Width, asset.Width);
        Assert.Equal(PlaceholderImageRenderer.Height, asset.Height);
        Assert.Equal(MediaRenditions.CurrentSetVersion, asset.RenditionSetVersion);
        Assert.StartsWith("Placeholder image", asset.AltText!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The demo catalogue seeds its rows as <c>Processing</c>, so a deployment with no media volume serves
    /// an honest 404 for them rather than claiming a published image that is not there.
    /// <para>
    /// The row is written by the test rather than picked out of the seeded catalogue. It used to be the
    /// last seeded demo asset, which worked only while nothing had rendered it — and the fixture now
    /// seeds a real media volume, so every demo row reaches this class already <c>Ready</c>. Depending on
    /// "an asset no other test has touched yet" was the fragile part; a row this test owns says the same
    /// thing and cannot be raced.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Before_the_files_exist_a_demo_image_url_is_a_404_not_a_broken_ready_asset()
    {
        await using var host = await StartAsync();

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Exactly what DemoCatalogueSeeder writes before the generator has drawn anything: a demo key,
        // placeholder alt text, and no file behind it.
        var asset = new MediaAsset
        {
            MediaType = MediaAssetType.Image,
            Visibility = MediaVisibility.Public,
            Status = MediaAssetStatus.Processing,
            OriginalFileName = "unrendered.jpg",
            StorageKey = DemoMediaPlaceholders.StorageKeyPrefix + $"unrendered-{Guid.NewGuid():N}.jpg",
            ContentType = "image/jpeg",
            // CK_MediaAssets_SizeBytes requires a positive size: the seeder writes a nominal one too,
            // because the real size is only known once the file behind the row has been drawn.
            SizeBytes = 1,
            Checksum = System.Security.Cryptography.SHA256.HashData([]),
            AltText = "Placeholder image — no photograph has been supplied yet",
        };

        dbContext.MediaAssets.Add(asset);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var client = host.CreateAnonymousClient();
        using var response = await client.GetAsync(new Uri("/media/" + asset.StorageKey, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The alt text the storefront renders says it is a placeholder. A grey box captioned "Wide-Leg Rinse
    /// Jeans in Indigo Rinse" would be a lie to a screen-reader user and to an accessibility audit.
    /// </summary>
    [Fact]
    public async Task Every_seeded_demo_image_carries_alt_text_that_says_it_is_a_placeholder()
    {
        await using var host = await StartAsync();

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var altTexts = await dbContext.MediaAssets
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(candidate => EF.Functions.Like(candidate.StorageKey, DemoMediaPlaceholders.StorageKeyPrefix + "%"))
            .Select(candidate => candidate.AltText)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(altTexts);
        Assert.All(altTexts, text => Assert.StartsWith("Placeholder image", text!, StringComparison.Ordinal));

        var overrides = await dbContext.ProductMedia
            .AsNoTracking()
            .Select(media => media.AltTextOverride)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(overrides);
        Assert.All(overrides, text => Assert.StartsWith("Placeholder image", text!, StringComparison.Ordinal));
    }

    /// <summary>
    /// The regression test for the failure that took the Linux CI job down, at the level it actually broke:
    /// several demo placeholders generated and then <b>saved in one transaction</b>, with no font
    /// configured, which is the deployment shape.
    /// <para>
    /// Before the identity block, every fontless placeholder was the same image, so the second row to reach
    /// <c>Ready</c> tripped <c>UX_MediaAssets_Checksum_Ready</c> and the save threw a
    /// <c>DbUpdateException</c> — killing the seeder and most of the suite behind it. Deliberately one
    /// <c>SaveChangesAsync</c> for all of them, because that is what makes the unique index the thing under
    /// test rather than an incidental detail of the ordering.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Several_demo_placeholders_save_together_without_tripping_the_checksum_index()
    {
        // No font, deliberately: this is the container's own state, and with a font present the test would
        // pass for the wrong reason — the labels would be doing the work the identity block has to do.
        await using var host = await StartWithoutFontsAsync();
        await using var scope = host.Factory.Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var generator = scope.ServiceProvider.GetRequiredService<DemoMediaPlaceholderGenerator>();

        // The last twelve seeded demo frames, so the single-asset tests above (which take the first) cannot
        // have finished them. Filtered to keys the demo data actually declares: other tests in this
        // assembly insert their own rows under the same prefix, and they have no placeholder descriptor.
        var candidates = await dbContext.MediaAssets
            .IgnoreQueryFilters()
            .Where(candidate => EF.Functions.Like(candidate.StorageKey, DemoMediaPlaceholders.StorageKeyPrefix + "%"))
            .OrderByDescending(candidate => candidate.Id)
            .Take(80)
            .ToListAsync(TestContext.Current.CancellationToken);

        var assets = candidates
            .Where(candidate => DemoMediaPlaceholders.ByStorageKey.ContainsKey(candidate.StorageKey))
            .Take(12)
            .ToList();

        Assert.Equal(12, assets.Count);

        foreach (var asset in assets)
        {
            var placeholder = DemoMediaPlaceholders.ByStorageKey[asset.StorageKey];

            var files = await generator.GenerateAsync(
                asset.PublicId,
                asset.StorageKey,
                placeholder.ProductName,
                placeholder.ColourName,
                placeholder.ColourHex,
                TestContext.Current.CancellationToken);

            DemoMediaPlaceholderGenerator.Apply(asset, files, placeholder.AltText);
        }

        // Every checksum distinct, before the database is asked — so a failure says which half is wrong.
        var checksums = assets
            .Select(asset => Convert.ToHexString(asset.Checksum))
            .ToList();

        Assert.Equal(checksums.Count, checksums.Distinct(StringComparer.Ordinal).Count());

        // And the index agrees: this is the save that used to throw.
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.All(assets, asset => Assert.Equal(MediaAssetStatus.Ready, asset.Status));
    }

    /// <summary>
    /// The other half of the same defect: the demo placeholders must not need a font at all, so the whole
    /// generate-and-serve path runs here against a host that reports none.
    /// </summary>
    [Fact]
    public async Task No_placeholder_font_is_configured_and_generation_works_anyway()
    {
        await using var host = await StartWithoutFontsAsync();
        await using var scope = host.Factory.Services.CreateAsyncScope();

        var options = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<SpencerDenim.Application.Media.MediaOptions>>();

        Assert.Equal(string.Empty, options.Value.PlaceholderFontPath);

        var (_, storageKey) = await GenerateOneAsync(host);
        Assert.True(host.PublicFileExists(storageKey), storageKey);

        // And it serves: a fontless placeholder is a real, decodable image, not an empty file.
        using var client = host.CreateAnonymousClient();
        using var response = await client.GetAsync(
            new Uri("/media/" + storageKey, UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        using var codec = SKCodec.Create(new SKMemoryStream(bytes));

        Assert.NotNull(codec);
        Assert.Equal(PlaceholderImageRenderer.Width, codec.Info.Width);
    }

    private async Task<MediaTestHost> StartWithoutFontsAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);
        return MediaTestHost.CreateWithoutFonts(sqlServer.ConnectionString);
    }

    private async Task<MediaTestHost> StartAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);
        return MediaTestHost.Create(sqlServer.ConnectionString);
    }

    /// <summary>
    /// Draws one demo asset's placeholder set and brings its row in line, which is exactly what the seeder
    /// does for every asset on a first boot.
    /// </summary>
    private static async Task<(Guid PublicId, string StorageKey)> GenerateOneAsync(MediaTestHost host)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var generator = scope.ServiceProvider.GetRequiredService<DemoMediaPlaceholderGenerator>();

        var asset = await dbContext.MediaAssets
            .IgnoreQueryFilters()
            .Where(candidate => EF.Functions.Like(candidate.StorageKey, DemoMediaPlaceholders.StorageKeyPrefix + "%"))
            .OrderBy(candidate => candidate.Id)
            .FirstAsync(TestContext.Current.CancellationToken);

        var placeholder = DemoMediaPlaceholders.ByStorageKey[asset.StorageKey];

        var files = await generator.GenerateAsync(
            asset.PublicId,
            asset.StorageKey,
            placeholder.ProductName,
            placeholder.ColourName,
            placeholder.ColourHex,
            TestContext.Current.CancellationToken);

        DemoMediaPlaceholderGenerator.Apply(asset, files, placeholder.AltText);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (asset.PublicId, asset.StorageKey);
    }
}
