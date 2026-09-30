using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Media.Abstractions;
using SpencerDenim.Application.Media.Contracts;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.Tests.MediaFixtures;

namespace SpencerDenim.IntegrationTests.Media;

/// <summary>
/// The three things the media pipeline promised and did not keep: that a soft delete takes the rows it
/// owns with it, that deduplication survives more than one request at a time, and that a rendition is
/// only ever written once per distinct size.
/// <para>
/// All three were found by a hostile read of code that every existing test passed, which is the point of
/// this file: each test below is the shortest sequence that reaches the broken state, so a regression
/// shows up as a failure rather than as a support ticket.
/// </para>
/// </summary>
public sealed class MediaConcurrencyAndCleanupTests(SqlServerDatabaseFixture sqlServer)
{
    /// <summary>
    /// Doc 08 §3.3: "An owner's save rewrites its usage rows in the same transaction." A soft delete is
    /// such a save.
    /// <para>
    /// Before the fix, deleting a video left behind the usage row naming that video as the poster's
    /// owner. The poster was then un-deletable for ever — 409, blocked by a video the soft-delete filter
    /// hides — and the "used in…" panel listed it with a null label, because the label lookup reads
    /// through the same filter. Two assets bricked by one delete.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Deleting_a_video_releases_the_poster_it_was_holding()
    {
        await using var host = await StartAsync();

        var video = Assert.IsType<MediaUploadOutcome.Created>(
            await UploadVideoAsync(host, TestVideos.UniqueMp4(), TestImages.UniqueJpeg(800, 600)));

        var posterId = video.Asset.PosterPublicId!.Value;

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var media = scope.ServiceProvider.GetRequiredService<IMediaService>();

        // While the video is there, the poster is genuinely in use.
        Assert.IsType<MediaDeleteOutcome.InUse>(
            await media.DeleteAsync(posterId, null, TestContext.Current.CancellationToken));

        Assert.IsType<MediaDeleteOutcome.Deleted>(
            await media.DeleteAsync(video.Asset.PublicId, null, TestContext.Current.CancellationToken));

        // The row the video owned is gone, not merely hidden: a hidden row still fails the delete guard.
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var videoRowId = await dbContext.MediaAssets
            .IgnoreQueryFilters()
            .Where(asset => asset.PublicId == video.Asset.PublicId)
            .Select(asset => asset.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        var ownerId = videoRowId.ToString(CultureInfo.InvariantCulture);
        var stillOwned = await dbContext.MediaUsages
            .IgnoreQueryFilters()
            .AnyAsync(
                usage => usage.OwnerType == MediaUsageOwnerTypes.MediaAsset && usage.OwnerId == ownerId,
                TestContext.Current.CancellationToken);

        Assert.False(stillOwned, "The soft-deleted video still owns a usage row.");

        // And the poster is free, which is the whole point.
        var detail = await media.GetAsync(posterId, TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Empty(detail.Usages);

        Assert.IsType<MediaDeleteOutcome.Deleted>(
            await media.DeleteAsync(posterId, null, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Doc 08 §3.3 states deduplication by checksum as a guarantee, and a read-then-write cannot give
    /// one. Eight simultaneous uploads of byte-identical JPEGs used to produce eight assets and forty
    /// files; <c>UX_MediaAssets_Checksum_Ready</c> is what makes the sentence true.
    /// <para>
    /// The uploads run on their own scopes, because that is what eight HTTP requests are — one
    /// <c>DbContext</c> each. Sharing one would test a different thing entirely and would not be
    /// thread-safe.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Simultaneous_uploads_of_the_same_bytes_still_produce_one_asset()
    {
        await using var host = await StartAsync();

        const int Uploaders = 8;
        var bytes = TestImages.UniqueJpeg(900, 1200);

        var outcomes = await Task.WhenAll(Enumerable
            .Range(0, Uploaders)
            .Select(index => UploadImageAsync(host, bytes, $"race-{index}.jpg")));

        var created = outcomes.OfType<MediaUploadOutcome.Created>().ToList();
        var duplicates = outcomes.OfType<MediaUploadOutcome.Duplicate>().ToList();

        Assert.Single(created);
        Assert.Equal(Uploaders - 1, duplicates.Count);

        // Every loser points at the one winner, so a caller cannot tell whether it was answered by the
        // cheap lookup or by the index.
        Assert.All(duplicates, duplicate => Assert.Equal(created[0].Asset.PublicId, duplicate.Asset.PublicId));

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var checksum = created[0].Asset.Checksum;
        var rows = await dbContext.MediaAssets
            .IgnoreQueryFilters()
            .Where(asset => asset.MediaType == MediaAssetType.Image)
            .Select(asset => new { asset.PublicId, asset.Checksum, asset.Status, asset.IsDeleted })
            .ToListAsync(TestContext.Current.CancellationToken);

        var matching = rows
            .Where(row => Convert.ToHexString(row.Checksum).Equals(checksum, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Not merely "one visible row": a loser left behind as Failed or soft-deleted would be a row in
        // the library that nobody asked for, and the next upload of the same bytes would make another.
        Assert.Single(matching);
        Assert.Equal(MediaAssetStatus.Ready, matching[0].Status);
        Assert.False(matching[0].IsDeleted);

        // And the losers took their files with them.
        var files = host.PublicFiles();
        var winnerPrefix = $"public/renditions/{created[0].Asset.PublicId:D}/";
        Assert.Equal(
            files.Count(file => file.StartsWith("public/renditions/", StringComparison.Ordinal)),
            files.Count(file => file.StartsWith(winnerPrefix, StringComparison.Ordinal)));
    }

    /// <summary>
    /// A rendition is written once per distinct size. Renditions are clamped to the source and never
    /// upscaled, so a source at or below the <c>large</c> width used to be encoded as <c>large</c> and
    /// then again, byte for byte, as <c>zoom</c> — a quarter of the seeded renditions were pure copies.
    /// </summary>
    [Fact]
    public async Task A_source_too_small_to_fill_zoom_is_not_written_twice()
    {
        await using var host = await StartAsync();

        // 1200 px is exactly the `large` target, so `zoom` could only repeat it.
        var created = Assert.IsType<MediaUploadOutcome.Created>(
            await UploadImageAsync(host, TestImages.UniqueJpeg(1200, 1600), "exactly-large.jpg"));

        var publicId = created.Asset.PublicId;

        Assert.True(host.PublicFileExists(MediaStorageKey.ForRendition(publicId, MediaRenditions.Thumb)));
        Assert.True(host.PublicFileExists(MediaStorageKey.ForRendition(publicId, MediaRenditions.Card)));
        Assert.True(host.PublicFileExists(MediaStorageKey.ForRendition(publicId, MediaRenditions.Large)));
        Assert.False(
            host.PublicFileExists(MediaStorageKey.ForRendition(publicId, MediaRenditions.Zoom)),
            "zoom.webp repeats large.webp for a 1200 px source and should not have been written.");

        // The payload must not name the file that was not written. ZoomUrl falls back to the largest
        // rendition that exists — not to the original, which would undo the whole point.
        Assert.True(created.Asset.RenditionUrls.ContainsKey(MediaRenditions.Large));
        Assert.False(created.Asset.RenditionUrls.ContainsKey(MediaRenditions.Zoom));
    }

    /// <summary>A source wider than <c>zoom</c> still gets the whole set, so the trim is not over-eager.</summary>
    [Fact]
    public async Task A_source_wider_than_zoom_still_gets_every_rendition()
    {
        await using var host = await StartAsync();

        var created = Assert.IsType<MediaUploadOutcome.Created>(
            await UploadImageAsync(host, TestImages.UniqueJpeg(2400, 3200), "wide.jpg"));

        foreach (var rendition in MediaRenditions.Current)
        {
            var key = MediaStorageKey.ForRendition(created.Asset.PublicId, rendition.Code);
            Assert.True(host.PublicFileExists(key), key);
        }
    }

    private async Task<MediaTestHost> StartAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);
        return MediaTestHost.Create(sqlServer.ConnectionString);
    }

    private static async Task<MediaUploadOutcome> UploadImageAsync(
        MediaTestHost host,
        byte[] bytes,
        string fileName,
        MediaVisibility visibility = MediaVisibility.Public)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var media = scope.ServiceProvider.GetRequiredService<IMediaService>();

        using var content = new MemoryStream(bytes);
        return await media.UploadImageAsync(
            new UploadImageRequest(new MediaUploadFile(content, fileName), visibility, null, null, null),
            TestContext.Current.CancellationToken);
    }

    private static async Task<MediaUploadOutcome> UploadVideoAsync(
        MediaTestHost host,
        byte[] video,
        byte[] poster)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var media = scope.ServiceProvider.GetRequiredService<IMediaService>();

        using var videoContent = new MemoryStream(video);
        using var posterContent = new MemoryStream(poster);

        return await media.UploadVideoAsync(
            new UploadVideoRequest(
                new MediaUploadFile(videoContent, "video.mp4"),
                new MediaUploadFile(posterContent, "poster.jpg"),
                null,
                null,
                null),
            TestContext.Current.CancellationToken);
    }
}
