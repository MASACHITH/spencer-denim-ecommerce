using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The ADM-14 sale-campaign endpoints, end to end against a real SQL Server and a real API (doc 11 P3
/// section A).
/// <para>
/// <b>Every test removes the campaigns it created and puts the affected products' stored summaries
/// back.</b> The fixture's database is shared by the whole assembly, whose classes run strictly one after
/// another, and <c>catalog.ProductStats</c> is what the listing, Sale-page and homepage suites assert
/// prices from — so a campaign left live would fail somebody else's test with no clue where it came from.
/// The clean-up goes through the API's own delete, which is also the assertion that a delete restores the
/// summary.
/// </para>
/// <para>
/// <b>These are also the first backend tests to exercise <c>AdminJsonBody</c>.</b> W3-05b extracted those
/// readers from <c>AdminPromotionEndpoints</c>, which had no backend test of its own — its coverage was a
/// Playwright suite against a stub, and #126 is the standing note that Playwright never starts the .NET
/// API, so a screen test cannot observe a backend change. The missing-key and bad-type cases below are
/// therefore the first thing in the repository that proves those readers behave as their remarks claim.
/// </para>
/// </summary>
public sealed class AdminSaleCampaignEndpointTests(SqlServerDatabaseFixture sqlServer)
{
    private const string Url = "/api/admin/marketing/sale-campaigns";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_route_refuses_an_unauthenticated_caller()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var client = host.CreateAnonymousClient();

        foreach (var (method, path) in Routes())
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
            using var response = await client.SendAsync(request, Token);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    /// <summary>
    /// A signed-in staff member with other rights entirely. Proves each check is "holds this
    /// <c>Promotions.*</c> permission" rather than "is signed in".
    /// </summary>
    [Fact]
    public async Task Every_route_refuses_a_signed_in_user_without_the_promotions_permission()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var client = await host.SignInAsync(Permissions.Media.Create, Permissions.Categories.Edit);

        foreach (var (method, path) in Routes())
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

            if (method != HttpMethod.Get)
            {
                request.Content = JsonContent.Create(new { });
            }

            using var response = await client.SendAsync(request, Token);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    /// <summary>
    /// <b>Doc 11 P3 steps 1 to 3 end to end:</b> a staff member creates "Linen Week", and the shirt's
    /// stored summary and the campaign's own row both say so.
    /// </summary>
    /// <remarks>
    /// The three things a create has to get right are asserted separately, because each can fail without
    /// the others: the row (the campaign is stored and comes back with its targets named and its derived
    /// status), the recompute (<c>productsRepriced</c> is non-zero and <c>ProductStats</c> has moved —
    /// doc 11 P3 step 3), and the dates (they went in as Colombo wall-clock and come back as the same
    /// wall-clock, not shifted by five and a half hours).
    /// </remarks>
    [Fact]
    public async Task Creating_a_campaign_stores_it_and_reprices_its_products()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var client = await host.SignInAsync(
            Permissions.Promotions.View,
            Permissions.Promotions.Create,
            Permissions.Promotions.Delete);

        var categoryId = await CategoryIdAsync(host, "women", "shirts-blouses");
        var productId = await ProductIdAsync(host, "relaxed-linen-blend-shirt");

        var body = Body(
            name: "Linen Week",
            discountValue: 20m,
            targets: [Category(categoryId)]);

        var created = await PostAsync(client, body);

        try
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);

            var campaign = created.Body.GetProperty("campaign");

            Assert.Equal("Linen Week", campaign.GetProperty("name").GetString());
            Assert.Equal("Percent", campaign.GetProperty("discountType").GetString());
            Assert.Equal(20m, campaign.GetProperty("discountValue").GetDecimal());
            Assert.Equal("Live", campaign.GetProperty("status").GetString());

            // Colombo wall-clock in and the same wall-clock out: exactly what was sent, plus the seconds.
            // A server that treated the input as UTC would answer midnight plus 05:30.
            Assert.Equal($"{body["startsAt"]}:00", campaign.GetProperty("startsAt").GetString());
            Assert.Equal($"{body["endsAt"]}:00", campaign.GetProperty("endsAt").GetString());

            var target = campaign.GetProperty("targets").EnumerateArray().Single();

            Assert.Equal("Category", target.GetProperty("targetType").GetString());
            Assert.Equal(categoryId, target.GetProperty("categoryId").GetInt32());
            Assert.True(target.GetProperty("includeSubcategories").GetBoolean());
            Assert.Equal("Women › Shirts & Blouses", target.GetProperty("name").GetString());

            // Doc 11 P3 step 3: the stored summary of every affected product, recomputed on save.
            Assert.True(
                created.Body.GetProperty("productsRepriced").GetInt32() >= 3,
                "the category has at least three products and the save repriced fewer");

            Assert.Equal(4_392m, await MinSalePriceAsync(host, productId));

            // No warnings: it is live, it reaches products, and it is not at the 90% ceiling.
            Assert.Empty(created.Body.GetProperty("warnings").EnumerateArray());

            // And the list shows it.
            using var listed = await client.GetAsync(new Uri(Url, UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

            var list = await listed.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

            Assert.Contains(
                list.EnumerateArray(),
                row => row.GetProperty("name").GetString() == "Linen Week");
        }
        finally
        {
            await DeleteAsync(client, created);
        }

        // The delete put the price back, which is the other half of doc 11 P3 step 3.
        Assert.Equal(5_490m, await MinSalePriceAsync(host, productId));
    }

    /// <summary>
    /// <b>The warning a campaign that reaches no product answers with.</b> This is the mistake the form
    /// would otherwise make silently: the campaign is stored, the screen looks the same as a working one,
    /// and the shop never goes on sale.
    /// </summary>
    /// <remarks>
    /// <b>The empty category is created by the test, because the seeded catalogue has none.</b> Measured:
    /// every one of its categories carries at least one product link, and all three departments have
    /// products — so a category with nothing in it is the only honest arrangement for this case and it
    /// has to be made. It is soft-deleted in a <c>finally</c>, through the domain method doc 08 §10.2 DB5
    /// requires, so the catalogue the storefront suites read is the one they expect.
    /// </remarks>
    [Fact]
    public async Task A_campaign_whose_targets_reach_no_product_still_saves_and_says_so()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var client = await host.SignInAsync(
            Permissions.Promotions.Create,
            Permissions.Promotions.Delete);

        var emptyCategoryId = await NewEmptyCategoryAsync(host);

        try
        {
            var created = await PostAsync(client, Body(
                name: "Reaches nothing",
                discountValue: 20m,
                targets: [Category(emptyCategoryId)]));

            try
            {
                Assert.Equal(HttpStatusCode.Created, created.Status);
                Assert.Equal(0, created.Body.GetProperty("productsRepriced").GetInt32());

                var warnings = created.Body.GetProperty("warnings")
                    .EnumerateArray()
                    .Select(warning => warning.GetString())
                    .ToList();

                Assert.Contains(
                    warnings,
                    warning => warning!.Contains("do not reach any product", StringComparison.Ordinal));
            }
            finally
            {
                await DeleteAsync(client, created);
            }
        }
        finally
        {
            await RemoveCategoryAsync(host, emptyCategoryId);
        }
    }

    /// <summary>
    /// Doc 11 P3 step 5: an edit that narrows a campaign puts the prices of the products it has dropped
    /// back, in the same save.
    /// </summary>
    /// <remarks>
    /// <b>This is the assertion behind "the union of before and after".</b> The campaign starts on the
    /// whole Women department and is narrowed to one category; a save that recomputed only what the
    /// campaign targets <i>now</i> would leave every other Women product advertising a discount it is no
    /// longer getting — and the bag, which prices live, would charge the list price while the card said
    /// otherwise.
    /// </remarks>
    [Fact]
    public async Task Narrowing_a_campaign_puts_the_dropped_products_prices_back()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var client = await host.SignInAsync(
            Permissions.Promotions.View,
            Permissions.Promotions.Create,
            Permissions.Promotions.Edit,
            Permissions.Promotions.Delete);

        var departmentId = await DepartmentIdAsync(host, "women");
        var categoryId = await CategoryIdAsync(host, "women", "shirts-blouses");
        var shirtId = await ProductIdAsync(host, "relaxed-linen-blend-shirt");
        var jeansId = await ProductIdAsync(host, "high-rise-slim-fit-jeans");

        var created = await PostAsync(client, Body(
            name: "Women 20",
            discountValue: 20m,
            targets: [Department(departmentId)]));

        try
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);

            // Both products are discounted while the campaign is department-wide.
            Assert.Equal(4_392m, await MinSalePriceAsync(host, shirtId));
            Assert.Equal(5_992m, await MinSalePriceAsync(host, jeansId));

            var id = created.Body.GetProperty("campaign").GetProperty("id").GetInt32();
            var rowVersion = created.Body.GetProperty("campaign").GetProperty("rowVersion").GetString();

            using var edit = new HttpRequestMessage(
                HttpMethod.Put,
                new Uri(string.Create(CultureInfo.InvariantCulture, $"{Url}/{id}"), UriKind.Relative))
            {
                Content = JsonContent.Create(
                    Body(name: "Shirts 20", discountValue: 20m, targets: [Category(categoryId)], rowVersion: rowVersion)),
            };

            using var edited = await client.SendAsync(edit, Token);

            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

            // The shirt is still on sale; the jeans are back at their list price.
            Assert.Equal(4_392m, await MinSalePriceAsync(host, shirtId));
            Assert.Equal(7_490m, await MinSalePriceAsync(host, jeansId));
        }
        finally
        {
            // `reread`, because the edit above moved the row version on and the one in the create's
            // response is now stale — which the delete answers with a 409, as it should.
            await DeleteAsync(client, created, reread: true);
        }

        Assert.Equal(5_490m, await MinSalePriceAsync(host, shirtId));
    }

    /// <summary>
    /// Doc 08 §1.5's optimistic concurrency: an edit carrying the row version somebody else has already
    /// superseded is a 409 and writes nothing.
    /// </summary>
    [Fact]
    public async Task An_edit_with_a_stale_row_version_is_refused()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var client = await host.SignInAsync(
            Permissions.Promotions.View,
            Permissions.Promotions.Create,
            Permissions.Promotions.Edit,
            Permissions.Promotions.Delete);

        var categoryId = await CategoryIdAsync(host, "women", "shirts-blouses");

        var created = await PostAsync(client, Body(
            name: "Concurrent",
            discountValue: 20m,
            targets: [Category(categoryId)]));

        try
        {
            var id = created.Body.GetProperty("campaign").GetProperty("id").GetInt32();
            var stale = created.Body.GetProperty("campaign").GetProperty("rowVersion").GetString();

            // The first edit succeeds and moves the row version on.
            using var first = new HttpRequestMessage(
                HttpMethod.Put,
                new Uri(string.Create(CultureInfo.InvariantCulture, $"{Url}/{id}"), UriKind.Relative))
            {
                Content = JsonContent.Create(
                    Body(name: "Concurrent v2", discountValue: 25m, targets: [Category(categoryId)], rowVersion: stale)),
            };

            using var firstResponse = await client.SendAsync(first, Token);
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

            // The second sends the version it read before the first.
            using var second = new HttpRequestMessage(
                HttpMethod.Put,
                new Uri(string.Create(CultureInfo.InvariantCulture, $"{Url}/{id}"), UriKind.Relative))
            {
                Content = JsonContent.Create(
                    Body(name: "Concurrent v3", discountValue: 30m, targets: [Category(categoryId)], rowVersion: stale)),
            };

            using var secondResponse = await client.SendAsync(second, Token);

            Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);

            var problem = await secondResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
            Assert.Equal("stale_row_version", problem.GetProperty("code").GetString());

            // And nothing was written: the name is still the first edit's.
            using var listed = await client.GetAsync(new Uri(Url, UriKind.Relative), Token);
            var list = await listed.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

            Assert.Contains(
                list.EnumerateArray(),
                row => row.GetProperty("id").GetInt32() == id
                    && row.GetProperty("name").GetString() == "Concurrent v2");
        }
        finally
        {
            await DeleteAsync(client, created, reread: true);
        }
    }

    /// <summary>
    /// Doc 11 P3 steps 1 and 2 and doc 08 §3.6's constraints as named field errors, one case each.
    /// </summary>
    /// <remarks>
    /// <b>Each case asserts the field the message is attached to, not only the 400.</b> A screen puts the
    /// message beside the control, so an error filed under the wrong key is a message a staff member
    /// never sees — and a test that only checked the status would not notice.
    /// </remarks>
    [Theory]
    [InlineData("discountValue", 91, "percent above the 90 ceiling")]
    [InlineData("discountValue", 0, "a campaign that discounts nothing")]
    public async Task A_bad_discount_value_is_a_field_error(string field, int value, string why)
    {
        sqlServer.SkipIfUnavailable();

        Assert.NotEmpty(why);

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var client = await host.SignInAsync(Permissions.Promotions.Create);

        var categoryId = await CategoryIdAsync(host, "women", "shirts-blouses");

        var created = await PostAsync(client, Body(
            name: "Out of range",
            discountValue: value,
            targets: [Category(categoryId)]));

        Assert.Equal(HttpStatusCode.BadRequest, created.Status);
        Assert.True(
            created.Body.GetProperty("errors").TryGetProperty(field, out _),
            $"no error was filed under '{field}'");
    }

    /// <summary>
    /// <b>The body the sale-campaign screen really sends, keyed and typed exactly as it builds it, against
    /// the real API — the one join #126 leaves open, closed for the create.</b>
    /// </summary>
    /// <remarks>
    /// Every other test in this file sends <see cref="Body"/>, which the suite wrote; the screen's Vitest
    /// (<c>sale-campaigns-page.spec.ts</c>, "creates doc 11 line 450 Linen Week") asserts the screen's
    /// body with <c>toEqual</c>, which is exact. So one side is pinned to the screen and the other to the
    /// API, and <b>nothing joined the two spellings</b> — a renamed or dropped key would pass both.
    /// That is precisely how the promo-code 500 survived: the screen was proved against a Node stub and
    /// the store was never proved at all.
    /// <para>
    /// The literal below is therefore a copy of that Vitest's expected body and must stay one — nine keys,
    /// in the screen's names and types, with <b>no <c>rowVersion</c></b> (the screen sends it in
    /// <c>If-Match</c> and omits it from the body, which <c>AdminJsonBody.RowVersionIn</c> tolerates and
    /// no test had shown), <c>bannerPublicId</c> null because no banner was chosen (#191 renamed it from <c>bannerAssetId</c>), and
    /// <c>description</c> null because the optional note was left blank.
    /// </para>
    /// <para>
    /// <b>The two dates are the one thing that is not a literal copy.</b> The Vitest's
    /// <c>2026-10-19T00:00</c> is only bytes the screen passes through, against a mocked API; here the
    /// real API derives the status from its real clock, and the status is asserted. So the window is a week to a
    /// fortnight from today (<see cref="ShopTime.Midnight"/>), in the same <c>yyyy-MM-ddTHH:mm</c> shape
    /// the screen's <c>datetime-local</c> boxes send. A fixed <c>2026-10-19T00:00</c> made this test fail
    /// from that day on, when the campaign it created was already Live.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_screens_own_body_creates_a_campaign()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var client = await host.SignInAsync(
            Permissions.Promotions.Create,
            Permissions.Promotions.Delete);

        var categoryId = await CategoryIdAsync(host, "women", "shirts-blouses");

        var screenBody = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = "Linen Week",
            ["description"] = null,
            ["discountType"] = "Percent",
            ["discountValue"] = 20m,
            ["startsAt"] = ShopTime.Midnight(7),
            ["endsAt"] = ShopTime.Midnight(14),
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
        };

        var created = await PostAsync(client, screenBody);

        try
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);

            var campaign = created.Body.GetProperty("campaign");

            Assert.Equal("Linen Week", campaign.GetProperty("name").GetString());
            Assert.Null(campaign.GetProperty("description").GetString());
            Assert.Equal(20m, campaign.GetProperty("discountValue").GetDecimal());

            // The window is in the future, so this one is Scheduled rather than Live — which is also the
            // status the screen's own Playwright test reads back as "Starts later".
            Assert.Equal("Scheduled", campaign.GetProperty("status").GetString());
            Assert.Equal($"{screenBody["startsAt"]}:00", campaign.GetProperty("startsAt").GetString());
            Assert.Equal($"{screenBody["endsAt"]}:00", campaign.GetProperty("endsAt").GetString());

            var target = campaign.GetProperty("targets").EnumerateArray().Single();

            Assert.Equal(categoryId, target.GetProperty("categoryId").GetInt32());
            Assert.Equal("Women › Shirts & Blouses", target.GetProperty("name").GetString());
        }
        finally
        {
            await DeleteAsync(client, created);
        }
    }

    /// <summary>
    /// A campaign with no target is refused: doc 02 line 119's "discount on categories/products" has to
    /// apply to something, and a campaign that applies to nothing is a sale that never happens.
    /// </summary>
    [Fact]
    public async Task A_campaign_with_no_target_is_refused()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var client = await host.SignInAsync(Permissions.Promotions.Create);

        var created = await PostAsync(client, Body(name: "Empty", discountValue: 20m, targets: []));

        Assert.Equal(HttpStatusCode.BadRequest, created.Status);
        Assert.True(created.Body.GetProperty("errors").TryGetProperty("targets", out _));
    }

    /// <summary>The same target twice is refused rather than silently tidied.</summary>
    [Fact]
    public async Task A_duplicate_target_is_refused()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var client = await host.SignInAsync(Permissions.Promotions.Create);

        var categoryId = await CategoryIdAsync(host, "women", "shirts-blouses");

        var created = await PostAsync(client, Body(
            name: "Twice",
            discountValue: 20m,
            targets: [Category(categoryId), Category(categoryId)]));

        Assert.Equal(HttpStatusCode.BadRequest, created.Status);
        Assert.True(created.Body.GetProperty("errors").TryGetProperty("targets[1]", out _));
    }

    /// <summary>
    /// A target naming a category that is not in the catalogue is a field error rather than a foreign-key
    /// violation surfacing as a 500.
    /// </summary>
    [Fact]
    public async Task A_target_that_does_not_exist_is_a_field_error()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var client = await host.SignInAsync(Permissions.Promotions.Create);

        var created = await PostAsync(client, Body(
            name: "Ghost",
            discountValue: 20m,
            targets: [Category(987_654)]));

        Assert.Equal(HttpStatusCode.BadRequest, created.Status);
        Assert.True(created.Body.GetProperty("errors").TryGetProperty("targets[0]", out _));
    }

    /// <summary>
    /// A target that names two things at once is refused, which is
    /// <c>CK_SaleCampaignTargets_Target</c> restated as a field error.
    /// </summary>
    [Fact]
    public async Task A_target_naming_two_things_is_refused()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var client = await host.SignInAsync(Permissions.Promotions.Create);

        var departmentId = await DepartmentIdAsync(host, "women");
        var categoryId = await CategoryIdAsync(host, "women", "shirts-blouses");

        var created = await PostAsync(client, Body(
            name: "Both",
            discountValue: 20m,
            targets:
            [
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["targetType"] = "Category",
                    ["departmentId"] = departmentId,
                    ["categoryId"] = categoryId,
                    ["productId"] = null,
                    ["includeSubcategories"] = true,
                },
            ]));

        Assert.Equal(HttpStatusCode.BadRequest, created.Status);
        Assert.True(created.Body.GetProperty("errors").TryGetProperty("targets[0]", out _));
    }

    /// <summary>
    /// <b>A body that leaves a field out is refused, naming it.</b> The <c>AdminJsonBody</c> contract:
    /// "left as it is" and "cleared" are different instructions, and a save that dropped
    /// <c>targets</c> or <c>isActive</c> would change what the shop discounts without saying so.
    /// </summary>
    [Theory]
    [InlineData("name")]
    [InlineData("description")]
    [InlineData("discountType")]
    [InlineData("discountValue")]
    [InlineData("startsAt")]
    [InlineData("endsAt")]
    [InlineData("isActive")]
    [InlineData("bannerPublicId")]
    [InlineData("targets")]
    public async Task A_body_missing_a_field_is_refused_naming_it(string field)
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var client = await host.SignInAsync(Permissions.Promotions.Create);

        var categoryId = await CategoryIdAsync(host, "women", "shirts-blouses");

        var body = Body(name: "Incomplete", discountValue: 20m, targets: [Category(categoryId)]);
        body.Remove(field);

        var created = await PostAsync(client, body);

        Assert.Equal(HttpStatusCode.BadRequest, created.Status);
        Assert.True(
            created.Body.GetProperty("errors").TryGetProperty(field, out _),
            $"a body with no '{field}' was not refused by name");
    }

    /// <summary>
    /// <b>A date with an offset or a trailing Z is refused rather than guessed at.</b> The server would
    /// otherwise have to decide whether the caller meant Colombo, and guessing wrong moves a campaign's
    /// window by five and a half hours.
    /// </summary>
    [Theory]
    [InlineData("2026-10-13T00:00:00Z")]
    [InlineData("2026-10-13T00:00:00+05:30")]
    public async Task A_date_with_an_offset_is_refused(string startsAt)
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var client = await host.SignInAsync(Permissions.Promotions.Create);

        var categoryId = await CategoryIdAsync(host, "women", "shirts-blouses");

        var body = Body(name: "Offset", discountValue: 20m, targets: [Category(categoryId)]);
        body["startsAt"] = startsAt;

        var created = await PostAsync(client, body);

        Assert.Equal(HttpStatusCode.BadRequest, created.Status);
        Assert.True(created.Body.GetProperty("errors").TryGetProperty("startsAt", out _));
    }

    /// <summary>Editing or removing a campaign that is not there is a 404, not a 500.</summary>
    [Fact]
    public async Task A_campaign_that_does_not_exist_is_a_404()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var client = await host.SignInAsync(Permissions.Promotions.Edit, Permissions.Promotions.Delete);

        using var edit = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri($"{Url}/987654", UriKind.Relative))
        {
            Content = JsonContent.Create(Body(
                name: "Ghost",
                discountValue: 20m,
                targets: [Category(1)],
                rowVersion: "AAAAAAAAB9E=")),
        };

        using var editResponse = await client.SendAsync(edit, Token);
        Assert.Equal(HttpStatusCode.NotFound, editResponse.StatusCode);

        using var delete = new HttpRequestMessage(
            HttpMethod.Delete,
            new Uri($"{Url}/987654", UriKind.Relative));
        delete.Headers.TryAddWithoutValidation("If-Match", "AAAAAAAAB9E=");

        using var deleteResponse = await client.SendAsync(delete, Token);
        Assert.Equal(HttpStatusCode.NotFound, deleteResponse.StatusCode);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static IEnumerable<(HttpMethod Method, string Path)> Routes() =>
    [
        (HttpMethod.Get, Url),
        (HttpMethod.Post, Url),
        (HttpMethod.Put, $"{Url}/1"),
        (HttpMethod.Delete, $"{Url}/1"),
    ];

    /// <summary>A whole, valid form. Tests remove or replace one key to make the case they are about.</summary>
    private static Dictionary<string, object?> Body(
        string name,
        decimal discountValue,
        IReadOnlyList<object> targets,
        string? rowVersion = null,
        string discountType = "Percent") =>
        new(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["description"] = "Arranged by the integration suite.",
            ["discountType"] = discountType,
            ["discountValue"] = discountValue,

            // From today's Colombo midnight to a week later, so the campaign is Live whenever this runs
            // (fixed dates expired: 2026-10-13 to 2026-10-20 ended that evening). Still whole wall-clock
            // values, so a test can assert the Colombo round-trip against exactly what it sent.
            ["startsAt"] = ShopTime.Midnight(0),
            ["endsAt"] = ShopTime.Midnight(7),
            ["isActive"] = true,
            ["bannerPublicId"] = null,
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

    private static Dictionary<string, object?> Department(int departmentId) =>
        new(StringComparer.Ordinal)
        {
            ["targetType"] = "Department",
            ["departmentId"] = departmentId,
            ["categoryId"] = null,
            ["productId"] = null,
            ["includeSubcategories"] = true,
        };

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        HttpClient client,
        Dictionary<string, object?> body)
    {
        using var response = await client.PostAsJsonAsync(new Uri(Url, UriKind.Relative), body, Token);

        // The body is read as text and written to the test output before it is parsed, because a 500 from
        // this endpoint is a problem document with nothing in it but a trace id — and the only way to see
        // *which* 500 it was, when this suite found two of them in EF translation, was to have the
        // response in the failure report rather than to go looking for it afterwards.
        var text = await response.Content.ReadAsStringAsync(Token);
        TestContext.Current.TestOutputHelper?.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"POST -> {(int)response.StatusCode}: {text}"));

        using var document = JsonDocument.Parse(text);

        return (response.StatusCode, document.RootElement.Clone());
    }

    /// <summary>
    /// Removes the campaign a test created, through the API, so the fixture's database goes back as it
    /// was — including the stored summaries, which the delete's own recompute restores.
    /// </summary>
    /// <param name="reread">
    /// True when the test has edited the campaign since it was created, so the row version in the create's
    /// response is stale and the current one has to be read from the list first.
    /// </param>
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

            rowVersion = list.EnumerateArray()
                .Single(row => row.GetProperty("id").GetInt32() == id)
                .GetProperty("rowVersion")
                .GetString();
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            new Uri(string.Create(CultureInfo.InvariantCulture, $"{Url}/{id}"), UriKind.Relative));

        request.Headers.TryAddWithoutValidation("If-Match", rowVersion);

        using var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<int> DepartmentIdAsync(MediaTestHost host, string slug)
    {
        using var scope = host.Factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Departments
            .AsNoTracking()
            .Where(department => department.Slug == slug)
            .Select(department => department.Id)
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

    private static async Task<int> ProductIdAsync(MediaTestHost host, string slug)
    {
        using var scope = host.Factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Products
            .AsNoTracking()
            .Where(product => product.Slug == slug)
            .Select(product => product.Id)
            .SingleAsync(Token);
    }

    /// <summary>
    /// A new top-level category under Women with no products and no children, for the "reaches nothing"
    /// case. Its <c>Path</c> is written in a second save, because a path contains the row's own id —
    /// which is what <c>CategoryPath.Build</c> and the real write store both do.
    /// </summary>
    private static async Task<int> NewEmptyCategoryAsync(MediaTestHost host)
    {
        using var scope = host.Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var departmentId = await dbContext.Departments
            .Where(department => department.Slug == "women")
            .Select(department => department.Id)
            .SingleAsync(Token);

        var suffix = Guid.NewGuid().ToString("N")[..8];

        var category = new Category
        {
            DepartmentId = departmentId,
            Name = "Suite empty " + suffix,
            Slug = "suite-empty-" + suffix,
            Depth = 1,
            SortOrder = 9_000,

            // Off the mega menu and inactive: it exists for one assertion, and no shopper should be
            // offered a category with nothing in it even for the second this test runs.
            ShowInMegaMenu = false,
            IsActive = false,
        };

        dbContext.Categories.Add(category);
        await dbContext.SaveChangesAsync(Token);

        category.Path = CategoryPath.Build(null, category.Id);
        await dbContext.SaveChangesAsync(Token);

        return category.Id;
    }

    /// <summary>Soft-deletes the arranged category, as doc 08 §10.2 DB5 requires.</summary>
    private static async Task RemoveCategoryAsync(MediaTestHost host, int categoryId)
    {
        using var scope = host.Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var category = await dbContext.Categories.SingleAsync(
            candidate => candidate.Id == categoryId,
            CancellationToken.None);

        category.SoftDelete(DateTime.UtcNow, userId: null);

        await dbContext.SaveChangesAsync(CancellationToken.None);
    }

    private static async Task<decimal> MinSalePriceAsync(MediaTestHost host, int productId)
    {
        using var scope = host.Factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProductStats
            .AsNoTracking()
            .Where(stats => stats.ProductId == productId)
            .Select(stats => stats.MinSalePrice)
            .SingleAsync(Token);
    }
}
