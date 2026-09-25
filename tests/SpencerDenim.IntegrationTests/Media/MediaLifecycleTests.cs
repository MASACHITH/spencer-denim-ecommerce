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
/// The asset lifecycle against a real SQL Server and a real pair of volumes: the status transitions of doc
/// 08 §3.3, the checksum rule, the poster rule, the usage guard and the rollback invariant.
/// <para>
/// These go through <c>IMediaService</c> rather than HTTP, because they are about the pipeline and the
/// database rather than about status codes; the endpoints have their own file.
/// </para>
/// </summary>
public sealed class MediaLifecycleTests(SqlServerDatabaseFixture sqlServer)
{
    [Fact]
    public async Task An_image_upload_goes_ready_with_its_original_and_every_rendition_on_disk()
    {
        await using var host = await StartAsync();
        var outcome = await UploadImageAsync(host, TestImages.UniqueJpeg(2400, 3200), "lookbook.jpg", "A jacket");

        var created = Assert.IsType<MediaUploadOutcome.Created>(outcome);
        var asset = created.Asset;

        Assert.Equal(MediaAssetStatus.Ready, asset.Status);
        Assert.Equal(MediaAssetType.Image, asset.MediaType);
        Assert.Equal(MediaVisibility.Public, asset.Visibility);
        Assert.Equal("image/jpeg", asset.ContentType);
        Assert.Equal(2400, asset.Width);
        Assert.Equal(3200, asset.Height);
        Assert.Equal(MediaRenditions.CurrentSetVersion, asset.RenditionSetVersion);
        Assert.Equal("A jacket", asset.AltText);

        Assert.True(host.PublicFileExists(asset.StorageKey), asset.StorageKey);

        foreach (var rendition in MediaRenditions.Current)
        {
            var key = MediaStorageKey.ForRendition(asset.PublicId, rendition.Code);
            Assert.True(host.PublicFileExists(key), key);
            Assert.Equal("/media/" + key, asset.RenditionUrls[rendition.Code]);
        }

        // Nothing at all on the private volume: a public upload never touches it.
        Assert.Empty(host.PrivateFiles());
    }

    /// <summary>
    /// The checksum is of the bytes that were uploaded, not of what was stored — the stored original has had
    /// its metadata removed and is a different file. That is what makes "the same photograph twice" a
    /// detectable thing.
    /// </summary>
    [Fact]
    public async Task The_checksum_is_the_sha256_of_the_uploaded_bytes()
    {
        await using var host = await StartAsync();
        var bytes = TestImages.UniqueJpeg(300, 400);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        var created = Assert.IsType<MediaUploadOutcome.Created>(
            await UploadImageAsync(host, bytes, "shirt.jpg"));

        Assert.Equal(expected, created.Asset.Checksum);
    }

    [Fact]
    public async Task The_same_bytes_twice_share_one_asset_and_one_file()
    {
        await using var host = await StartAsync();
        var bytes = TestImages.UniquePng(500, 500);

        var first = Assert.IsType<MediaUploadOutcome.Created>(
            await UploadImageAsync(host, bytes, "swatch.png"));

        var filesAfterFirst = host.PublicFiles().Count;

        var second = await UploadImageAsync(host, bytes, "swatch-copy.png");
        var duplicate = Assert.IsType<MediaUploadOutcome.Duplicate>(second);

        Assert.Equal(first.Asset.PublicId, duplicate.Asset.PublicId);
        Assert.Equal(filesAfterFirst, host.PublicFiles().Count);

        var checksum = System.Security.Cryptography.SHA256.HashData(bytes);

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await dbContext.MediaAssets.CountAsync(
            asset => asset.Checksum == checksum,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, rows);
    }

    /// <summary>
    /// The one thing the checksum rule must never do. A customer's bank slip lives on the private volume and
    /// a public upload of the same bytes must get its own public file, not a pointer at the private one —
    /// and the reverse must not happen either.
    /// </summary>
    [Fact]
    public async Task Deduplication_never_crosses_the_public_private_boundary()
    {
        await using var host = await StartAsync();
        var bytes = TestImages.Jpeg(200, 200, new SkiaSharp.SKColor(0x11, 0x22, 0x33));

        var privateAsset = Assert.IsType<MediaUploadOutcome.Created>(
            await UploadImageAsync(host, bytes, "slip.jpg", visibility: MediaVisibility.Private));

        var publicAsset = Assert.IsType<MediaUploadOutcome.Created>(
            await UploadImageAsync(host, bytes, "product.jpg"));

        Assert.NotEqual(privateAsset.Asset.PublicId, publicAsset.Asset.PublicId);
        Assert.StartsWith("private/", privateAsset.Asset.StorageKey, StringComparison.Ordinal);
        Assert.StartsWith("public/", publicAsset.Asset.StorageKey, StringComparison.Ordinal);

        // Same bytes, two assets, two files — one on each volume, and the private one has no URL at all.
        Assert.Null(privateAsset.Asset.Url);
        Assert.NotNull(publicAsset.Asset.Url);
        Assert.Single(host.PrivateFiles());
    }

    /// <summary>A private asset gets no renditions: a copy of a bank slip on the public volume is the leak.</summary>
    [Fact]
    public async Task A_private_upload_gets_no_renditions_on_the_public_volume()
    {
        await using var host = await StartAsync();

        var created = Assert.IsType<MediaUploadOutcome.Created>(
            await UploadImageAsync(host, TestImages.UniqueJpeg(1200, 900), "slip.jpg", visibility: MediaVisibility.Private));

        Assert.Null(created.Asset.RenditionSetVersion);
        Assert.Empty(created.Asset.RenditionUrls);
        Assert.Empty(host.PublicFiles());
        Assert.Single(host.PrivateFiles());
    }

    [Fact]
    public async Task A_video_is_stored_with_its_poster_and_its_container_metadata()
    {
        await using var host = await StartAsync();

        var outcome = await UploadVideoAsync(
            host,
            TestVideos.UniqueMp4(width: 1920, height: 1080, durationSeconds: 8.25),
            TestImages.UniqueJpeg(1920, 1080));

        var created = Assert.IsType<MediaUploadOutcome.Created>(outcome);

        Assert.Equal(MediaAssetType.Video, created.Asset.MediaType);
        Assert.Equal("video/mp4", created.Asset.ContentType);
        Assert.Equal(1920, created.Asset.Width);
        Assert.Equal(1080, created.Asset.Height);
        Assert.Equal(8.25m, created.Asset.DurationSeconds);
        Assert.NotNull(created.Asset.PosterPublicId);
        Assert.NotNull(created.Asset.PosterUrl);

        // No renditions for a video: nothing converts it (doc 03 §3b).
        Assert.Null(created.Asset.RenditionSetVersion);
        Assert.True(host.PublicFileExists(created.Asset.StorageKey));
    }

    /// <summary>
    /// A poster the server stored for a video that was then refused would be an orphan nobody asked for, so
    /// it is taken back out — row and files.
    /// </summary>
    [Fact]
    public async Task A_refused_video_takes_its_poster_back_out_again()
    {
        await using var host = await StartAsync();
        var filesBefore = host.PublicFiles().Count;

        // A name unique to this test: the whole assembly shares one database, so "is there a poster.jpg"
        // would be answered by another test's perfectly legitimate one.
        var posterName = $"orphan-{Guid.NewGuid():N}.jpg";

        var outcome = await UploadVideoAsync(
            host,
            TestVideos.Mp4(codec: "hvc1"),
            TestImages.UniqueJpeg(640, 480),
            posterName);

        var rejected = Assert.IsType<MediaUploadOutcome.Rejected>(outcome);
        Assert.Equal(MediaUploadRejection.Unprocessable, rejected.Reason);

        // Not one extra file on the volume, and not one row: the poster went back out with the refusal.
        Assert.Equal(filesBefore, host.PublicFiles().Count);

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var orphans = await dbContext.MediaAssets
            .IgnoreQueryFilters()
            .CountAsync(
                asset => asset.OriginalFileName == posterName,
                TestContext.Current.CancellationToken);

        Assert.Equal(0, orphans);
    }

    /// <summary>
    /// The guard of doc 08 §3.3: a video's poster is in use by the video, so it cannot be deleted, and the
    /// answer names what is using it.
    /// </summary>
    [Fact]
    public async Task A_poster_cannot_be_deleted_while_its_video_needs_it()
    {
        await using var host = await StartAsync();

        var video = Assert.IsType<MediaUploadOutcome.Created>(
            await UploadVideoAsync(host, TestVideos.UniqueMp4(), TestImages.UniqueJpeg(800, 600)));

        var posterId = video.Asset.PosterPublicId!.Value;

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var media = scope.ServiceProvider.GetRequiredService<IMediaService>();

        var outcome = await media.DeleteAsync(posterId, null, TestContext.Current.CancellationToken);

        var inUse = Assert.IsType<MediaDeleteOutcome.InUse>(outcome);
        Assert.Contains(inUse.Usages, usage => usage.UsageRole == MediaUsageRoles.Poster);
    }

    [Fact]
    public async Task An_unused_asset_is_soft_deleted_and_its_files_stay_for_the_purge_job()
    {
        await using var host = await StartAsync();

        var created = Assert.IsType<MediaUploadOutcome.Created>(
            await UploadImageAsync(host, TestImages.UniqueJpeg(400, 300), "unused.jpg"));

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var media = scope.ServiceProvider.GetRequiredService<IMediaService>();

        Assert.IsType<MediaDeleteOutcome.Deleted>(
            await media.DeleteAsync(created.Asset.PublicId, null, TestContext.Current.CancellationToken));

        // Soft: the row is filtered out, not gone, and the bytes are still there for the 30-day purge.
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await dbContext.MediaAssets.AnyAsync(
            asset => asset.PublicId == created.Asset.PublicId,
            TestContext.Current.CancellationToken));

        var deleted = await dbContext.MediaAssets
            .IgnoreQueryFilters()
            .SingleAsync(asset => asset.PublicId == created.Asset.PublicId, TestContext.Current.CancellationToken);

        Assert.True(deleted.IsDeleted);
        Assert.NotNull(deleted.DeletedAtUtc);
        Assert.True(host.PublicFileExists(created.Asset.StorageKey));

        // And deleting it again is a 404, not a second delete.
        Assert.IsType<MediaDeleteOutcome.NotFound>(
            await media.DeleteAsync(created.Asset.PublicId, null, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The "used in…" query on a real catalogue: the seeded demo products write <c>MediaUsages</c> rows, and
    /// the panel has to name the product rather than print an id.
    /// </summary>
    [Fact]
    public async Task A_seeded_product_image_reports_where_it_is_used_with_a_name_a_person_recognises()
    {
        await using var host = await StartAsync();
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var media = scope.ServiceProvider.GetRequiredService<IMediaService>();

        var usage = await dbContext.MediaUsages
            .AsNoTracking()
            .Where(candidate => candidate.OwnerType == MediaUsageOwnerTypes.Product)
            .OrderBy(candidate => candidate.Id)
            .FirstAsync(TestContext.Current.CancellationToken);

        var asset = await dbContext.MediaAssets
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == usage.MediaAssetId, TestContext.Current.CancellationToken);

        var detail = await media.GetAsync(asset.PublicId, TestContext.Current.CancellationToken);

        Assert.NotNull(detail);
        Assert.NotEmpty(detail.Usages);
        Assert.All(detail.Usages, row => Assert.Equal(MediaUsageOwnerTypes.Product, row.OwnerType));
        Assert.All(detail.Usages, row => Assert.False(string.IsNullOrWhiteSpace(row.OwnerLabel)));

        // And the guard refuses the delete, with the same list.
        var outcome = await media.DeleteAsync(asset.PublicId, null, TestContext.Current.CancellationToken);
        var inUse = Assert.IsType<MediaDeleteOutcome.InUse>(outcome);
        Assert.Equal(detail.Usages.Count, inUse.Usages.Count);
    }

    [Fact]
    public async Task A_missing_asset_reads_as_nothing_and_deletes_as_not_found()
    {
        await using var host = await StartAsync();
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var media = scope.ServiceProvider.GetRequiredService<IMediaService>();

        Assert.Null(await media.GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.IsType<MediaDeleteOutcome.NotFound>(
            await media.DeleteAsync(Guid.NewGuid(), null, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The rollback invariant, forced rather than argued: storage is made to fail on the third file, which
    /// is a rendition, so the original and two renditions are already on disk. Afterwards there must be no
    /// file and no row claiming Ready.
    /// </summary>
    [Fact]
    public async Task An_upload_that_fails_part_way_leaves_no_file_and_no_row_claiming_ready()
    {
        await using var host = await StartAsync(storage => new FailAfterNWritesStorage(storage, failOnWrite: 3));

        var bytes = TestImages.UniqueJpeg(1500, 2000);

        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var media = scope.ServiceProvider.GetRequiredService<IMediaService>();

            await Assert.ThrowsAsync<IOException>(async () => await media.UploadImageAsync(
                new UploadImageRequest(
                    new MediaUploadFile(new MemoryStream(bytes), "doomed.jpg"),
                    MediaVisibility.Public,
                    null,
                    null,
                    null),
                TestContext.Current.CancellationToken));
        }

        Assert.Empty(host.PublicFiles());

        await using var check = host.Factory.Services.CreateAsyncScope();
        var dbContext = check.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await dbContext.MediaAssets
            .IgnoreQueryFilters()
            .SingleAsync(asset => asset.OriginalFileName == "doomed.jpg", TestContext.Current.CancellationToken);

        Assert.Equal(MediaAssetStatus.Failed, row.Status);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<MediaTestHost> StartAsync(Func<IFileStorage, IFileStorage>? decorateStorage = null)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        return decorateStorage is null
            ? MediaTestHost.Create(sqlServer.ConnectionString)
            : MediaTestHost.CreateWithStorage(sqlServer.ConnectionString, decorateStorage);
    }

    private static async Task<MediaUploadOutcome> UploadImageAsync(
        MediaTestHost host,
        byte[] bytes,
        string fileName,
        string? altText = null,
        MediaVisibility visibility = MediaVisibility.Public)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var media = scope.ServiceProvider.GetRequiredService<IMediaService>();

        using var content = new MemoryStream(bytes);
        return await media.UploadImageAsync(
            new UploadImageRequest(new MediaUploadFile(content, fileName), visibility, altText, null, null),
            TestContext.Current.CancellationToken);
    }

    private static async Task<MediaUploadOutcome> UploadVideoAsync(
        MediaTestHost host,
        byte[] video,
        byte[] poster,
        string posterFileName = "poster.jpg")
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var media = scope.ServiceProvider.GetRequiredService<IMediaService>();

        using var videoContent = new MemoryStream(video);
        using var posterContent = new MemoryStream(poster);

        return await media.UploadVideoAsync(
            new UploadVideoRequest(
                new MediaUploadFile(videoContent, "clip.mp4"),
                new MediaUploadFile(posterContent, posterFileName),
                null,
                null,
                null),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Storage that works until the <i>n</i>th write and then fails the way a full disk does. Not a stub of
    /// the interface — the real adapter does everything, so the files that were written really are on disk
    /// when the failure happens, which is the state the rollback has to clean up.
    /// </summary>
    private sealed class FailAfterNWritesStorage(IFileStorage inner, int failOnWrite) : IFileStorage
    {
        private int _writes;

        public bool IsConfigured => inner.IsConfigured;

        public Task<StoredFileInfo> WriteAsync(string storageKey, Stream content, CancellationToken cancellationToken) =>
            ++_writes >= failOnWrite
                ? throw new IOException("There is not enough space on the disk.")
                : inner.WriteAsync(storageKey, content, cancellationToken);

        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(storageKey, cancellationToken);

        public Task<StoredFileInfo?> GetInfoAsync(string storageKey, CancellationToken cancellationToken) =>
            inner.GetInfoAsync(storageKey, cancellationToken);

        public Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken) =>
            inner.DeleteAsync(storageKey, cancellationToken);

        public Task<int> DeletePrefixAsync(string keyPrefix, CancellationToken cancellationToken) =>
            inner.DeletePrefixAsync(keyPrefix, cancellationToken);
    }
}
