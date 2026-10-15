using System.Net;
using System.Text.Json;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The admin customers screen's API, end to end against a real SQL Server (ADM-15, doc 11 P11 step 16).
/// <para>
/// <b>Every route has a backend test, and that is #126's lesson.</b> The promo-code admin API answered 500
/// for a day and a half because its only coverage was Playwright against a Node stub; the cause was an EF
/// translation failure no stub can produce. Each field the screen reads is asserted here on a real row.
/// </para>
/// </summary>
public sealed class AdminCustomerEndpointTests(SqlServerDatabaseFixture sqlServer)
    : AdminCustomerTestBase(sqlServer)
{
    private const string CustomersUrl = "/api/admin/customers";

    // ---- authentication and authorization -------------------------------------------------------

    [Fact]
    public async Task Every_route_refuses_an_unauthenticated_caller()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        foreach (var path in Routes())
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Token);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    /// <summary>
    /// A staff user holding the neighbouring permissions — orders and the newsletter — and not
    /// <c>Customers.View</c> is refused on every route, so the permission is the gate and not the session.
    /// </summary>
    [Fact]
    public async Task Every_route_refuses_a_staff_user_without_Customers_View()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(
            Permissions.Orders.View,
            Permissions.Newsletter.View,
            Permissions.Customers.Edit);

        foreach (var path in Routes())
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Token);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task Customers_View_alone_opens_every_route()
    {
        await using var host = await StartAsync();
        var customerId = await CreateCustomerAsync(host, $"{Marker}@example.com", "Only");

        using var client = await host.SignInAsync(Permissions.Customers.View);

        foreach (var path in Routes(customerId))
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
        }
    }

    // ---- the list -------------------------------------------------------------------------------

    /// <summary>
    /// <b>Every field of a real list row</b>, including the three live statistics, from a customer with one
    /// paid and one unpaid order.
    /// </summary>
    [Fact]
    public async Task A_list_row_carries_every_field_the_screen_reads()
    {
        await using var host = await StartAsync();
        var email = $"{Marker}@example.com";
        var customerId = await CreateCustomerAsync(
            host, email, "Kasun", $"Silva{Marker}", "+94771234567", "Kandy", emailConfirmed: false);

        var paid = await PlaceOrderAsync(host, email);
        var unpaid = await PlaceOrderAsync(host, email);
        await LinkAsync(paid.PublicId, customerId);
        await LinkAsync(unpaid.PublicId, customerId);
        await ApproveAsync(host, paid.PublicId);

        using var client = await host.SignInAsync(Permissions.Customers.View);
        var page = await GetJsonAsync(client, $"{CustomersUrl}?search={email}");

        var row = Assert.Single(page.GetProperty("rows").EnumerateArray());

        Assert.Equal(customerId, row.GetProperty("customerId").GetInt32());
        Assert.Equal($"Kasun Silva{Marker}", row.GetProperty("name").GetString());
        Assert.Equal(email, row.GetProperty("email").GetString());
        Assert.False(row.GetProperty("emailConfirmed").GetBoolean());
        Assert.Equal("+94771234567", row.GetProperty("phone").GetString());
        Assert.Equal("Kandy", row.GetProperty("districtName").GetString());
        Assert.True(row.GetProperty("isActive").GetBoolean());
        Assert.True(row.GetProperty("registeredAtUtc").GetDateTime() > DateTime.UtcNow.AddHours(-1));
        Assert.Equal(2, row.GetProperty("orderCount").GetInt32());

        // Paid orders only: the unpaid one is placed and counted, and is not spend.
        Assert.Equal(paid.GrandTotal, row.GetProperty("lifetimeSpend").GetDecimal());
        Assert.Equal(SalesCurrency.Lkr, row.GetProperty("currency").GetString());
        Assert.True(row.GetProperty("lastOrderAtUtc").GetDateTime() > DateTime.UtcNow.AddHours(-1));

        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(25, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, page.GetProperty("totalCount").GetInt32());

        // The district filter's options are the 25 seeded districts, sent by the server.
        var districts = page.GetProperty("districts").EnumerateArray().ToList();
        Assert.Equal(25, districts.Count);
        Assert.Contains(districts, district => district.GetProperty("name").GetString() == "Kandy");
    }

    [Fact]
    public async Task A_customer_with_no_orders_and_no_district_reads_as_zero_and_null()
    {
        await using var host = await StartAsync();
        var email = $"{Marker}@example.com";
        await CreateCustomerAsync(host, email, "Sunil");

        using var client = await host.SignInAsync(Permissions.Customers.View);
        var row = Assert.Single((await GetJsonAsync(client, $"{CustomersUrl}?search={email}"))
            .GetProperty("rows").EnumerateArray());

        Assert.Equal("Sunil", row.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("districtName").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("phone").ValueKind);
        Assert.Equal(0, row.GetProperty("orderCount").GetInt32());
        Assert.Equal(0m, row.GetProperty("lifetimeSpend").GetDecimal());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("lastOrderAtUtc").ValueKind);
    }

    /// <summary>
    /// <b>A staff account is never a customer</b>: not on the list, and its id answers 404 on both detail
    /// routes. Doc 08 D7 keeps one table for both, so this is the filter that separates them.
    /// </summary>
    [Fact]
    public async Task A_staff_account_is_not_on_the_list_and_its_id_answers_404()
    {
        await using var host = await StartAsync();
        var email = $"{Marker}-staff@example.com";
        var staffId = await CreateStaffAsync(host, email);

        using var client = await host.SignInAsync(Permissions.Customers.View);

        var page = await GetJsonAsync(client, $"{CustomersUrl}?search={email}");
        Assert.Equal(0, page.GetProperty("totalCount").GetInt32());

        var byName = await GetJsonAsync(client, $"{CustomersUrl}?search={Uri.EscapeDataString("Office Staff")}");
        Assert.DoesNotContain(
            byName.GetProperty("rows").EnumerateArray(),
            row => row.GetProperty("customerId").GetInt32() == staffId);

        using var detail = await client.GetAsync(new Uri($"{CustomersUrl}/{staffId}", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);

        using var orders = await client.GetAsync(new Uri($"{CustomersUrl}/{staffId}/orders", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.NotFound, orders.StatusCode);
    }

    [Fact]
    public async Task An_id_that_does_not_exist_answers_404()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Customers.View);

        using var detail = await client.GetAsync(new Uri($"{CustomersUrl}/{int.MaxValue}", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);

        using var orders = await client.GetAsync(
            new Uri($"{CustomersUrl}/{int.MaxValue}/orders", UriKind.Relative),
            Token);
        Assert.Equal(HttpStatusCode.NotFound, orders.StatusCode);
    }

    // ---- search ---------------------------------------------------------------------------------

    /// <summary>
    /// The three kinds of term: an email prefix, a telephone number however the caller says it, and a
    /// name typed as "first last" — each finds exactly the one customer and not the decoy beside it.
    /// </summary>
    [Fact]
    public async Task Search_finds_by_email_prefix_by_telephone_and_by_full_name()
    {
        await using var host = await StartAsync();
        var target = await CreateCustomerAsync(
            host, $"{Marker}-target@example.com", "Chamari", $"Atapattu{Marker}", "+94712223334");
        var decoy = await CreateCustomerAsync(
            host, $"{Marker}-decoy@example.com", "Chamari", $"Jayasinghe{Marker}", "+94719998887");

        using var client = await host.SignInAsync(Permissions.Customers.View);

        // An email prefix, in a different case from the stored one.
        Assert.Equal([target], await IdsAsync(client, $"{Marker.ToUpperInvariant()}-TARGET@"));

        // The local form of the number, with the separators a caller reads it out with.
        Assert.Equal([target], await IdsAsync(client, "071 222 3334"));

        // The international form.
        Assert.Equal([target], await IdsAsync(client, "+94712223334"));

        // "First last", typed together.
        Assert.Equal([target], await IdsAsync(client, $"Chamari Atapattu{Marker}"));

        // Part of the marker matches both, newest first — the decoy was created second.
        Assert.Equal([decoy, target], await IdsAsync(client, Marker));
    }

    /// <summary>A LIKE wildcard in the term is a literal, not a pattern: "%" matches nobody.</summary>
    [Fact]
    public async Task A_wildcard_in_the_search_is_matched_literally()
    {
        await using var host = await StartAsync();
        await CreateCustomerAsync(host, $"{Marker}@example.com", "Ruwan", $"Fernando{Marker}");

        using var client = await host.SignInAsync(Permissions.Customers.View);

        Assert.Empty(await IdsAsync(client, $"{Marker}%"));
        Assert.Empty(await IdsAsync(client, $"Ruwan_Fernando{Marker}"));
    }

    [Fact]
    public async Task The_district_filter_keeps_customers_whose_default_district_it_is()
    {
        await using var host = await StartAsync();
        var galle = await CreateCustomerAsync(host, $"{Marker}-g@example.com", "Galle", Marker, districtName: "Galle");
        await CreateCustomerAsync(host, $"{Marker}-m@example.com", "Matara", Marker, districtName: "Matara");
        await CreateCustomerAsync(host, $"{Marker}-n@example.com", "None", Marker);

        using var client = await host.SignInAsync(Permissions.Customers.View);
        var galleId = await DistrictIdAsync("Galle");

        Assert.Equal([galle], await IdsAsync(client, Marker, $"&districtId={galleId}"));
    }

    // ---- paging ---------------------------------------------------------------------------------

    /// <summary>
    /// Server-side paging, newest registration first, with no row on two pages and none missing.
    /// </summary>
    [Fact]
    public async Task Pages_are_newest_first_and_neither_repeat_nor_skip()
    {
        await using var host = await StartAsync();
        var start = DateTime.UtcNow.AddDays(-3);

        var oldest = await CreateCustomerAsync(host, $"{Marker}-1@example.com", "A", Marker, createdAtUtc: start);
        var middle = await CreateCustomerAsync(host, $"{Marker}-2@example.com", "B", Marker, createdAtUtc: start.AddHours(1));
        var newest = await CreateCustomerAsync(host, $"{Marker}-3@example.com", "C", Marker, createdAtUtc: start.AddHours(2));

        using var client = await host.SignInAsync(Permissions.Customers.View);

        var first = await GetJsonAsync(client, $"{CustomersUrl}?search={Marker}&pageSize=2&page=1");
        var second = await GetJsonAsync(client, $"{CustomersUrl}?search={Marker}&pageSize=2&page=2");

        Assert.Equal(3, first.GetProperty("totalCount").GetInt32());
        Assert.Equal([newest, middle], Ids(first));
        Assert.Equal([oldest], Ids(second));
        Assert.Equal(2, second.GetProperty("page").GetInt32());
    }

    /// <summary>
    /// Out-of-range paging is clamped, never refused: a page size above the ceiling is 100, and page 0 is
    /// page 1.
    /// </summary>
    [Fact]
    public async Task Paging_inputs_are_clamped()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Customers.View);

        var huge = await GetJsonAsync(client, $"{CustomersUrl}?pageSize=100000&page=0");

        Assert.Equal(100, huge.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, huge.GetProperty("page").GetInt32());
        Assert.True(huge.GetProperty("rows").GetArrayLength() <= 100);
    }

    // ---- the detail and the order history ------------------------------------------------------

    [Fact]
    public async Task The_detail_carries_the_profile_the_statistics_and_the_addresses_default_first()
    {
        await using var host = await StartAsync();
        var email = $"{Marker}@example.com";
        var customerId = await CreateCustomerAsync(
            host, email, "Dilani", "Wickramasinghe", "+94761112223", "Gampaha");

        var office = await AddAddressAsync(customerId, "Dilani at work", "Colombo", isDefault: false, label: "Office");
        var home = await AddAddressAsync(customerId, "Dilani at home", "Gampaha", isDefault: true, label: "Home");

        var order = await PlaceOrderAsync(host, email);
        await LinkAsync(order.PublicId, customerId);
        await ApproveAsync(host, order.PublicId);

        using var client = await host.SignInAsync(Permissions.Customers.View);
        var detail = await GetJsonAsync(client, $"{CustomersUrl}/{customerId}");

        Assert.Equal(customerId, detail.GetProperty("customerId").GetInt32());
        Assert.Equal("Dilani", detail.GetProperty("firstName").GetString());
        Assert.Equal("Wickramasinghe", detail.GetProperty("lastName").GetString());
        Assert.Equal(email, detail.GetProperty("email").GetString());
        Assert.True(detail.GetProperty("emailConfirmed").GetBoolean());
        Assert.Equal("+94761112223", detail.GetProperty("phone").GetString());
        Assert.Equal("Gampaha", detail.GetProperty("districtName").GetString());
        Assert.True(detail.GetProperty("isActive").GetBoolean());
        Assert.True(detail.GetProperty("registeredAtUtc").GetDateTime() > DateTime.UtcNow.AddHours(-1));
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("lastSignInAtUtc").ValueKind);
        Assert.Equal(1, detail.GetProperty("orderCount").GetInt32());
        Assert.Equal(order.GrandTotal, detail.GetProperty("lifetimeSpend").GetDecimal());
        Assert.Equal(SalesCurrency.Lkr, detail.GetProperty("currency").GetString());
        Assert.NotEqual(JsonValueKind.Null, detail.GetProperty("lastOrderAtUtc").ValueKind);

        var addresses = detail.GetProperty("addresses").EnumerateArray().ToList();
        Assert.Equal([home, office], addresses.Select(address => address.GetProperty("addressId").GetInt32()));

        var first = addresses[0];
        Assert.Equal("Home", first.GetProperty("label").GetString());
        Assert.Equal("Dilani at home", first.GetProperty("recipientName").GetString());
        Assert.Equal("+94771234567", first.GetProperty("phone").GetString());
        Assert.Equal("14 Galle Road", first.GetProperty("line1").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("line2").ValueKind);
        Assert.Equal("Kollupitiya", first.GetProperty("city").GetString());
        Assert.Equal("Gampaha", first.GetProperty("districtName").GetString());
        Assert.Equal("00300", first.GetProperty("postalCode").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("landmark").ValueKind);
        Assert.True(first.GetProperty("isDefault").GetBoolean());
        Assert.Equal("Colombo", addresses[1].GetProperty("districtName").GetString());
        Assert.False(addresses[1].GetProperty("isDefault").GetBoolean());
    }

    /// <summary>
    /// <b>The privacy test.</b> The customer's history holds their own two orders and nothing else, against
    /// three decoys that each reach the wrong account if the reader matches the wrong thing:
    /// <list type="number">
    /// <item>another customer's order;</item>
    /// <item>a <b>guest order under the customer's own address</b>, not yet linked — exactly what doc 08
    /// §3.1 D5 leaves unlinked until the address is confirmed, and what a reader matching by email would
    /// show;</item>
    /// <item>a guest order under an address that <i>starts</i> with the customer's, which a prefix match
    /// would show.</item>
    /// </list>
    /// The list row's statistics are asserted to agree, because they are computed by a different query.
    /// </summary>
    [Fact]
    public async Task The_order_history_holds_only_that_customer_s_orders()
    {
        await using var host = await StartAsync();
        var email = $"{Marker}@example.com";
        var otherEmail = $"{Marker}-other@example.com";

        var customerId = await CreateCustomerAsync(host, email, "Mine", Marker);
        var otherId = await CreateCustomerAsync(host, otherEmail, "Other", Marker);

        var mineOlder = await PlaceOrderAsync(host, email);
        var mineNewer = await PlaceOrderAsync(host, email);
        await LinkAsync(mineOlder.PublicId, customerId);
        await LinkAsync(mineNewer.PublicId, customerId);
        await ApproveAsync(host, mineOlder.PublicId);

        var othersOrder = await PlaceOrderAsync(host, otherEmail);
        await LinkAsync(othersOrder.PublicId, otherId);
        await ApproveAsync(host, othersOrder.PublicId);

        var sameEmailGuest = await PlaceOrderAsync(host, email);
        await ApproveAsync(host, sameEmailGuest.PublicId);
        var similarEmailGuest = await PlaceOrderAsync(host, $"{Marker}@example.com.lk");
        await ApproveAsync(host, similarEmailGuest.PublicId);

        using var client = await host.SignInAsync(Permissions.Customers.View);

        var history = await GetJsonAsync(client, $"{CustomersUrl}/{customerId}/orders");
        var numbers = history.GetProperty("rows").EnumerateArray()
            .Select(row => row.GetProperty("orderNumber").GetString())
            .ToList();

        Assert.Equal([mineNewer.OrderNumber, mineOlder.OrderNumber], numbers);
        Assert.Equal(2, history.GetProperty("totalCount").GetInt32());

        var detail = await GetJsonAsync(client, $"{CustomersUrl}/{customerId}");
        Assert.Equal(2, detail.GetProperty("orderCount").GetInt32());
        Assert.Equal(mineOlder.GrandTotal, detail.GetProperty("lifetimeSpend").GetDecimal());

        var row = Assert.Single((await GetJsonAsync(client, $"{CustomersUrl}?search={email}"))
            .GetProperty("rows").EnumerateArray());
        Assert.Equal(2, row.GetProperty("orderCount").GetInt32());
        Assert.Equal(mineOlder.GrandTotal, row.GetProperty("lifetimeSpend").GetDecimal());

        // And the other customer's history is theirs alone.
        var othersHistory = await GetJsonAsync(client, $"{CustomersUrl}/{otherId}/orders");
        Assert.Equal(
            [othersOrder.OrderNumber],
            othersHistory.GetProperty("rows").EnumerateArray().Select(order => order.GetProperty("orderNumber").GetString()));
    }

    [Fact]
    public async Task An_order_history_row_carries_every_field_and_pages()
    {
        await using var host = await StartAsync();
        var email = $"{Marker}@example.com";
        var customerId = await CreateCustomerAsync(host, email, "Paged", Marker);

        var first = await PlaceOrderAsync(host, email);
        var second = await PlaceOrderAsync(host, email);
        await LinkAsync(first.PublicId, customerId);
        await LinkAsync(second.PublicId, customerId);
        await ApproveAsync(host, second.PublicId);

        using var client = await host.SignInAsync(Permissions.Customers.View);

        var pageOne = await GetJsonAsync(client, $"{CustomersUrl}/{customerId}/orders?pageSize=1&page=1");
        var pageTwo = await GetJsonAsync(client, $"{CustomersUrl}/{customerId}/orders?pageSize=1&page=2");

        Assert.Equal(2, pageOne.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, pageOne.GetProperty("pageSize").GetInt32());

        var newest = Assert.Single(pageOne.GetProperty("rows").EnumerateArray());
        Assert.Equal(second.PublicId, newest.GetProperty("orderPublicId").GetGuid());
        Assert.Equal(second.OrderNumber, newest.GetProperty("orderNumber").GetString());
        Assert.Equal(nameof(OrderStatus.Paid), newest.GetProperty("status").GetString());
        Assert.Equal("Paid", newest.GetProperty("statusLabel").GetString());
        Assert.Equal(second.GrandTotal, newest.GetProperty("grandTotal").GetDecimal());
        Assert.Equal(SalesCurrency.Lkr, newest.GetProperty("currency").GetString());
        Assert.True(newest.GetProperty("placedAtUtc").GetDateTime() > DateTime.UtcNow.AddHours(-1));
        Assert.NotEqual(JsonValueKind.Null, newest.GetProperty("paidAtUtc").ValueKind);

        var older = Assert.Single(pageTwo.GetProperty("rows").EnumerateArray());
        Assert.Equal(first.OrderNumber, older.GetProperty("orderNumber").GetString());
        Assert.Equal("Awaiting payment", older.GetProperty("statusLabel").GetString());
        Assert.Equal(JsonValueKind.Null, older.GetProperty("paidAtUtc").ValueKind);
    }

    /// <summary>A Sinhala and a Tamil name survive the round trip and are found by a search in that script.</summary>
    [Fact]
    public async Task Sinhala_and_Tamil_names_round_trip_and_are_searchable()
    {
        await using var host = await StartAsync();
        var sinhala = await CreateCustomerAsync(host, $"{Marker}-si@example.com", "නිමාලි", $"පෙරේරා{Marker}");
        var tamil = await CreateCustomerAsync(host, $"{Marker}-ta@example.com", "கவிதா", $"ராஜா{Marker}");

        using var client = await host.SignInAsync(Permissions.Customers.View);

        var found = await GetJsonAsync(client, $"{CustomersUrl}?search={Uri.EscapeDataString("නිමාලි පෙරේරා" + Marker)}");
        var row = Assert.Single(found.GetProperty("rows").EnumerateArray());
        Assert.Equal(sinhala, row.GetProperty("customerId").GetInt32());
        Assert.Equal($"නිමාලි පෙරේරා{Marker}", row.GetProperty("name").GetString());

        Assert.Equal([tamil], await IdsAsync(client, $"கவிதா ராஜா{Marker}"));
    }

    private static IEnumerable<string> Routes(int customerId = 1)
    {
        yield return CustomersUrl;
        yield return $"{CustomersUrl}/{customerId}";
        yield return $"{CustomersUrl}/{customerId}/orders";
    }

    private static async Task<List<int>> IdsAsync(HttpClient client, string search, string extra = "") =>
        Ids(await GetJsonAsync(client, $"{CustomersUrl}?search={Uri.EscapeDataString(search)}{extra}"));

    private static List<int> Ids(JsonElement page) =>
        page.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("customerId").GetInt32()).ToList();
}
