using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;
using SpencerDenim.Application.Media.Abstractions;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.Tests.MediaFixtures;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The independent tester's additions to #191: the <b>read</b> path of the campaign banner, not only the
/// write path. The write handler refuses a private, non-image, processing or deleted asset, but an asset can
/// change after a campaign was saved (a re-process, a visibility change, a soft delete that slipped past the
/// usage guard), and a campaign saved before #191 through the API could already point at any asset at all,
/// because the old check tested only "ready and not deleted". Neither the Sale page nor the admin screen
/// may show such a picture.
/// </summary>
/// <remarks>
/// Also: the tie-break of two live banners with the same start, a stale editor's banner change, and a
/// deterministic interleaving of a campaign save with a media-library delete under RCSI. Every test
/// removes what it created; every window is relative to now.
/// </remarks>
public sealed class SaleCampaignBannerTesterTests(SqlServerDatabaseFixture sqlServer)
{
    private const string Url = "/api/admin/marketing/sale-campaigns";
    private const string BannerUrl = "/api/content/sale-banner";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A campaign whose banner was a good public image when it was saved, and then stopped being one. Each
    /// way it can stop is applied straight to the row, and both the Sale page and the admin payload must
    /// drop the banner: the reader's filters are the only guard left once the save has happened.
    /// </summary>
    [Theory]
    [InlineData("private")]
    [InlineData("document")]
    [InlineData("processing")]
    [InlineData("failed")]
    [InlineData("deleted")]
    public async Task A_banner_that_stops_being_a_ready_public_image_after_the_save_is_no_longer_shown(string change)
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(
            Permissions.Promotions.View, Permissions.Promotions.Create, Permissions.Promotions.Delete,
            Permissions.Media.Create);
        using var shopper = host.CreateAnonymousClient();

        var banner = await UploadImageAsync(admin, "Tester banner " + change);
        var shirts = await CategoryIdAsync(host, "women", "shirts-blouses");

        var created = await SendAsync(admin, HttpMethod.Post, Url, Body("Tester " + change, 15m, [Category(shirts)], banner));

        try
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);
            var id = created.Body.GetProperty("campaign").GetProperty("id").GetInt32();

            Assert.Contains(banner.ToString(), await ShownUrlAsync(shopper) ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            await ChangeAssetAsync(host, banner, change);

            Assert.DoesNotContain(banner.ToString(), await ShownUrlAsync(shopper) ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(JsonValueKind.Null, (await AdminRowAsync(admin, id)).GetProperty("banner").ValueKind);
        }
        finally
        {
            await RestoreAssetAsync(host, banner);
            await DeleteAsync(admin, created, reread: true);
        }
    }

    /// <summary>
    /// <b>A campaign saved before #191 pointing at a private asset</b> — the old validator accepted any ready
    /// asset, a customer's bank slip included, and no migration revisits existing rows. Written straight to
    /// the column, as that old API would have stored it, and neither the Sale page nor the admin payload may
    /// show it.
    /// </summary>
    [Fact]
    public async Task A_legacy_campaign_pointing_at_a_private_asset_shows_nothing_anywhere()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(
            Permissions.Promotions.View, Permissions.Promotions.Create, Permissions.Promotions.Edit,
            Permissions.Promotions.Delete);
        using var shopper = host.CreateAnonymousClient();

        var shirts = await CategoryIdAsync(host, "women", "shirts-blouses");
        var slip = await InsertPrivateSlipAsync(host);

        var created = await SendAsync(admin, HttpMethod.Post, Url, Body("Tester legacy", 15m, [Category(shirts)], null));

        try
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);
            var id = created.Body.GetProperty("campaign").GetProperty("id").GetInt32();

            await SetBannerColumnAsync(host, id, slip.Id);

            var (status, body) = await GetAsync(shopper, BannerUrl);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.DoesNotContain(slip.PublicId.ToString(), body.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private", body.GetRawText(), StringComparison.OrdinalIgnoreCase);

            var row = await AdminRowAsync(admin, id);
            Assert.Equal(JsonValueKind.Null, row.GetProperty("banner").ValueKind);
            Assert.DoesNotContain(slip.PublicId.ToString(), row.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await DeleteAsync(admin, created, reread: true);
        }
    }

    /// <summary>
    /// The second half of the owner decision: with two live banners that started at the same instant, the
    /// higher id wins, on every read. The starts are written to the row so they are equal to the tick and
    /// more recent than anything else live in the shared database.
    /// </summary>
    [Fact]
    public async Task With_two_live_banners_started_at_the_same_instant_the_higher_id_wins()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(
            Permissions.Promotions.View, Permissions.Promotions.Create, Permissions.Promotions.Delete,
            Permissions.Media.Create);
        using var shopper = host.CreateAnonymousClient();

        var firstBanner = await UploadImageAsync(admin, "Tester tie first");
        var secondBanner = await UploadImageAsync(admin, "Tester tie second");
        var shirts = await CategoryIdAsync(host, "women", "shirts-blouses");

        var first = await SendAsync(admin, HttpMethod.Post, Url, Body("Tester tie A", 10m, [Category(shirts)], firstBanner));
        var second = await SendAsync(admin, HttpMethod.Post, Url, Body("Tester tie B", 10m, [Category(shirts)], secondBanner));

        try
        {
            Assert.Equal(HttpStatusCode.Created, first.Status);
            Assert.Equal(HttpStatusCode.Created, second.Status);

            var firstId = first.Body.GetProperty("campaign").GetProperty("id").GetInt32();
            var secondId = second.Body.GetProperty("campaign").GetProperty("id").GetInt32();
            Assert.True(secondId > firstId);

            var start = DateTime.UtcNow.AddMinutes(-1);
            await SetStartAsync(host, firstId, start);
            await SetStartAsync(host, secondId, start);

            Assert.Contains(secondBanner.ToString(), await ShownUrlAsync(shopper) ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(secondBanner.ToString(), await ShownUrlAsync(shopper) ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await DeleteAsync(admin, second, reread: true);
            await DeleteAsync(admin, first, reread: true);
        }
    }

    /// <summary>
    /// Two editors open the same campaign. The first saves a banner; the second, holding the old row
    /// version, tries to save a different one. The second is refused, and the usage table still names the
    /// first banner only: the usage rewrite rode in the refused batch and went with it.
    /// </summary>
    [Fact]
    public async Task A_stale_editor_cannot_swap_the_banner_and_leaves_no_usage_row_behind()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(
            Permissions.Promotions.View, Permissions.Promotions.Create, Permissions.Promotions.Edit,
            Permissions.Promotions.Delete, Permissions.Media.Create);

        var mine = await UploadImageAsync(admin, "Tester editor one");
        var theirs = await UploadImageAsync(admin, "Tester editor two");
        var shirts = await CategoryIdAsync(host, "women", "shirts-blouses");

        var created = await SendAsync(admin, HttpMethod.Post, Url, Body("Tester editors", 10m, [Category(shirts)], null));

        try
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);
            var id = created.Body.GetProperty("campaign").GetProperty("id").GetInt32();
            var opened = created.Body.GetProperty("campaign").GetProperty("rowVersion").GetString();

            var firstSave = await SendAsync(
                admin, HttpMethod.Put, $"{Url}/{id}", Body("Tester editors", 10m, [Category(shirts)], mine, opened));
            Assert.Equal(HttpStatusCode.OK, firstSave.Status);

            var staleSave = await SendAsync(
                admin, HttpMethod.Put, $"{Url}/{id}", Body("Tester editors", 10m, [Category(shirts)], theirs, opened));
            Assert.True(
                staleSave.Status is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed,
                $"a stale save answered {(int)staleSave.Status}");

            Assert.Equal([mine], await UsagePublicIdsAsync(host, id));
            Assert.Equal(mine, (await AdminRowAsync(admin, id)).GetProperty("banner").GetProperty("publicId").GetGuid());
        }
        finally
        {
            await DeleteAsync(admin, created, reread: true);
        }
    }

    /// <summary>
    /// <b>A deterministic interleaving of a campaign save and a media-library delete, under RCSI.</b> The
    /// save checks the banner (step 1), the library deletes the picture before the save commits (step 2:
    /// no usage row exists yet, so nothing refuses it), then the save commits its usage row (step 3).
    /// <para>
    /// Whatever state that leaves in the tables, the shop must not show a deleted picture: the Sale page
    /// and the admin payload both read the asset's own state, not the usage row. The state the tables are
    /// left in is written to the test output, for the report.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_media_delete_racing_a_campaign_save_never_puts_a_deleted_picture_on_the_sale_page()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(
            Permissions.Promotions.View, Permissions.Promotions.Create, Permissions.Promotions.Edit,
            Permissions.Promotions.Delete, Permissions.Media.Create);
        using var shopper = host.CreateAnonymousClient();

        var banner = await UploadImageAsync(admin, "Tester race");
        var shirts = await CategoryIdAsync(host, "women", "shirts-blouses");

        var created = await SendAsync(admin, HttpMethod.Post, Url, Body("Tester race", 10m, [Category(shirts)], null));

        try
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);
            var id = created.Body.GetProperty("campaign").GetProperty("id").GetInt32();

            using var saveScope = host.Factory.Services.CreateScope();
            var store = saveScope.ServiceProvider.GetRequiredService<ISaleCampaignAdminStore>();

            // 1. The save's reference check: the picture is a ready public image.
            var candidate = await store.FindBannerAsync(banner, Token);
            Assert.NotNull(candidate);

            var campaign = await store.FindAsync(id, Token);
            Assert.NotNull(campaign);
            campaign.BannerAssetId = candidate.MediaAssetId;

            // 2. Another staff member deletes the picture in between. No usage row exists yet.
            using (var deleteScope = host.Factory.Services.CreateScope())
            {
                var media = deleteScope.ServiceProvider.GetRequiredService<IMediaService>();
                var outcome = await media.DeleteAsync(banner, null, Token);

                TestContext.Current.TestOutputHelper?.WriteLine($"media delete during the save: {outcome.GetType().Name}");
            }

            // 3. The save commits.
            var saved = await store.SaveAsync(campaign, null, Token);
            TestContext.Current.TestOutputHelper?.WriteLine($"campaign save after the delete: {saved}");

            var usages = await UsagePublicIdsAsync(host, id);
            var deleted = await AssetDeletedAsync(host, banner);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"usage rows for the campaign: {usages.Count}; asset soft-deleted: {deleted}");

            // The property that matters: a deleted picture is never shown, whichever way the race went.
            if (deleted)
            {
                Assert.DoesNotContain(banner.ToString(), await ShownUrlAsync(shopper) ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(JsonValueKind.Null, (await AdminRowAsync(admin, id)).GetProperty("banner").ValueKind);
            }
        }
        finally
        {
            await DeleteAsync(admin, created, reread: true);
        }
    }

    /// <summary>
    /// The public banner carries the picture and nothing else: no internal asset id, no campaign id or
    /// name, no storage key outside the URL. Exactly the five fields of <c>SaleBannerImageDto</c>.
    /// </summary>
    [Fact]
    public async Task The_public_banner_carries_no_campaign_name_and_no_internal_id()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(
            Permissions.Promotions.View, Permissions.Promotions.Create, Permissions.Promotions.Delete,
            Permissions.Media.Create);
        using var shopper = host.CreateAnonymousClient();

        var banner = await UploadImageAsync(admin, "Tester public shape");
        var shirts = await CategoryIdAsync(host, "women", "shirts-blouses");
        const string SecretName = "Tester internal codename Zebra";

        var created = await SendAsync(admin, HttpMethod.Post, Url, Body(SecretName, 10m, [Category(shirts)], banner));

        try
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);
            await SetStartAsync(host, created.Body.GetProperty("campaign").GetProperty("id").GetInt32(), DateTime.UtcNow.AddMinutes(-1));

            var (status, body) = await GetAsync(shopper, BannerUrl);
            Assert.Equal(HttpStatusCode.OK, status);

            var shown = body.GetProperty("banner");
            Assert.Equal(
                ["altText", "height", "imageSrcSet", "imageUrl", "width"],
                shown.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

            var text = body.GetRawText();
            Assert.DoesNotContain("Zebra", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"id\"", text, StringComparison.OrdinalIgnoreCase);

            var assetId = await AssetIdAsync(host, banner);
            Assert.DoesNotContain(
                string.Create(CultureInfo.InvariantCulture, $":{assetId},"), text, StringComparison.Ordinal);
            Assert.Contains(banner.ToString(), shown.GetProperty("imageUrl").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            await DeleteAsync(admin, created, reread: true);
        }
    }

    /// <summary>
    /// A description that is padded, or nothing but spaces, as an older upload or a direct edit could leave
    /// it. The Sale page reads the trimmed text (or empty, decorative), and the admin payload reports a
    /// description of spaces as none, so the screen warns about it.
    /// </summary>
    [Theory]
    [InlineData("  Linen Week  ", "Linen Week", "  Linen Week  ")]
    [InlineData("   ", "", null)]
    public async Task A_padded_or_blank_description_is_trimmed_for_shoppers_and_blank_is_none_for_staff(
        string stored,
        string shopperAlt,
        string? staffAlt)
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(
            Permissions.Promotions.View, Permissions.Promotions.Create, Permissions.Promotions.Delete,
            Permissions.Media.Create);
        using var shopper = host.CreateAnonymousClient();

        var banner = await UploadImageAsync(admin, "Tester padded");
        var shirts = await CategoryIdAsync(host, "women", "shirts-blouses");

        var created = await SendAsync(admin, HttpMethod.Post, Url, Body("Tester padded", 10m, [Category(shirts)], banner));

        try
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);
            var id = created.Body.GetProperty("campaign").GetProperty("id").GetInt32();
            await SetStartAsync(host, id, DateTime.UtcNow.AddMinutes(-1));

            using (var scope = host.Factory.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().MediaAssets
                    .IgnoreQueryFilters()
                    .Where(asset => asset.PublicId == banner)
                    .ExecuteUpdateAsync(set => set.SetProperty(a => a.AltText, stored), Token);
            }

            var (status, body) = await GetAsync(shopper, BannerUrl);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains(banner.ToString(), body.GetProperty("banner").GetProperty("imageUrl").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(shopperAlt, body.GetProperty("banner").GetProperty("altText").GetString());

            var staff = (await AdminRowAsync(admin, id)).GetProperty("banner").GetProperty("altText");
            Assert.Equal(staffAlt, staff.ValueKind == JsonValueKind.Null ? null : staff.GetString());
        }
        finally
        {
            await DeleteAsync(admin, created, reread: true);
        }
    }

    /// <summary>
    /// <b>KNOWN DEFECT (#191 tester).</b> A campaign saved before #191 with a banner has no
    /// <c>MediaUsages</c> row (no migration backfills one), and <c>MediaService.DeleteAsync</c> guards a
    /// banner only through that row — unlike the poster and the payment slip, which it checks by their real
    /// foreign key "so it also protects the ones attached before this check existed". So the library
    /// deletes the picture of a live campaign. Nothing leaks (the readers filter deleted assets), but the
    /// Sale page silently loses its banner. Un-skip with the fix.
    /// </summary>
    [Fact(Skip = "Known defect #191: a pre-#191 banner has no usage row and can be deleted while live")]
    public async Task A_legacy_banner_without_a_usage_row_still_cannot_be_deleted_while_its_campaign_uses_it()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(
            Permissions.Promotions.View, Permissions.Promotions.Create, Permissions.Promotions.Delete,
            Permissions.Media.Create, Permissions.Media.Delete);

        var banner = await UploadImageAsync(admin, "Tester legacy public");
        var shirts = await CategoryIdAsync(host, "women", "shirts-blouses");

        var created = await SendAsync(admin, HttpMethod.Post, Url, Body("Tester legacy public", 10m, [Category(shirts)], null));

        try
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);
            var id = created.Body.GetProperty("campaign").GetProperty("id").GetInt32();

            // As the pre-#191 API stored it: the column set, no usage row.
            await SetBannerColumnAsync(host, id, await AssetIdAsync(host, banner));
            Assert.Empty(await UsagePublicIdsAsync(host, id));

            using var deleted = await admin.DeleteAsync(new Uri($"/api/admin/media/{banner}", UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
        }
        finally
        {
            await RestoreAssetAsync(host, banner);
            await DeleteAsync(admin, created, reread: true);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static Dictionary<string, object?> Body(
        string name,
        decimal discountValue,
        IReadOnlyList<object> targets,
        Guid? banner,
        string? rowVersion = null) =>
        new(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["description"] = "Arranged by the #191 tester.",
            ["discountType"] = "Percent",
            ["discountValue"] = discountValue,
            ["startsAt"] = ShopTime.Midnight(0),
            ["endsAt"] = ShopTime.Midnight(7),
            ["isActive"] = true,
            ["bannerPublicId"] = banner?.ToString(),
            ["targets"] = targets,
            ["rowVersion"] = rowVersion,
        };

    private static Dictionary<string, object?> Category(int categoryId) =>
        new(StringComparer.Ordinal)
        {
            ["targetType"] = "Category",
            ["departmentId"] = null,
            ["categoryId"] = categoryId,
            ["productId"] = null,
            ["includeSubcategories"] = true,
        };

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        Dictionary<string, object?> body)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };

        if (body.TryGetValue("rowVersion", out var rowVersion) && rowVersion is string version)
        {
            request.Headers.TryAddWithoutValidation("If-Match", version);
        }

        using var response = await client.SendAsync(request, Token);
        var text = await response.Content.ReadAsStringAsync(Token);

        TestContext.Current.TestOutputHelper?.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"{method} {path} -> {(int)response.StatusCode}: {text}"));

        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);
        return (response.StatusCode, document.RootElement.Clone());
    }

    private static async Task DeleteAsync(HttpClient client, (HttpStatusCode Status, JsonElement Body) created, bool reread)
    {
        if (created.Status != HttpStatusCode.Created)
        {
            return;
        }

        var id = created.Body.GetProperty("campaign").GetProperty("id").GetInt32();
        var rowVersion = created.Body.GetProperty("campaign").GetProperty("rowVersion").GetString();

        if (reread)
        {
            using var listed = await client.GetAsync(new Uri(Url, UriKind.Relative), Token);
            var list = await listed.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
            var row = list.EnumerateArray().SingleOrDefault(candidate => candidate.GetProperty("id").GetInt32() == id);

            if (row.ValueKind == JsonValueKind.Undefined)
            {
                return;
            }

            rowVersion = row.GetProperty("rowVersion").GetString();
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            new Uri(string.Create(CultureInfo.InvariantCulture, $"{Url}/{id}"), UriKind.Relative));
        request.Headers.TryAddWithoutValidation("If-Match", rowVersion);

        using var response = await client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Token);
        var text = await response.Content.ReadAsStringAsync(Token);
        using var document = JsonDocument.Parse(text);

        return (response.StatusCode, document.RootElement.Clone());
    }

    private static async Task<JsonElement> AdminRowAsync(HttpClient admin, int id)
    {
        using var listed = await admin.GetAsync(new Uri(Url, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        var list = await listed.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
        return list.EnumerateArray().Single(candidate => candidate.GetProperty("id").GetInt32() == id);
    }

    /// <summary>The URL of the banner the Sale page would draw, or null for none. It carries the publicId.</summary>
    private static async Task<string?> ShownUrlAsync(HttpClient shopper)
    {
        var (status, body) = await GetAsync(shopper, BannerUrl);
        Assert.Equal(HttpStatusCode.OK, status);

        var banner = body.GetProperty("banner");
        return banner.ValueKind == JsonValueKind.Null ? null : banner.GetProperty("imageUrl").GetString();
    }

    private static async Task<Guid> UploadImageAsync(HttpClient client, string altText)
    {
        using var form = MediaTestHost.FilePart("file", TestImages.UniqueJpeg(1600, 500), "banner.jpg", "image/jpeg");
        form.Add(new StringContent(altText), "altText");

        using var response = await client.PostAsync(new Uri("/api/admin/media/images", UriKind.Relative), form, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
        return body.GetProperty("publicId").GetGuid();
    }

    private static async Task ChangeAssetAsync(MediaTestHost host, Guid publicId, string change)
    {
        using var scope = host.Factory.Services.CreateScope();
        var assets = scope.ServiceProvider.GetRequiredService<AppDbContext>().MediaAssets
            .IgnoreQueryFilters()
            .Where(asset => asset.PublicId == publicId);

        var rows = change switch
        {
            "private" => await assets.ExecuteUpdateAsync(set => set.SetProperty(a => a.Visibility, MediaVisibility.Private), Token),
            "document" => await assets.ExecuteUpdateAsync(set => set.SetProperty(a => a.MediaType, MediaAssetType.Document), Token),
            "video" => await assets.ExecuteUpdateAsync(set => set.SetProperty(a => a.MediaType, MediaAssetType.Video), Token),
            "processing" => await assets.ExecuteUpdateAsync(set => set.SetProperty(a => a.Status, MediaAssetStatus.Processing), Token),
            "failed" => await assets.ExecuteUpdateAsync(set => set.SetProperty(a => a.Status, MediaAssetStatus.Failed), Token),
            "deleted" => await assets.ExecuteUpdateAsync(
                set => set.SetProperty(a => a.IsDeleted, true).SetProperty(a => a.DeletedAtUtc, DateTime.UtcNow), Token),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

        Assert.Equal(1, rows);
    }

    private static async Task RestoreAssetAsync(MediaTestHost host, Guid publicId)
    {
        using var scope = host.Factory.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<AppDbContext>().MediaAssets
            .IgnoreQueryFilters()
            .Where(asset => asset.PublicId == publicId)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(a => a.Visibility, MediaVisibility.Public)
                    .SetProperty(a => a.MediaType, MediaAssetType.Image)
                    .SetProperty(a => a.Status, MediaAssetStatus.Ready)
                    .SetProperty(a => a.IsDeleted, false)
                    .SetProperty(a => a.DeletedAtUtc, (DateTime?)null),
                CancellationToken.None);
    }

    private static async Task<(int Id, Guid PublicId)> InsertPrivateSlipAsync(MediaTestHost host)
    {
        using var scope = host.Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var publicId = Guid.NewGuid();
        var asset = new MediaAsset
        {
            PublicId = publicId,
            MediaType = MediaAssetType.Image,
            Visibility = MediaVisibility.Private,
            Status = MediaAssetStatus.Ready,
            OriginalFileName = "bank-slip.jpg",
            StorageKey = $"private/originals/{publicId}.jpg",
            ContentType = "image/jpeg",
            SizeBytes = 1_024,
            Width = 1600,
            Height = 500,
            Checksum = RandomNumberGenerator.GetBytes(32),
            AltText = "A customer's bank slip",
        };

        dbContext.MediaAssets.Add(asset);
        await dbContext.SaveChangesAsync(Token);

        return (asset.Id, publicId);
    }

    private static async Task SetBannerColumnAsync(MediaTestHost host, int campaignId, int assetId)
    {
        using var scope = host.Factory.Services.CreateScope();

        var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().SaleCampaigns
            .Where(campaign => campaign.Id == campaignId)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.BannerAssetId, (int?)assetId), Token);

        Assert.Equal(1, rows);
    }

    private static async Task SetStartAsync(MediaTestHost host, int campaignId, DateTime startUtc)
    {
        using var scope = host.Factory.Services.CreateScope();

        var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().SaleCampaigns
            .Where(campaign => campaign.Id == campaignId)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.StartsAtUtc, startUtc), Token);

        Assert.Equal(1, rows);
    }

    private static async Task<List<Guid>> UsagePublicIdsAsync(MediaTestHost host, int campaignId)
    {
        using var scope = host.Factory.Services.CreateScope();
        var ownerId = campaignId.ToString(CultureInfo.InvariantCulture);

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().MediaUsages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(usage => usage.OwnerType == MediaUsageOwnerTypes.SaleCampaign && usage.OwnerId == ownerId)
            .Select(usage => usage.MediaAsset!.PublicId)
            .ToListAsync(Token);
    }

    private static async Task<bool> AssetDeletedAsync(MediaTestHost host, Guid publicId)
    {
        using var scope = host.Factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().MediaAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(asset => asset.PublicId == publicId)
            .Select(asset => asset.IsDeleted)
            .SingleAsync(Token);
    }

    private static async Task<int> AssetIdAsync(MediaTestHost host, Guid publicId)
    {
        using var scope = host.Factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().MediaAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(asset => asset.PublicId == publicId)
            .Select(asset => asset.Id)
            .SingleAsync(Token);
    }

    private static async Task<int> CategoryIdAsync(MediaTestHost host, string departmentSlug, string slug)
    {
        using var scope = host.Factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Categories
            .AsNoTracking()
            .Where(category => category.Slug == slug && category.Department!.Slug == departmentSlug)
            .Select(category => category.Id)
            .SingleAsync(Token);
    }
}
