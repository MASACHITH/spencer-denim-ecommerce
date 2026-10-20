using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.Tests.MediaFixtures;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// Issue #191 end to end against SQL Server and the real API: a sale campaign aimed at <b>individual
/// products</b>, the campaign <b>banner</b> chosen by its media-library <c>publicId</c> and shown on the
/// Sale page only while the campaign is live, and the homepage eviction every campaign save owes doc 08
/// §3.7 (#192).
/// </summary>
/// <remarks>
/// <b>Every test removes what it created</b>, through the API's own delete where there is one, for the
/// reason <see cref="AdminSaleCampaignEndpointTests"/> gives: the fixture's database is shared by the whole
/// assembly, and a campaign left live would reprice somebody else's assertions. Every window is relative to
/// today in the shop's time zone (<see cref="ShopTime"/>), never a calendar date.
/// </remarks>
public sealed class AdminSaleCampaignTargetsAndBannerTests(SqlServerDatabaseFixture sqlServer)
{
    private const string Url = "/api/admin/marketing/sale-campaigns";
    private const string BannerUrl = "/api/content/sale-banner";
    private const string ShirtSlug = "relaxed-linen-blend-shirt";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- product targets ------------------------------------------------------------------------

    /// <summary>
    /// <b>A product target is priced everywhere a shopper sees a price:</b> the listing card, the product
    /// page and the bag. Rs. 5,490.00 at 20% off is Rs. 4,392.00, and a neighbour in the same category that
    /// the campaign does not name keeps its price.
    /// </summary>
    [Fact]
    public async Task A_product_target_is_priced_on_the_listing_the_product_page_and_the_bag()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(
            Permissions.Promotions.View, Permissions.Promotions.Create, Permissions.Promotions.Delete);
        using var shopper = host.CreateAnonymousClient();

        var shirt = await ProductIdAsync(host, ShirtSlug);
        var neighbour = await NeighbourSlugAsync(host);
        var neighbourBefore = Current(await CardAsync(shopper, neighbour));

        var created = await SendAsync(admin, HttpMethod.Post, Url, Body("Shirt only", 20m, [Product(shirt)]));

        try
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);

            var target = created.Body.GetProperty("campaign").GetProperty("targets").EnumerateArray().Single();

            Assert.Equal("Product", target.GetProperty("targetType").GetString());
            Assert.Equal(shirt, target.GetProperty("productId").GetInt32());
            Assert.Equal(JsonValueKind.Null, target.GetProperty("categoryId").ValueKind);
            Assert.Equal("Relaxed Linen Blend Shirt", target.GetProperty("name").GetString());
            Assert.Equal(1, created.Body.GetProperty("productsRepriced").GetInt32());

            // The listing card, from the stored summary the save recomputed.
            var card = await CardAsync(shopper, ShirtSlug);
            Assert.Equal(4_392m, Current(card));
            Assert.Equal(37m, card.GetProperty("price").GetProperty("discountPercent").GetDecimal());

            // A product in the same category the campaign does not name is untouched.
            Assert.Equal(neighbourBefore, Current(await CardAsync(shopper, neighbour)));

            // The product page, which prices live.
            var (detailStatus, detail) = await GetAsync(shopper, $"/api/catalog/products/{ShirtSlug}");
            Assert.Equal(HttpStatusCode.OK, detailStatus);
            Assert.Equal(4_392m, Current(detail));
            Assert.All(detail.GetProperty("variants").EnumerateArray(), variant => Assert.Equal(4_392m, Current(variant)));

            // The bag, which prices live too and is what the shopper is charged.
            var variantId = await InStockVariantAsync(host, shirt);

            using var added = await shopper.PostAsJsonAsync(
                new Uri("/api/cart/items", UriKind.Relative), new { variantId, quantity = 1 }, JsonOptions, Token);
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);

            var bag = await added.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
            var line = bag.GetProperty("lines").EnumerateArray().Single();

            Assert.Equal(4_392m, Current(line));
        }
        finally
        {
            await DeleteAsync(admin, created);
        }

        Assert.Equal(5_490m, Current(await CardAsync(shopper, ShirtSlug)));
    }

    /// <summary>
    /// <b>A product target and a category target on the same product: the lower price wins, they never
    /// add up</b> (doc 08 §6 step 3, doc 11 line 491's own example). 20% off Women › Shirts &amp; Blouses
    /// is Rs. 4,392.00 for the shirt; Rs. 1,500 off the shirt alone is Rs. 3,990.00, which is lower, so the
    /// shirt is Rs. 3,990.00 — and a second campaign does not change that. Every other product in the
    /// category gets the 20%.
    /// </summary>
    [Fact]
    public async Task A_product_target_and_a_category_target_resolve_to_the_lower_price_without_stacking()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(
            Permissions.Promotions.View, Permissions.Promotions.Create, Permissions.Promotions.Delete);
        using var shopper = host.CreateAnonymousClient();

        var shirt = await ProductIdAsync(host, ShirtSlug);
        var shirts = await CategoryIdAsync(host, "women", "shirts-blouses");
        var neighbour = await NeighbourSlugAsync(host);
        var neighbourBefore = Current(await CardAsync(shopper, neighbour));

        var sameCampaign = await SendAsync(admin, HttpMethod.Post, Url, Body(
            "Category and product",
            1_500m,
            [Category(shirts), Product(shirt)],
            discountType: "FixedAmount"));

        try
        {
            Assert.Equal(HttpStatusCode.Created, sameCampaign.Status);

            // One campaign naming the shirt twice over, by category and by product: Rs. 1,500 off once.
            Assert.Equal(3_990m, Current(await CardAsync(shopper, ShirtSlug)));
        }
        finally
        {
            await DeleteAsync(admin, sameCampaign);
        }

        var category = await SendAsync(admin, HttpMethod.Post, Url, Body("Category 20%", 20m, [Category(shirts)]));
        var product = await SendAsync(admin, HttpMethod.Post, Url, Body(
            "Shirt Rs. 1,500 off", 1_500m, [Product(shirt)], discountType: "FixedAmount"));

        try
        {
            Assert.Equal(HttpStatusCode.Created, category.Status);
            Assert.Equal(HttpStatusCode.Created, product.Status);

            // The lower of 4,392.00 and 3,990.00, on the card and on the live product page.
            Assert.Equal(3_990m, Current(await CardAsync(shopper, ShirtSlug)));

            var (_, detail) = await GetAsync(shopper, $"/api/catalog/products/{ShirtSlug}");
            Assert.Equal(3_990m, Current(detail));

            // The neighbour is reached by the category alone.
            Assert.Equal(
                Math.Round(neighbourBefore * 0.8m, 2, MidpointRounding.AwayFromZero),
                Current(await CardAsync(shopper, neighbour)));
        }
        finally
        {
            await DeleteAsync(admin, product);
            await DeleteAsync(admin, category);
        }

        Assert.Equal(5_490m, Current(await CardAsync(shopper, ShirtSlug)));
        Assert.Equal(neighbourBefore, Current(await CardAsync(shopper, neighbour)));
    }

    /// <summary>
    /// An edit that drops a product target puts that product's price back in the same save: the
    /// recompute covers the products the campaign reached before the edit as well as after.
    /// </summary>
    [Fact]
    public async Task Removing_a_product_target_on_an_edit_restores_its_price()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(
            Permissions.Promotions.View, Permissions.Promotions.Create, Permissions.Promotions.Edit,
            Permissions.Promotions.Delete);
        using var shopper = host.CreateAnonymousClient();

        var shirt = await ProductIdAsync(host, ShirtSlug);
        var jeans = await ProductIdAsync(host, "high-rise-slim-fit-jeans");
        var jeansBefore = Current(await CardAsync(shopper, "high-rise-slim-fit-jeans"));

        var created = await SendAsync(admin, HttpMethod.Post, Url, Body("Two products", 20m, [Product(shirt), Product(jeans)]));

        try
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);
            Assert.Equal(4_392m, Current(await CardAsync(shopper, ShirtSlug)));

            var campaign = created.Body.GetProperty("campaign");
            var edited = await SendAsync(
                admin,
                HttpMethod.Put,
                $"{Url}/{campaign.GetProperty("id").GetInt32()}",
                Body("Jeans only", 20m, [Product(jeans)], rowVersion: campaign.GetProperty("rowVersion").GetString()));

            Assert.Equal(HttpStatusCode.OK, edited.Status);
            Assert.Equal(2, edited.Body.GetProperty("productsRepriced").GetInt32());

            Assert.Equal(5_490m, Current(await CardAsync(shopper, ShirtSlug)));
            Assert.Equal(
                Math.Round(jeansBefore * 0.8m, 2, MidpointRounding.AwayFromZero),
                Current(await CardAsync(shopper, "high-rise-slim-fit-jeans")));
        }
        finally
        {
            await DeleteAsync(admin, created, reread: true);
        }
    }

    [Fact]
    public async Task An_unknown_product_is_refused_against_its_row()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(Permissions.Promotions.Create);

        var shirt = await ProductIdAsync(host, ShirtSlug);

        var created = await SendAsync(admin, HttpMethod.Post, Url, Body("Ghost", 20m, [Product(shirt), Product(987_654)]));

        Assert.Equal(HttpStatusCode.BadRequest, created.Status);

        var errors = created.Body.GetProperty("errors");
        Assert.True(errors.TryGetProperty("targets[1]", out var message));
        Assert.Contains("no longer exists", message[0].GetString(), StringComparison.Ordinal);
        Assert.False(errors.TryGetProperty("targets[0]", out _));
    }

    /// <summary>
    /// A removed product cannot be put on a sale. The foreign key alone would accept it, because removal is
    /// a soft delete; the existence check reads through the soft-delete filter.
    /// </summary>
    /// <remarks>
    /// The seeded product is soft-deleted for the length of one request and put back exactly in a
    /// <c>finally</c>, by one statement that cannot fail a constraint.
    /// </remarks>
    [Fact]
    public async Task A_removed_product_is_refused_against_its_row()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(Permissions.Promotions.Create);

        var jeans = await ProductIdAsync(host, "high-rise-slim-fit-jeans");

        await SetProductDeletedAsync(host, jeans, deleted: true);

        try
        {
            var created = await SendAsync(admin, HttpMethod.Post, Url, Body("Removed", 20m, [Product(jeans)]));

            Assert.Equal(HttpStatusCode.BadRequest, created.Status);
            Assert.True(created.Body.GetProperty("errors").TryGetProperty("targets[0]", out _));
        }
        finally
        {
            await SetProductDeletedAsync(host, jeans, deleted: false);
        }
    }

    [Fact]
    public async Task The_same_product_twice_is_refused_against_the_second_row()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(Permissions.Promotions.Create);

        var shirt = await ProductIdAsync(host, ShirtSlug);

        var created = await SendAsync(admin, HttpMethod.Post, Url, Body("Twice", 20m, [Product(shirt), Product(shirt)]));

        Assert.Equal(HttpStatusCode.BadRequest, created.Status);
        Assert.True(created.Body.GetProperty("errors").TryGetProperty("targets[1]", out var message));
        Assert.Contains("already on the campaign", message[0].GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The cap is 200 targets, counted across every type. 201 product ids are refused as one field error
    /// before any of them is looked up.
    /// </summary>
    [Fact]
    public async Task More_than_two_hundred_targets_are_refused()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(Permissions.Promotions.Create);

        var targets = Enumerable.Range(1, 201).Select(id => (object)Product(id)).ToList();

        var created = await SendAsync(admin, HttpMethod.Post, Url, Body("Too many", 20m, targets));

        Assert.Equal(HttpStatusCode.BadRequest, created.Status);
        Assert.True(created.Body.GetProperty("errors").TryGetProperty("targets", out var message));
        Assert.Contains("at most 200", message[0].GetString(), StringComparison.Ordinal);
    }

    // ---- the banner -----------------------------------------------------------------------------

    /// <summary>
    /// <b>The whole banner path:</b> upload a picture, put it on a campaign by its <c>publicId</c>, see it in
    /// the admin payload and on the Sale page, find the library refusing to delete it, then clear it and
    /// find the library allowing it again.
    /// </summary>
    [Fact]
    public async Task A_banner_is_saved_tracked_in_media_usages_shown_on_the_sale_page_and_released()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(
            Permissions.Promotions.View, Permissions.Promotions.Create, Permissions.Promotions.Edit,
            Permissions.Promotions.Delete, Permissions.Media.Create, Permissions.Media.Delete);
        using var shopper = host.CreateAnonymousClient();

        var banner = await UploadImageAsync(admin, "Linen Week: 20% off shirts");
        var shirts = await CategoryIdAsync(host, "women", "shirts-blouses");

        var created = await SendAsync(admin, HttpMethod.Post, Url, Body("Banner week", 20m, [Category(shirts)], banner: banner));

        try
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);

            var campaign = created.Body.GetProperty("campaign");
            var id = campaign.GetProperty("id").GetInt32();
            var payload = campaign.GetProperty("banner");

            Assert.Equal(banner, payload.GetProperty("publicId").GetGuid());
            Assert.Equal("Linen Week: 20% off shirts", payload.GetProperty("altText").GetString());
            Assert.StartsWith("/media/", payload.GetProperty("url").GetString(), StringComparison.Ordinal);
            Assert.False(campaign.TryGetProperty("bannerAssetId", out _), "the internal asset id reached a client");
            Assert.DoesNotContain(
                created.Body.GetProperty("warnings").EnumerateArray(),
                warning => warning.GetString()!.Contains("banner", StringComparison.Ordinal));

            // doc 08 §3.3: the owner's save wrote its usage row.
            Assert.Equal([(banner, "Banner")], await UsagesAsync(host, id));

            // The Sale page shows it, to an anonymous visitor.
            var shown = await BannerAsync(shopper);
            Assert.Equal("Linen Week: 20% off shirts", shown.GetProperty("altText").GetString());
            Assert.Contains(banner.ToString(), shown.GetProperty("imageUrl").GetString(), StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.Number, shown.GetProperty("width").ValueKind);
            Assert.Equal(JsonValueKind.Number, shown.GetProperty("height").ValueKind);

            // The library refuses to delete a picture the Sale page is drawing, and says where it is used.
            using (var refused = await admin.DeleteAsync(new Uri($"/api/admin/media/{banner}", UriKind.Relative), Token))
            {
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

                var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
                var usage = problem.GetProperty("usages").EnumerateArray().Single();

                Assert.Equal("SaleCampaign", usage.GetProperty("ownerType").GetString());
                Assert.Equal("Banner", usage.GetProperty("usageRole").GetString());
                Assert.Equal("Banner week", usage.GetProperty("ownerLabel").GetString());
            }

            // Cleared on an edit: the usage row goes in the same save, and the Sale page has no banner.
            var cleared = await SendAsync(
                admin,
                HttpMethod.Put,
                $"{Url}/{id}",
                Body("Banner week", 20m, [Category(shirts)], rowVersion: campaign.GetProperty("rowVersion").GetString()));

            Assert.Equal(HttpStatusCode.OK, cleared.Status);
            Assert.Equal(JsonValueKind.Null, cleared.Body.GetProperty("campaign").GetProperty("banner").ValueKind);
            Assert.Empty(await UsagesAsync(host, id));
            Assert.Null(await BannerOrNullAsync(shopper));

            using var released = await admin.DeleteAsync(new Uri($"/api/admin/media/{banner}", UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
        }
        finally
        {
            await DeleteAsync(admin, created, reread: true);
        }
    }

    /// <summary>
    /// Replacing the banner moves the usage row to the new picture, and removing the campaign releases it:
    /// a campaign nobody can see on the screen must not keep a picture undeletable.
    /// </summary>
    [Fact]
    public async Task Replacing_the_banner_moves_its_usage_and_removing_the_campaign_releases_it()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(
            Permissions.Promotions.View, Permissions.Promotions.Create, Permissions.Promotions.Edit,
            Permissions.Promotions.Delete, Permissions.Media.Create);
        using var shopper = host.CreateAnonymousClient();

        var first = await UploadImageAsync(admin, "First banner");
        var second = await UploadImageAsync(admin, "Second banner");
        var shirts = await CategoryIdAsync(host, "women", "shirts-blouses");

        var created = await SendAsync(admin, HttpMethod.Post, Url, Body("Swap", 20m, [Category(shirts)], banner: first));
        Assert.Equal(HttpStatusCode.Created, created.Status);

        var campaign = created.Body.GetProperty("campaign");
        var id = campaign.GetProperty("id").GetInt32();

        var replaced = await SendAsync(
            admin,
            HttpMethod.Put,
            $"{Url}/{id}",
            Body("Swap", 20m, [Category(shirts)], banner: second, rowVersion: campaign.GetProperty("rowVersion").GetString()));

        Assert.Equal(HttpStatusCode.OK, replaced.Status);
        Assert.Equal([(second, "Banner")], await UsagesAsync(host, id));
        Assert.Equal("Second banner", (await BannerAsync(shopper)).GetProperty("altText").GetString());

        await DeleteAsync(admin, created, reread: true);

        Assert.Empty(await UsagesAsync(host, id));
        Assert.Null(await BannerOrNullAsync(shopper));
    }

    /// <summary>
    /// Each of the four ways an asset cannot be a banner, named by the field: a document, a private file
    /// (a customer's bank slip is one), an image still processing, and a deleted image. Plus an identifier
    /// nobody issued, and one in the wrong shape.
    /// </summary>
    [Theory]
    [InlineData("document")]
    [InlineData("private")]
    [InlineData("processing")]
    [InlineData("deleted")]
    [InlineData("unknown")]
    public async Task An_asset_that_cannot_be_a_banner_is_refused_by_name(string kind)
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(Permissions.Promotions.Create);

        var shirts = await CategoryIdAsync(host, "women", "shirts-blouses");
        var asset = kind == "unknown" ? Guid.NewGuid() : await InsertAssetAsync(host, kind);

        var created = await SendAsync(admin, HttpMethod.Post, Url, Body("Bad banner", 20m, [Category(shirts)], banner: asset));

        Assert.Equal(HttpStatusCode.BadRequest, created.Status);
        Assert.True(created.Body.GetProperty("errors").TryGetProperty("bannerPublicId", out var message));
        Assert.Contains("ready public image", message[0].GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("not-a-guid")]
    [InlineData("{0000f00d-0000-4000-8000-000000000001}")]
    public async Task A_banner_that_is_not_a_public_id_is_refused_by_name(string value)
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(Permissions.Promotions.Create);

        var shirts = await CategoryIdAsync(host, "women", "shirts-blouses");
        var body = Body("Bad banner", 20m, [Category(shirts)]);
        body["bannerPublicId"] = value == "42" ? 42 : value;

        var created = await SendAsync(admin, HttpMethod.Post, Url, body);

        Assert.Equal(HttpStatusCode.BadRequest, created.Status);
        Assert.True(created.Body.GetProperty("errors").TryGetProperty("bannerPublicId", out var message));

        // Refused for its *shape*, before any lookup: a braced GUID is not quietly read and then refused
        // as an unknown picture (a mutation of the parser to Guid.TryParse survived until this line).
        Assert.Contains("as an identifier like", message[0].GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The old field name is not quietly accepted: a body that still sends <c>bannerAssetId</c> and no
    /// <c>bannerPublicId</c> is refused naming the field it is missing.
    /// </summary>
    [Fact]
    public async Task A_body_without_banner_public_id_is_refused_naming_it()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(Permissions.Promotions.Create);

        var shirts = await CategoryIdAsync(host, "women", "shirts-blouses");
        var body = Body("Old shape", 20m, [Category(shirts)]);
        body.Remove("bannerPublicId");
        body["bannerAssetId"] = null;

        var created = await SendAsync(admin, HttpMethod.Post, Url, body);

        Assert.Equal(HttpStatusCode.BadRequest, created.Status);
        Assert.True(created.Body.GetProperty("errors").TryGetProperty("bannerPublicId", out _));
    }

    [Fact]
    public async Task A_banner_without_a_description_saves_with_a_warning_and_an_empty_alt_on_the_sale_page()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(
            Permissions.Promotions.View, Permissions.Promotions.Create, Permissions.Promotions.Delete,
            Permissions.Media.Create);
        using var shopper = host.CreateAnonymousClient();

        var banner = await UploadImageAsync(admin, altText: null);
        var shirts = await CategoryIdAsync(host, "women", "shirts-blouses");

        var created = await SendAsync(admin, HttpMethod.Post, Url, Body("Quiet banner", 20m, [Category(shirts)], banner: banner));

        try
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);
            Assert.Contains(
                created.Body.GetProperty("warnings").EnumerateArray(),
                warning => warning.GetString()!.StartsWith("The banner image has no description", StringComparison.Ordinal));

            var shown = await BannerAsync(shopper);
            Assert.Equal(JsonValueKind.String, shown.GetProperty("altText").ValueKind);
            Assert.Equal(string.Empty, shown.GetProperty("altText").GetString());
        }
        finally
        {
            await DeleteAsync(admin, created);
        }
    }

    // ---- the storefront banner and the window ---------------------------------------------------

    /// <summary>
    /// <b>Only while live, in shop time.</b> A campaign that starts tomorrow, one that ended yesterday and
    /// one that is switched off all have a banner, and the Sale page shows none of them.
    /// </summary>
    [Theory]
    [InlineData("scheduled")]
    [InlineData("ended")]
    [InlineData("inactive")]
    public async Task A_campaign_that_is_not_live_shows_no_banner(string state)
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(
            Permissions.Promotions.View, Permissions.Promotions.Create, Permissions.Promotions.Delete,
            Permissions.Media.Create);
        using var shopper = host.CreateAnonymousClient();

        var banner = await UploadImageAsync(admin, "Not yet");
        var shirts = await CategoryIdAsync(host, "women", "shirts-blouses");

        var body = Body("Not live", 20m, [Category(shirts)], banner: banner);

        switch (state)
        {
            case "scheduled":
                body["startsAt"] = ShopTime.Midnight(1);
                body["endsAt"] = ShopTime.Midnight(3);
                break;
            case "ended":
                body["startsAt"] = ShopTime.Midnight(-3);
                body["endsAt"] = ShopTime.Midnight(-1);
                break;
            default:
                body["isActive"] = false;
                break;
        }

        var created = await SendAsync(admin, HttpMethod.Post, Url, body);

        try
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);
            Assert.NotEqual("Live", created.Body.GetProperty("campaign").GetProperty("status").GetString());

            Assert.Null(await BannerOrNullAsync(shopper));
        }
        finally
        {
            await DeleteAsync(admin, created);
        }
    }

    /// <summary>
    /// The window's own boundaries, read by the reader directly with an instant either side of each:
    /// start inclusive, end exclusive — <c>CampaignWindow.IsLive</c>'s convention, written again in SQL.
    /// </summary>
    [Fact]
    public async Task The_banner_window_is_start_inclusive_and_end_exclusive()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(
            Permissions.Promotions.View, Permissions.Promotions.Create, Permissions.Promotions.Delete,
            Permissions.Media.Create);

        var banner = await UploadImageAsync(admin, "Boundaries");
        var shirts = await CategoryIdAsync(host, "women", "shirts-blouses");

        var created = await SendAsync(admin, HttpMethod.Post, Url, Body("Boundaries", 20m, [Category(shirts)], banner: banner));

        try
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);

            var (startsUtc, endsUtc) = await WindowAsync(host, created.Body.GetProperty("campaign").GetProperty("id").GetInt32());

            using var scope = host.Factory.Services.CreateScope();
            var reader = scope.ServiceProvider
                .GetRequiredService<SpencerDenim.Application.Marketing.SaleBanner.ISaleBannerReader>();

            Assert.Null(await reader.GetLiveBannerAsync(startsUtc.AddMilliseconds(-1), Token));
            Assert.NotNull(await reader.GetLiveBannerAsync(startsUtc, Token));
            Assert.NotNull(await reader.GetLiveBannerAsync(endsUtc.AddMilliseconds(-1), Token));
            Assert.Null(await reader.GetLiveBannerAsync(endsUtc, Token));
        }
        finally
        {
            await DeleteAsync(admin, created);
        }
    }

    /// <summary>
    /// Two live campaigns with a banner each: the one that started most recently is shown — the owner
    /// decision recorded in the #191 journal, because neither doc 08 nor doc 11 says.
    /// </summary>
    [Fact]
    public async Task With_two_live_banners_the_most_recent_start_is_shown()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(
            Permissions.Promotions.View, Permissions.Promotions.Create, Permissions.Promotions.Delete,
            Permissions.Media.Create);
        using var shopper = host.CreateAnonymousClient();

        var older = await UploadImageAsync(admin, "Started yesterday");
        var newer = await UploadImageAsync(admin, "Started today");
        var shirts = await CategoryIdAsync(host, "women", "shirts-blouses");

        var olderBody = Body("Older", 10m, [Category(shirts)], banner: older);
        olderBody["startsAt"] = ShopTime.Midnight(-1);

        // Created second, so a reader that ordered by id alone would pick it — and it must not.
        var newerBody = Body("Newer", 10m, [Category(shirts)], banner: newer);

        var newerCreated = await SendAsync(admin, HttpMethod.Post, Url, newerBody);
        var olderCreated = await SendAsync(admin, HttpMethod.Post, Url, olderBody);

        try
        {
            Assert.Equal(HttpStatusCode.Created, newerCreated.Status);
            Assert.Equal(HttpStatusCode.Created, olderCreated.Status);

            Assert.Equal("Started today", (await BannerAsync(shopper)).GetProperty("altText").GetString());
        }
        finally
        {
            await DeleteAsync(admin, olderCreated);
            await DeleteAsync(admin, newerCreated);
        }
    }

    /// <summary>
    /// The banner endpoint is public, answers a normal 200 with <c>banner: null</c> on an ordinary day,
    /// and carries nothing that depends on who asks — so a server-side render can hand it to the browser.
    /// </summary>
    [Fact]
    public async Task The_sale_banner_endpoint_is_public_and_answers_null_when_there_is_none()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var shopper = host.CreateAnonymousClient();

        var (status, body) = await GetAsync(shopper, BannerUrl);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("banner").ValueKind);
        Assert.Equal(["banner"], body.EnumerateObject().Select(property => property.Name));

        using var head = new HttpRequestMessage(HttpMethod.Head, new Uri(BannerUrl, UriKind.Relative));
        using var headResponse = await shopper.SendAsync(head, Token);
        Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
    }

    // ---- the homepage eviction (#192) -----------------------------------------------------------

    /// <summary>
    /// <b>The homepage follows every campaign save at once</b>, rather than serving its five-minute cache
    /// (doc 08 §3.7: "evicted on save"). The page is read first so the cache is warm, then a create, an
    /// edit and a delete each change the shirt's homepage price on the very next read.
    /// </summary>
    [Fact]
    public async Task Every_campaign_save_evicts_the_cached_homepage()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(
            Permissions.Promotions.View, Permissions.Promotions.Create, Permissions.Promotions.Edit,
            Permissions.Promotions.Delete);
        using var shopper = host.CreateAnonymousClient();

        var shirt = await ProductIdAsync(host, ShirtSlug);

        // Warm: this read fills the five-minute cache.
        Assert.Equal(5_490m, Current(await HomeCardAsync(shopper)));

        var created = await SendAsync(admin, HttpMethod.Post, Url, Body("Home 20%", 20m, [Product(shirt)]));
        var deleted = false;

        try
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);
            Assert.Equal(4_392m, Current(await HomeCardAsync(shopper)));

            var campaign = created.Body.GetProperty("campaign");
            var edited = await SendAsync(
                admin,
                HttpMethod.Put,
                $"{Url}/{campaign.GetProperty("id").GetInt32()}",
                Body("Home 10%", 10m, [Product(shirt)], rowVersion: campaign.GetProperty("rowVersion").GetString()));

            Assert.Equal(HttpStatusCode.OK, edited.Status);
            Assert.Equal(4_941m, Current(await HomeCardAsync(shopper)));

            await DeleteAsync(admin, created, reread: true);
            deleted = true;

            Assert.Equal(5_490m, Current(await HomeCardAsync(shopper)));
        }
        finally
        {
            if (!deleted)
            {
                await DeleteAsync(admin, created, reread: true);
            }
        }
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static Dictionary<string, object?> Body(
        string name,
        decimal discountValue,
        IReadOnlyList<object> targets,
        Guid? banner = null,
        string? rowVersion = null,
        string discountType = "Percent") =>
        new(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["description"] = "Arranged by the #191 integration suite.",
            ["discountType"] = discountType,
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

    private static Dictionary<string, object?> Product(int productId) =>
        new(StringComparer.Ordinal)
        {
            ["targetType"] = "Product",
            ["departmentId"] = null,
            ["categoryId"] = null,
            ["productId"] = productId,
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

        using var response = await client.SendAsync(request, Token);

        var text = await response.Content.ReadAsStringAsync(Token);
        TestContext.Current.TestOutputHelper?.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"{method} {path} -> {(int)response.StatusCode}: {text}"));

        using var document = JsonDocument.Parse(text);

        return (response.StatusCode, document.RootElement.Clone());
    }

    private static async Task DeleteAsync(
        HttpClient client,
        (HttpStatusCode Status, JsonElement Body) created,
        bool reread = false)
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

    private static decimal Current(JsonElement withPrice) =>
        withPrice.GetProperty("price").GetProperty("current").GetDecimal();

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Token);

        var text = await response.Content.ReadAsStringAsync(Token);
        using var document = JsonDocument.Parse(text);

        return (response.StatusCode, document.RootElement.Clone());
    }

    private static async Task<JsonElement> CardAsync(HttpClient client, string slug)
    {
        var (status, body) = await GetAsync(client, "/api/catalog/products?department=women&pageSize=96");

        Assert.Equal(HttpStatusCode.OK, status);

        return body.GetProperty("items")
            .EnumerateArray()
            .Single(card => card.GetProperty("slug").GetString() == slug);
    }

    private static async Task<JsonElement> HomeCardAsync(HttpClient client)
    {
        var (status, body) = await GetAsync(client, "/api/content/home");

        Assert.Equal(HttpStatusCode.OK, status);

        return body.GetProperty("sections")
            .EnumerateArray()
            .SelectMany(section => section.GetProperty("products").EnumerateArray())
            .First(card => card.GetProperty("slug").GetString() == ShirtSlug);
    }

    private static async Task<JsonElement> BannerAsync(HttpClient client) =>
        await BannerOrNullAsync(client)
            ?? throw new Xunit.Sdk.XunitException("The Sale page had no banner.");

    private static async Task<JsonElement?> BannerOrNullAsync(HttpClient client)
    {
        var (status, body) = await GetAsync(client, BannerUrl);

        Assert.Equal(HttpStatusCode.OK, status);

        var banner = body.GetProperty("banner");

        return banner.ValueKind == JsonValueKind.Null ? null : banner;
    }

    private static async Task<Guid> UploadImageAsync(HttpClient client, string? altText)
    {
        using var form = MediaTestHost.FilePart("file", TestImages.UniqueJpeg(1600, 500), "banner.jpg", "image/jpeg");

        if (altText is not null)
        {
            form.Add(new StringContent(altText), "altText");
        }

        using var response = await client.PostAsync(new Uri("/api/admin/media/images", UriKind.Relative), form, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        return body.GetProperty("publicId").GetGuid();
    }

    /// <summary>
    /// A media row that is not a usable banner, written straight to the table: the upload endpoint cannot
    /// make a private, processing or document asset on purpose, and that is exactly the point.
    /// </summary>
    private static async Task<Guid> InsertAssetAsync(MediaTestHost host, string kind)
    {
        using var scope = host.Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var publicId = Guid.NewGuid();
        var asset = new MediaAsset
        {
            PublicId = publicId,
            MediaType = kind == "document" ? MediaAssetType.Document : MediaAssetType.Image,
            Visibility = kind == "private" ? MediaVisibility.Private : MediaVisibility.Public,
            Status = kind == "processing" ? MediaAssetStatus.Processing : MediaAssetStatus.Ready,
            OriginalFileName = "not-a-banner.jpg",
            StorageKey = $"public/originals/{publicId}.jpg",
            ContentType = kind == "document" ? "application/pdf" : "image/jpeg",
            SizeBytes = 1_024,
            Width = 1600,
            Height = 500,
            Checksum = RandomNumberGenerator.GetBytes(32),
            AltText = "Not a banner",
        };

        dbContext.MediaAssets.Add(asset);
        await dbContext.SaveChangesAsync(Token);

        if (kind == "deleted")
        {
            asset.SoftDelete(DateTime.UtcNow, null);
            await dbContext.SaveChangesAsync(Token);
        }

        return publicId;
    }

    private static async Task<List<(Guid PublicId, string Role)>> UsagesAsync(MediaTestHost host, int campaignId)
    {
        using var scope = host.Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var ownerId = campaignId.ToString(CultureInfo.InvariantCulture);

        var rows = await dbContext.MediaUsages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(usage => usage.OwnerType == MediaUsageOwnerTypes.SaleCampaign && usage.OwnerId == ownerId)
            .Select(usage => new { usage.MediaAsset!.PublicId, usage.UsageRole })
            .ToListAsync(Token);

        return rows.Select(row => (row.PublicId, row.UsageRole)).ToList();
    }

    private static async Task<(DateTime StartsUtc, DateTime EndsUtc)> WindowAsync(MediaTestHost host, int campaignId)
    {
        using var scope = host.Factory.Services.CreateScope();

        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().SaleCampaigns
            .AsNoTracking()
            .Where(campaign => campaign.Id == campaignId)
            .Select(campaign => new { campaign.StartsAtUtc, campaign.EndsAtUtc })
            .SingleAsync(Token);

        return (row.StartsAtUtc, row.EndsAtUtc);
    }

    private static async Task<int> ProductIdAsync(MediaTestHost host, string slug)
    {
        using var scope = host.Factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Products
            .AsNoTracking()
            .Where(product => product.Slug == slug)
            .Select(product => product.Id)
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

    /// <summary>Another published product filed under Women › Shirts &amp; Blouses, which the shirt is not.</summary>
    private static async Task<string> NeighbourSlugAsync(MediaTestHost host)
    {
        using var scope = host.Factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Products
            .AsNoTracking()
            .Where(product => product.IsPublished
                && product.Slug != ShirtSlug
                && product.PrimaryCategory!.Slug == "shirts-blouses"
                && product.Department!.Slug == "women")
            .OrderBy(product => product.Id)
            .Select(product => product.Slug)
            .FirstAsync(Token);
    }

    private static async Task<int> InStockVariantAsync(MediaTestHost host, int productId)
    {
        using var scope = host.Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.ProductId == productId
                && variant.IsActive
                && dbContext.StockLevels.Any(stock => stock.VariantId == variant.Id && stock.OnHand - stock.Reserved > 0))
            .OrderBy(variant => variant.Id)
            .Select(variant => variant.Id)
            .FirstAsync(Token);
    }

    /// <summary>
    /// Soft-deletes or restores a seeded product with one statement, the only way past the interceptor's
    /// soft-delete guard that also cannot be refused halfway.
    /// </summary>
    private static async Task SetProductDeletedAsync(MediaTestHost host, int productId, bool deleted)
    {
        using var scope = host.Factory.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Products
            .IgnoreQueryFilters()
            .Where(product => product.Id == productId)
            .ExecuteUpdateAsync(
                product => product
                    .SetProperty(row => row.IsDeleted, deleted)
                    .SetProperty(row => row.DeletedAtUtc, deleted ? DateTime.UtcNow : (DateTime?)null),
                CancellationToken.None);
    }
}
