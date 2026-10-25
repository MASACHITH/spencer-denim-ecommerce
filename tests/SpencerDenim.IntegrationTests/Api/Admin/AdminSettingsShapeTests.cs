using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Api.Endpoints.Admin;
using SpencerDenim.Application.Settings.Contracts;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// Pins the settings payloads against the hand-written TypeScript copies in
/// <c>projects/admin/src/app/core/settings/admin-settings.model.ts</c> (ADM-22).
/// <para>
/// <b>Why this file exists, in one sentence from W1-12's post-mortem:</b> "every category save silently
/// wiped the client's images, and 1,571 passing tests missed it, because every fixture set those columns to
/// <c>null</c>, so <c>null → null</c> round-tripped vacuously." The cause was a front-end model that had
/// never listed three columns; the API bound the absent fields to <c>null</c> and the handler assigned them.
/// </para>
/// <para>
/// So there are two halves here, and the <b>write</b> half is the one that catches that defect:
/// </para>
/// <list type="number">
/// <item>the <b>read</b> payload's property names, so the screen's interfaces cannot drift from it;</item>
/// <item>the <b>write</b> request's property set against the handler's own list of keys — and a round trip
/// whose fixtures are <b>deliberately non-null on every field</b>, so a dropped field cannot round-trip
/// null to null unnoticed.</item>
/// </list>
/// </summary>
public sealed class AdminSettingsShapeTests(SqlServerDatabaseFixture sqlServer)
{
    private const string SettingsUrl = "/api/admin/settings";
    private const string ContactSocialUrl = "/api/admin/settings/contact-social";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- the read payload --------------------------------------------------------------------------

    [Fact]
    public async Task The_settings_payload_has_exactly_the_properties_the_screen_reads()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        var body = await GetAsync(client, SettingsUrl);

        Assert.Equal(["groups", "logo"], Names(body));

        var group = body.GetProperty("groups").EnumerateArray().First();
        Assert.Equal(["groupKey", "rowVersion", "settings"], Names(group));

        var setting = group.GetProperty("settings").EnumerateArray().First();
        Assert.Equal(
            ["description", "hasValue", "isSecret", "key", "requiredPermission", "updatedAtUtc", "value", "valueType"],
            Names(setting));
    }

    [Fact]
    public async Task The_logo_payload_has_exactly_the_properties_the_screen_reads()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit, Permissions.Media.Create);

        try
        {
            var asset = await UploadAsync(client);
            await SetLogoAsync(client, asset);

            var body = await GetAsync(client, SettingsUrl);

            Assert.Equal(
                ["altText", "height", "originalFileName", "publicId", "url", "width"],
                Names(body.GetProperty("logo")));
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    /// <summary>
    /// The enum travels as its <b>name</b> (<c>JsonStringEnumConverter</c> is registered globally), so the
    /// TypeScript union has to be the member names rather than numbers. The values are doc 08 §3.8's, and a
    /// rename here would silently break a screen that switches on them.
    /// </summary>
    [Fact]
    public void The_value_type_names_are_the_ones_the_screens_union_lists()
    {
        Assert.Equal(
            ["Bool", "Decimal", "Int", "Json", "MediaAssetRef", "Secret", "String", "TextHtml"],
            Enum.GetNames<SettingValueType>().Order(StringComparer.Ordinal));
    }

    // ---- the write request -------------------------------------------------------------------------

    /// <summary>
    /// The request record's fields are exactly the keys the handler writes, plus the concurrency token.
    /// <para>
    /// This is the pairing W1-12 lost. A fifth social network added to
    /// <see cref="SettingCatalog"/> and to the handler but not to the request would make every save clear it;
    /// a field added to the request and not to the handler would make the screen silently ineffective.
    /// </para>
    /// </summary>
    [Fact]
    public void The_contact_and_social_request_carries_one_field_per_key_it_writes()
    {
        var fields = typeof(UpdateContactAndSocialRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        // Five more in W2-11 (GLB-10): doc 11 P13 step 13's contact details, which join the existing
        // Contact+Social save unit. This list is spelled out as well as derived below, deliberately — the
        // derived check alone would pass if a key and its request field were both deleted.
        Assert.Equal(
            [
                "Address",
                "Email",
                "FacebookUrl",
                "InstagramUrl",
                "MapEmbedUrl",
                "OpeningHours",
                "Phone",
                "RowVersion",
                "TikTokUrl",
                "WhatsApp",
                "YouTubeUrl",
            ],
            fields);

        // The keys the Contact and Social groups hold, derived rather than listed, so adding one to the
        // catalogue and forgetting the request fails here.
        var keys = SettingCatalog.KeysIn(SettingKeys.Contact.Group)
            .Concat(SettingCatalog.KeysIn(SettingKeys.Social.Group))
            .Select(key => key.Split('.')[^1])
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(
            keys,
            fields.Where(field => field != "RowVersion").Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Issue #193's Traffic form: the OpenAPI record, the handler's field list and the catalogue's Traffic group
    /// name the same two periods. The endpoint reads the handler's list, so a field added to one and not the
    /// others is a box the screen sends and the API ignores.
    /// </summary>
    [Fact]
    public void The_traffic_request_carries_one_field_per_key_it_writes()
    {
        var fields = typeof(UpdateTrafficSettingsRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["BotRetentionDays", "RawRetentionDays", "RowVersion"], fields);

        Assert.Equal(
            SettingCatalog.KeysIn(SettingKeys.Traffic.Group).Select(key => key.Split('.')[^1]).Order(StringComparer.OrdinalIgnoreCase),
            SpencerDenim.Application.Settings.Admin.UpdateTrafficSettingsCommandHandler.Fields.Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Issue #248's display rates: the OpenAPI record, the handler's field list, the table of five currencies and the
    /// catalogue's group name the same five rates. The body fields are the lower-case currency codes rather than the
    /// key's last segment (<c>usd</c>, not <c>UsdLkrPerUnit</c>), so the pin goes through the table that joins the two.
    /// A field added to one list and not the others is a box the screen sends and the API ignores.
    /// </summary>
    [Fact]
    public void The_currency_rates_request_carries_one_field_per_currency_it_writes()
    {
        var fields = typeof(UpdateCurrencyRatesRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Aed", "Aud", "Eur", "Gbp", "RowVersion", "Usd"], fields);

        var handlerFields = SpencerDenim.Application.Settings.Admin.UpdateCurrencyRatesCommandHandler.Fields;

        Assert.Equal(
            fields.Where(field => field != "RowVersion").Order(StringComparer.OrdinalIgnoreCase),
            handlerFields.Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(DisplayCurrencies.All.Select(currency => currency.Field), handlerFields);
        Assert.Equal(
            DisplayCurrencies.All.Select(currency => currency.SettingKey),
            SettingCatalog.KeysIn(SettingKeys.CurrencyDisplay.Group));
    }

    [Fact]
    public void The_store_logo_request_carries_the_asset_and_the_token_and_nothing_else()
    {
        Assert.Equal(
            ["LogoAssetPublicId", "RowVersion"],
            typeof(SetStoreLogoRequest)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// <b>Every field non-null, deliberately.</b> A fixture that left one null would round-trip null to null
    /// and prove nothing about whether the field reached the row — which is exactly how the W1-12 blocker hid
    /// behind 1,571 passing tests.
    /// </summary>
    [Fact]
    public async Task Every_field_the_request_carries_reaches_its_row()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        try
        {
            var sent = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SettingKeys.Contact.WhatsApp] = "94770000042",
                [SettingKeys.Contact.Phone] = "+94 11 234 5678",
                [SettingKeys.Contact.Email] = "shape-test@example.com",
                [SettingKeys.Contact.Address] = "Shape Test Studio\nColombo 03\nSri Lanka",
                [SettingKeys.Contact.OpeningHours] = "Monday to Friday, 9am \u2013 6pm\nSaturday, 9am \u2013 1pm",
                [SettingKeys.Contact.MapEmbedUrl] = "https://www.google.com/maps/embed?pb=shape-test",
                [SettingKeys.Social.FacebookUrl] = "https://www.facebook.com/shape-test",
                [SettingKeys.Social.InstagramUrl] = "https://www.instagram.com/shape-test",
                [SettingKeys.Social.TikTokUrl] = "https://www.tiktok.com/@shape-test",
                [SettingKeys.Social.YouTubeUrl] = "https://www.youtube.com/@shape-test",
            };

            using var response = await client.PutAsJsonAsync(
                new Uri(ContactSocialUrl, UriKind.Relative),
                new
                {
                    whatsApp = sent[SettingKeys.Contact.WhatsApp],
                    phone = sent[SettingKeys.Contact.Phone],
                    email = sent[SettingKeys.Contact.Email],
                    address = sent[SettingKeys.Contact.Address],
                    openingHours = sent[SettingKeys.Contact.OpeningHours],
                    mapEmbedUrl = sent[SettingKeys.Contact.MapEmbedUrl],
                    facebookUrl = sent[SettingKeys.Social.FacebookUrl],
                    instagramUrl = sent[SettingKeys.Social.InstagramUrl],
                    tikTokUrl = sent[SettingKeys.Social.TikTokUrl],
                    youTubeUrl = sent[SettingKeys.Social.YouTubeUrl],
                    rowVersion = await ContactVersionAsync(client),
                },
                JsonOptions,
                Token);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            await using var scope = host.Factory.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var stored = await dbContext.Settings.AsNoTracking()
                .Where(setting => sent.Keys.Contains(setting.Key))
                .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, StringComparer.Ordinal, Token);

            foreach (var (key, value) in sent)
            {
                Assert.Equal(value, stored[key]);
            }
        }
        finally
        {
            await RestoreAsync(host);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private async Task<MediaTestHost> StartAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        return MediaTestHost.Create(sqlServer.ConnectionString);
    }

    private static string[] Names(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();

    private static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
    }

    private static async Task<string> ContactVersionAsync(HttpClient client)
    {
        var body = await GetAsync(client, SettingsUrl);

        return body.GetProperty("groups").EnumerateArray()
            .Single(group => group.GetProperty("groupKey").GetString() == SettingKeys.Contact.Group)
            .GetProperty("rowVersion").GetString()!;
    }

    private static async Task<Guid> UploadAsync(HttpClient client)
    {
        using var form = MediaTestHost.FilePart(
            "file", SpencerDenim.Tests.MediaFixtures.TestImages.UniquePng(600, 150), "shape-logo.png", "image/png");
        form.Add(new StringContent("Logo"), "altText");

        using var response = await client.PostAsync(new Uri("/api/admin/media/images", UriKind.Relative), form, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        return body.GetProperty("publicId").GetGuid();
    }

    private static async Task SetLogoAsync(HttpClient client, Guid asset)
    {
        var body = await GetAsync(client, SettingsUrl);
        var token = body.GetProperty("groups").EnumerateArray()
            .Single(group => group.GetProperty("groupKey").GetString() == SettingKeys.Store.Group)
            .GetProperty("rowVersion").GetString();

        using var response = await client.PutAsJsonAsync(
            new Uri("/api/admin/settings/store-logo", UriKind.Relative),
            new { logoAssetPublicId = asset, rowVersion = token },
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <inheritdoc cref="AdminSettingsEndpointTests"/>
    private static async Task RestoreAsync(MediaTestHost host)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider
            .GetRequiredService<SpencerDenim.Application.Settings.Abstractions.ISiteSettingsCache>();

        var keys = SettingCatalog.All.Select(definition => definition.Key).ToList();

        await dbContext.Settings
            .Where(setting => keys.Contains(setting.Key))
            .ExecuteUpdateAsync(update => update.SetProperty(setting => setting.Value, (string?)null), Token);

        // Then back to the seeder's value where it has one, as AdminSettingsEndpointTests.RestoreAsync does.
        // Nulling every key left e.g. Orders.PayPalExpiryMinutes (seeded 60) null for the rest of the run, so
        // PayPalSettingsEndpointTests failed whenever xUnit ordered it after this class.
        foreach (var definition in SettingCatalog.All.Where(item => item.SeedValue is not null))
        {
            var key = definition.Key;
            var value = definition.SeedValue;

            await dbContext.Settings
                .Where(setting => setting.Key == key)
                .ExecuteUpdateAsync(update => update.SetProperty(setting => setting.Value, value), Token);
        }

        await dbContext.MediaUsages
            .Where(usage => usage.OwnerType == SpencerDenim.Domain.Media.MediaUsageOwnerTypes.Setting)
            .ExecuteDeleteAsync(Token);

        cache.Evict();
    }
}
