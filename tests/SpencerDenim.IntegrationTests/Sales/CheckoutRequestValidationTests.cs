using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// What the checkout endpoint does with a request it cannot act on (CHK-01, doc 11 P6 step 8).
/// <para>
/// <b>Issue #47 is the reason this file leads with body shapes.</b> A well-formed JSON object that simply
/// omits a required <b>string</b> property binds <see langword="null"/> into a non-nullable reference
/// property — the annotation is not a runtime check — and the public API answered <b>500</b> for that
/// until a tester found it. A checkout body is almost entirely strings, so <c>{}</c> and
/// <c>{"email":"a@b.c"}</c> are the shapes most likely to arrive from a broken client, a scanner or a
/// half-filled form, and every one of them must be a 400 that names the fields.
/// </para>
/// <para>
/// The bodies are sent as <b>raw bytes</b> through <c>PlaceRawAsync</c>, which is the only way to express
/// them: a typed request object cannot omit a property, so a typed helper could never send the shape the
/// bug needs.
/// </para>
/// </summary>
public sealed class CheckoutRequestValidationTests(SqlServerDatabaseFixture sqlServer)
    : CheckoutTestBase(sqlServer)
{
    /// <summary>
    /// Issue #47's shapes, plus the neighbours worth sending for the same reason.
    /// <list type="bullet">
    /// <item><description><c>{}</c> — every string absent.</description></item>
    /// <item><description><c>{"email":"a@b.c"}</c> — one string present, the rest absent.</description></item>
    /// <item><description>
    /// Explicit <c>null</c>s — the same end state by a different route, because a client that sends
    /// <c>null</c> deliberately exercises the binder differently from one that omits the key.
    /// </description></item>
    /// <item><description>
    /// <c>null</c> as the whole body, and a JSON array where an object is expected: both arrive as a null
    /// or unbindable model, which is the other half of the same dereference.
    /// </description></item>
    /// </list>
    /// </summary>
    public static TheoryData<string> UnbindableBodies() =>
    [
        "{}",
        """{"email":"a@b.c"}""",
        """{"email":null,"firstName":null,"phone":null,"paymentMethodCode":null}""",
        """{"address":{}}""",
        """{"address":null}""",
        """{"address":{"line1":null,"city":null}}""",
        "null",
    ];

    /// <summary>
    /// **#47, as a request.** Every one of these is a well-formed JSON body that omits required strings,
    /// and every one must answer 400 with field errors — never 500.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnbindableBodies))]
    public async Task A_body_missing_its_required_strings_is_a_400_and_never_a_500(string body)
    {
        SqlServer.SkipIfUnavailable();

        var variantId = await CheckoutVariantIdAsync();
        var (_, cartId, api) = await NewBagAsync(variantId);

        var ordersBefore = await OrderCountAsync();
        var reservedBefore = await ReservedAsync(variantId);

        var response = await api.PlaceRawAsync(body, "application/json", Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var problem = await CheckoutApiClient.ProblemAsync(response, Token);
        var errors = problem.RootElement.GetProperty("errors");

        // Named fields, not a bare "invalid": the shopper has to be told which boxes to fill in.
        Assert.True(errors.EnumerateObject().Any(), "A 400 with no field errors tells the shopper nothing.");
        Assert.Contains(
            "firstName",
            errors.EnumerateObject().Select(property => property.Name),
            StringComparer.Ordinal);

        // Nothing was written and nothing was reserved.
        await AssertNothingPlacedAsync(cartId, variantId, ordersBefore, reservedBefore);
    }

    /// <summary>
    /// The same shapes with no bag at all. The order of the checks matters: a request naming nothing valid
    /// must not need a bag to be refused, and must not create one.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnbindableBodies))]
    public async Task A_body_missing_its_required_strings_is_refused_without_a_bag_too(string body)
    {
        SqlServer.SkipIfUnavailable();

        var api = NewCheckoutVisitor();

        var response = await api.PlaceRawAsync(body, "application/json", Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A body that is not JSON at all, and a body with no content type. Both are the framework's to refuse
    /// — the assertion is that neither becomes a 500.
    /// </summary>
    [Theory]
    [InlineData("not json at all", "application/json")]
    [InlineData("{", "application/json")]
    [InlineData("""{"checkoutKey":"not-a-guid"}""", "application/json")]
    [InlineData("""{"deliveryMethodId":"one"}""", "application/json")]
    [InlineData("""{"expectedGrandTotal":"lots"}""", "application/json")]
    [InlineData("{}", "text/plain")]
    public async Task An_unparseable_body_is_a_4xx_and_never_a_500(string body, string contentType)
    {
        SqlServer.SkipIfUnavailable();

        var api = NewCheckoutVisitor();

        var response = await api.PlaceRawAsync(body, contentType, Token);

        Assert.InRange((int)response.StatusCode, 400, 499);
    }

    [Fact]
    public async Task A_request_with_no_body_at_all_is_a_4xx_and_never_a_500()
    {
        SqlServer.SkipIfUnavailable();

        var api = NewCheckoutVisitor();

        var response = await api.PlaceRawAsync(null, null, Token);

        Assert.InRange((int)response.StatusCode, 400, 499);
    }

    /// <summary>
    /// The four columns <c>CK_Orders_ShipAddress</c> requires for a courier order. A missing one must be a
    /// 400 naming it, not a constraint violation inside the placement transaction.
    /// </summary>
    [Theory]
    [InlineData("recipientName", "address.recipientName")]
    [InlineData("phone", "address.phone")]
    [InlineData("line1", "address.line1")]
    [InlineData("city", "address.city")]
    public async Task A_missing_address_field_is_reported_against_its_own_name(
        string field,
        string expectedKey)
    {
        var variantId = await CheckoutVariantIdAsync();
        var (_, cartId, api) = await NewBagAsync(variantId);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (_, session) = await api.GetAsync(colombo, standard, Token);
        var body = ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session));

        var address = (Dictionary<string, object?>)body["address"]!;
        address[field] = null;

        var ordersBefore = await OrderCountAsync();
        var reservedBefore = await ReservedAsync(variantId);
        var (response, _) = await api.PlaceAsync(body, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var problem = await CheckoutApiClient.ProblemAsync(response, Token);
        Assert.True(
            problem.RootElement.GetProperty("errors").TryGetProperty(expectedKey, out _),
            $"No error was reported against {expectedKey}.");

        await AssertNothingPlacedAsync(cartId, variantId, ordersBefore, reservedBefore);
    }

    /// <summary>
    /// Whitespace is the same as missing. Three shapes that arrive as different values and are the same
    /// mistake, and a field that accepted "   " would write a blank recipient name on to a parcel label.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task Whitespace_in_a_required_field_is_refused(string value)
    {
        var variantId = await CheckoutVariantIdAsync();
        var (_, cartId, api) = await NewBagAsync(variantId);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (_, session) = await api.GetAsync(colombo, standard, Token);
        var body = ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session));
        body["firstName"] = value;

        var ordersBefore = await OrderCountAsync();
        var reservedBefore = await ReservedAsync(variantId);
        var (response, _) = await api.PlaceAsync(body, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingPlacedAsync(cartId, variantId, ordersBefore, reservedBefore);
    }

    [Theory]
    [InlineData("nimali")]
    [InlineData("0771234567")]
    [InlineData("nimali@example")]
    [InlineData("nimali perera@example.com")]
    public async Task An_email_that_is_not_an_address_is_refused(string email)
    {
        var variantId = await CheckoutVariantIdAsync();
        var (_, cartId, api) = await NewBagAsync(variantId);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (_, session) = await api.GetAsync(colombo, standard, Token);
        var body = ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session));
        body["email"] = email;

        var ordersBefore = await OrderCountAsync();
        var reservedBefore = await ReservedAsync(variantId);
        var (response, _) = await api.PlaceAsync(body, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingPlacedAsync(cartId, variantId, ordersBefore, reservedBefore);
    }

    /// <summary>
    /// A value one character longer than its column. The boundary is where an off-by-one lives, and a
    /// value that slipped through here would arrive as a SQL truncation error mid-transaction.
    /// </summary>
    [Theory]
    [InlineData("firstName", 80)]
    [InlineData("lastName", 80)]
    [InlineData("phone", 20)]
    [InlineData("customerNote", 500)]
    public async Task A_value_one_character_over_its_column_is_refused(string field, int maxLength)
    {
        var variantId = await CheckoutVariantIdAsync();
        var (_, cartId, api) = await NewBagAsync(variantId);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (_, session) = await api.GetAsync(colombo, standard, Token);
        var body = ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session));
        body[field] = new string('x', maxLength + 1);

        var ordersBefore = await OrderCountAsync();
        var reservedBefore = await ReservedAsync(variantId);
        var (response, _) = await api.PlaceAsync(body, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingPlacedAsync(cartId, variantId, ordersBefore, reservedBefore);
    }

    [Fact]
    public async Task An_empty_checkout_key_is_refused()
    {
        var variantId = await CheckoutVariantIdAsync();
        var (_, cartId, api) = await NewBagAsync(variantId);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (_, session) = await api.GetAsync(colombo, standard, Token);
        var body = ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session));
        body["checkoutKey"] = Guid.Empty;

        var ordersBefore = await OrderCountAsync();
        var reservedBefore = await ReservedAsync(variantId);
        var (response, _) = await api.PlaceAsync(body, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingPlacedAsync(cartId, variantId, ordersBefore, reservedBefore);
    }

    /// <summary>
    /// Doc 11 P6: "delivery method switched off or zone rate removed after the customer chose it → this
    /// delivery option is no longer available, choose another." An id that is not an enabled method is
    /// refused rather than honoured.
    /// </summary>
    [Fact]
    public async Task A_delivery_method_that_is_not_enabled_is_refused()
    {
        var variantId = await CheckoutVariantIdAsync();
        var (_, cartId, api) = await NewBagAsync(variantId);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);
        var pickup = await DeliveryMethodIdAsync(DeliveryMethod.PickupCode);

        var (_, session) = await api.GetAsync(colombo, standard, Token);
        var body = ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session));

        // PICKUP is seeded disabled (doc 08 §9), so its id exists and must still be refused.
        body["deliveryMethodId"] = pickup;

        var ordersBefore = await OrderCountAsync();
        var reservedBefore = await ReservedAsync(variantId);
        var (response, _) = await api.PlaceAsync(body, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var problem = await CheckoutApiClient.ProblemAsync(response, Token);
        var message = problem.RootElement
            .GetProperty("errors")
            .GetProperty("deliveryMethodId")[0]
            .GetString();

        Assert.Contains("no longer available", message!, StringComparison.OrdinalIgnoreCase);
        await AssertNothingPlacedAsync(cartId, variantId, ordersBefore, reservedBefore);
    }

    [Fact]
    public async Task A_delivery_method_with_no_rate_for_the_district_is_refused()
    {
        var variantId = await CheckoutVariantIdAsync();
        var (_, cartId, api) = await NewBagAsync(variantId);
        var jaffna = await DistrictIdAsync("Jaffna");
        var express = await DeliveryMethodIdAsync(DeliveryMethod.ExpressCode);
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        // Quote with Standard, which is available island-wide, then submit Express, which is not.
        var (_, session) = await api.GetAsync(jaffna, standard, Token);
        var body = ValidBody(session!.CheckoutKey, express, jaffna, QuotedTotal(session));

        var ordersBefore = await OrderCountAsync();
        var reservedBefore = await ReservedAsync(variantId);
        var (response, _) = await api.PlaceAsync(body, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var problem = await CheckoutApiClient.ProblemAsync(response, Token);
        var message = problem.RootElement
            .GetProperty("errors")
            .GetProperty("deliveryMethodId")[0]
            .GetString();

        Assert.Contains("not available for that district", message!, StringComparison.OrdinalIgnoreCase);
        await AssertNothingPlacedAsync(cartId, variantId, ordersBefore, reservedBefore);
    }

    /// <summary>
    /// An unknown district id and a deactivated one must answer the same way, for the reason
    /// <c>ICartReader.FindSellableVariantAsync</c> gives: a public endpoint must not let a caller map the
    /// shop's reference data by probing ids.
    /// </summary>
    [Fact]
    public async Task An_unknown_district_is_refused()
    {
        var variantId = await CheckoutVariantIdAsync();
        var (_, cartId, api) = await NewBagAsync(variantId);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (_, session) = await api.GetAsync(colombo, standard, Token);
        var body = ValidBody(session!.CheckoutKey, standard, 9_999, QuotedTotal(session));

        var ordersBefore = await OrderCountAsync();
        var reservedBefore = await ReservedAsync(variantId);
        var (response, _) = await api.PlaceAsync(body, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingPlacedAsync(cartId, variantId, ordersBefore, reservedBefore);
    }

    /// <summary>
    /// Doc 11 P6 precondition 2: the provider has to be one the shop has switched on. PayPal is seeded
    /// <b>disabled</b> (doc 08 §9), and must be refused even though the row exists.
    /// </summary>
    [Fact]
    public async Task A_payment_provider_that_is_not_enabled_is_refused()
    {
        var variantId = await CheckoutVariantIdAsync();
        var (_, cartId, api) = await NewBagAsync(variantId);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (_, session) = await api.GetAsync(colombo, standard, Token);
        var body = ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session));
        body["paymentMethodCode"] = PaymentProviderCodes.PayPal;

        var ordersBefore = await OrderCountAsync();
        var reservedBefore = await ReservedAsync(variantId);
        var (response, _) = await api.PlaceAsync(body, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingPlacedAsync(cartId, variantId, ordersBefore, reservedBefore);
    }

    [Fact]
    public async Task A_payment_provider_that_does_not_exist_is_refused()
    {
        var variantId = await CheckoutVariantIdAsync();
        var (_, cartId, api) = await NewBagAsync(variantId);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (_, session) = await api.GetAsync(colombo, standard, Token);
        var body = ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session));
        body["paymentMethodCode"] = "CashOnDelivery";

        var ordersBefore = await OrderCountAsync();
        var reservedBefore = await ReservedAsync(variantId);
        var (response, _) = await api.PlaceAsync(body, Token);

        // Doc 02 §I removes cash on delivery from the scope and issue #55 holds it as a change request.
        // An order must not be placeable for a provider the shop does not have.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingPlacedAsync(cartId, variantId, ordersBefore, reservedBefore);
    }

    [Fact]
    public async Task Placing_an_order_with_no_bag_is_refused_as_an_empty_bag()
    {
        var api = NewCheckoutVisitor();
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var body = ValidBody(Guid.NewGuid(), standard, colombo, 1_000m);

        // No bag and so no variant to watch: the only thing this one owes is that no order appeared.
        var ordersBefore = await OrderCountAsync();
        var (response, _) = await api.PlaceAsync(body, Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ordersBefore, await OrderCountAsync());

        using var problem = await CheckoutApiClient.ProblemAsync(response, Token);
        Assert.Contains(
            nameof(Application.Sales.Checkout.Contracts.CheckoutBlocker.EmptyBag),
            problem.RootElement.GetProperty("blockers").EnumerateArray().Select(value => value.GetString()));
    }

    /// <summary>
    /// A district or method query value that is not a number. The GET must come back with an unquoted
    /// checkout rather than a 500 — a scanner hitting <c>?districtId=abc</c> is an ordinary event.
    /// </summary>
    [Theory]
    [InlineData("?districtId=abc")]
    [InlineData("?districtId=")]
    [InlineData("?districtId=-1")]
    [InlineData("?districtId=99999999999999999999")]
    [InlineData("?deliveryMethodId=abc")]
    [InlineData("?districtId=1&deliveryMethodId=abc")]
    public async Task A_nonsense_query_value_does_not_break_the_checkout_read(string query)
    {
        SqlServer.SkipIfUnavailable();

        var variantId = await CheckoutVariantIdAsync();
        var (_, _, api) = await NewBagAsync(variantId);

        var response = await api.Http.GetAsync(
            new Uri($"{CheckoutApiClient.Path}{query}", UriKind.Relative),
            Token);

        Assert.InRange((int)response.StatusCode, 200, 499);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// A public token that is not a GUID must be a 404 from the route constraint, not a 500.
    /// </summary>
    /// <remarks>
    /// <b><c>%00</c> was in this list and has been taken out, because this client cannot send it.</b>
    /// <c>TestServer</c>'s handler decodes the path itself and throws <i>"the path contains null
    /// characters"</i> before anything reaches the server — so the case was failing on the test client,
    /// not on the API, and leaving it in would have been a red test about nothing. The project has been
    /// here before: W2-10's <c>//robots.txt</c> fix survived a whole round because Playwright normalised
    /// the path client-side, and the lesson recorded then was to check the client can express the thing
    /// being tested. A percent-encoded NUL in a path needs a client that puts the bytes on the wire
    /// verbatim (Node's <c>http.request({ path })</c>); it is <b>owed</b>, and it is in the task report
    /// rather than pretended at here.
    /// </remarks>
    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("1")]
    [InlineData("00000000-0000-0000-0000-00000000000")]
    [InlineData("ZZZZZZZZ-0000-0000-0000-000000000000")]
    public async Task An_order_token_that_is_not_a_guid_is_a_404(string token)
    {
        SqlServer.SkipIfUnavailable();

        var api = NewCheckoutVisitor();

        var response = await api.Http.GetAsync(
            new Uri($"{CheckoutApiClient.OrderPath}/{token}", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Nothing was written, nothing was reserved, the bag is intact: the three facts a refused checkout
    /// owes.
    /// </summary>
    /// <param name="ordersBefore">
    /// The order count taken immediately before the request. **Counted rather than filtered by time**: the
    /// suite shares one database and orders are never deleted, so "nothing placed in the last minute"
    /// counts the previous test's order. That is exactly how this helper first failed — twenty-eight tests
    /// red on an assertion that was wrong while the code was right.
    /// </param>
    /// <param name="reservedBefore">
    /// The variant's <c>Reserved</c> immediately before the request, for the same reason. The first
    /// version asserted <b>zero</b>, which is true only of a database no checkout has ever run against:
    /// an order from an earlier test in the same run is still holding its stock on the very same variant,
    /// and thirteen tests went red in a full run while passing on their own. What a refused checkout owes
    /// is that it changed nothing, not that nothing exists.
    /// </param>
    private async Task AssertNothingPlacedAsync(
        int cartId,
        int variantId,
        int ordersBefore,
        int reservedBefore)
    {
        Assert.Equal(ordersBefore, await OrderCountAsync());
        Assert.Equal(reservedBefore, await ReservedAsync(variantId));
        Assert.True(await CartItemCountAsync(cartId) > 0, "The bag was emptied by a refused checkout.");
    }
}
