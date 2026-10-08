using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Orders.Abstractions;
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
/// <b>Every test puts the settings rows back.</b> The fixture's database is shared by the whole assembly,
/// whose classes <b>run strictly one after another</b> — measured with four timed probe classes, zero
/// overlap, no <c>xunit.runner.json</c> to override the assembly attribute (see <c>AssemblyInfo.cs</c>).
/// The storefront tests read the same rows, so a logo left set or a social address left behind would fail
/// somebody else's test with no clue where it came from. The restore is in a <c>finally</c> and it clears
/// the <c>MediaUsages</c> rows too.
/// <para>
/// <b>Serialisation makes the restore sufficient, and that is exactly why getting it wrong is expensive.</b>
/// Nothing runs between this class's mutation and its <c>finally</c>, so a correct restore is airtight — and
/// an *incorrect* one is not a window that might be missed but a value every later class reads. W3-04's
/// round-1 restore set every catalogued key to NULL, including the one key the seeder gives a value, and so
/// broke a later class's assertion in whichever runs happened to order it afterwards. The fix is below: put
/// each key back to <c>SettingDefinition.SeedValue</c>, which is the only place that knows what "back"
/// means. An earlier version of this remark said classes interleave and called it measured; they do not,
/// and #114 is about unsafe fixtures rather than about this attribute.
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
    private const string InvoiceUrl = "/api/admin/settings/invoice-and-fulfilment";
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

        // `Bank` is absent for a Settings.Edit holder and that is the point of the filter (doc 08 §3.8
        // DB2); `Orders` arrived with W3-04 and is Settings.Edit, so it is here.
        Assert.Equal(["Store", "Contact", "Social", "Orders"], groups);

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

        // **Both settings permissions, because the catalogue now spans two** (W3-03): the `Bank.*` keys
        // need Settings.Payments and the rest need Settings.Edit. This assertion is "every seeded key
        // arrives", so the caller has to be one who may read every seeded key — and the test below is the
        // one that proves a caller who may not read a group does not get it.
        using var client = await host.SignInAsync(
            Permissions.Settings.Edit,
            Permissions.Settings.Payments);

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

        // And the bank keys arrive with the permission doc 08 §3.8 DB2 gives them, which is what the read
        // model filters on per row.
        var accountNumber = byKey[SettingKeys.Bank.AccountNumber];
        Assert.Equal("String", accountNumber.GetProperty("valueType").GetString());
        Assert.Equal(Permissions.Settings.Payments, accountNumber.GetProperty("requiredPermission").GetString());
        Assert.False(accountNumber.GetProperty("isSecret").GetBoolean());
    }

    /// <summary>
    /// A caller with only <c>Settings.Edit</c> gets everything but the bank group, and a caller with only
    /// <c>Settings.Payments</c> gets the bank group and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>The per-row filter, in both directions, against real rows.</b> ADM-22 built it with nothing to
    /// filter and said so; these are the first keys in the catalogue carrying a different permission, so
    /// this is the first time either direction is exercised. The second half also covers the widening
    /// W3-03 made to the route guard: before it, a caller holding only <c>Settings.Payments</c> got a 403
    /// from the route and could not reach the screen at all.
    /// </remarks>
    [Fact]
    public async Task Each_settings_permission_sees_exactly_its_own_groups()
    {
        await using var host = await StartAsync();

        using var editor = await host.SignInAsync(Permissions.Settings.Edit);
        var editorKeys = await ReadSettingsAsync(editor);

        Assert.Equal(
            SettingCatalog.All
                .Where(definition => definition.RequiredPermission == Permissions.Settings.Edit)
                .Select(definition => definition.Key)
                .Order(StringComparer.Ordinal),
            editorKeys.Keys.Order(StringComparer.Ordinal));

        using var payments = await host.SignInAsync(Permissions.Settings.Payments);
        var paymentsKeys = await ReadSettingsAsync(payments);

        Assert.Equal(
            SettingCatalog.All
                .Where(definition => definition.RequiredPermission == Permissions.Settings.Payments)
                .Select(definition => definition.Key)
                .Order(StringComparer.Ordinal),
            paymentsKeys.Keys.Order(StringComparer.Ordinal));

        // Neither list is empty, so neither half of this can pass vacuously.
        Assert.NotEmpty(editorKeys);
        Assert.NotEmpty(paymentsKeys);
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
    /// empty. An empty <c>Bank</c> group would confirm to a Store Manager that bank details exist and tell
    /// them exactly which five keys to go looking for another way.
    /// </summary>
    /// <remarks>
    /// <b>This test used to insert a <c>Bank.AccountNumber</c> row by hand and delete it in a
    /// <c>finally</c>, and W3-03 had to change that before it did damage.</b> No bank key was seeded when
    /// ADM-22 wrote it, so inserting a stand-in was the only way to prove the filter — and that was the
    /// right call at the time. Now the key is seeded: the insert would hit the primary key, and the
    /// <c>finally</c> would <b>delete a seeded row</b>, which the next test would find missing and every
    /// save of it would answer <c>NotSeeded</c> (a 500) until somebody re-ran the seeder.
    /// <para>
    /// It is a better test for the change. It now proves the filter against the real group, with a real
    /// value in it, which is the thing the shop actually has to hide — and the value is restored to null in
    /// the <c>finally</c> rather than the row being removed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_group_the_caller_may_not_read_is_absent_rather_than_empty()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        try
        {
            await WriteDirectlyAsync(host, SettingKeys.Bank.AccountNumber, "8001234567");

            var body = await GetJsonAsync(client, SettingsUrl);

            var groups = body.GetProperty("groups").EnumerateArray()
                .Select(group => group.GetProperty("groupKey").GetString())
                .ToList();

            Assert.DoesNotContain(SettingKeys.Bank.Group, groups);

            // And the value is nowhere in the payload at all, including in a field nobody thought to check.
            Assert.DoesNotContain("8001234567", body.GetRawText(), StringComparison.Ordinal);

            // The caller who may read it does get it, so this is a statement about the permission rather
            // than about the row being unreadable by everybody.
            using var payments = await host.SignInAsync(Permissions.Settings.Payments);
            var allowed = await GetJsonAsync(payments, SettingsUrl);

            Assert.Contains("8001234567", allowed.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            await WriteDirectlyAsync(host, SettingKeys.Bank.AccountNumber, null);
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

            // The payload has four named members and no general-purpose value, so there is nowhere for one
            // to be. `contact` joined the three in W2-11 (GLB-10); it is itself a named record with five
            // named members, which is the same guarantee one level down — see
            // SettingSecretGuardTests.The_contact_block_has_no_field_a_secret_could_occupy.
            Assert.Equal(
                ["contact", "logo", "social", "whatsAppNumber"],
                body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

            // And the same for the nested block, on the live payload rather than on the type: a secret
            // flipped onto any of the five Contact keys must not appear here either. They are read through
            // the same `!setting.IsSecret` clause, so this is that clause being exercised rather than a
            // second mechanism.
            Assert.Equal(
                ["addressLines", "email", "hasAny", "mapEmbedUrl", "openingHours", "phone"],
                body.GetProperty("contact")
                    .EnumerateObject()
                    .Select(property => property.Name)
                    .Order(StringComparer.Ordinal));
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

    // ---- W3-04: the invoice details and the auto-deliver window ---------------------------------

    /// <summary>
    /// <b>The pairing issue #126 asks for.</b> Playwright never starts this API, so the six Vitest specs
    /// over this form prove only what the screen <i>sends</i>; what the server does with it is provable
    /// only here, against a real SQL Server.
    /// </summary>
    [Fact]
    public async Task Saving_the_invoice_details_and_the_window_stores_every_field()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        try
        {
            var (status, _) = await PutAsync(client, InvoiceUrl, new
            {
                legalName = "Spencer Denim Industries (Pvt) Ltd",
                registeredAddress = "No. 1, Example Road\nColombo 00100",
                brNumber = "PV-00000",
                vatNumber = "VAT-00000",
                autoDeliverAfterDays = "10",
                rowVersion = await InvoiceVersionAsync(client),
            });

            Assert.Equal(HttpStatusCode.NoContent, status);

            // Read from the database rather than from the payload, for W1-12's reason: the blocker there
            // was a field that never reached the request at all, and a round trip through the same read
            // model could not have seen it.
            Assert.Equal("Spencer Denim Industries (Pvt) Ltd", await ValueAsync(host, SettingKeys.Store.LegalName));
            Assert.Equal("No. 1, Example Road\nColombo 00100", await ValueAsync(host, SettingKeys.Store.Address));
            Assert.Equal("PV-00000", await ValueAsync(host, SettingKeys.Store.BRNumber));
            Assert.Equal("VAT-00000", await ValueAsync(host, SettingKeys.Store.VatNumber));
            Assert.Equal("10", await ValueAsync(host, SettingKeys.Orders.AutoDeliverAfterDays));
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// <b>Absent is not null, over a fixture that holds real data.</b> This is the GLB-10 / W1-12 defect
    /// class: PUT replaces, so a handler that read a missing field as <see langword="null"/> would clear
    /// four legal keys because a screen sent one. The fixture is filled first on purpose - against empty
    /// rows the bug is invisible.
    /// </summary>
    [Fact]
    public async Task A_field_the_invoice_request_omits_is_left_alone_rather_than_cleared()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        try
        {
            await PutAsync(client, InvoiceUrl, new
            {
                legalName = "A Registered Company (Pvt) Ltd",
                registeredAddress = "No. 2, Example Road",
                brNumber = "PV-11111",
                vatNumber = "VAT-11111",
                autoDeliverAfterDays = "9",
                rowVersion = await InvoiceVersionAsync(client),
            });

            // One field, by itself, with the other five absent from the JSON entirely.
            var (status, _) = await PutRawAsync(
                client,
                InvoiceUrl,
                $"{{\"brNumber\":\"PV-22222\",\"rowVersion\":\"{await InvoiceVersionAsync(client)}\"}}");

            Assert.Equal(HttpStatusCode.NoContent, status);

            Assert.Equal("PV-22222", await ValueAsync(host, SettingKeys.Store.BRNumber));
            Assert.Equal("A Registered Company (Pvt) Ltd", await ValueAsync(host, SettingKeys.Store.LegalName));
            Assert.Equal("No. 2, Example Road", await ValueAsync(host, SettingKeys.Store.Address));
            Assert.Equal("VAT-11111", await ValueAsync(host, SettingKeys.Store.VatNumber));
            Assert.Equal("9", await ValueAsync(host, SettingKeys.Orders.AutoDeliverAfterDays));
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// An explicit null <i>does</i> clear, and the cleared value is <see langword="null"/> rather than
    /// <c>""</c> - which is what makes the invoice print no line at all instead of an empty one, and what
    /// makes the auto-deliver job skip the pass instead of reading a number out of an empty string.
    /// </summary>
    [Fact]
    public async Task A_blank_invoice_field_clears_it_as_null_rather_than_an_empty_string()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        try
        {
            await PutAsync(client, InvoiceUrl, new
            {
                legalName = "A Registered Company (Pvt) Ltd",
                vatNumber = "VAT-33333",
                autoDeliverAfterDays = "5",
                rowVersion = await InvoiceVersionAsync(client),
            });

            await PutAsync(client, InvoiceUrl, new
            {
                vatNumber = (string?)null,
                autoDeliverAfterDays = "   ",
                rowVersion = await InvoiceVersionAsync(client),
            });

            Assert.Null(await ValueAsync(host, SettingKeys.Store.VatNumber));
            Assert.Null(await ValueAsync(host, SettingKeys.Orders.AutoDeliverAfterDays));

            // And the reader agrees that a cleared window means "do not run", rather than falling back to
            // a number no screen could show.
            await using var scope = host.Factory.Services.CreateAsyncScope();
            Assert.Null(await scope.ServiceProvider
                .GetRequiredService<IOrderSettingsReader>()
                .ReadAutoDeliverAfterDaysAsync(Token));
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// The window's range, on the wire. <b>Nought is refused for its own reason</b> (doc 08 section 4.1
    /// G-18): it would mark an order delivered the moment it shipped and hand out a Verified buyer badge
    /// for goods still in the van.
    /// </summary>
    /// <remarks>
    /// <b>Two different guards are being exercised here, and the cases are not interchangeable.</b> Only
    /// <c>"0"</c> and <c>"91"</c> reach the range comparison. <c>"-1"</c> never does: the handler parses
    /// with <see cref="NumberStyles.None"/>, which forbids a leading sign, so a negative is refused by the
    /// *parse* — as are <c>"7.5"</c>, <c>"seven"</c> and <c>"+7"</c>. Measured, by widening the range to
    /// plus or minus a thousand: the <c>"0"</c> and <c>"91"</c> cases went red and <c>"-1"</c> stayed green.
    /// <para>
    /// All six stay, because each is something a staff member can actually type and every one of them must
    /// be refused against this field. But a reader who assumed <c>"-1"</c> proved the lower bound would be
    /// wrong, and the lower bound would be unguarded if <c>"0"</c> were ever removed.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("91")]
    [InlineData("7.5")]
    [InlineData("seven")]
    [InlineData("+7")]
    public async Task A_window_outside_the_range_is_refused_and_nothing_is_written(string value)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        try
        {
            await PutAsync(client, InvoiceUrl, new
            {
                autoDeliverAfterDays = "8",
                rowVersion = await InvoiceVersionAsync(client),
            });

            var (status, body) = await PutAsync(client, InvoiceUrl, new
            {
                autoDeliverAfterDays = value,
                rowVersion = await InvoiceVersionAsync(client),
            });

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.True(
                body.GetProperty("errors").TryGetProperty("autoDeliverAfterDays", out _),
                "The refusal must name the field the form can show it against.");

            // The refused value is not half-written. A validation pass that returned errors *and* saved
            // would leave the job running on a figure the screen had just rejected.
            Assert.Equal("8", await ValueAsync(host, SettingKeys.Orders.AutoDeliverAfterDays));
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// The bounds themselves are accepted, because an off-by-one here is a figure the client cannot set.
    /// </summary>
    [Theory]
    [InlineData("1")]
    [InlineData("90")]
    public async Task The_window_bounds_are_accepted_on_the_wire(string value)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        try
        {
            var (status, _) = await PutAsync(client, InvoiceUrl, new
            {
                autoDeliverAfterDays = value,
                rowVersion = await InvoiceVersionAsync(client),
            });

            Assert.Equal(HttpStatusCode.NoContent, status);
            Assert.Equal(value, await ValueAsync(host, SettingKeys.Orders.AutoDeliverAfterDays));
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// Over-long legal details are refused per field in one response, and nothing is written.
    /// </summary>
    [Fact]
    public async Task Over_long_invoice_details_are_each_named_in_one_response()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        var (status, body) = await PutAsync(client, InvoiceUrl, new
        {
            legalName = new string('a', 201),
            registeredAddress = new string('b', 401),
            brNumber = new string('c', 41),
            vatNumber = new string('d', 41),
            rowVersion = await InvoiceVersionAsync(client),
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);

        var errors = body.GetProperty("errors");

        // All four in one response: a form that has to be submitted four times to learn four mistakes is
        // the defect this project has already fixed twice.
        Assert.True(errors.TryGetProperty("legalName", out _));
        Assert.True(errors.TryGetProperty("registeredAddress", out _));
        Assert.True(errors.TryGetProperty("brNumber", out _));
        Assert.True(errors.TryGetProperty("vatNumber", out _));

        Assert.Null(await ValueAsync(host, SettingKeys.Store.LegalName));
    }

    /// <summary>
    /// A stale token is a 409 and writes nothing - <b>including the fields that are not in conflict</b>,
    /// because the save unit is the unit of concurrency.
    /// </summary>
    [Fact]
    public async Task An_invoice_save_with_a_stale_row_version_is_a_409_and_writes_nothing()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        try
        {
            var stale = await InvoiceVersionAsync(client);

            await PutAsync(client, InvoiceUrl, new
            {
                legalName = "The First Writer (Pvt) Ltd",
                rowVersion = stale,
            });

            var (status, body) = await PutAsync(client, InvoiceUrl, new
            {
                legalName = "The Second Writer (Pvt) Ltd",
                brNumber = "PV-99999",
                rowVersion = stale,
            });

            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal(SettingsConflictCodes.StaleRowVersion, body.GetProperty("code").GetString());

            Assert.Equal("The First Writer (Pvt) Ltd", await ValueAsync(host, SettingKeys.Store.LegalName));
            Assert.Null(await ValueAsync(host, SettingKeys.Store.BRNumber));
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// One audit row per changed key and none for a key re-sent unchanged (doc 08 section 3.8). The
    /// settings screen's own history is how a client later asks why their invoices say what they say.
    /// </summary>
    [Fact]
    public async Task One_audit_row_is_written_per_changed_invoice_key()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        try
        {
            await PutAsync(client, InvoiceUrl, new
            {
                legalName = "An Audited Company (Pvt) Ltd",
                brNumber = "PV-44444",
                autoDeliverAfterDays = "6",
                rowVersion = await InvoiceVersionAsync(client),
            });

            var mark = await AuditCountAsync(host);

            // The same legal name again, one changed registration number, and the same window.
            var (status, _) = await PutAsync(client, InvoiceUrl, new
            {
                legalName = "An Audited Company (Pvt) Ltd",
                brNumber = "PV-55555",
                autoDeliverAfterDays = "6",
                rowVersion = await InvoiceVersionAsync(client),
            });

            Assert.Equal(HttpStatusCode.NoContent, status);

            var rows = await AuditRowsAsync(host, mark);

            Assert.Single(rows);
            Assert.Contains(SettingKeys.Store.BRNumber, rows[0].EntityId, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// Re-sending every field unchanged is a 204 that writes nothing, so <c>UpdatedAtUtc</c> keeps meaning
    /// "when this value last changed" rather than "when somebody last pressed Save".
    /// </summary>
    [Fact]
    public async Task An_invoice_save_that_changes_nothing_writes_no_audit_row()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        try
        {
            await PutAsync(client, InvoiceUrl, new
            {
                legalName = "An Unchanged Company (Pvt) Ltd",
                rowVersion = await InvoiceVersionAsync(client),
            });

            var mark = await AuditCountAsync(host);

            var (status, _) = await PutAsync(client, InvoiceUrl, new
            {
                legalName = "An Unchanged Company (Pvt) Ltd",
                rowVersion = await InvoiceVersionAsync(client),
            });

            Assert.Equal(HttpStatusCode.NoContent, status);
            Assert.Empty(await AuditRowsAsync(host, mark));
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// <b>The form and the invoice reader agree, through the database.</b> They are different code with
    /// different key lists, and the one failure that would reach a client - a form that saves and an
    /// invoice that still prints nothing - is invisible to either half on its own.
    /// </summary>
    [Fact]
    public async Task What_the_form_saves_is_what_the_invoice_reader_reads()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        try
        {
            await PutAsync(client, InvoiceUrl, new
            {
                legalName = "A Printing Company (Pvt) Ltd",
                registeredAddress = "No. 3, Example Road\n\nColombo 00300\n",
                brNumber = "PV-66666",
                vatNumber = "VAT-66666",
                rowVersion = await InvoiceVersionAsync(client),
            });

            await using var scope = host.Factory.Services.CreateAsyncScope();
            var seller = await scope.ServiceProvider
                .GetRequiredService<IOrderSettingsReader>()
                .ReadInvoiceSellerAsync(Token);

            Assert.Equal("A Printing Company (Pvt) Ltd", seller.LegalName);
            Assert.Equal("PV-66666", seller.BRNumber);
            Assert.Equal("VAT-66666", seller.VatNumber);

            // The blank line a textarea leaves behind is dropped rather than printed as an empty line on a
            // legal document.
            Assert.Equal(["No. 3, Example Road", "Colombo 00300"], seller.AddressLines);

            // And the warning the screen shows before printing clears once both are present.
            Assert.True(seller.IsComplete);
        }
        finally
        {
            await RestoreAsync(host);
        }
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

        // W3-04's. It was missing from this list until its own tests were written, which meant neither
        // "refuses an anonymous caller" nor "refuses a signed-in caller without Settings.Edit" covered the
        // newest write route in the file - the exact gap this helper exists to close.
        yield return (HttpMethod.Put, InvoiceUrl);
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

    /// <summary>
    /// The token the invoice form sends: the <c>Store</c> group's, because <c>Store</c> and <c>Orders</c>
    /// are <b>one save unit</b> (<c>SettingCatalog.SaveUnits</c>) and the read model publishes one token
    /// per unit.
    /// </summary>
    /// <remarks>
    /// Asserted rather than assumed - publishing a per-group token and comparing a per-form one is the
    /// mismatch that made every valid ADM-22 save a 409, so this file states which token is which.
    /// </remarks>
    private static async Task<string> InvoiceVersionAsync(HttpClient client)
    {
        var store = await GroupVersionAsync(client, SettingKeys.Store.Group);
        var orders = await GroupVersionAsync(client, SettingKeys.Orders.Group);

        Assert.Equal(store, orders);

        return store;
    }

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
    /// The assembly's database is shared and its classes run one after another, so a logo left set would be
    /// a failure in somebody else's test — and, because nothing interleaves, in *every* test that happens to
    /// be ordered afterwards rather than in an unlucky few. The cache is evicted too, because the restore
    /// does not go through a use case.
    /// </para>
    /// <para>
    /// <b>Back to the catalogue's resting value, not unconditionally to null.</b> Null was right while every
    /// key was seeded empty; W3-04 added <c>Orders.AutoDeliverAfterDays</c>, which the seeder writes with
    /// doc 08 section 3.8's 7, so nulling it left the database in a state the seeder never produces.
    /// <para>
    /// <b>Not a window — a permanent leak</b>, which is the sharper way to say it and was measured four runs
    /// each way: with the round-1 form the key's resting value was 7, 7, 7, NULL; with this form, 7, 7, 7.
    /// Once nulled it stayed nulled for the remainder of the run, so a later class asserting its resting
    /// value failed in whichever runs xUnit happened to order it afterwards. <c>SettingDefinition.SeedValue</c>
    /// is the one place that knows the resting value, so this reads it rather than repeating it.
    /// </para>
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

        // Then the few the seeder gives a value. One statement each, because there are one or two of them
        // and a CASE expression here would be the kind of cleverness that outlives the reason for it.
        foreach (var definition in SettingCatalog.All.Where(item => item.SeedValue is not null))
        {
            var key = definition.Key;
            var value = definition.SeedValue;

            await dbContext.Settings
                .Where(setting => setting.Key == key)
                .ExecuteUpdateAsync(
                    update => update.SetProperty(setting => setting.Value, value),
                    Token);
        }

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
