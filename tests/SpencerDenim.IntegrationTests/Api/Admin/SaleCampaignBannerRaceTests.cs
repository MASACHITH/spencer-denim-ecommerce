using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.Tests.MediaFixtures;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// #191 second round: <b>a campaign save putting a banner on and a media-library delete of that picture
/// cannot both succeed.</b> Both paths now take an update lock on the asset's <c>media.MediaAssets</c> row
/// first (<c>SaleCampaignAdminStore.BannerStillUsableAsync</c>, <c>MediaService.DeleteAsync</c>), so the
/// one that gets the row first wins and the other sees what it did.
/// </summary>
/// <remarks>
/// <b>Deterministic, not a timing race.</b> The test itself holds the same update lock on the row from its
/// own connection. It starts the first operation through the real API and waits until SQL Server reports it
/// waiting on a lock in this database. Then it starts the second and waits until there are two waiters, and
/// only then releases. SQL Server grants a row's waiting update locks in arrival order, so the first one
/// started is the first one through, every time. Both orders are run. If either path stopped taking the
/// lock, it would never be seen waiting, and the wait would time out with a message saying which.
/// </remarks>
public sealed class SaleCampaignBannerRaceTests(SqlServerDatabaseFixture sqlServer)
{
    private const string Url = "/api/admin/marketing/sale-campaigns";

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task When_the_save_gets_the_picture_first_the_delete_is_refused_as_in_use()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await SignInAsync(host);

        var (banner, assetId, campaignId, rowVersion) = await ArrangeAsync(host, admin, "Race save first");

        try
        {
            var (save, delete) = await InterleaveAsync(
                assetId,
                () => SaveBannerAsync(admin, campaignId, rowVersion, banner, "Race save first"),
                () => admin.DeleteAsync(new Uri($"/api/admin/media/{banner}", UriKind.Relative), Token));

            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);

            var problem = await delete.Content.ReadFromJsonAsync<JsonElement>(Token);
            Assert.Equal("SaleCampaign", problem.GetProperty("usages").EnumerateArray().Single().GetProperty("ownerType").GetString());

            Assert.False(await AssetDeletedAsync(host, assetId));
            Assert.Equal(assetId, await BannerColumnAsync(host, campaignId));
            Assert.Equal(1, await UsageRowsAsync(host, campaignId));
        }
        finally
        {
            await RemoveCampaignAsync(admin, campaignId);
        }
    }

    [Fact]
    public async Task When_the_delete_gets_the_picture_first_the_save_is_refused_and_writes_nothing()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await SignInAsync(host);

        var (banner, assetId, campaignId, rowVersion) = await ArrangeAsync(host, admin, "Race delete first");

        try
        {
            var (delete, save) = await InterleaveAsync(
                assetId,
                () => admin.DeleteAsync(new Uri($"/api/admin/media/{banner}", UriKind.Relative), Token),
                () => SaveBannerAsync(admin, campaignId, rowVersion, banner, "Race delete first"));

            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);

            var problem = await save.Content.ReadFromJsonAsync<JsonElement>(Token);
            Assert.True(problem.GetProperty("errors").TryGetProperty("bannerPublicId", out var message));
            Assert.Contains("ready public image", message[0].GetString(), StringComparison.Ordinal);

            // Nothing of the refused save reached the tables: no banner, no usage row for a deleted picture.
            Assert.True(await AssetDeletedAsync(host, assetId));
            Assert.Null(await BannerColumnAsync(host, campaignId));
            Assert.Equal(0, await UsageRowsAsync(host, campaignId));
        }
        finally
        {
            await RemoveCampaignAsync(admin, campaignId);
        }
    }

    /// <summary>
    /// Holds the asset row's update lock, starts <paramref name="first"/>, waits until it is blocked, starts
    /// <paramref name="second"/>, waits until both are blocked, then releases and returns both answers.
    /// </summary>
    /// <summary>
    /// Tester (#191 round 2): the same race on a <b>create</b>. A new campaign has no id until its insert, so
    /// its banner re-check runs on the insert path (<c>InsertAsync</c>), not the edit path the two tests above
    /// drive. The delete gets the row first; the create must be refused and leave no campaign and no usage
    /// row behind.
    /// </summary>
    [Fact]
    public async Task When_the_delete_gets_the_picture_first_a_new_campaign_is_refused_and_nothing_is_created()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await SignInAsync(host);

        // Arranged like the others, then the arranged campaign is removed: only its picture is wanted.
        var (banner, assetId, arranged, _) = await ArrangeAsync(host, admin, "Race create");
        await RemoveCampaignAsync(admin, arranged);

        const string Name = "Race create, delete first";

        var (delete, create) = await InterleaveAsync(
            assetId,
            () => admin.DeleteAsync(new Uri($"/api/admin/media/{banner}", UriKind.Relative), Token),
            () => admin.PostAsJsonAsync(new Uri(Url, UriKind.Relative), Body(Name, banner, null, null), Token));

        int? leaked = null;

        try
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);

            var problem = await create.Content.ReadFromJsonAsync<JsonElement>(Token);
            Assert.True(problem.GetProperty("errors").TryGetProperty("bannerPublicId", out _));

            Assert.True(await AssetDeletedAsync(host, assetId));

            using var scope = host.Factory.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            leaked = await dbContext.SaleCampaigns
                .IgnoreQueryFilters()
                .Where(campaign => campaign.Name == Name)
                .Select(campaign => (int?)campaign.Id)
                .FirstOrDefaultAsync(Token);

            Assert.Null(leaked);
            Assert.False(await dbContext.MediaUsages.IgnoreQueryFilters().AnyAsync(usage => usage.MediaAssetId == assetId, Token));
        }
        finally
        {
            if (leaked is { } id)
            {
                await RemoveCampaignAsync(admin, id);
            }
        }
    }

    private async Task<(HttpResponseMessage First, HttpResponseMessage Second)> InterleaveAsync(
        int assetId,
        Func<Task<HttpResponseMessage>> first,
        Func<Task<HttpResponseMessage>> second)
    {
        await using var holder = new SqlConnection(sqlServer.ConnectionString);
        await holder.OpenAsync(Token);

        var transaction = (SqlTransaction)await holder.BeginTransactionAsync(Token);

        await using (var hold = holder.CreateCommand())
        {
            hold.Transaction = transaction;
            hold.CommandText =
                "SELECT Id FROM media.MediaAssets WITH (UPDLOCK, READCOMMITTEDLOCK, ROWLOCK) WHERE Id = @id";
            hold.Parameters.AddWithValue("@id", assetId);
            Assert.Equal(assetId, Convert.ToInt32(await hold.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture));
        }

        Task<HttpResponseMessage> firstTask;
        Task<HttpResponseMessage> secondTask;

        try
        {
            firstTask = first();
            await WaitForLockWaitersAsync(1, "the first operation never waited on the asset row's lock");

            secondTask = second();
            await WaitForLockWaitersAsync(2, "the second operation never waited on the asset row's lock");
        }
        finally
        {
            await transaction.RollbackAsync(Token);
        }

        return (await firstTask, await secondTask);
    }

    /// <summary>Polls until this database has at least <paramref name="count"/> requests waiting on a lock.</summary>
    private async Task WaitForLockWaitersAsync(int count, string failure)
    {
        await using var probe = new SqlConnection(sqlServer.ConnectionString);
        await probe.OpenAsync(Token);

        var deadline = DateTime.UtcNow + WaitLimit;

        while (DateTime.UtcNow < deadline)
        {
            await using var command = probe.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE database_id = DB_ID() AND wait_type LIKE 'LCK[_]M[_]%'";

            if ((int)(await command.ExecuteScalarAsync(Token))! >= count)
            {
                return;
            }

            await Task.Delay(50, Token);
        }

        Assert.Fail(failure);
    }

    private static Task<HttpClient> SignInAsync(MediaTestHost host) =>
        host.SignInAsync(
            Permissions.Promotions.View, Permissions.Promotions.Create, Permissions.Promotions.Edit,
            Permissions.Promotions.Delete, Permissions.Media.Create, Permissions.Media.Delete);

    /// <summary>An uploaded picture and a live campaign with no banner yet.</summary>
    private static async Task<(Guid Banner, int AssetId, int CampaignId, string RowVersion)> ArrangeAsync(
        MediaTestHost host,
        HttpClient admin,
        string name)
    {
        using var form = MediaTestHost.FilePart("file", TestImages.UniqueJpeg(1600, 500), "race.jpg", "image/jpeg");
        form.Add(new StringContent(name), "altText");

        using var uploaded = await admin.PostAsync(new Uri("/api/admin/media/images", UriKind.Relative), form, Token);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var banner = (await uploaded.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("publicId").GetGuid();

        using var created = await admin.PostAsJsonAsync(new Uri(Url, UriKind.Relative), Body(name, null, null, await ShirtsAsync(host)), Token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var campaign = (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("campaign");

        using var scope = host.Factory.Services.CreateScope();
        var assetId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().MediaAssets
            .Where(asset => asset.PublicId == banner)
            .Select(asset => asset.Id)
            .SingleAsync(Token);

        return (banner, assetId, campaign.GetProperty("id").GetInt32(), campaign.GetProperty("rowVersion").GetString()!);
    }

    private static async Task<HttpResponseMessage> SaveBannerAsync(
        HttpClient admin,
        int campaignId,
        string rowVersion,
        Guid banner,
        string name)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri(string.Create(CultureInfo.InvariantCulture, $"{Url}/{campaignId}"), UriKind.Relative))
        {
            Content = JsonContent.Create(Body(name, banner, rowVersion, null)),
        };

        return await admin.SendAsync(request, Token);
    }

    private static Dictionary<string, object?> Body(string name, Guid? banner, string? rowVersion, int? categoryId) =>
        new(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["description"] = "Arranged by the #191 race tests.",
            ["discountType"] = "Percent",
            ["discountValue"] = 10m,
            ["startsAt"] = ShopTime.Midnight(0),
            ["endsAt"] = ShopTime.Midnight(7),
            ["isActive"] = true,
            ["bannerPublicId"] = banner?.ToString(),
            ["targets"] = new[]
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["targetType"] = "Category",
                    ["departmentId"] = null,
                    ["categoryId"] = categoryId ?? ShirtsCategoryId,
                    ["productId"] = null,
                    ["includeSubcategories"] = true,
                },
            },
            ["rowVersion"] = rowVersion,
        };

    /// <summary>Set by <see cref="ShirtsAsync"/> before any edit body is built.</summary>
    private static int ShirtsCategoryId { get; set; }

    private static async Task<int> ShirtsAsync(MediaTestHost host)
    {
        using var scope = host.Factory.Services.CreateScope();

        ShirtsCategoryId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Categories
            .Where(category => category.Slug == "shirts-blouses" && category.Department!.Slug == "women")
            .Select(category => category.Id)
            .SingleAsync(Token);

        return ShirtsCategoryId;
    }

    private static async Task<bool> AssetDeletedAsync(MediaTestHost host, int assetId)
    {
        using var scope = host.Factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().MediaAssets
            .IgnoreQueryFilters()
            .Where(asset => asset.Id == assetId)
            .Select(asset => asset.IsDeleted)
            .SingleAsync(Token);
    }

    private static async Task<int?> BannerColumnAsync(MediaTestHost host, int campaignId)
    {
        using var scope = host.Factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().SaleCampaigns
            .Where(campaign => campaign.Id == campaignId)
            .Select(campaign => campaign.BannerAssetId)
            .SingleAsync(Token);
    }

    private static async Task<int> UsageRowsAsync(MediaTestHost host, int campaignId)
    {
        using var scope = host.Factory.Services.CreateScope();
        var ownerId = campaignId.ToString(CultureInfo.InvariantCulture);

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().MediaUsages
            .CountAsync(usage => usage.OwnerType == MediaUsageOwnerTypes.SaleCampaign && usage.OwnerId == ownerId, Token);
    }

    /// <summary>Removes the campaign through the API, reading its current row version first.</summary>
    private static async Task RemoveCampaignAsync(HttpClient admin, int campaignId)
    {
        using var listed = await admin.GetAsync(new Uri(Url, UriKind.Relative), CancellationToken.None);
        var list = await listed.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        var row = list.EnumerateArray().SingleOrDefault(candidate => candidate.GetProperty("id").GetInt32() == campaignId);

        if (row.ValueKind == JsonValueKind.Undefined)
        {
            return;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            new Uri(string.Create(CultureInfo.InvariantCulture, $"{Url}/{campaignId}"), UriKind.Relative));
        request.Headers.TryAddWithoutValidation("If-Match", row.GetProperty("rowVersion").GetString());

        using var response = await admin.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
