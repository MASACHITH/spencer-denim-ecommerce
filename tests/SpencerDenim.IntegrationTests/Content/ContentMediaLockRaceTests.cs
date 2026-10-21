using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.Tests.MediaFixtures;

namespace SpencerDenim.IntegrationTests.Content;

/// <summary>
/// #215 tester M1, for the other owners of the shared content save (<c>ContentAdminStores.SaveWithUsageAsync</c>):
/// a homepage item saved with a picture at the same moment the media library deletes that picture must not both
/// succeed. The save locks the asset row (UPDLOCK, READCOMMITTEDLOCK) and re-checks it before writing, the lock
/// <c>MediaService.DeleteAsync</c> takes first (#191, doc 08 §5.3), so exactly one wins.
/// </summary>
public sealed class ContentMediaLockRaceTests(SqlServerDatabaseFixture sqlServer)
{
    private const string HomeUrl = "/api/admin/content/home";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] Everything =
    [
        Permissions.Content.View,
        Permissions.Content.Create,
        Permissions.Content.Edit,
        Permissions.Content.Delete,
        Permissions.Media.Create,
        Permissions.Media.View,
        Permissions.Media.Delete,
    ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_homepage_item_save_racing_a_delete_of_its_picture_never_leaves_it_on_a_deleted_asset()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);
        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var client = await host.SignInAsync(Everything);

        var broken = 0;
        var refusedBoth = 0;
        var items = new List<int>();

        try
        {
            for (var round = 0; round < 15; round++)
            {
                var image = await UploadImageAsync(client);
                var social = await SectionAsync(client, "SocialGallery");
                var sectionId = social.GetProperty("id").GetInt32();

                var body = ItemBody(social.GetProperty("rowVersion").GetString(), "Race " + round);
                body["imagePublicId"] = image;

                var save = SendAsync(client, "POST", $"{HomeUrl}/sections/{sectionId}/items", body);
                var delete = client.DeleteAsync(new Uri($"/api/admin/media/{image}", UriKind.Relative), Token);
                using var saveAnswer = await save;
                using var deleteAnswer = await delete;

                if (saveAnswer.StatusCode == HttpStatusCode.Created)
                {
                    items.Add((await saveAnswer.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("id").GetInt32());
                }

                if (saveAnswer.StatusCode == HttpStatusCode.Created && deleteAnswer.IsSuccessStatusCode)
                {
                    broken++;
                }

                if (saveAnswer.StatusCode != HttpStatusCode.Created && !deleteAnswer.IsSuccessStatusCode)
                {
                    refusedBoth++;
                }

                foreach (var id in items)
                {
                    await DeleteItemAsync(host, id);
                }

                items.Clear();
            }

            Assert.True(broken == 0, string.Create(CultureInfo.InvariantCulture, $"{broken} of 15 rounds left an item on a deleted picture."));
            Assert.True(refusedBoth == 0, string.Create(CultureInfo.InvariantCulture, $"{refusedBoth} of 15 rounds refused both."));
        }
        finally
        {
            foreach (var id in items)
            {
                await DeleteItemAsync(host, id);
            }
        }
    }

    private static Dictionary<string, object?> ItemBody(string? rowVersion, string title) => new()
    {
        ["title"] = title,
        ["subtitle"] = null,
        ["body"] = null,
        ["buttonLabel"] = null,
        ["linkUrl"] = "https://instagram.com/colombowardrobe",
        ["imagePublicId"] = null,
        ["mobileImagePublicId"] = null,
        ["videoPublicId"] = null,
        ["departmentId"] = null,
        ["productSlug"] = null,
        ["iconKey"] = null,
        ["sortOrder"] = 90,
        ["isEnabled"] = false,
        ["startsAt"] = null,
        ["endsAt"] = null,
        ["rowVersion"] = rowVersion,
    };

    private static async Task<JsonElement> SectionAsync(HttpClient client, string type)
    {
        using var response = await client.GetAsync(new Uri(HomeUrl, UriKind.Relative), Token);
        var home = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
        return home.GetProperty("sections").EnumerateArray().Single(section => section.GetProperty("type").GetString() == type);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string url, object body)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(url, UriKind.Relative))
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        return await client.SendAsync(request, Token);
    }

    private static async Task<Guid> UploadImageAsync(HttpClient client)
    {
        using var form = MediaTestHost.FilePart("file", TestImages.UniqueJpeg(), "race.jpg", "image/jpeg");
        form.Add(new StringContent("A race picture"), "altText");

        using var response = await client.PostAsync(new Uri("/api/admin/media/images", UriKind.Relative), form, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("publicId").GetGuid();
    }

    private static async Task DeleteItemAsync(MediaTestHost host, int itemId)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = itemId.ToString(CultureInfo.InvariantCulture);

        await db.MediaUsages.Where(usage => usage.OwnerType == MediaUsageOwnerTypes.HomeSectionItem && usage.OwnerId == owner).ExecuteDeleteAsync(Token);
        await db.HomeSectionItems.Where(item => item.Id == itemId).ExecuteDeleteAsync(Token);
    }
}
