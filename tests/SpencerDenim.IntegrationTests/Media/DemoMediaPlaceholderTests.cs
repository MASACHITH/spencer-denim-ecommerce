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
/// The generator is driven one asset at a time rather than over the whole catalogue: the demo set is well
/// over a hundred assets and several hundred encodes, which belongs in a developer's first boot, not in
/// every run of the test suite.
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

        foreach (var rendition in MediaRenditions.Current)
        {
            var key = MediaStorageKey.ForRendition(publicId, rendition.Code);
            Assert.True(host.PublicFileExists(key), key);
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
    /// </summary>
    [Fact]
    public async Task Before_the_files_exist_a_demo_image_url_is_a_404_not_a_broken_ready_asset()
    {
        await using var host = await StartAsync();

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The last demo asset, deliberately: the whole assembly shares one database and the generator
        // tests above fill in the first one, so asking for "the first" here would be asking about an asset
        // another test has already finished.
        var asset = await dbContext.MediaAssets
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(candidate => EF.Functions.Like(candidate.StorageKey, DemoMediaPlaceholders.StorageKeyPrefix + "%"))
            .OrderByDescending(candidate => candidate.Id)
            .FirstAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MediaAssetStatus.Processing, asset.Status);

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
