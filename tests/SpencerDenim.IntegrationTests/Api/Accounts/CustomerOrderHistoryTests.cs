using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SpencerDenim.Domain.Sales;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.IntegrationTests.Sales;

namespace SpencerDenim.IntegrationTests.Api.Accounts;

/// <summary>
/// My account's order history and <b>its tracking</b> (ACC-02, doc 11 P11 step 12) — the gap W3-01 exists
/// to close.
/// </summary>
/// <remarks>
/// <b>Before W3-01 a shopper who placed an order had no way to look it up again.</b> The tracking number
/// was written into <c>sales.Orders</c> by doc 11 P9's dispatch and read only by the admin screens and
/// the <c>OrderShipped</c> email; nothing on the storefront showed it. These tests are the acceptance
/// test for the fix.
/// <para>
/// <b>There is no Playwright stub anywhere near this.</b> #126's lesson was a screen whose only coverage
/// was a browser test against a Node stub, while the real API answered 500 for a day and a half — the
/// cause being a positional-record projection with the ordering applied after the <c>Select</c>, which is
/// exactly the shape <c>CustomerOrderReader</c> has. So the route is called over HTTP against the real
/// database, with real order rows.
/// </para>
/// </remarks>
public sealed class CustomerOrderHistoryTests(SqlServerDatabaseFixture sqlServer)
    : CustomerAccountTestBase(sqlServer)
{
    private const string OrdersUrl = "/api/account/orders";

    /// <summary>
    /// A shipped order shows its courier, its tracking number and a link built from that courier's
    /// current template.
    /// </summary>
    /// <remarks>
    /// <b>This is the acceptance test of the whole task.</b> The customer registers, confirms, signs in,
    /// and reads their own order with its tracking number — and the number asserted here is the one a
    /// staff member typed into the dispatch, read back through the customer's own route.
    /// </remarks>
    [Fact]
    public async Task A_shipped_order_shows_its_courier_its_number_and_its_link()
    {
        await ReadyAsync();

        var reference = await ReferenceAsync();
        var email = UniqueEmail("history-shipped");

        var courierId = await Writer.InsertAsync(
            "sales.Couriers",
            SalesRowWriter.With(
                SalesRowWriter.CourierRow(),
                "TrackingUrlTemplate",
                "https://courier.test/track?id={trackingNumber}"),
            Token);

        const string trackingNumber = "CT-9981-AB";

        var orderId = await InsertOrderAsync(
            reference,
            email,
            status: OrderStatus.Shipped,
            courierId: courierId,
            courierName: "Test Courier",
            trackingNumber: trackingNumber);

        _ = orderId;

        await using var factory = CreateHost();
        using var client = CreateClient(factory);

        await RegisterAsync(client, email);

        var confirmToken = await ReadLinkTokenAsync(factory, Templates.Confirmation, email);
        using var confirmed = await client.PostAsJsonAsync("/api/account/confirm-email", new { token = confirmToken }, Token);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        using var response = await client.GetAsync(new Uri(OrdersUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());

        var order = body.GetProperty("orders")[0];

        Assert.Equal("Shipped", order.GetProperty("status").GetString());
        Assert.Equal("Shipped", order.GetProperty("statusLabel").GetString());

        // Three quantities of one line, so the row reads as "3 items" rather than "1 line".
        Assert.Equal(3, order.GetProperty("itemCount").GetInt32());

        var tracking = order.GetProperty("tracking");
        Assert.Equal("Test Courier", tracking.GetProperty("courierName").GetString());
        Assert.Equal(trackingNumber, tracking.GetProperty("trackingNumber").GetString());
        Assert.Equal(
            $"https://courier.test/track?id={Uri.EscapeDataString(trackingNumber)}",
            tracking.GetProperty("trackingUrl").GetString());

        // And the token is the address of the order page the shopper can open.
        Assert.True(Guid.TryParse(order.GetProperty("token").GetString(), out var token));
        Assert.NotEqual(Guid.Empty, token);

        // The same order, read through the guest route by its own token, says the same thing about where
        // it is — doc 11 P11 step 14's "view status, tracking", which that payload did not carry before
        // W3-01 either.
        using var guestView = await client.GetAsync(new Uri($"/api/orders/{token:D}", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, guestView.StatusCode);

        var guestBody = await guestView.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(
            trackingNumber,
            guestBody.GetProperty("tracking").GetProperty("trackingNumber").GetString());
    }

    /// <summary>
    /// A courier with no online tracking leaves the number as text, which the shopper can quote.
    /// </summary>
    /// <remarks>
    /// <b>Doc 08 §3.5 makes <c>TrackingUrlTemplate</c> nullable for this case.</b> A link built from a
    /// null template would be the string "null" in an <c>href</c>, and a <i>missing</i> tracking block
    /// would hide a number the shopper can ring the courier with.
    /// </remarks>
    [Fact]
    public async Task A_courier_with_no_online_tracking_still_shows_the_number()
    {
        await ReadyAsync();

        var reference = await ReferenceAsync();
        var email = UniqueEmail("history-no-template");

        var courierId = await Writer.InsertAsync(
            "sales.Couriers",
            SalesRowWriter.With(SalesRowWriter.CourierRow(), "TrackingUrlTemplate", null),
            Token);

        await InsertOrderAsync(
            reference,
            email,
            status: OrderStatus.Shipped,
            courierId: courierId,
            courierName: "Hand Delivery",
            trackingNumber: "NOTE-1");

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        await ConfirmedCustomerAsync(factory, client, email);

        var order = await FirstOrderAsync(client);
        var tracking = order.GetProperty("tracking");

        Assert.Equal("NOTE-1", tracking.GetProperty("trackingNumber").GetString());
        Assert.Equal(JsonValueKind.Null, tracking.GetProperty("trackingUrl").ValueKind);
    }

    /// <summary>
    /// An order that has not shipped carries <b>no tracking block at all</b>, rather than a block of
    /// nulls.
    /// </summary>
    /// <remarks>
    /// <b>A block of nulls would make every consumer write the same three-way check</b>, and the first
    /// one to forget would render "Tracking: null". The absence is the contract.
    /// </remarks>
    [Fact]
    public async Task An_unshipped_order_has_no_tracking_block()
    {
        await ReadyAsync();

        var reference = await ReferenceAsync();
        var email = UniqueEmail("history-unshipped");

        await InsertOrderAsync(reference, email, status: OrderStatus.Paid);

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        await ConfirmedCustomerAsync(factory, client, email);

        var order = await FirstOrderAsync(client);

        Assert.Equal("Paid", order.GetProperty("statusLabel").GetString());
        Assert.Equal(JsonValueKind.Null, order.GetProperty("tracking").ValueKind);
    }

    /// <summary>
    /// Doc 11 P9's pickup variant: <c>Shipped</c> reads as "Ready for collection", and there is no
    /// courier to track.
    /// </summary>
    /// <remarks>
    /// <b>Rendered by the one function the admin screens use</b> (<c>OrderStatusLabels.For</c>), so the
    /// admin list, the admin detail, the guest order page and this list cannot disagree. The guest order
    /// page shipped a defect of exactly this kind — it printed <c>status.toLowerCase()</c> and a pickup
    /// customer read "This order is shipped."
    /// </remarks>
    [Fact]
    public async Task A_pickup_order_reads_as_ready_for_collection_and_has_nothing_to_track()
    {
        await ReadyAsync();

        var reference = await ReferenceAsync();
        var email = UniqueEmail("history-pickup");

        await InsertOrderAsync(
            reference,
            email,
            status: OrderStatus.Shipped,
            deliveryMethodId: reference.PickupDeliveryMethodId,
            deliveryMethodCode: DeliveryMethod.PickupCode,
            pickupLocationId: reference.PickupLocationId);

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        await ConfirmedCustomerAsync(factory, client, email);

        var order = await FirstOrderAsync(client);

        Assert.Equal("Shipped", order.GetProperty("status").GetString());
        Assert.Equal("Ready for collection", order.GetProperty("statusLabel").GetString());
        Assert.Equal(JsonValueKind.Null, order.GetProperty("tracking").ValueKind);
    }

    /// <summary>
    /// One customer's history holds only their own orders.
    /// </summary>
    /// <remarks>
    /// <b>The decoy is another customer's <i>linked</i> order, not an unlinked guest order.</b> An
    /// unlinked order is excluded by the <c>CustomerUserId</c> filter being non-null at all; what this
    /// guards against is the filter comparing the wrong id, which only a second real customer can show.
    /// </remarks>
    [Fact]
    public async Task A_customer_sees_only_their_own_orders()
    {
        await ReadyAsync();

        var reference = await ReferenceAsync();
        var mine = UniqueEmail("history-mine");
        var theirs = UniqueEmail("history-theirs");

        await InsertOrderAsync(reference, mine);
        await InsertOrderAsync(reference, theirs);

        await using var factory = CreateHost();

        using var mineClient = CreateClient(factory);
        await ConfirmedCustomerAsync(factory, mineClient, mine);

        using var theirsClient = CreateClient(factory);
        await ConfirmedCustomerAsync(factory, theirsClient, theirs);

        using var mineResponse = await mineClient.GetAsync(new Uri(OrdersUrl, UriKind.Relative), Token);
        var mineBody = await mineResponse.Content.ReadFromJsonAsync<JsonElement>(Token);

        using var theirsResponse = await theirsClient.GetAsync(new Uri(OrdersUrl, UriKind.Relative), Token);
        var theirsBody = await theirsResponse.Content.ReadFromJsonAsync<JsonElement>(Token);

        Assert.Equal(1, mineBody.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, theirsBody.GetProperty("totalCount").GetInt32());

        var mineNumber = mineBody.GetProperty("orders")[0].GetProperty("orderNumber").GetString();
        var theirsNumber = theirsBody.GetProperty("orders")[0].GetProperty("orderNumber").GetString();

        Assert.NotEqual(mineNumber, theirsNumber);
    }

    /// <summary>
    /// Newest first (doc 11 P11 step 12), and paged without repeating or hiding a row.
    /// </summary>
    /// <remarks>
    /// <b>The tie-break on the key is what makes paging safe.</b> Three orders placed in the same
    /// millisecond — which a test does, and a real shop does under load — would have no stable order
    /// between two queries, so a row could appear on both pages or on neither.
    /// </remarks>
    [Fact]
    public async Task The_list_is_newest_first_and_pages_without_losing_a_row()
    {
        await ReadyAsync();

        var reference = await ReferenceAsync();
        var email = UniqueEmail("history-paging");

        var placed = DateTime.UtcNow.AddDays(-5);

        for (var index = 0; index < 5; index++)
        {
            await InsertOrderAsync(reference, email, placedAtUtc: placed.AddDays(index));
        }

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        await ConfirmedCustomerAsync(factory, client, email);

        using var firstPage = await client.GetAsync(new Uri($"{OrdersUrl}?page=1&pageSize=2", UriKind.Relative), Token);
        var first = await firstPage.Content.ReadFromJsonAsync<JsonElement>(Token);

        Assert.Equal(5, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, first.GetProperty("orders").GetArrayLength());
        Assert.True(first.GetProperty("hasMore").GetBoolean());

        var seen = new List<string>();
        var dates = new List<DateTime>();

        for (var page = 1; page <= 3; page++)
        {
            using var response = await client.GetAsync(
                new Uri($"{OrdersUrl}?page={page}&pageSize=2", UriKind.Relative),
                Token);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);

            foreach (var order in body.GetProperty("orders").EnumerateArray())
            {
                seen.Add(order.GetProperty("orderNumber").GetString()!);
                dates.Add(order.GetProperty("placedAtUtc").GetDateTime());
            }
        }

        // Every order exactly once across the three pages.
        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct(StringComparer.Ordinal).Count());

        // And newest first, across the page boundary as well as within a page.
        Assert.Equal(dates.OrderByDescending(date => date).ToList(), dates);

        using var lastPage = await client.GetAsync(new Uri($"{OrdersUrl}?page=3&pageSize=2", UriKind.Relative), Token);
        var last = await lastPage.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.False(last.GetProperty("hasMore").GetBoolean());
    }

    /// <summary>
    /// Five orders placed at the <b>same instant</b> page without repeating or losing one.
    /// </summary>
    /// <remarks>
    /// <b>This closes a hole the mutation run found.</b> Removing the key tie-break from
    /// <c>CustomerOrderReader</c>'s ordering left <c>The_list_is_newest_first_and_pages_without_losing_a_row</c>
    /// green, because that test seeds its orders a day apart and the tie-break never decides anything.
    /// The property it guards only appears when two rows share a <c>PlacedAtUtc</c> — which a test does
    /// trivially, and a real shop does under load.
    /// <para>
    /// <b>Why it matters rather than being tidy.</b> <c>OFFSET … FETCH</c> over a non-deterministic
    /// order is free to return the rows in a different sequence for each page, so a row can appear on
    /// page 1 and page 2 while another appears on neither. A customer would see one order twice and lose
    /// another, which is exactly the complaint nobody can reproduce.
    /// </para>
    /// <para>
    /// Asserted as a set across the pages, not as a sequence, because <i>which</i> order comes first
    /// among equals is genuinely arbitrary — what must hold is that the paging is a partition.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Orders_placed_at_the_same_instant_page_without_repeating_or_losing_one()
    {
        await ReadyAsync();

        var reference = await ReferenceAsync();
        var email = UniqueEmail("history-same-instant");

        // One instant, to the millisecond the column stores (doc 08 §1.4: datetime2(3)).
        var placed = new DateTime(2026, 10, 10, 9, 15, 30, 123, DateTimeKind.Utc);

        for (var index = 0; index < 5; index++)
        {
            await InsertOrderAsync(reference, email, placedAtUtc: placed);
        }

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        await ConfirmedCustomerAsync(factory, client, email);

        var seen = new List<string>();

        for (var page = 1; page <= 3; page++)
        {
            using var response = await client.GetAsync(
                new Uri($"{OrdersUrl}?page={page}&pageSize=2", UriKind.Relative),
                Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);

            foreach (var order in body.GetProperty("orders").EnumerateArray())
            {
                seen.Add(order.GetProperty("orderNumber").GetString()!);
            }
        }

        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>A page number or size that makes no sense is corrected, never a 500 and never a scan.</summary>
    [Theory]
    [InlineData("?page=0")]
    [InlineData("?page=-5")]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=-1")]
    [InlineData("?pageSize=100000")]
    [InlineData("?page=99999")]
    public async Task A_nonsense_page_is_corrected_rather_than_crashing(string query)
    {
        await ReadyAsync();

        var reference = await ReferenceAsync();
        var email = UniqueEmail("history-paging-edge");
        await InsertOrderAsync(reference, email);

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        await ConfirmedCustomerAsync(factory, client, email);

        using var response = await client.GetAsync(new Uri($"{OrdersUrl}{query}", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);

        Assert.True(body.GetProperty("page").GetInt32() >= 1);
        Assert.InRange(body.GetProperty("pageSize").GetInt32(), 1, 50);
        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
    }

    /// <summary>A customer with no orders gets an empty page, not an error.</summary>
    [Fact]
    public async Task A_customer_with_no_orders_gets_an_empty_page()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var email = UniqueEmail("history-empty");

        await RegisterAsync(client, email);
        await SignInAsync(client, email);

        using var response = await client.GetAsync(new Uri(OrdersUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);

        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, body.GetProperty("orders").GetArrayLength());
        Assert.False(body.GetProperty("hasMore").GetBoolean());
    }

    /// <summary>Registers, confirms and signs in, which is the shopper's own route to their history.</summary>
    private static async Task ConfirmedCustomerAsync(ApiFactory factory, HttpClient client, string email)
    {
        await RegisterAsync(client, email);

        var token = await ReadLinkTokenAsync(factory, Templates.Confirmation, email);

        using var confirmed = await client.PostAsJsonAsync(
            "/api/account/confirm-email",
            new { token },
            Token);

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
    }

    private static async Task<JsonElement> FirstOrderAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri(OrdersUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.True(body.GetProperty("orders").GetArrayLength() > 0, "The history was empty.");

        return body.GetProperty("orders")[0];
    }

    /// <summary>
    /// One guest order under <paramref name="email"/>, with one line of three items.
    /// </summary>
    /// <remarks>
    /// Written as a <i>guest</i> order (no <c>CustomerUserId</c>) and claimed by the confirmation, which
    /// is the shopper's real route: doc 08 §3.1 D5. A test that wrote <c>CustomerUserId</c> directly would
    /// skip the one step the feature is about.
    /// </remarks>
    private async Task<int> InsertOrderAsync(
        SalesReferenceData reference,
        string email,
        OrderStatus status = OrderStatus.PendingPayment,
        int? courierId = null,
        string? courierName = null,
        string? trackingNumber = null,
        int? deliveryMethodId = null,
        string? deliveryMethodCode = null,
        int? pickupLocationId = null,
        DateTime? placedAtUtc = null)
    {
        var row = SalesRowWriter.OrderRow(
            reference.PaymentProviderCode,
            deliveryMethodId ?? reference.StandardDeliveryMethodId,
            deliveryMethodCode ?? DeliveryMethod.StandardCode);

        row["Email"] = email;
        row["EmailNormalized"] = email.ToUpperInvariant();
        row["Status"] = (byte)status;
        row["PickupLocationId"] = pickupLocationId;

        if (placedAtUtc is { } placed)
        {
            row["PlacedAtUtc"] = placed;
        }

        if (courierId is not null || courierName is not null)
        {
            row["CourierId"] = courierId;
            row["CourierName"] = courierName;
            row["TrackingNumber"] = trackingNumber;
            row["ShippedAtUtc"] = DateTime.UtcNow;
        }

        // A pickup order has no ship-to address at all: CK_Orders_ShipAddress branches on the delivery
        // method code, and CK_Orders_Pickup needs the location.
        if (string.Equals(deliveryMethodCode, DeliveryMethod.PickupCode, StringComparison.Ordinal))
        {
            row["Ship_RecipientName"] = null;
            row["Ship_Phone"] = null;
            row["Ship_Line1"] = null;
            row["Ship_City"] = null;
        }

        var orderId = await Writer.InsertAsync("sales.Orders", row, Token);

        await Writer.InsertAsync(
            "sales.OrderLines",
            SalesRowWriter.OrderLineRow(
                orderId,
                reference.ProductId,
                reference.VariantId,
                reference.DepartmentId,
                reference.CategoryId),
            Token);

        return orderId;
    }
}
