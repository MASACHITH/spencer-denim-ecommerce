using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Catalog;

/// <summary>
/// <b>Measured, not inferred: that the storefront's listing, Sale page, homepage and product page really
/// do show a campaign price once a campaign exists.</b>
/// </summary>
/// <remarks>
/// <b>Why this class was added by the W3-05b verification rather than by the task.</b> W3-05b ships the
/// campaign backend with no admin screen, and its own journal records that "listings pick campaigns up
/// through <c>ProductStats</c>" was <i>inference from reading <c>ProductCatalogReader</c></i> rather than
/// an observed fact. <c>AdminSaleCampaignEndpointTests</c> gets as far as asserting that
/// <c>ProductStats.MinSalePrice</c> moves, which is the stored column — not the four payloads a shopper
/// is actually served. The difference between "the column moved" and "the card shows it" is a projection,
/// a filter and a cache, and each of those has been wrong in this project before.
/// <para>
/// So this creates the campaign <b>through the admin API</b>, exactly as a staff member would once the
/// screen exists, and then reads the storefront endpoints with an anonymous client on the same host.
/// </para>
/// </remarks>
public sealed class CampaignStorefrontVerificationTests(SqlServerDatabaseFixture sqlServer)
{
    private const string Url = "/api/admin/marketing/sale-campaigns";
    private const string ShirtSlug = "relaxed-linen-blend-shirt";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The listing, the Sale page, the homepage and the product page, all four read after one campaign
    /// was created through the admin API. Rs. 5,490.00 becomes Rs. 4,392.00 and the card carries -37%.
    /// </summary>
    [Fact]
    public async Task A_campaign_created_through_the_admin_api_is_shown_by_every_storefront_payload()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var admin = await host.SignInAsync(
            Permissions.Promotions.View,
            Permissions.Promotions.Create,
            Permissions.Promotions.Delete);
        using var shopper = host.CreateAnonymousClient();

        var categoryId = await CategoryIdAsync(host, "women", "shirts-blouses");

        var created = await PostAsync(admin, categoryId);

        try
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);

            // 1. The department listing: the card a shopper sees in a grid.
            var listing = await CardAsync(shopper, "/api/catalog/products?category=shirts-blouses&pageSize=96");

            Assert.Equal(4_392m, Current(listing));
            Assert.Equal(6_990m, listing.GetProperty("price").GetProperty("original").GetDecimal());
            Assert.Equal(37m, listing.GetProperty("price").GetProperty("discountPercent").GetDecimal());
            Assert.True(listing.GetProperty("price").GetProperty("hasDiscount").GetBoolean());

            // 2. The Sale page, which is the same listing filtered on the stored HasDiscount flag. The
            //    shirt carries a markdown of its own, so its *presence* proves nothing - the price does.
            var sale = await CardAsync(shopper, "/api/catalog/products?onSale=true&category=shirts-blouses&pageSize=96");

            Assert.Equal(4_392m, Current(sale));

            // 3. The homepage, whose cards come from the same projection through a five-minute cache.
            var home = await HomeCardAsync(shopper);

            Assert.Equal(4_392m, Current(home));

            // 4. The product page, which prices live rather than from the stored summary.
            var (status, detail) = await GetAsync(shopper, $"/api/catalog/products/{ShirtSlug}");

            Assert.Equal(HttpStatusCode.OK, status);

            foreach (var variant in detail.GetProperty("variants").EnumerateArray())
            {
                Assert.Equal(4_392m, Current(variant));
            }

            // The product-level block the PDP header reads, which must agree with the variants below it.
            Assert.Equal(4_392m, Current(detail));
            Assert.True(detail.GetProperty("price").GetProperty("hasDiscount").GetBoolean());
            Assert.Equal(4_392m, detail.GetProperty("minPrice").GetDecimal());
        }
        finally
        {
            await DeleteAsync(admin, created);
        }

        // And the listing goes back when the campaign is removed.
        var restored = await CardAsync(shopper, "/api/catalog/products?category=shirts-blouses&pageSize=96");

        Assert.Equal(5_490m, Current(restored));
    }

    /// <summary>
    /// <b>The product page must not contradict itself while the stored summary lags.</b> A campaign that
    /// goes live by its own schedule is picked up by <c>RefreshCampaignPricing</c> within five minutes
    /// (doc 08 §7.4), so for up to five minutes <c>ProductStats</c> still holds the undiscounted figure -
    /// and the product page reads its variants live but its header block from that summary.
    /// </summary>
    /// <remarks>
    /// Arranged the way the lag really arises: the campaign row is written straight to the database with
    /// no recompute, which is exactly the state the clock leaves behind between a campaign's
    /// <c>StartsAtUtc</c> and the next pass of the job. The bag and checkout charge Rs. 4,392.00 in that
    /// window, so a header that says Rs. 5,490.00 with no badge is the defect W3-05b fixed for the
    /// variant rows and left in place one block higher up.
    /// </remarks>
    [Fact]
    public async Task The_product_page_header_agrees_with_its_variants_while_the_stored_summary_lags()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var shopper = host.CreateAnonymousClient();

        var campaignId = await InsertLiveCampaignWithoutRecomputeAsync(host, "women", "shirts-blouses");

        try
        {
            var (status, detail) = await GetAsync(shopper, $"/api/catalog/products/{ShirtSlug}");

            Assert.Equal(HttpStatusCode.OK, status);

            // The variants price live - this is W3-05b's own fix and it holds.
            foreach (var variant in detail.GetProperty("variants").EnumerateArray())
            {
                Assert.Equal(4_392m, Current(variant));
            }

            // The header block has to say the same thing, from the same live read.
            Assert.Equal(4_392m, Current(detail));
            Assert.Equal(37m, detail.GetProperty("price").GetProperty("discountPercent").GetDecimal());
            Assert.True(detail.GetProperty("price").GetProperty("hasDiscount").GetBoolean());
            Assert.Equal(4_392m, detail.GetProperty("minPrice").GetDecimal());
            Assert.Equal(4_392m, detail.GetProperty("maxPrice").GetDecimal());
        }
        finally
        {
            await RemoveCampaignAndRestoreAsync(host, campaignId);
        }
    }

    // ------------------------------------------------------------------ arrangement

    /// <summary>
    /// A live campaign written straight to the database, with no pricing recompute - the state the five
    /// minutes after a scheduled campaign's start leave behind.
    /// </summary>
    private static async Task<int> InsertLiveCampaignWithoutRecomputeAsync(
        MediaTestHost host,
        string departmentSlug,
        string categorySlug)
    {
        var categoryId = await CategoryIdAsync(host, departmentSlug, categorySlug);

        using var scope = host.Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The API's own clock, which is what decides whether the campaign is live.
        var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow.UtcDateTime;
        var campaign = new SaleCampaign
        {
            Name = string.Create(CultureInfo.InvariantCulture, $"Verification {Guid.NewGuid():N}"),
            Description = "Arranged by the W3-05b verification.",
            DiscountType = SaleDiscountType.Percent,
            DiscountValue = 20m,
            StartsAtUtc = now.AddMinutes(-1),
            EndsAtUtc = now.AddHours(1),
            IsActive = true,
        };

        campaign.Targets.Add(new SaleCampaignTarget
        {
            TargetType = SaleCampaignTargetType.Category,
            CategoryId = categoryId,
            IncludeSubcategories = true,
        });

        dbContext.SaleCampaigns.Add(campaign);
        await dbContext.SaveChangesAsync(Token);

        return campaign.Id;
    }

    /// <summary>Soft-deletes the campaign and puts every affected stored summary back.</summary>
    private static async Task RemoveCampaignAndRestoreAsync(MediaTestHost host, int campaignId)
    {
        using var scope = host.Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var refresher = scope.ServiceProvider.GetRequiredService<ICampaignPricingRefresher>();

        var campaign = await dbContext.SaleCampaigns.SingleAsync(
            row => row.Id == campaignId, CancellationToken.None);

        campaign.SoftDelete(DateTime.UtcNow, userId: null);

        await dbContext.SaveChangesAsync(CancellationToken.None);

        // After the delete, because `ProductsTargetedByAsync` ignores the query filters precisely so that
        // a removed campaign's affected set is still answerable.
        var affected = await refresher.ProductsTargetedByAsync([campaignId], CancellationToken.None);

        await refresher.RefreshProductsAsync(affected, CancellationToken.None);
    }

    // ------------------------------------------------------------------ reads

    private static decimal Current(JsonElement withPrice) =>
        withPrice.GetProperty("price").GetProperty("current").GetDecimal();

    private static async Task<JsonElement> CardAsync(HttpClient client, string path)
    {
        var (status, body) = await GetAsync(client, path);

        Assert.Equal(HttpStatusCode.OK, status);

        return body.GetProperty("items")
            .EnumerateArray()
            .Single(card => card.GetProperty("slug").GetString() == ShirtSlug);
    }

    private static async Task<JsonElement> HomeCardAsync(HttpClient client)
    {
        var (status, body) = await GetAsync(client, "/api/content/home");

        Assert.Equal(HttpStatusCode.OK, status);

        var cards = body.GetProperty("sections")
            .EnumerateArray()
            .SelectMany(section => section.GetProperty("products").EnumerateArray())
            .Where(card => card.GetProperty("slug").GetString() == ShirtSlug)
            .ToList();

        Assert.NotEmpty(cards);

        return cards[0];
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Token);

        var text = await response.Content.ReadAsStringAsync(Token);
        TestContext.Current.TestOutputHelper?.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"GET {path} -> {(int)response.StatusCode}"));

        using var document = JsonDocument.Parse(text);

        return (response.StatusCode, document.RootElement.Clone());
    }

    // ------------------------------------------------------------------ admin API

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(HttpClient client, int categoryId)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = "Verification Linen Week",
            ["description"] = "Arranged by the W3-05b verification.",
            ["discountType"] = "Percent",
            ["discountValue"] = 20m,
            // Live whenever this runs: today's Colombo midnight to a week later. The storefront prices
            // asserted above only exist while the campaign is Live.
            ["startsAt"] = ShopTime.Midnight(0),
            ["endsAt"] = ShopTime.Midnight(7),
            ["isActive"] = true,
            ["bannerPublicId"] = null,
            ["targets"] = new[]
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["targetType"] = "Category",
                    ["departmentId"] = null,
                    ["categoryId"] = categoryId,
                    ["productId"] = null,
                    ["includeSubcategories"] = true,
                },
            },
            ["rowVersion"] = null,
        };

        using var response = await client.PostAsJsonAsync(new Uri(Url, UriKind.Relative), body, Token);

        var text = await response.Content.ReadAsStringAsync(Token);
        TestContext.Current.TestOutputHelper?.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"POST -> {(int)response.StatusCode}: {text}"));

        using var document = JsonDocument.Parse(text);

        return (response.StatusCode, document.RootElement.Clone());
    }

    private static async Task DeleteAsync(HttpClient client, (HttpStatusCode Status, JsonElement Body) created)
    {
        if (created.Status != HttpStatusCode.Created)
        {
            return;
        }

        var campaign = created.Body.GetProperty("campaign");
        var id = campaign.GetProperty("id").GetInt32();

        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            new Uri(string.Create(CultureInfo.InvariantCulture, $"{Url}/{id}"), UriKind.Relative));

        request.Headers.TryAddWithoutValidation("If-Match", campaign.GetProperty("rowVersion").GetString());

        using var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
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
