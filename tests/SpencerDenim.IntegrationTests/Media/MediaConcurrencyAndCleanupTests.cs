using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Media.Abstractions;
using SpencerDenim.Application.Media.Contracts;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Media;
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
    // Index names as SQL Server quotes them in the message, which is what the predicate reads.
    private const string Table = "'media.MediaAssets'";
    private const string ChecksumIndex = "'UX_MediaAssets_Checksum_Ready'";
    private const string PublicIdIndex = "'UX_MediaAssets_PublicId'";
    private const string StorageKeyIndex = "'UX_MediaAssets_StorageKey'";

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

    /// <summary>
    /// What <c>UX_MediaAssets_Checksum_Ready</c> must <b>not</b> refuse. The index is what makes doc 08
    /// §3.3&apos;s deduplication a guarantee, and every column and clause in it is load-bearing: narrow it
    /// by accident and an upload that should be allowed starts coming back as somebody else&apos;s asset.
    /// <para>
    /// A deleted asset is the case a shop actually hits — an image is removed, then re-uploaded, and it
    /// has to become a real asset again rather than resurrect a soft-deleted row the library no longer
    /// shows.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_same_bytes_may_be_uploaded_again_once_the_first_asset_is_deleted()
    {
        await using var host = await StartAsync();
        var bytes = TestImages.UniqueJpeg(600, 800);

        var first = Assert.IsType<MediaUploadOutcome.Created>(
            await UploadImageAsync(host, bytes, "first.jpg"));

        // While it is there, the same bytes are a duplicate.
        Assert.IsType<MediaUploadOutcome.Duplicate>(await UploadImageAsync(host, bytes, "again.jpg"));

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var media = scope.ServiceProvider.GetRequiredService<IMediaService>();
        Assert.IsType<MediaDeleteOutcome.Deleted>(
            await media.DeleteAsync(first.Asset.PublicId, null, TestContext.Current.CancellationToken));

        // The index is filtered on IsDeleted = 0, so the soft-deleted row does not stand in the way.
        var second = Assert.IsType<MediaUploadOutcome.Created>(
            await UploadImageAsync(host, bytes, "after-delete.jpg"));

        Assert.NotEqual(first.Asset.PublicId, second.Asset.PublicId);
    }

    /// <summary>
    /// A row left <c>Failed</c> by a processing error must not block the retry. The index is filtered on
    /// <c>Status = 1</c> for exactly this: the failed row keeps its checksum for diagnosis, and the next
    /// attempt at the same bytes still gets to become an asset.
    /// </summary>
    [Fact]
    public async Task A_failed_row_with_the_same_checksum_does_not_block_the_retry()
    {
        await using var host = await StartAsync();
        var bytes = TestImages.UniqueJpeg(600, 800);

        var created = Assert.IsType<MediaUploadOutcome.Created>(
            await UploadImageAsync(host, bytes, "will-fail.jpg"));

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Exactly what a part-way failure leaves behind: the row still Ready-shaped but marked Failed.
        await dbContext.MediaAssets
            .Where(asset => asset.PublicId == created.Asset.PublicId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(asset => asset.Status, MediaAssetStatus.Failed),
                TestContext.Current.CancellationToken);

        var retry = Assert.IsType<MediaUploadOutcome.Created>(
            await UploadImageAsync(host, bytes, "retry.jpg"));

        Assert.NotEqual(created.Asset.PublicId, retry.Asset.PublicId);
        Assert.Equal(MediaAssetStatus.Ready, retry.Asset.Status);
    }

    /// <summary>
    /// The index keys on media type as well as checksum and visibility.
    /// <para>
    /// No upload can reach this — the type comes from the file signature, so one byte sequence is either
    /// a JPEG or an MP4 and never both. The rows are written directly for that reason: the point is to
    /// pin the index&apos;s <i>definition</i>, which is the thing a later migration could quietly narrow,
    /// and doing it through the pipeline would be impossible rather than merely artificial.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_index_keys_on_media_type_so_a_video_and_an_image_may_share_a_checksum()
    {
        await using var host = await StartAsync();

        // A real image first, so the video below has a poster to point at (CK_MediaAssets_Poster).
        var poster = Assert.IsType<MediaUploadOutcome.Created>(
            await UploadImageAsync(host, TestImages.UniqueJpeg(400, 300), "poster.jpg"));

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var posterRowId = await dbContext.MediaAssets
            .Where(asset => asset.PublicId == poster.Asset.PublicId)
            .Select(asset => asset.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        var shared = SHA256.HashData(Guid.NewGuid().ToByteArray());

        dbContext.MediaAssets.Add(NewRow(MediaAssetType.Image, shared, posterAssetId: null));
        dbContext.MediaAssets.Add(NewRow(MediaAssetType.Video, shared, posterAssetId: posterRowId));

        // One statement, both rows: if the index keyed on checksum and visibility alone this throws.
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var count = await dbContext.MediaAssets
            .CountAsync(asset => asset.Checksum == shared, TestContext.Current.CancellationToken);

        Assert.Equal(2, count);
    }

    /// <summary>
    /// The negative behind <c>PublishAsync</c>: a unique-index violation is answered as a duplicate
    /// upload only when it came from the checksum index.
    /// <para>
    /// <c>UX_MediaAssets_PublicId</c> and <c>UX_MediaAssets_StorageKey</c> are unique on the same table.
    /// A collision on either is a generated GUID repeating itself — something that must surface as the
    /// error it is, not be quietly answered with somebody else&apos;s asset. Matching "any unique
    /// violation" would be the easy mistake here, and nothing else would notice it.
    /// </para>
    /// </summary>
    [Theory]
    // The real SQL Server texts for 2601 (unique index) and 2627 (unique constraint).
    [InlineData(2601, "Cannot insert duplicate key row in object " + Table + " with unique index " + ChecksumIndex + ".", true)]
    [InlineData(2627, "Violation of UNIQUE KEY constraint " + ChecksumIndex + ". Cannot insert duplicate key.", true)]
    [InlineData(2601, "Cannot insert duplicate key row in object " + Table + " with unique index " + PublicIdIndex + ".", false)]
    [InlineData(2601, "Cannot insert duplicate key row in object " + Table + " with unique index " + StorageKeyIndex + ".", false)]
    // A deadlock and a timeout are not duplicate keys at all.
    [InlineData(1205, "Transaction (Process ID 55) was deadlocked on lock resources.", false)]
    [InlineData(-2, "Execution Timeout Expired.", false)]
    public void Only_the_checksum_index_is_read_as_a_duplicate_upload(int errorNumber, string message, bool expected) =>
        Assert.Equal(expected, MediaService.IsChecksumIndexViolation(errorNumber, message));

    private static MediaAsset NewRow(MediaAssetType mediaType, byte[] checksum, int? posterAssetId)
    {
        var publicId = Guid.NewGuid();
        var isVideo = mediaType == MediaAssetType.Video;

        return new MediaAsset
        {
            PublicId = publicId,
            MediaType = mediaType,
            Visibility = MediaVisibility.Public,
            Status = MediaAssetStatus.Ready,
            OriginalFileName = isVideo ? "shared.mp4" : "shared.jpg",
            StorageKey = MediaStorageKey.ForOriginal(MediaVisibility.Public, publicId, isVideo ? ".mp4" : ".jpg"),
            ContentType = isVideo ? "video/mp4" : "image/jpeg",
            SizeBytes = 1,
            Width = 400,
            Height = 300,
            DurationSeconds = isVideo ? 1 : null,
            PosterAssetId = posterAssetId,
            Checksum = checksum,
        };
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
