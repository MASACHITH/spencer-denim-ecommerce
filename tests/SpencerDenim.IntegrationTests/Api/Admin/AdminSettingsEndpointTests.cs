using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Media;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.Tests.MediaFixtures;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The ADM-22 settings endpoints and GLB-02's logo, end to end against a real SQL Server (doc 06 §4).
/// <para>
/// <b>Every test puts the five settings rows back.</b> The fixture's database is shared by the whole
/// assembly (classes run sequentially — see <c>AssemblyInfo.cs</c>), and the storefront tests read the same
/// rows, so a logo left set or a social address left behind would fail somebody else's test with no clue
/// where it came from. The restore is in a <c>finally</c> and it clears the <c>MediaUsages</c> rows too.
/// </para>
/// <para>
/// A real <see cref="MediaTestHost"/> rather than a database-only fixture, because the logo path is a real
/// upload: the screen posts a file to the media library and then points the setting at the result, and the
/// interesting failures — a non-image asset, an asset that is not ready, the library refusing to delete a
/// logo in use — are all facts about what is actually in the library.
/// </para>
/// </summary>
public sealed class AdminSettingsEndpointTests(SqlServerDatabaseFixture sqlServer)
{
    private const string SettingsUrl = "/api/admin/settings";
    private const string StoreLogoUrl = "/api/admin/settings/store-logo";
    private const string ContactSocialUrl = "/api/admin/settings/contact-social";
    private const string SiteSettingsUrl = "/api/site/settings";
    private const string ImagesUrl = "/api/admin/media/images";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- authentication and authorization -------------------------------------------------------

    [Fact]
    public async Task Every_route_refuses_an_unauthenticated_caller()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        foreach (var (method, path) in Routes())
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
            using var response = await client.SendAsync(request, Token);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    /// <summary>
    /// A signed-in user with no settings rights at all. The role exists and grants nothing, so this proves
    /// the check is "holds <c>Settings.Edit</c>", not "is signed in".
    /// </summary>
    [Fact]
    public async Task Every_route_refuses_a_signed_in_user_without_settings_edit()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Media.Create, Permissions.Categories.Edit);

        foreach (var (method, path) in Routes())
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
            if (method != HttpMethod.Get)
            {
                request.Content = JsonContent.Create(new { rowVersion = "AAAAAAAAAAE=" });
            }

            using var response = await client.SendAsync(request, Token);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    /// <summary>
    /// <b>The public storefront endpoint is anonymous and must stay that way</b> — it is read during the
    /// server render of every page, by a visitor who has no session.
    /// </summary>
    [Fact]
    public async Task The_storefront_settings_endpoint_is_readable_without_a_session()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        using var response = await client.GetAsync(new Uri(SiteSettingsUrl, UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- the read model -------------------------------------------------------------------------

    [Fact]
    public async Task The_read_model_returns_the_seeded_groups_with_a_row_version_each()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        var body = await GetJsonAsync(client, SettingsUrl);

        var groups = body.GetProperty("groups").EnumerateArray()
            .Select(group => group.GetProperty("groupKey").GetString())
            .ToList();

        Assert.Equal(["Store", "Contact", "Social"], groups);

        foreach (var group in body.GetProperty("groups").EnumerateArray())
        {
            // Every group carries a token the screen sends straight back; a blank one would make the
            // concurrency check unsatisfiable and every save a 409.
            Assert.False(string.IsNullOrWhiteSpace(group.GetProperty("rowVersion").GetString()));
            Assert.NotEmpty(group.GetProperty("settings").EnumerateArray());
        }
    }

    [Fact]
    public async Task Every_seeded_key_arrives_with_the_facts_the_screen_needs()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        var byKey = await ReadSettingsAsync(client);

        Assert.Equal(
            SettingCatalog.All.Select(definition => definition.Key).Order(StringComparer.Ordinal),
            byKey.Keys.Order(StringComparer.Ordinal));

        var logo = byKey[SettingKeys.Store.LogoAssetId];
        Assert.Equal("MediaAssetRef", logo.GetProperty("valueType").GetString());
        Assert.Equal(Permissions.Settings.Edit, logo.GetProperty("requiredPermission").GetString());
        Assert.False(logo.GetProperty("isSecret").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(logo.GetProperty("description").GetString()));

        Assert.Equal("String", byKey[SettingKeys.Contact.WhatsApp].GetProperty("valueType").GetString());
    }

    /// <summary>
    /// <b>The trap this task was warned about, as an assertion.</b> The shop's name is a compile-time
    /// constant in one place per application (W1-15, docs/02 §I2) — so no settings key may be it, and the
    /// settings payload must not carry one. A name box is the obvious way to "finish" ADM-22's "store info".
    /// </summary>
    [Fact]
    public async Task The_settings_payload_carries_no_shop_name_field()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        var byKey = await ReadSettingsAsync(client);

        Assert.DoesNotContain(
            byKey.Keys,
            key => key.EndsWith(".Name", StringComparison.OrdinalIgnoreCase)
                || key.Contains("StoreName", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Doc 08 §3.8 DB2: a group whose <c>RequiredPermission</c> the caller lacks is <b>absent</b>, not
    /// empty. An empty <c>Bank</c> group would confirm to a Store Manager that bank details exist.
    /// <para>
    /// No <c>Bank</c> key is seeded, so the row is inserted directly for the duration of the test — which is
    /// the only way to prove the filter before #53 lands, and the point of building it now.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_group_the_caller_may_not_read_is_absent_rather_than_empty()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        await InsertRowAsync(host, new Setting
        {
            Key = "Bank.AccountNumber",
            GroupKey = "Bank",
            ValueType = SettingValueType.String,
            Value = "8001234567",
            RequiredPermission = Permissions.Settings.Payments,
            Description = "Inserted by AdminSettingsEndpointTests.",
        });

        try
        {
            var body = await GetJsonAsync(client, SettingsUrl);

            var groups = body.GetProperty("groups").EnumerateArray()
                .Select(group => group.GetProperty("groupKey").GetString())
                .ToList();

            Assert.DoesNotContain("Bank", groups);

            // And the value is nowhere in the payload at all, including in a field nobody thought to check.
            Assert.DoesNotContain("8001234567", body.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            await DeleteRowAsync(host, "Bank.AccountNumber");
        }
    }

    /// <summary>
    /// <b>A secret's value never leaves the server, for anybody.</b> Stronger than doc 08 requires and
    /// deliberately so — see <c>AdminSettingDto</c>. The caller here holds the key's own permission, which is
    /// exactly the caller a permission-based guard would let through.
    /// <para>
    /// <b>Why a catalogued key is flipped rather than a new one inserted.</b> The first version of this test
    /// inserted <c>Contact.TestSecret</c> and then looked for it in the payload — and could not find it,
    /// because the read model projects only the keys in <see cref="SettingCatalog"/>. That is correct
    /// behaviour (the catalogue is the allow-list) and it made the test vacuous: it would have passed just as
    /// well with the redaction removed. So an existing key is made secret for the duration instead, which is
    /// the only way to get a secret row onto the read model's own path.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_secrets_value_is_never_returned_even_to_a_caller_who_can_read_its_group()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        const string Ciphertext = "CfDJ8-pretend-ciphertext-nobody-should-see";

        await MakeSecretAsync(host, SettingKeys.Contact.WhatsApp, Ciphertext);

        try
        {
            var body = await GetJsonAsync(client, SettingsUrl);

            Assert.DoesNotContain("pretend-ciphertext", body.GetRawText(), StringComparison.Ordinal);

            var secret = body.GetProperty("groups").EnumerateArray()
                .SelectMany(group => group.GetProperty("settings").EnumerateArray())
                .Single(setting => setting.GetProperty("key").GetString() == SettingKeys.Contact.WhatsApp);

            Assert.Equal(JsonValueKind.Null, secret.GetProperty("value").ValueKind);
            Assert.True(secret.GetProperty("isSecret").GetBoolean());
            Assert.Equal("Secret", secret.GetProperty("valueType").GetString());

            // The one thing a screen can learn, and all it needs: "a value is set, enter a new one to replace".
            Assert.True(secret.GetProperty("hasValue").GetBoolean());
        }
        finally
        {
            await UnmakeSecretAsync(host, SettingKeys.Contact.WhatsApp);
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// And not on the anonymous storefront payload either.
    /// <para>
    /// <b>Two independent reasons it cannot appear, and the test states which is which.</b> The payload names
    /// five keys rather than selecting "every non-secret setting", so a key outside that list — every secret
    /// doc 08 §3.8 lists is outside it — has nowhere to go. And for a key that <i>is</i> on the list, the
    /// value is still re-validated on the way out, so ciphertext in <c>Contact.WhatsApp</c> fails
    /// <c>IsAcceptableWhatsAppNumber</c> and is dropped. The second is the one this test exercises, because it
    /// is the only one a synthetic scenario can reach — and it is worth knowing that the public reader's
    /// safety comes from the key allow-list and per-value validation rather than from the <c>IsSecret</c>
    /// flag, which it never reads.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_secret_never_reaches_the_anonymous_storefront_payload()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        const string Ciphertext = "CfDJ8-pretend-ciphertext-nobody-should-see";

        await MakeSecretAsync(host, SettingKeys.Contact.WhatsApp, Ciphertext);

        try
        {
            var body = await GetJsonAsync(client, SiteSettingsUrl);

            Assert.DoesNotContain("pretend-ciphertext", body.GetRawText(), StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.Null, body.GetProperty("whatsAppNumber").ValueKind);

            // The payload has three members and no general-purpose value, so there is nowhere for one to be.
            Assert.Equal(
                ["logo", "social", "whatsAppNumber"],
                body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        }
        finally
        {
            await UnmakeSecretAsync(host, SettingKeys.Contact.WhatsApp);
            await RestoreAsync(host);
        }
    }

    // ---- the logo ------------------------------------------------------------------------------

    /// <summary>
    /// The whole flow the owner asked for: upload, see it, replace it, and remove it — with the usage rows
    /// following every step, because that is what lets them delete the old file afterwards.
    /// </summary>
    [Fact]
    public async Task Setting_replacing_and_removing_the_logo_moves_the_usage_row_with_it()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit, Permissions.Media.Create);

        try
        {
            var first = await UploadImageAsync(client, TestImages.UniquePng(600, 150), "logo-one.png");
            var second = await UploadImageAsync(client, TestImages.UniquePng(620, 160), "logo-two.png");

            // --- set
            Assert.Equal(HttpStatusCode.NoContent, await SetLogoAsync(client, first));

            var afterSet = await UsagesAsync(host);
            Assert.Equal(await AssetIdAsync(host, first), Assert.Single(afterSet).MediaAssetId);
            Assert.Equal(MediaUsageRoles.Logo, afterSet[0].UsageRole);
            Assert.Equal(SettingKeys.Store.LogoAssetId, afterSet[0].OwnerId);

            // --- replace: exactly one usage row, naming the new asset. The old one has to be released, or
            // the client can never delete the file they just replaced — which is what they asked for.
            Assert.Equal(HttpStatusCode.NoContent, await SetLogoAsync(client, second));

            var afterReplace = await UsagesAsync(host);
            Assert.Equal(await AssetIdAsync(host, second), Assert.Single(afterReplace).MediaAssetId);

            // --- remove
            Assert.Equal(HttpStatusCode.NoContent, await SetLogoAsync(client, null));

            Assert.Empty(await UsagesAsync(host));
            Assert.Null(await ValueAsync(host, SettingKeys.Store.LogoAssetId));
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// The guard that makes the usage row worth writing (W1-04): the library refuses to delete the live
    /// logo, and says what is using it.
    /// </summary>
    [Fact]
    public async Task The_media_library_refuses_to_delete_the_live_logo_and_names_the_setting()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(
            Permissions.Settings.Edit, Permissions.Media.Create, Permissions.Media.Delete, Permissions.Media.View);

        try
        {
            var asset = await UploadImageAsync(client, TestImages.UniquePng(600, 150), "guarded-logo.png");
            Assert.Equal(HttpStatusCode.NoContent, await SetLogoAsync(client, asset));

            using var refused = await client.DeleteAsync(new Uri($"/api/admin/media/{asset}", UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
            var usages = problem.GetProperty("usages").EnumerateArray().ToList();

            var usage = Assert.Single(usages);
            Assert.Equal(MediaUsageOwnerTypes.Setting, usage.GetProperty("ownerType").GetString());
            Assert.Equal(SettingKeys.Store.LogoAssetId, usage.GetProperty("ownerId").GetString());

            // A label a person recognises, rather than "Setting Store.LogoAssetId" — this is the screen the
            // owner asked for so they could remove an old logo, so the refusal has to be readable.
            Assert.False(string.IsNullOrWhiteSpace(usage.GetProperty("ownerLabel").GetString()));

            // …and once the logo is removed, the same delete succeeds. The release is the half that is easy
            // to forget, and without this assertion the test above would pass with a permanent 409.
            Assert.Equal(HttpStatusCode.NoContent, await SetLogoAsync(client, null));

            using var allowed = await client.DeleteAsync(new Uri($"/api/admin/media/{asset}", UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    [Fact]
    public async Task The_read_model_shows_the_logo_once_it_is_set()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit, Permissions.Media.Create);

        try
        {
            var asset = await UploadImageAsync(client, TestImages.UniquePng(600, 150), "shown-logo.png");
            await SetLogoAsync(client, asset);

            var body = await GetJsonAsync(client, SettingsUrl);
            var logo = body.GetProperty("logo");

            Assert.Equal(asset.ToString(), logo.GetProperty("publicId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(logo.GetProperty("url").GetString()));
            Assert.Equal(600, logo.GetProperty("width").GetInt32());
            Assert.Equal(150, logo.GetProperty("height").GetInt32());
            Assert.Equal("shown-logo.png", logo.GetProperty("originalFileName").GetString());

            // The internal MediaAssets.Id is what the setting stores and must never be in the payload.
            var assetId = await AssetIdAsync(host, asset);
            Assert.DoesNotContain(
                $"\"{assetId.ToString(CultureInfo.InvariantCulture)}\"",
                logo.GetRawText(),
                StringComparison.Ordinal);
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    [Fact]
    public async Task An_asset_that_does_not_exist_is_a_named_field_error_rather_than_a_500()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        var (status, body) = await PutAsync(
            client, StoreLogoUrl, new { logoAssetPublicId = Guid.NewGuid(), rowVersion = await StoreVersionAsync(client) });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("logoAssetPublicId", out _));
    }

    /// <summary>
    /// A video, not an image. A <c>MediaAssetRef</c> in the header has to be drawable, and the refusal has to
    /// come on the way in rather than as a broken element on every page.
    /// </summary>
    [Fact]
    public async Task An_asset_that_is_not_an_image_is_refused()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit, Permissions.Media.Create);

        var video = await UploadVideoAsync(client);

        var (status, body) = await PutAsync(
            client, StoreLogoUrl, new { logoAssetPublicId = video, rowVersion = await StoreVersionAsync(client) });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("logoAssetPublicId", out _));

        Assert.Null(await ValueAsync(host, SettingKeys.Store.LogoAssetId));
        Assert.Empty(await UsagesAsync(host));
    }

    /// <summary>
    /// An asset the library has soft-deleted. Reachable in practice: delete the old logo, then a stale screen
    /// re-sends it. The setting must not end up pointing at a file that is scheduled for purging.
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_asset_is_refused()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(
            Permissions.Settings.Edit, Permissions.Media.Create, Permissions.Media.Delete);

        var asset = await UploadImageAsync(client, TestImages.UniquePng(600, 150), "gone-logo.png");

        using var deleted = await client.DeleteAsync(new Uri($"/api/admin/media/{asset}", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var (status, _) = await PutAsync(
            client, StoreLogoUrl, new { logoAssetPublicId = asset, rowVersion = await StoreVersionAsync(client) });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Null(await ValueAsync(host, SettingKeys.Store.LogoAssetId));
    }

    // ---- contact and social --------------------------------------------------------------------

    [Fact]
    public async Task Saving_the_contact_and_social_settings_stores_every_field()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        try
        {
            var (status, _) = await PutAsync(client, ContactSocialUrl, new
            {
                whatsApp = "94771234567",
                facebookUrl = "https://www.facebook.com/colombowardrobe",
                instagramUrl = "https://www.instagram.com/colombowardrobe",
                tikTokUrl = "https://www.tiktok.com/@colombowardrobe",
                rowVersion = await ContactVersionAsync(client),
            });

            Assert.Equal(HttpStatusCode.NoContent, status);

            // Read back from the database, not from the payload: the W1-12 blocker was a field that never
            // reached the request, and a round trip through the same read model could not have seen it.
            Assert.Equal("94771234567", await ValueAsync(host, SettingKeys.Contact.WhatsApp));
            Assert.Equal("https://www.facebook.com/colombowardrobe", await ValueAsync(host, SettingKeys.Social.FacebookUrl));
            Assert.Equal("https://www.instagram.com/colombowardrobe", await ValueAsync(host, SettingKeys.Social.InstagramUrl));
            Assert.Equal("https://www.tiktok.com/@colombowardrobe", await ValueAsync(host, SettingKeys.Social.TikTokUrl));
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// Blank clears, which is how a client says "we have no TikTok" — and the cleared value is
    /// <see langword="null"/> rather than <c>""</c>, so "has a TikTok URL" is false for a shop that has none.
    /// </summary>
    [Fact]
    public async Task A_blank_value_clears_the_setting_as_null_rather_than_an_empty_string()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        try
        {
            await PutAsync(client, ContactSocialUrl, new
            {
                whatsApp = "94771234567",
                facebookUrl = "https://www.facebook.com/x",
                instagramUrl = (string?)null,
                tikTokUrl = "   ",
                rowVersion = await ContactVersionAsync(client),
            });

            Assert.Null(await ValueAsync(host, SettingKeys.Social.InstagramUrl));
            Assert.Null(await ValueAsync(host, SettingKeys.Social.TikTokUrl));
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// The attack list, on the wire. Each of these would be an <c>href</c> on every page of the shop.
    /// </summary>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("/women/jeans")]
    [InlineData("//evil.example/")]
    [InlineData("http:///profile")]
    [InlineData("https://instagram.com@evil.example/")]
    public async Task A_social_address_a_browser_should_not_follow_is_refused_and_nothing_is_written(string url)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        var (status, body) = await PutAsync(client, ContactSocialUrl, new
        {
            whatsApp = (string?)null,
            facebookUrl = (string?)null,
            instagramUrl = url,
            tikTokUrl = (string?)null,
            rowVersion = await ContactVersionAsync(client),
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("instagramUrl", out _));

        // Nothing at all was written — not even the fields that were valid. A partly applied form save is
        // worse than a refused one, because nobody can tell which half landed.
        Assert.Null(await ValueAsync(host, SettingKeys.Social.InstagramUrl));
    }

    [Fact]
    public async Task A_whats_app_number_that_is_not_in_international_form_is_refused()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        var (status, body) = await PutAsync(client, ContactSocialUrl, new
        {
            whatsApp = "0771234567",
            facebookUrl = (string?)null,
            instagramUrl = (string?)null,
            tikTokUrl = (string?)null,
            rowVersion = await ContactVersionAsync(client),
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("whatsApp", out _));
    }

    /// <summary>Several bad fields come back together, so the screen can mark all of them at once.</summary>
    [Fact]
    public async Task Every_rejected_field_is_named_in_one_response()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        var (status, body) = await PutAsync(client, ContactSocialUrl, new
        {
            whatsApp = "+94 77 123 4567",
            facebookUrl = "javascript:alert(1)",
            instagramUrl = "/women",
            tikTokUrl = "https://www.tiktok.com/@ok",
            rowVersion = await ContactVersionAsync(client),
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);

        var errors = body.GetProperty("errors");
        Assert.True(errors.TryGetProperty("whatsApp", out _));
        Assert.True(errors.TryGetProperty("facebookUrl", out _));
        Assert.True(errors.TryGetProperty("instagramUrl", out _));
        Assert.False(errors.TryGetProperty("tikTokUrl", out _));
    }

    // ---- concurrency, antiforgery and malformed bodies ------------------------------------------

    [Fact]
    public async Task A_stale_row_version_is_a_409_naming_the_reason()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        try
        {
            var stale = await ContactVersionAsync(client);

            // Somebody else saves first, which moves the group's token.
            var (first, _) = await PutAsync(client, ContactSocialUrl, new
            {
                whatsApp = "94770000001",
                facebookUrl = (string?)null,
                instagramUrl = (string?)null,
                tikTokUrl = (string?)null,
                rowVersion = stale,
            });
            Assert.Equal(HttpStatusCode.NoContent, first);

            var (status, body) = await PutAsync(client, ContactSocialUrl, new
            {
                whatsApp = "94770000002",
                facebookUrl = (string?)null,
                instagramUrl = (string?)null,
                tikTokUrl = (string?)null,
                rowVersion = stale,
            });

            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal(SettingsConflictCodes.StaleRowVersion, body.GetProperty("code").GetString());

            // The first save stands; the second wrote nothing.
            Assert.Equal("94770000001", await ValueAsync(host, SettingKeys.Contact.WhatsApp));
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// A missing token is refused too, rather than taken as "no opinion": a client that did not send one
    /// cannot have seen the current rows, and allowing the write is how optimistic concurrency becomes
    /// decorative.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!!")]
    public async Task A_missing_or_malformed_row_version_is_a_409(string? rowVersion)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        var (status, body) = await PutAsync(client, ContactSocialUrl, new
        {
            whatsApp = "94770000003",
            facebookUrl = (string?)null,
            instagramUrl = (string?)null,
            tikTokUrl = (string?)null,
            rowVersion,
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(SettingsConflictCodes.StaleRowVersion, body.GetProperty("code").GetString());
        Assert.Null(await ValueAsync(host, SettingKeys.Contact.WhatsApp));
    }

    [Fact]
    public async Task A_logo_write_with_a_stale_row_version_is_a_409()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit, Permissions.Media.Create);

        try
        {
            var asset = await UploadImageAsync(client, TestImages.UniquePng(600, 150), "stale-logo.png");
            var stale = await StoreVersionAsync(client);

            Assert.Equal(HttpStatusCode.NoContent, await SetLogoAsync(client, asset, stale));

            var (status, body) = await PutAsync(
                client, StoreLogoUrl, new { logoAssetPublicId = (Guid?)null, rowVersion = stale });

            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal(SettingsConflictCodes.StaleRowVersion, body.GetProperty("code").GetString());

            // The logo is still set: a refused removal must not remove anything.
            Assert.NotNull(await ValueAsync(host, SettingKeys.Store.LogoAssetId));
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    [Fact]
    public async Task A_mutating_request_without_the_antiforgery_header_is_refused()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        var token = await ContactVersionAsync(client);
        client.DefaultRequestHeaders.Remove(MediaTestHost.AntiforgeryHeader);

        foreach (var path in new[] { StoreLogoUrl, ContactSocialUrl })
        {
            using var response = await client.PutAsJsonAsync(
                new Uri(path, UriKind.Relative), new { rowVersion = token }, Token);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
            Assert.Equal("invalid_antiforgery_token", problem.GetProperty("code").GetString());
        }
    }

    /// <summary>
    /// <b>Issue #47, which is diagnosed and must not be repeated.</b> A well-formed JSON object with a
    /// required string property omitted binds that property to <see langword="null"/> on a non-nullable
    /// reference type, and the admin login answers <b>500</b>. Every field on these request records is
    /// nullable, so an omitted one is the use case's decision.
    /// <para>
    /// Six bodies: an empty object, one field, the literal <c>null</c>, broken syntax, a mistyped field, and
    /// the right shape with the wrong content type. None may be a 500 — in <c>Testing</c> or in
    /// <c>Development</c>, where minimal APIs are configured to throw and <c>UseExceptionHandler</c> turns the
    /// throw into one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_malformed_body_is_never_a_500_in_any_environment()
    {
        foreach (var environment in new[] { "Testing", "Development" })
        {
            await using var host = await StartAsync(environment);
            using var client = await host.SignInAsync(Permissions.Settings.Edit);

            (string Body, string ContentType)[] bodies =
            [
                ("{}", "application/json"),
                ("""{"whatsApp":"94771234567"}""", "application/json"),
                ("null", "application/json"),
                ("{bad", "application/json"),
                ("""{"whatsApp":123}""", "application/json"),
                ("""{"instagramUrl":{"nested":true}}""", "application/json"),
                ("""{"whatsApp":"94771234567"}""", "text/plain"),

                // Valid JSON that is not an object. Since round 1 the endpoints read a JsonObject, so these
                // reach a different path from "{bad" — and each one has to be a 400 rather than an unhandled
                // cast or a null-reference deeper in.
                ("[1,2,3]", "application/json"),
                (""""just a string"""", "application/json"),
                ("42", "application/json"),
                ("true", "application/json"),
            ];

            foreach (var path in new[] { StoreLogoUrl, ContactSocialUrl })
            {
                foreach (var (body, contentType) in bodies)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(path, UriKind.Relative))
                    {
                        Content = new StringContent(body, System.Text.Encoding.UTF8, contentType),
                    };

                    using var response = await client.SendAsync(request, Token);

                    Assert.True(
                        (int)response.StatusCode is >= 400 and < 500,
                        $"{environment} {path} with body {body} as {contentType} answered "
                        + $"{(int)response.StatusCode}; a client mistake must never be a 5xx (issue #47).");
                }
            }
        }
    }

    /// <summary>
    /// The specific #47 shape, asserted on its own so the reason is on the record: an object that omits every
    /// field is a <b>409</b> here (no row version), never a 500 and never a silent success that clears four
    /// settings.
    /// </summary>
    [Fact]
    public async Task An_empty_object_body_is_refused_rather_than_clearing_every_setting()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        try
        {
            await PutAsync(client, ContactSocialUrl, new
            {
                whatsApp = "94771234567",
                facebookUrl = "https://www.facebook.com/x",
                instagramUrl = (string?)null,
                tikTokUrl = (string?)null,
                rowVersion = await ContactVersionAsync(client),
            });

            var (status, _) = await PutRawAsync(client, ContactSocialUrl, "{}");

            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal("94771234567", await ValueAsync(host, SettingKeys.Contact.WhatsApp));
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    // ---- audit -----------------------------------------------------------------------------------

    /// <summary>
    /// Doc 08 §3.8: one audit row per changed key, and it commits with the change (issue #59). A logo
    /// replacement in particular is the most visible change anybody can make to this shop.
    /// </summary>
    [Fact]
    public async Task One_audit_row_is_written_per_changed_key_and_none_for_an_unchanged_one()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        try
        {
            var before = await AuditCountAsync(host);

            await PutAsync(client, ContactSocialUrl, new
            {
                whatsApp = "94771234567",
                facebookUrl = "https://www.facebook.com/one",
                instagramUrl = (string?)null,
                tikTokUrl = (string?)null,
                rowVersion = await ContactVersionAsync(client),
            });

            var rows = await AuditRowsAsync(host, before);

            Assert.Equal(2, rows.Count);
            Assert.Equal(
                [SettingKeys.Contact.WhatsApp, SettingKeys.Social.FacebookUrl],
                rows.Select(row => row.EntityId).Order(StringComparer.Ordinal));
            Assert.All(rows, row => Assert.Equal(AuditActions.SettingChange, row.Action));
            Assert.All(rows, row => Assert.Equal("Setting", row.EntityType));

            // The actor is recorded, which is the whole point: "who put that link there" has to have an
            // answer (issue #59).
            Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.UserEmail)));

            // Saving the identical values again changes nothing, so writes nothing. A trail full of
            // "changed nothing" rows is how an audit log stops being read.
            var afterFirst = await AuditCountAsync(host);

            var (status, _) = await PutAsync(client, ContactSocialUrl, new
            {
                whatsApp = "94771234567",
                facebookUrl = "https://www.facebook.com/one",
                instagramUrl = (string?)null,
                tikTokUrl = (string?)null,
                rowVersion = await ContactVersionAsync(client),
            });

            Assert.Equal(HttpStatusCode.NoContent, status);
            Assert.Equal(afterFirst, await AuditCountAsync(host));
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    [Fact]
    public async Task A_logo_change_is_audited_with_both_asset_ids()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit, Permissions.Media.Create);

        try
        {
            var asset = await UploadImageAsync(client, TestImages.UniquePng(600, 150), "audited-logo.png");
            var before = await AuditCountAsync(host);

            await SetLogoAsync(client, asset);

            var row = Assert.Single(await AuditRowsAsync(host, before));

            Assert.Equal(SettingKeys.Store.LogoAssetId, row.EntityId);
            Assert.Equal(AuditActions.SettingChange, row.Action);

            var expected = (await AssetIdAsync(host, asset)).ToString(CultureInfo.InvariantCulture);
            Assert.Contains(expected, row.AfterJson!, StringComparison.Ordinal);
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// A refused write leaves no audit row — which is the right answer for a validation failure and what
    /// <c>IAuditLogWriter.Enlist</c> is for: the trail and the change are one transaction, so a trail of a
    /// change that never happened cannot exist.
    /// </summary>
    [Fact]
    public async Task A_refused_write_leaves_no_audit_row()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        var before = await AuditCountAsync(host);

        await PutAsync(client, ContactSocialUrl, new
        {
            whatsApp = (string?)null,
            facebookUrl = "javascript:alert(1)",
            instagramUrl = (string?)null,
            tikTokUrl = (string?)null,
            rowVersion = await ContactVersionAsync(client),
        });

        Assert.Equal(before, await AuditCountAsync(host));
    }

    /// <summary>
    /// Doc 08 §3.8's redaction list, on the wire: a secret's value is logged as <c>[secret changed]</c>.
    /// <c>ops.AuditLogs</c> is kept for three years and has no delete path, so this is the one place a secret
    /// could become permanent.
    /// </summary>
    [Fact]
    public async Task A_secrets_value_never_reaches_an_audit_row()
    {
        await using var host = await StartAsync();

        await InsertRowAsync(host, new Setting
        {
            Key = "Contact.TestSecret",
            GroupKey = SettingKeys.Contact.Group,
            ValueType = SettingValueType.Secret,
            IsSecret = true,
            RequiredPermission = Permissions.Settings.Edit,
            Description = "Inserted by AdminSettingsEndpointTests.",
        });

        try
        {
            // Written through the Application layer rather than an endpoint, because no endpoint writes a
            // secret in this build — which is exactly why the guard has to be proved another way.
            await using var scope = host.Factory.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<ISettingSecretProtector>();

            var row = (await store.ListAsync(["Contact.TestSecret"], Token)).Single();
            var ciphertext = protector.Protect("super-secret-value");

            var audit = SettingsAuditTrail.ForValueChange(
                row, ciphertext, SpencerDenim.Application.Abstractions.Auditing.AdminActor.Unknown, DateTime.UtcNow)!;

            row.Value = ciphertext;
            dbContext.AuditLogs.Add(audit);
            await dbContext.SaveChangesAsync(Token);

            var stored = await dbContext.AuditLogs.AsNoTracking()
                .OrderByDescending(log => log.Id)
                .FirstAsync(Token);

            Assert.Equal(SettingsAuditTrail.RedactedSecret, ValueIn(stored.AfterJson));
            Assert.DoesNotContain(ciphertext, stored.AfterJson!, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret-value", stored.AfterJson!, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteRowAsync(host, "Contact.TestSecret");
        }
    }

    // ---- the storefront payload ------------------------------------------------------------------

    /// <summary>
    /// <b>The storefront changes when the settings change</b>, through the API a page actually reads —
    /// not by asserting a row. The browser-level proof is in <c>e2e/site-branding.e2e.ts</c>.
    /// </summary>
    [Fact]
    public async Task The_storefront_payload_follows_the_settings()
    {
        await using var host = await StartAsync();
        using var admin = await host.SignInAsync(Permissions.Settings.Edit, Permissions.Media.Create);
        using var visitor = host.CreateAnonymousClient();

        try
        {
            // Nothing configured: no logo, no social icons. The state every visitor meets today.
            var empty = await GetJsonAsync(visitor, SiteSettingsUrl);
            Assert.Equal(JsonValueKind.Null, empty.GetProperty("logo").ValueKind);
            Assert.Empty(empty.GetProperty("social").EnumerateArray());

            var asset = await UploadImageAsync(admin, TestImages.UniquePng(600, 150), "storefront-logo.png");
            await SetLogoAsync(admin, asset);
            await PutAsync(admin, ContactSocialUrl, new
            {
                whatsApp = "94771234567",
                facebookUrl = (string?)null,
                instagramUrl = "https://www.instagram.com/colombowardrobe",
                tikTokUrl = "https://www.tiktok.com/@colombowardrobe",
                rowVersion = await ContactVersionAsync(admin),
            });

            var configured = await GetJsonAsync(visitor, SiteSettingsUrl);

            // The cache is evicted by the save, so this is immediate rather than within sixty seconds — which
            // matters because a client who uploads a logo and still sees the wordmark concludes it failed.
            var logo = configured.GetProperty("logo");
            Assert.False(string.IsNullOrWhiteSpace(logo.GetProperty("url").GetString()));
            Assert.Equal(600, logo.GetProperty("width").GetInt32());
            Assert.Equal("94771234567", configured.GetProperty("whatsAppNumber").GetString());

            // Facebook was left empty, so it is *absent* — not present with a null url for the footer to
            // render as a dead icon.
            Assert.Equal(
                ["instagram", "tiktok"],
                configured.GetProperty("social").EnumerateArray()
                    .Select(link => link.GetProperty("network").GetString())
                    .Order(StringComparer.Ordinal));

            foreach (var link in configured.GetProperty("social").EnumerateArray())
            {
                Assert.StartsWith("https://", link.GetProperty("url").GetString()!, StringComparison.Ordinal);
            }
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// A value the current rule would refuse, written straight into the row — an earlier build, a migration,
    /// a database edit. The read model refuses it on the way out as well, so the link disappears rather than
    /// going live.
    /// </summary>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("/women/jeans")]
    [InlineData("//evil.example/")]
    public async Task A_stored_address_the_rule_would_refuse_is_dropped_on_the_way_out(string url)
    {
        await using var host = await StartAsync();
        using var visitor = host.CreateAnonymousClient();

        await WriteDirectlyAsync(host, SettingKeys.Social.InstagramUrl, url);

        try
        {
            var body = await GetJsonAsync(visitor, SiteSettingsUrl);

            Assert.Empty(body.GetProperty("social").EnumerateArray());
            Assert.DoesNotContain(url, body.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// A logo reference pointing at an asset that is gone. The header has to fall back to the wordmark rather
    /// than show a broken image — so "no logo" and "a dangling reference" have to be the same payload.
    /// </summary>
    [Fact]
    public async Task A_dangling_logo_reference_reads_as_no_logo_rather_than_a_broken_one()
    {
        await using var host = await StartAsync();
        using var visitor = host.CreateAnonymousClient();

        await WriteDirectlyAsync(host, SettingKeys.Store.LogoAssetId, "2147483647");

        try
        {
            var body = await GetJsonAsync(visitor, SiteSettingsUrl);

            Assert.Equal(JsonValueKind.Null, body.GetProperty("logo").ValueKind);
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// <b>W2-10's lesson: a shell element must never take the page down.</b> With the database unreachable
    /// the endpoint still answers 200 with the empty payload — the same payload an unconfigured shop gets, so
    /// the storefront has one rendering for both and no second, less-looked-at path.
    /// </summary>
    [Fact]
    public async Task The_storefront_payload_answers_200_and_empty_when_the_database_is_unreachable()
    {
        sqlServer.SkipIfUnavailable();

        await using var factory = new ApiFactory(ApiFactory.UnreachableConnectionString);
        using var client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
            });

        using var response = await client.GetAsync(new Uri(SiteSettingsUrl, UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("logo").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("whatsAppNumber").ValueKind);
        Assert.Empty(body.GetProperty("social").EnumerateArray());
    }

    /// <summary>HEAD, because a monitor or a link checker leads with it.</summary>
    [Fact]
    public async Task The_storefront_payload_answers_head()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        using var request = new HttpRequestMessage(HttpMethod.Head, new Uri(SiteSettingsUrl, UriKind.Relative));
        using var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }


    // ---- round 1: normalisation on the wire ------------------------------------------------------

    /// <summary>
    /// <b>The stored value tells the truth about where the link goes.</b>
    /// <para>
    /// U+3002 (IDEOGRAPHIC FULL STOP) is the case that made normalisation necessary: both .NET's
    /// <c>IdnHost</c> and Chromium map it to an ordinary dot, so storing the raw text left a row that
    /// <i>read</i> <c>instagram.com</c> and <i>went</i> to <c>evil.example</c>. A staff user reviewing the
    /// settings screen would have had no way to tell.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_homograph_host_is_stored_as_the_address_it_actually_resolves_to()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        try
        {
            var (status, _) = await PutAsync(client, ContactSocialUrl, new
            {
                whatsApp = (string?)null,
                facebookUrl = (string?)null,
                instagramUrl = "https://instagram.com\u3002evil.example/shop",
                tikTokUrl = (string?)null,
                rowVersion = await ContactVersionAsync(client),
            });

            Assert.Equal(HttpStatusCode.NoContent, status);

            var stored = await ValueAsync(host, SettingKeys.Social.InstagramUrl);

            Assert.Equal("https://instagram.com.evil.example/shop", stored);
            Assert.DoesNotContain("\u3002", stored!, StringComparison.Ordinal);
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// Case, a default port and an empty path are normalised too, so two staff users typing the same profile
    /// two ways do not produce two different rows — and the screen shows them what the link will do.
    /// </summary>
    [Fact]
    public async Task An_accepted_address_is_stored_in_its_canonical_form()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        try
        {
            await PutAsync(client, ContactSocialUrl, new
            {
                whatsApp = (string?)null,
                facebookUrl = "https://WWW.Facebook.com:443",
                instagramUrl = (string?)null,
                tikTokUrl = (string?)null,
                rowVersion = await ContactVersionAsync(client),
            });

            Assert.Equal("https://www.facebook.com/", await ValueAsync(host, SettingKeys.Social.FacebookUrl));
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// <b>The two silent-drop rows.</b> U+FF20 (FULLWIDTH COMMERCIAL AT) and U+FF0F (FULLWIDTH SOLIDUS) used
    /// to be accepted server-side and then <i>throw</i> in the browser — so a client got a 204, no error
    /// anywhere, and an icon that never appeared. <c>Uri.IdnHost</c> throws for both, which is what refuses
    /// them now.
    /// </summary>
    [Theory]
    [InlineData("https://instagram.com\uFF20evil.example/")]
    [InlineData("https://instagram.com\uFF0Fevil.example/")]
    public async Task An_address_the_browser_cannot_parse_is_refused_rather_than_silently_dropped(string url)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        var (status, body) = await PutAsync(client, ContactSocialUrl, new
        {
            whatsApp = (string?)null,
            facebookUrl = (string?)null,
            instagramUrl = url,
            tikTokUrl = (string?)null,
            rowVersion = await ContactVersionAsync(client),
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("instagramUrl", out _));
        Assert.Null(await ValueAsync(host, SettingKeys.Social.InstagramUrl));
    }

    // ---- round 1: the per-row permission check ----------------------------------------------------

    /// <summary>
    /// Doc 08 §3.8 DB2 on the write path. The row's <c>RequiredPermission</c> is changed to one the caller
    /// does not hold — which is exactly the shape #53 introduces when <c>Bank.*</c> arrives — and the write
    /// is refused with 403 naming the key.
    /// </summary>
    [Fact]
    public async Task A_key_whose_permission_the_caller_lacks_cannot_be_written()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        await RequirePermissionAsync(host, SettingKeys.Social.FacebookUrl, Permissions.Settings.Payments);

        try
        {
            // The token is read *after* the permission change, and that ordering is the point: changing
            // RequiredPermission is an UPDATE, so it moves the row's rowversion. Reading the token first made
            // this test see a 409 rather than the 403 — the concurrency check runs before the permission
            // check, which is the right order (tell a stale form to reload before telling it about
            // permissions) and was worth finding out rather than assuming.
            var (status, body) = await PutAsync(client, ContactSocialUrl, new
            {
                whatsApp = "94771234567",
                facebookUrl = "https://www.facebook.com/shop",
                instagramUrl = (string?)null,
                tikTokUrl = (string?)null,
                rowVersion = await ContactVersionAsync(client),
            });

            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal("setting_forbidden", body.GetProperty("code").GetString());
            Assert.Contains(SettingKeys.Social.FacebookUrl, body.GetProperty("detail").GetString()!, StringComparison.Ordinal);

            // Nothing was written, including the key they could have changed: a partly applied save is worse
            // than a refused one, because nobody can tell which half landed.
            Assert.Null(await ValueAsync(host, SettingKeys.Contact.WhatsApp));
            Assert.Null(await ValueAsync(host, SettingKeys.Social.FacebookUrl));
        }
        finally
        {
            await RequirePermissionAsync(host, SettingKeys.Social.FacebookUrl, Permissions.Settings.Edit);
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// **The refusal must not depend on whether the guess was right.**
    ///
    /// The first version checked the caller's permission against the keys that turned out to have
    /// <i>changed</i>, which made the endpoint a one-bit oracle on a value the caller may not read: submit a
    /// candidate equal to the stored value and the key never enters the diff, so the answer is 204; submit one
    /// that differs and it is 403. One guess, one bit, repeatable — and the round-2 fix that lets a request
    /// carry a single field made it a clean probe, because nothing else masks the signal.
    ///
    /// So this asserts the two answers are the <b>same</b>. It is deliberately not an assertion about 403
    /// alone: a handler that refused both would pass that, and a handler that leaked would too.
    /// </summary>
    [Fact]
    public async Task A_refusal_does_not_reveal_whether_the_submitted_value_matched()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        const string stored = "https://www.facebook.com/the-real-one";

        try
        {
            // Put a known value in while the caller is still allowed to write it, then take the permission away.
            await PutAsync(client, ContactSocialUrl, new
            {
                facebookUrl = stored,
                rowVersion = await ContactVersionAsync(client),
            });

            await RequirePermissionAsync(host, SettingKeys.Social.FacebookUrl, Permissions.Settings.Payments);

            // A guess that is wrong.
            var (wrongStatus, _) = await PutAsync(client, ContactSocialUrl, new
            {
                facebookUrl = "https://www.facebook.com/a-wrong-guess",
                rowVersion = await ContactVersionAsync(client),
            });

            // And a guess that is exactly right. Under the old check this one answered 204, because an
            // unchanged key never reached the permission test.
            var (rightStatus, _) = await PutAsync(client, ContactSocialUrl, new
            {
                facebookUrl = stored,
                rowVersion = await ContactVersionAsync(client),
            });

            Assert.Equal(HttpStatusCode.Forbidden, wrongStatus);
            Assert.Equal(wrongStatus, rightStatus);

            // And the value the caller was guessing at is still what it was.
            Assert.Equal(stored, await ValueAsync(host, SettingKeys.Social.FacebookUrl));
        }
        finally
        {
            await RequirePermissionAsync(host, SettingKeys.Social.FacebookUrl, Permissions.Settings.Edit);
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// And a field the request <b>omits</b> is not written, so a caller whose screen cannot read a group does
    /// not clear it. This is the server-side half of W1-12's shape, which the first version had backwards:
    /// absent read as null and cleared the key.
    /// </summary>
    [Fact]
    public async Task A_field_the_request_omits_is_left_alone_rather_than_cleared()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        try
        {
            await PutAsync(client, ContactSocialUrl, new
            {
                whatsApp = "94771234567",
                facebookUrl = "https://www.facebook.com/kept",
                instagramUrl = "https://www.instagram.com/kept",
                tikTokUrl = "https://www.tiktok.com/@kept",
                rowVersion = await ContactVersionAsync(client),
            });

            // A body carrying only the WhatsApp number — what a screen that could not read Social sends.
            var (status, _) = await PutRawAsync(
                client,
                ContactSocialUrl,
                $"{{\"whatsApp\":\"94779999999\",\"rowVersion\":\"{await ContactVersionAsync(client)}\"}}");

            Assert.Equal(HttpStatusCode.NoContent, status);

            Assert.Equal("94779999999", await ValueAsync(host, SettingKeys.Contact.WhatsApp));
            Assert.Equal("https://www.facebook.com/kept", await ValueAsync(host, SettingKeys.Social.FacebookUrl));
            Assert.Equal("https://www.instagram.com/kept", await ValueAsync(host, SettingKeys.Social.InstagramUrl));
            Assert.Equal("https://www.tiktok.com/@kept", await ValueAsync(host, SettingKeys.Social.TikTokUrl));
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// The logo route insists on an explicit decision: an absent field is a 400, not a removal. A screen that
    /// stopped sending it must not silently delete the shop's logo.
    /// </summary>
    [Fact]
    public async Task A_logo_request_that_omits_the_asset_field_is_refused()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit, Permissions.Media.Create);

        try
        {
            var asset = await UploadImageAsync(client, TestImages.UniquePng(600, 150), "kept-logo.png");
            Assert.Equal(HttpStatusCode.NoContent, await SetLogoAsync(client, asset));

            var (status, body) = await PutRawAsync(
                client,
                StoreLogoUrl,
                $"{{\"rowVersion\":\"{await StoreVersionAsync(client)}\"}}");

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.True(body.GetProperty("errors").TryGetProperty("logoAssetPublicId", out _));

            // Still set: a refused request removes nothing.
            Assert.NotNull(await ValueAsync(host, SettingKeys.Store.LogoAssetId));
            Assert.Single(await UsagesAsync(host));
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// A field of the wrong JSON type is a named field error rather than "malformed JSON" — the improvement
    /// reading the body by hand buys, and the right side of issue #47's family.
    /// </summary>
    [Fact]
    public async Task A_field_of_the_wrong_json_type_names_the_field()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        var (status, body) = await PutRawAsync(client, ContactSocialUrl, "{\"whatsApp\":123}");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("whatsApp", out _));
    }

    // ---- round 1: the third secret floor ----------------------------------------------------------

    /// <summary>
    /// <b>The storefront reader now refuses a secret row outright.</b>
    /// <para>
    /// Before round 1 it never read <c>IsSecret</c>: the protection was the key allow-list plus the contract's
    /// shape, so a catalogued key flipped to Secret would have had its value published subject only to format
    /// validation. The tester's correction to the brief was right — it was two floors and a shape, not three.
    /// This is the third, and the value used here is one that <i>passes</i> format validation, so the test
    /// cannot pass for the old reason.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_secret_row_is_not_published_even_when_its_value_looks_valid()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        // A real WhatsApp number, so IsAcceptableWhatsAppNumber would happily publish it.
        await MakeSecretAsync(host, SettingKeys.Contact.WhatsApp, "94771234567");

        try
        {
            var body = await GetJsonAsync(client, SiteSettingsUrl);

            Assert.Equal(JsonValueKind.Null, body.GetProperty("whatsAppNumber").ValueKind);
            Assert.DoesNotContain("94771234567", body.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            await UnmakeSecretAsync(host, SettingKeys.Contact.WhatsApp);
            await RestoreAsync(host);
        }
    }

    // ---- round 1: case-insensitive keys -----------------------------------------------------------

    /// <summary>
    /// The collation is case-insensitive, so a row scripted in with different casing is one row to SQL Server.
    /// The first version's <c>Ordinal</c> dictionaries missed it, which made the box vanish from the screen and
    /// every save answer <c>NotSeeded</c> — a 500 — for ever, while the seeder reported nothing wrong.
    /// </summary>
    [Fact]
    public async Task A_row_whose_key_is_cased_differently_is_still_readable_and_writable()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        await RecaseAsync(host, SettingKeys.Social.FacebookUrl, "social.facebookurl");

        try
        {
            var byKey = await ReadSettingsAsync(client);
            Assert.Contains("social.facebookurl", byKey.Keys);

            var (status, _) = await PutAsync(client, ContactSocialUrl, new
            {
                whatsApp = (string?)null,
                facebookUrl = "https://www.facebook.com/recased",
                instagramUrl = (string?)null,
                tikTokUrl = (string?)null,
                rowVersion = await ContactVersionAsync(client),
            });

            Assert.Equal(HttpStatusCode.NoContent, status);
            Assert.Equal("https://www.facebook.com/recased", await ValueAsync(host, "social.facebookurl"));
        }
        finally
        {
            await RecaseAsync(host, "social.facebookurl", SettingKeys.Social.FacebookUrl);
            await RestoreAsync(host);
        }
    }

    // ---- round 1: the media library label ---------------------------------------------------------

    /// <summary>
    /// Doc 08 §3.3 documents <c>OwnerLabel</c> as "a name a person recognises". The first version put the
    /// setting's 185-character <c>Description</c> there, beside entries like "Wide-Leg Rinse Jeans".
    /// </summary>
    [Fact]
    public async Task The_usage_label_for_a_setting_is_a_name_rather_than_a_paragraph()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(
            Permissions.Settings.Edit, Permissions.Media.Create, Permissions.Media.Delete, Permissions.Media.View);

        try
        {
            var asset = await UploadImageAsync(client, TestImages.UniquePng(600, 150), "labelled-logo.png");
            Assert.Equal(HttpStatusCode.NoContent, await SetLogoAsync(client, asset));

            using var refused = await client.DeleteAsync(new Uri($"/api/admin/media/{asset}", UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
            var label = problem.GetProperty("usages").EnumerateArray().Single()
                .GetProperty("ownerLabel").GetString();

            Assert.Equal(SettingCatalog.Find(SettingKeys.Store.LogoAssetId)!.Label, label);
            Assert.InRange(label!.Length, 1, SettingDefinition.LabelMaxLength);
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static IEnumerable<(HttpMethod Method, string Path)> Routes()
    {
        yield return (HttpMethod.Get, SettingsUrl);
        yield return (HttpMethod.Put, StoreLogoUrl);
        yield return (HttpMethod.Put, ContactSocialUrl);
    }

    private async Task<MediaTestHost> StartAsync(string environment = "Testing")
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        return MediaTestHost.Create(sqlServer.ConnectionString, environment: environment);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
    }

    private static async Task<Dictionary<string, JsonElement>> ReadSettingsAsync(HttpClient client)
    {
        var body = await GetJsonAsync(client, SettingsUrl);

        return body.GetProperty("groups").EnumerateArray()
            .SelectMany(group => group.GetProperty("settings").EnumerateArray())
            .ToDictionary(setting => setting.GetProperty("key").GetString()!, setting => setting, StringComparer.Ordinal);
    }

    private static async Task<string> StoreVersionAsync(HttpClient client) =>
        await GroupVersionAsync(client, SettingKeys.Store.Group);

    private static async Task<string> ContactVersionAsync(HttpClient client) =>
        await GroupVersionAsync(client, SettingKeys.Contact.Group);

    private static async Task<string> GroupVersionAsync(HttpClient client, string groupKey)
    {
        var body = await GetJsonAsync(client, SettingsUrl);

        return body.GetProperty("groups").EnumerateArray()
            .Single(group => group.GetProperty("groupKey").GetString() == groupKey)
            .GetProperty("rowVersion").GetString()!;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PutAsync(
        HttpClient client,
        string path,
        object body)
    {
        using var response = await client.PutAsJsonAsync(new Uri(path, UriKind.Relative), body, JsonOptions, Token);

        return (response.StatusCode, await ReadBodyAsync(response));
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PutRawAsync(
        HttpClient client,
        string path,
        string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, Token);

        return (response.StatusCode, await ReadBodyAsync(response));
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync(Token);

        return string.IsNullOrWhiteSpace(payload)
            ? default
            : JsonSerializer.Deserialize<JsonElement>(payload, JsonOptions);
    }

    private static async Task<HttpStatusCode> SetLogoAsync(HttpClient client, Guid? asset, string? rowVersion = null)
    {
        var (status, _) = await PutAsync(client, StoreLogoUrl, new
        {
            logoAssetPublicId = asset,
            rowVersion = rowVersion ?? await StoreVersionAsync(client),
        });

        return status;
    }

    private static async Task<Guid> UploadImageAsync(HttpClient client, byte[] bytes, string fileName)
    {
        using var form = MediaTestHost.FilePart("file", bytes, fileName, "image/png");
        form.Add(new StringContent("The shop's logo"), "altText");

        using var response = await client.PostAsync(new Uri(ImagesUrl, UriKind.Relative), form, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        return body.GetProperty("publicId").GetGuid();
    }

    private static async Task<Guid> UploadVideoAsync(HttpClient client)
    {
        using var form = MediaTestHost.FilePart("file", TestVideos.UniqueMp4(), "clip.mp4", "video/mp4");
        MediaTestHost.AddFile(form, "poster", TestImages.UniqueJpeg(1280, 720), "poster.jpg", "image/jpeg");

        using var response = await client.PostAsync(new Uri("/api/admin/media/videos", UriKind.Relative), form, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        return body.GetProperty("publicId").GetGuid();
    }

    private static async Task<string?> ValueAsync(MediaTestHost host, string key)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.Settings.AsNoTracking()
            .Where(setting => setting.Key == key)
            .Select(setting => setting.Value)
            .SingleAsync(Token);
    }

    private static async Task<List<MediaUsage>> UsagesAsync(MediaTestHost host)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.MediaUsages.AsNoTracking()
            .Where(usage => usage.OwnerType == MediaUsageOwnerTypes.Setting)
            .OrderBy(usage => usage.Id)
            .ToListAsync(Token);
    }

    private static async Task<int> AssetIdAsync(MediaTestHost host, Guid publicId)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.MediaAssets.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(asset => asset.PublicId == publicId)
            .Select(asset => asset.Id)
            .SingleAsync(Token);
    }

    private static async Task<long> AuditCountAsync(MediaTestHost host)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.AuditLogs.AsNoTracking()
            .Where(log => log.EntityType == "Setting")
            .MaxAsync(log => (long?)log.Id, Token) ?? 0;
    }

    private static async Task<List<AuditLog>> AuditRowsAsync(MediaTestHost host, long afterId)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.AuditLogs.AsNoTracking()
            .Where(log => log.EntityType == "Setting" && log.Id > afterId)
            .OrderBy(log => log.Id)
            .ToListAsync(Token);
    }

    /// <summary>
    /// Makes a catalogued key secret for the duration of a test, which no code path can do — the seeder
    /// realigns the metadata and no endpoint touches those columns. <c>CK_Settings_IsSecret</c> requires both
    /// columns to move together, so they do.
    /// </summary>
    private static async Task MakeSecretAsync(MediaTestHost host, string key, string ciphertext)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ISiteSettingsCache>();

        await dbContext.Settings
            .Where(setting => setting.Key == key)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(setting => setting.ValueType, SettingValueType.Secret)
                    .SetProperty(setting => setting.IsSecret, true)
                    .SetProperty(setting => setting.Value, ciphertext),
                Token);

        cache.Evict();
    }

    /// <inheritdoc cref="MakeSecretAsync"/>
    private static async Task UnmakeSecretAsync(MediaTestHost host, string key)
    {
        var definition = SettingCatalog.Find(key)!;

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.Settings
            .Where(setting => setting.Key == key)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(setting => setting.ValueType, definition.ValueType)
                    .SetProperty(setting => setting.IsSecret, definition.IsSecret)
                    .SetProperty(setting => setting.Value, (string?)null),
                Token);
    }

    /// <summary>Changes one row's <c>RequiredPermission</c>, which no code path can do.</summary>
    private static async Task RequirePermissionAsync(MediaTestHost host, string key, string permission)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.Settings
            .Where(setting => setting.Key == key)
            .ExecuteUpdateAsync(
                update => update.SetProperty(setting => setting.RequiredPermission, permission),
                Token);
    }

    /// <summary>
    /// Re-cases a row's primary key, standing in for a row scripted into the database by hand. The collation is
    /// case-insensitive, so this is one row to SQL Server either way — which is the whole point.
    /// </summary>
    private static async Task RecaseAsync(MediaTestHost host, string from, string to)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ISiteSettingsCache>();

        // A key change is a primary-key change, so it goes through raw SQL rather than the change tracker.
        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE ops.Settings SET [Key] = {0} WHERE [Key] = {1}",
            [to, from],
            Token);

        cache.Evict();
    }

    private static async Task InsertRowAsync(MediaTestHost host, Setting row)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        dbContext.Settings.Add(row);
        await dbContext.SaveChangesAsync(Token);
    }

    private static async Task DeleteRowAsync(MediaTestHost host, string key)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.Settings.Where(setting => setting.Key == key).ExecuteDeleteAsync(Token);
    }

    /// <summary>
    /// Writes a value straight into a row, bypassing every rule — which is the only way to test what the read
    /// model does with a value no endpoint would accept.
    /// </summary>
    private static async Task WriteDirectlyAsync(MediaTestHost host, string key, string? value)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ISiteSettingsCache>();

        await dbContext.Settings
            .Where(setting => setting.Key == key)
            .ExecuteUpdateAsync(update => update.SetProperty(setting => setting.Value, value), Token);

        // Nothing went through a use case, so nothing evicted the sixty-second cache.
        cache.Evict();
    }

    /// <summary>
    /// Puts the five settings rows back to NULL and clears their usage rows.
    /// <para>
    /// The assembly's database is shared and its classes run sequentially, so a logo left set would be a
    /// failure in somebody else's test with no clue where it came from. The cache is evicted too, because the
    /// restore does not go through a use case.
    /// </para>
    /// </summary>
    private static async Task RestoreAsync(MediaTestHost host)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ISiteSettingsCache>();

        var keys = SettingCatalog.All.Select(definition => definition.Key).ToList();

        await dbContext.Settings
            .Where(setting => keys.Contains(setting.Key))
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(setting => setting.Value, (string?)null)
                    .SetProperty(setting => setting.UpdatedAtUtc, (DateTime?)null)
                    .SetProperty(setting => setting.UpdatedByUserId, (int?)null),
                Token);

        await dbContext.MediaUsages
            .Where(usage => usage.OwnerType == MediaUsageOwnerTypes.Setting)
            .ExecuteDeleteAsync(Token);

        cache.Evict();
    }

    private static string? ValueIn(string? json)
    {
        var value = JsonDocument.Parse(json!).RootElement.GetProperty("value");

        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }
}
