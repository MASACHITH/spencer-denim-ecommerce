using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Media.Abstractions;
using SpencerDenim.Application.Media.Contracts;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.Tests.MediaFixtures;

namespace SpencerDenim.IntegrationTests.Media;

/// <summary>
/// Serving the public volume at <c>/media</c>: the cache headers and ETag of doc 03 §3b, the byte ranges a
/// <c>&lt;video&gt;</c> needs to seek, and the six different ways a request answers 404 — including the one
/// that matters most, a key on the private volume.
/// </summary>
public sealed class MediaServingTests(SqlServerDatabaseFixture sqlServer)
{
    [Fact]
    public async Task A_public_image_is_served_with_a_years_cache_a_strong_etag_and_nosniff()
    {
        await using var host = await StartAsync();
        var asset = await UploadImageAsync(host, TestImages.UniqueJpeg(1200, 900), "shirt.jpg");

        using var client = host.CreateAnonymousClient();
        using var response = await client.GetAsync(new Uri("/media/" + asset.StorageKey, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);

        var cacheControl = response.Headers.CacheControl;
        Assert.NotNull(cacheControl);
        Assert.True(cacheControl.Public);
        Assert.Equal(TimeSpan.FromDays(365), cacheControl.MaxAge);
        Assert.Contains("immutable", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());

        var eTag = response.Headers.ETag;
        Assert.NotNull(eTag);
        Assert.False(eTag.IsWeak);
        Assert.Contains(asset.Checksum, eTag.Tag, StringComparison.Ordinal);

        Assert.NotNull(response.Content.Headers.LastModified);
        Assert.True(response.Content.Headers.ContentLength > 0);
    }

    [Fact]
    public async Task Every_rendition_is_served_as_webp()
    {
        await using var host = await StartAsync();
        var asset = await UploadImageAsync(host, TestImages.UniqueJpeg(2400, 3200), "jacket.jpg");

        using var client = host.CreateAnonymousClient();

        foreach (var rendition in MediaRenditions.Current)
        {
            var key = MediaStorageKey.ForRendition(asset.PublicId, rendition.Code);
            using var response = await client.GetAsync(new Uri("/media/" + key, UriKind.Relative), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);

            // The ETag distinguishes the renditions of one asset, or a cache would serve the thumb for the
            // zoom after a revalidation.
            Assert.Contains(rendition.Code, response.Headers.ETag!.Tag, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_conditional_request_with_the_etag_answers_304_with_no_body()
    {
        await using var host = await StartAsync();
        var asset = await UploadImageAsync(host, TestImages.UniquePng(500, 500), "swatch.png");
        var url = new Uri("/media/" + asset.StorageKey, UriKind.Relative);

        using var client = host.CreateAnonymousClient();

        using var first = await client.GetAsync(url, TestContext.Current.CancellationToken);
        var eTag = first.Headers.ETag!;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.IfNoneMatch.Add(eTag);

        using var second = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Doc 03 §3b: "Served with HTTP range requests for streaming." Without this a browser downloads the
    /// whole 50 MB to jump to the middle of a clip.
    /// </summary>
    [Fact]
    public async Task A_range_request_for_a_video_answers_206_with_exactly_those_bytes()
    {
        await using var host = await StartAsync();
        var video = TestVideos.UniqueMp4(payloadBytes: 40_000);
        var asset = await UploadVideoAsync(host, video, TestImages.UniqueJpeg(640, 480));

        var url = new Uri("/media/" + asset.StorageKey, UriKind.Relative);
        using var client = host.CreateAnonymousClient();

        using var whole = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal("bytes", whole.Headers.AcceptRanges.Single());
        var full = await whole.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(video.Length, full.Length);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(1000, 1999);

        using var partial = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var bytes = await partial.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(1000, bytes.Length);
        Assert.Equal(full.AsSpan(1000, 1000).ToArray(), bytes);

        var range = partial.Content.Headers.ContentRange;
        Assert.NotNull(range);
        Assert.Equal(1000, range.From);
        Assert.Equal(1999, range.To);
        Assert.Equal(video.Length, range.Length);
    }

    [Fact]
    public async Task A_range_past_the_end_of_the_file_answers_416()
    {
        await using var host = await StartAsync();
        var asset = await UploadImageAsync(host, TestImages.UniqueJpeg(200, 200), "small.jpg");

        using var client = host.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/media/" + asset.StorageKey, UriKind.Relative));
        request.Headers.Range = new RangeHeaderValue(100_000_000, 100_000_100);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
    }

    [Fact]
    public async Task Head_answers_the_headers_without_the_body()
    {
        await using var host = await StartAsync();
        var asset = await UploadImageAsync(host, TestImages.UniqueJpeg(600, 400), "head.jpg");

        using var client = host.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, new Uri("/media/" + asset.StorageKey, UriKind.Relative));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    // -------------------------------------------------------------- the 404s

    /// <summary>
    /// <b>The proof.</b> A file really is written to the private volume, its key really is in the database,
    /// and the file really is on disk — and then every way of asking <c>/media</c> for it answers 404. The
    /// same request against the public asset's key answers 200 in the same test, so a 404 here cannot come
    /// from the endpoint being broken.
    /// </summary>
    [Fact]
    public async Task A_private_file_cannot_be_fetched_through_media_by_any_spelling_of_its_key()
    {
        await using var host = await StartAsync();

        var privateAsset = await UploadImageAsync(
            host,
            TestImages.UniqueJpeg(800, 600),
            "bank-slip.jpg",
            MediaVisibility.Private);

        var publicAsset = await UploadImageAsync(host, TestImages.UniqueJpeg(640, 480), "product.jpg");

        // The private file exists: on the private volume, nowhere under the public one.
        Assert.Contains(privateAsset.StorageKey, host.PrivateFiles());
        Assert.DoesNotContain(host.PublicFiles(), key => key.EndsWith("bank-slip.jpg", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(host.PrivateRoot, privateAsset.StorageKey.Replace('/', Path.DirectorySeparatorChar))));

        using var client = host.CreateAnonymousClient();

        // The public asset answers, so the endpoint is working.
        using var control = await client.GetAsync(new Uri("/media/" + publicAsset.StorageKey, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, control.StatusCode);

        string[] attempts =
        [
            "/media/" + privateAsset.StorageKey,
            "/media/" + privateAsset.StorageKey.ToUpperInvariant(),
            "/media/public/../" + privateAsset.StorageKey,
            "/media/public/originals/../../" + privateAsset.StorageKey,
            "/media/public/originals/..%2f..%2f" + privateAsset.StorageKey,
            "/media/%2e%2e/" + privateAsset.StorageKey,
            "/media/public/originals/%2e%2e%2f%2e%2e%2fprivate-volume/" + privateAsset.StorageKey,
            "/media//" + privateAsset.StorageKey,
            "/media/" + privateAsset.StorageKey.Replace('/', '\\'),
        ];

        foreach (var attempt in attempts)
        {
            using var response = await client.GetAsync(new Uri(attempt, UriKind.Relative), TestContext.Current.CancellationToken);

            Assert.True(
                response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
                $"'{attempt}' answered {(int)response.StatusCode}, and must not.");

            // Whatever the status, the bank slip's bytes must not be in the body.
            var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
            Assert.True(body.Length < 1024, $"'{attempt}' returned {body.Length} bytes.");
        }
    }

    /// <summary>
    /// An asset that is still <c>Processing</c> has a row and a file, and must still 404: the bytes are not
    /// published until the pipeline says they are.
    /// </summary>
    [Fact]
    public async Task An_asset_that_is_not_ready_answers_404_even_though_its_file_is_there()
    {
        await using var host = await StartAsync();
        var asset = await UploadImageAsync(host, TestImages.UniqueJpeg(700, 500), "not-ready.jpg");
        var url = new Uri("/media/" + asset.StorageKey, UriKind.Relative);

        using var client = host.CreateAnonymousClient();

        using var beforeChange = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, beforeChange.StatusCode);

        await SetStatusAsync(host, asset.PublicId, MediaAssetStatus.Processing);

        using var afterChange = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, afterChange.StatusCode);

        // Still on disk: only the row changed.
        Assert.True(host.PublicFileExists(asset.StorageKey));

        await SetStatusAsync(host, asset.PublicId, MediaAssetStatus.Failed);

        using var failed = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, failed.StatusCode);
    }

    [Fact]
    public async Task A_soft_deleted_asset_stops_being_served_at_once()
    {
        await using var host = await StartAsync();
        var asset = await UploadImageAsync(host, TestImages.UniqueJpeg(400, 400), "deleted.jpg");
        var url = new Uri("/media/" + asset.StorageKey, UriKind.Relative);

        using var client = host.CreateAnonymousClient();
        using (var before = await client.GetAsync(url, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var media = scope.ServiceProvider.GetRequiredService<IMediaService>();
            Assert.IsType<MediaDeleteOutcome.Deleted>(
                await media.DeleteAsync(asset.PublicId, null, TestContext.Current.CancellationToken));
        }

        using var after = await client.GetAsync(url, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        Assert.True(host.PublicFileExists(asset.StorageKey), "the bytes stay for the 30-day purge");
    }

    [Theory]
    [InlineData("/media/public/originals/00000000-0000-0000-0000-000000000000.jpg")]
    [InlineData("/media/public/renditions/00000000-0000-0000-0000-000000000000/card.webp")]
    [InlineData("/media/public/originals/../../../etc/passwd")]
    [InlineData("/media/../../appsettings.json")]
    [InlineData("/media/public/originals/CON")]
    [InlineData("/media/originals/whatever.jpg")]
    [InlineData("/media/")]
    public async Task Everything_unservable_answers_the_same_404(string url)
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        using var response = await client.GetAsync(new Uri(url, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
            $"'{url}' answered {(int)response.StatusCode}.");
    }

    /// <summary>
    /// A rendition key for an asset that exists but is a video has no file behind it, and must not fall back
    /// to anything.
    /// </summary>
    [Fact]
    public async Task A_rendition_key_for_a_video_answers_404()
    {
        await using var host = await StartAsync();
        var video = await UploadVideoAsync(host, TestVideos.UniqueMp4(), TestImages.UniqueJpeg(640, 480));

        using var client = host.CreateAnonymousClient();
        var key = MediaStorageKey.ForRendition(video.PublicId, MediaRenditions.Card);

        using var response = await client.GetAsync(new Uri("/media/" + key, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// A row whose <c>ContentType</c> has been tampered with to something executable is not served: the
    /// media type comes from a fixed allow-list, not from a free-text column.
    /// </summary>
    [Fact]
    public async Task A_row_claiming_a_content_type_outside_the_allow_list_is_not_served()
    {
        await using var host = await StartAsync();
        var asset = await UploadImageAsync(host, TestImages.UniqueJpeg(300, 300), "tampered.jpg");

        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await dbContext.MediaAssets
                .Where(candidate => candidate.PublicId == asset.PublicId)
                .ExecuteUpdateAsync(
                    update => update.SetProperty(candidate => candidate.ContentType, "text/html"),
                    TestContext.Current.CancellationToken);
        }

        using var client = host.CreateAnonymousClient();
        using var response = await client.GetAsync(new Uri("/media/" + asset.StorageKey, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The polyglot, end to end: a JPEG with a script appended is stored, served as <c>image/jpeg</c> with
    /// <c>nosniff</c>, and the payload is gone from every rendition because each one is re-encoded.
    /// </summary>
    [Fact]
    public async Task A_jpeg_with_a_script_appended_is_served_as_an_image_and_its_renditions_carry_no_payload()
    {
        const string script = "<script>fetch('/api/admin/auth/whoami')</script>";

        await using var host = await StartAsync();
        var asset = await UploadImageAsync(host, TestImages.JpegWithAppendedScript(script), "polyglot.jpg");

        using var client = host.CreateAnonymousClient();
        using var original = await client.GetAsync(new Uri("/media/" + asset.StorageKey, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal("image/jpeg", original.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", original.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("inline", original.Content.Headers.ContentDisposition?.DispositionType);

        foreach (var rendition in MediaRenditions.Current)
        {
            var key = MediaStorageKey.ForRendition(asset.PublicId, rendition.Code);
            using var response = await client.GetAsync(new Uri("/media/" + key, UriKind.Relative), TestContext.Current.CancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

            Assert.DoesNotContain(script, Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- helpers

    private async Task<MediaTestHost> StartAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);
        return MediaTestHost.Create(sqlServer.ConnectionString);
    }

    private static async Task<MediaAssetDto> UploadImageAsync(
        MediaTestHost host,
        byte[] bytes,
        string fileName,
        MediaVisibility visibility = MediaVisibility.Public)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var media = scope.ServiceProvider.GetRequiredService<IMediaService>();

        using var content = new MemoryStream(bytes);
        var outcome = await media.UploadImageAsync(
            new UploadImageRequest(new MediaUploadFile(content, fileName), visibility, "Alt text", null, null),
            TestContext.Current.CancellationToken);

        return Assert.IsType<MediaUploadOutcome.Created>(outcome).Asset;
    }

    private static async Task<MediaAssetDto> UploadVideoAsync(MediaTestHost host, byte[] video, byte[] poster)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var media = scope.ServiceProvider.GetRequiredService<IMediaService>();

        using var videoContent = new MemoryStream(video);
        using var posterContent = new MemoryStream(poster);

        var outcome = await media.UploadVideoAsync(
            new UploadVideoRequest(
                new MediaUploadFile(videoContent, "clip.mp4"),
                new MediaUploadFile(posterContent, "poster.jpg"),
                "Alt text",
                null,
                null),
            TestContext.Current.CancellationToken);

        return Assert.IsType<MediaUploadOutcome.Created>(outcome).Asset;
    }

    private static async Task SetStatusAsync(MediaTestHost host, Guid publicId, MediaAssetStatus status)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.MediaAssets
            .Where(asset => asset.PublicId == publicId)
            .ExecuteUpdateAsync(
                update => update.SetProperty(asset => asset.Status, status),
                TestContext.Current.CancellationToken);
    }
}
