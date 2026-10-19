using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SpencerDenim.Domain.Identity;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The ADM-13 promo-code endpoints, end to end against a real SQL Server and a real API — <b>only the
/// part W3-05c added</b>: doc 11 P3 step 7's "exclude sale items".
/// <para>
/// <b>Why this class exists at all.</b> The promo-code endpoints shipped in W3-05 with no backend test;
/// their coverage was a Playwright suite against a Node stub, and #126 is the standing note that
/// Playwright never starts the .NET API — so no screen test could have observed <c>excludeSaleItems</c>
/// reaching the database. The three tests below are not a retrofit of W3-05's whole surface: they are the
/// round trip, the presence check and the default, for one column.
/// </para>
/// <para>
/// <b>Every test removes the code it created.</b> The fixture's database is shared by the whole assembly,
/// and <c>UX_PromoCodes_Code</c> is filtered on <c>IsDeleted = 0</c>, so a code left behind would refuse
/// the next run of the same test by its own name rather than by anything it asserts.
/// </para>
/// </summary>
public sealed class AdminPromoCodeEndpointTests(SqlServerDatabaseFixture sqlServer)
{
    private const string Url = "/api/admin/marketing/promo-codes";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The round trip doc 11 §7 G-07 is about: the flag goes in as true, is stored, and comes back true
    /// on the save's own answer <b>and</b> on a fresh list read.
    /// </summary>
    /// <remarks>
    /// The list read is the half that matters, and it is separate on purpose: the save's response is built
    /// by <c>PromoCodeWriteHandlers.CommitAsync</c> re-reading through <c>IPromoCodeAdminReader</c>, so
    /// both halves go through <c>PromoCodeAdminStore.Projection</c> — but only the list proves the row
    /// still says so after the request that wrote it has ended.
    /// </remarks>
    [Fact]
    public async Task Exclude_sale_items_is_stored_and_read_back()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var client = await host.SignInAsync(
            Permissions.Promotions.View,
            Permissions.Promotions.Create,
            Permissions.Promotions.Delete);

        var code = UniqueCode();
        var created = await PostAsync(client, Body(code, excludeSaleItems: true));

        try
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);
            Assert.True(
                created.Body.GetProperty("promoCode").GetProperty("excludeSaleItems").GetBoolean());

            var listed = await ListAsync(client, code);

            Assert.True(listed.GetProperty("excludeSaleItems").GetBoolean());
        }
        finally
        {
            await DeleteAsync(client, created);
        }
    }

    /// <summary>
    /// False is stored as false rather than falling back to doc 08 §3.6's column default.
    /// </summary>
    /// <remarks>
    /// Paired with the test above because a single assertion on <c>true</c> would also pass if the save
    /// ignored the body and the column defaulted to the asserted value — which is the shape of the
    /// thirteen tests this project has shipped that could not fail. The column defaults to 0, so the
    /// <c>true</c> case carries the load and this one pins the other direction of a save that does read
    /// the body.
    /// </remarks>
    [Fact]
    public async Task A_code_saved_without_the_exclusion_keeps_sale_items_eligible()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var client = await host.SignInAsync(
            Permissions.Promotions.View,
            Permissions.Promotions.Create,
            Permissions.Promotions.Delete);

        var code = UniqueCode();
        var created = await PostAsync(client, Body(code, excludeSaleItems: false));

        try
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);

            var listed = await ListAsync(client, code);

            Assert.False(listed.GetProperty("excludeSaleItems").GetBoolean());
        }
        finally
        {
            await DeleteAsync(client, created);
        }
    }

    /// <summary>
    /// <b>The edit path, which answered 500 on <c>main</c> for the same reason the list did and which
    /// nothing exercised.</b> The flag is turned off on an existing code and read back.
    /// </summary>
    /// <remarks>
    /// Added in the W3-05c tester pass. The three tests this class shipped with all POST, and
    /// <c>PromoCodeWriteHandlers.CommitAsync</c> re-reads through <c>PromoCodeAdminReader.FindAsync</c> on
    /// a <b>PUT</b> as well — so the create proves the shared reader and nothing proved the route. Doc 11
    /// P3 step 7 puts "exclude sale items" on an edit as much as on a create, and an edit that answered
    /// 500 after writing the row is the worse of the two: the change lands and the screen says it failed,
    /// so a staff member tries again.
    /// <para>
    /// The row version travels in <c>If-Match</c>, which is where the screen sends it. The reload asserts
    /// the <b>stored</b> row rather than only the PUT's own answer, because both halves of the save come
    /// back through the same reader and only a fresh request shows the row survived it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Editing_a_code_can_turn_the_exclusion_off_again()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var client = await host.SignInAsync(
            Permissions.Promotions.View,
            Permissions.Promotions.Create,
            Permissions.Promotions.Edit,
            Permissions.Promotions.Delete);

        var code = UniqueCode();
        var created = await PostAsync(client, Body(code, excludeSaleItems: true));

        try
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);

            var id = created.Body.GetProperty("promoCode").GetProperty("id").GetInt32();
            var rowVersion = created.Body.GetProperty("promoCode").GetProperty("rowVersion").GetString();

            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                new Uri(string.Create(CultureInfo.InvariantCulture, $"{Url}/{id}"), UriKind.Relative))
            {
                Content = JsonContent.Create(Body(code, excludeSaleItems: false)),
            };

            request.Headers.TryAddWithoutValidation("If-Match", rowVersion);

            using var response = await client.SendAsync(request, Token);

            var text = await response.Content.ReadAsStringAsync(Token);
            TestContext.Current.TestOutputHelper?.WriteLine(
                string.Create(CultureInfo.InvariantCulture, $"PUT -> {(int)response.StatusCode}: {text}"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var document = JsonDocument.Parse(text);

            Assert.False(
                document.RootElement
                    .GetProperty("promoCode")
                    .GetProperty("excludeSaleItems")
                    .GetBoolean());

            var listed = await ListAsync(client, code);

            Assert.False(listed.GetProperty("excludeSaleItems").GetBoolean());
        }
        finally
        {
            await DeleteAsync(client, created, reread: true);
        }
    }

    /// <summary>
    /// A body that leaves <c>excludeSaleItems</c> out is refused, naming the field.
    /// </summary>
    /// <remarks>
    /// The rule <c>AdminJsonBody</c> exists for, and the reason it is worth a test on this one field: an
    /// edit screen that forgot to send the flag would turn every code it touched back into one that
    /// discounts sale items, silently and in the shop's favour against itself. W1-12 cleared every
    /// category image exactly this way.
    /// </remarks>
    [Fact]
    public async Task A_save_that_omits_the_exclusion_is_refused_and_names_it()
    {
        sqlServer.SkipIfUnavailable();

        await using var host = MediaTestHost.Create(sqlServer.ConnectionString);
        using var client = await host.SignInAsync(
            Permissions.Promotions.View,
            Permissions.Promotions.Create);

        var body = Body(UniqueCode(), excludeSaleItems: true);
        body.Remove("excludeSaleItems");

        var refused = await PostAsync(client, body);

        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.True(
            refused.Body.GetProperty("errors").TryGetProperty("excludeSaleItems", out _),
            "the refusal did not name excludeSaleItems");
    }

    /// <summary>A whole, valid form. Tests remove or replace one key to make the case they are about.</summary>
    private static Dictionary<string, object?> Body(string code, bool excludeSaleItems) =>
        new(StringComparer.Ordinal)
        {
            ["code"] = code,
            ["description"] = "Arranged by the integration suite.",
            ["discountType"] = "Percent",
            ["discountValue"] = 10m,
            ["maxDiscountAmount"] = null,
            ["minSpendAmount"] = null,

            // The window is never asserted, only sent, but it is kept current — today's Colombo midnight
            // to a week later — so every code these tests create is Live rather than quietly Ended (a
            // fixed 2026-10-13 to 2026-10-20 window ended that evening and only drew a warning).
            ["startsAt"] = ShopTime.Midnight(0),
            ["endsAt"] = ShopTime.Midnight(7),
            ["excludeSaleItems"] = excludeSaleItems,
            ["totalUsageLimit"] = null,
            ["perCustomerLimit"] = null,
            ["isActive"] = true,
            ["rowVersion"] = null,
        };

    /// <summary>
    /// A code no other run can collide with. <c>UX_PromoCodes_Code</c> is filtered on
    /// <c>IsDeleted = 0</c>, so a failed teardown would otherwise make the next run fail on the name.
    /// </summary>
    private static string UniqueCode() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"W305C{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8].ToUpperInvariant()}");

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        HttpClient client,
        Dictionary<string, object?> body)
    {
        using var response = await client.PostAsJsonAsync(new Uri(Url, UriKind.Relative), body, Token);

        var text = await response.Content.ReadAsStringAsync(Token);
        TestContext.Current.TestOutputHelper?.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"POST -> {(int)response.StatusCode}: {text}"));

        using var document = JsonDocument.Parse(text);

        return (response.StatusCode, document.RootElement.Clone());
    }

    private static async Task<JsonElement> ListAsync(HttpClient client, string code)
    {
        using var response = await client.GetAsync(new Uri(Url, UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var list = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        return list.EnumerateArray()
            .Single(row => string.Equals(
                row.GetProperty("code").GetString(),
                code,
                StringComparison.Ordinal));
    }

    /// <param name="reread">
    /// True when the test has edited the code since it was created, so the row version in the create's
    /// response is stale and the current one has to be read from the list first — the same parameter
    /// <c>AdminSaleCampaignEndpointTests.DeleteAsync</c> takes, for the same reason. A teardown that is
    /// refused with a 412 is not a teardown: the code would stay behind and <c>UX_PromoCodes_Code</c>
    /// would refuse the next run of this test by its own name.
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

        var promoCode = created.Body.GetProperty("promoCode");
        var id = promoCode.GetProperty("id").GetInt32();
        var rowVersion = promoCode.GetProperty("rowVersion").GetString();

        if (reread)
        {
            rowVersion = (await ListAsync(client, promoCode.GetProperty("code").GetString()!))
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
}
