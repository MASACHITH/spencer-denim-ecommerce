using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The admin orders screen's API, end to end against a real SQL Server (ADM-09, doc 11 P9).
/// <para>
/// <b>Every field the screen reads is asserted on a real row, and that is issue #126's lesson rather
/// than thoroughness for its own sake.</b> Playwright never starts the .NET API — it runs against Node
/// stubs — so a dropped payload field left all 860 browser tests green. A screen test cannot observe a
/// backend change, so anything the order list or the order detail depends on has to be pinned here.
/// </para>
/// <para>
/// <b>One host, two kinds of caller</b>, the rule <c>AdminBankTransferEndpointTests</c> established: a
/// shopper places the order through the public endpoints and a staff user signs in on the <i>same</i>
/// host, so anything cached per host — effective permissions above all — is genuinely shared.
/// </para>
/// </summary>
public sealed class AdminOrderEndpointTests(SqlServerDatabaseFixture sqlServer)
{
    private const string OrdersUrl = "/api/admin/orders";
    private const string CouriersUrl = "/api/admin/couriers";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- authentication and authorization -------------------------------------------------------

    [Fact]
    public async Task Every_admin_route_refuses_an_unauthenticated_caller()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        foreach (var (method, path) in AdminRoutes())
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
            using var response = await client.SendAsync(request, Token);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    /// <summary>
    /// A signed-in staff user without the right permission is refused on every route.
    /// </summary>
    /// <remarks>
    /// The role grants two unrelated permissions, so what is proved is that the <i>permission</i> is the
    /// gate rather than the session. A role with none at all would pass this against an endpoint that only
    /// checked for a cookie.
    /// </remarks>
    [Fact]
    public async Task Every_admin_route_refuses_a_staff_user_without_the_permission()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Products.View, Permissions.Categories.View);

        foreach (var (method, path) in AdminRoutes())
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

            if (method != HttpMethod.Get)
            {
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            }

            using var response = await client.SendAsync(request, Token);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    /// <summary>
    /// <b>Doc 11 P9's permission table, as three separate gates.</b> Reading needs <c>Orders.View</c>, the
    /// transitions need <c>Orders.Edit</c>, and the couriers need <c>Settings.Edit</c>.
    /// </summary>
    /// <remarks>
    /// Asserted from <i>both</i> sides: a holder of each permission gets through its own routes and is
    /// refused the others. A one-sided test would pass against an endpoint that required nothing.
    /// </remarks>
    [Fact]
    public async Task The_three_permissions_are_separate()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var viewer = await host.SignInAsync(Permissions.Orders.View);

        using var list = await viewer.GetAsync(new Uri(OrdersUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        using var detail = await viewer.GetAsync(new Uri(OrderUrl(order.PublicId), UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);

        using var couriers = await viewer.GetAsync(new Uri(CouriersUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, couriers.StatusCode);

        // Orders.View does not move an order, and does not add a courier.
        using var refusedTransition = await viewer.PostAsJsonAsync(
            new Uri($"{OrderUrl(order.PublicId)}/start-processing", UriKind.Relative),
            new { note = (string?)null },
            JsonOptions,
            Token);
        Assert.Equal(HttpStatusCode.Forbidden, refusedTransition.StatusCode);

        using var refusedCourier = await viewer.PostAsJsonAsync(
            new Uri(CouriersUrl, UriKind.Relative),
            NewCourierBody("xunit-refused"),
            JsonOptions,
            Token);
        Assert.Equal(HttpStatusCode.Forbidden, refusedCourier.StatusCode);

        // Orders.Edit moves an order and does not add a courier.
        using var editor = await host.SignInAsync(Permissions.Orders.Edit, Permissions.Orders.View);

        using var editorCourier = await editor.PostAsJsonAsync(
            new Uri(CouriersUrl, UriKind.Relative),
            NewCourierBody("xunit-editor"),
            JsonOptions,
            Token);
        Assert.Equal(HttpStatusCode.Forbidden, editorCourier.StatusCode);

        // Settings.Edit adds a courier and does not move an order.
        using var settings = await host.SignInAsync(Permissions.Settings.Edit);

        using var settingsTransition = await settings.PostAsJsonAsync(
            new Uri($"{OrderUrl(order.PublicId)}/start-processing", UriKind.Relative),
            new { note = (string?)null },
            JsonOptions,
            Token);
        Assert.Equal(HttpStatusCode.Forbidden, settingsTransition.StatusCode);
    }

    // ---- the list -------------------------------------------------------------------------------

    /// <summary>
    /// <b>Every field of a real list row, because a client model that had never listed three of them was
    /// W1-12's blocker and because no browser test can see this.</b>
    /// </summary>
    [Fact]
    public async Task A_list_row_carries_every_field_the_screen_reads()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var client = await host.SignInAsync(Permissions.Orders.View);

        var page = await ListAsync(client, $"?search={order.OrderNumber}");

        var rows = page.GetProperty("rows").EnumerateArray().ToList();
        var row = Assert.Single(rows);

        Assert.Equal(order.PublicId, row.GetProperty("orderPublicId").GetGuid());
        Assert.Equal(order.OrderNumber, row.GetProperty("orderNumber").GetString());
        Assert.Equal(nameof(OrderStatus.PendingPayment), row.GetProperty("status").GetString());
        Assert.Equal("Awaiting payment", row.GetProperty("statusLabel").GetString());
        Assert.Equal("Nimali Perera", row.GetProperty("customerName").GetString());
        Assert.Equal("nimali@example.com", row.GetProperty("customerEmail").GetString());
        Assert.Equal(order.GrandTotal, row.GetProperty("grandTotal").GetDecimal());
        Assert.Equal(SalesCurrency.Lkr, row.GetProperty("currency").GetString());
        Assert.Equal(PaymentProviderCodes.BankTransfer, row.GetProperty("paymentProviderCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("deliveryMethodName").GetString()));
        Assert.False(row.GetProperty("isPickup").GetBoolean());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("courierName").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("trackingNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("invoiceNumber").ValueKind);
        Assert.True(row.GetProperty("placedAtUtc").GetDateTime() > DateTime.UtcNow.AddHours(-1));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("paidAtUtc").ValueKind);

        // Awaiting payment, so the deadline countdown is a number. Positive, because the order was placed
        // a second ago and the window is 48 hours.
        Assert.True(row.GetProperty("secondsUntilExpiry").GetInt64() > 0);

        // The page's own facts.
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(25, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
        Assert.Equal("Newest", page.GetProperty("sort").GetString());

        // Every status has a count, including the ones at zero: a tab whose count disappeared when it
        // reached zero would be a tab a staff member could not tell from one that was never there.
        var counts = page.GetProperty("statusCounts");
        foreach (var status in Enum.GetNames<OrderStatus>())
        {
            Assert.True(counts.TryGetProperty(status, out _), status);
        }
    }

    /// <summary>
    /// A paid order carries no countdown, because it has no deadline left to run out.
    /// </summary>
    [Fact]
    public async Task A_paid_order_has_no_countdown_and_carries_its_invoice_number()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var approver = await host.SignInAsync(Permissions.Orders.Approve);
        await ApproveAsync(approver, order.PublicId);

        using var client = await host.SignInAsync(Permissions.Orders.View);
        var row = Assert.Single(
            (await ListAsync(client, $"?search={order.OrderNumber}")).GetProperty("rows").EnumerateArray().ToList());

        Assert.Equal(nameof(OrderStatus.Paid), row.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("secondsUntilExpiry").ValueKind);
        Assert.StartsWith("INV-", row.GetProperty("invoiceNumber").GetString()!, StringComparison.Ordinal);
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("paidAtUtc").ValueKind);
    }

    /// <summary>
    /// <b>The search finds an order by the number a customer quotes, in the three forms they quote it.</b>
    /// </summary>
    /// <remarks>
    /// This is doc 11 §6.6 — "a customer phones about an order" — and the reason the whole screen exists.
    /// The digits-only form matters most: somebody reading a number off a screen says the part that
    /// changes.
    /// </remarks>
    [Fact]
    public async Task The_search_finds_an_order_by_its_number_however_it_is_quoted()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var client = await host.SignInAsync(Permissions.Orders.View);

        // The whole number, a prefix of it, and the digits alone.
        var digits = order.OrderNumber.Split('-')[^1];

        // The year-and-sequence part, which is what somebody quotes when they read the number off a
        // screen and skip the prefix; and the same number typed with spaces, which is how it is read
        // aloud. Both missed before review: ten digits read as a telephone number, and a spaced term
        // matched no stored value because the series writes dashes.
        var yearAndSequence = string.Join('-', order.OrderNumber.Split('-')[1..]);

        foreach (var term in new[]
        {
            order.OrderNumber,
            order.OrderNumber[..^3],
            digits,
            digits.TrimStart('0'),
            yearAndSequence,
            order.OrderNumber.Replace('-', ' '),
            yearAndSequence.Replace('-', ' '),
            order.OrderNumber.ToLowerInvariant(),
        })
        {
            var rows = (await ListAsync(client, $"?search={Uri.EscapeDataString(term)}"))
                .GetProperty("rows")
                .EnumerateArray()
                .ToList();

            Assert.Contains(
                rows,
                row => row.GetProperty("orderNumber").GetString() == order.OrderNumber);
        }
    }

    [Fact]
    public async Task The_search_finds_an_order_by_email_address_and_by_name()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var client = await host.SignInAsync(Permissions.Orders.View);

        foreach (var term in new[] { "nimali@example.com", "NIMALI@EXAMPLE.COM", "Perera", "perera" })
        {
            var rows = (await ListAsync(client, $"?search={Uri.EscapeDataString(term)}"))
                .GetProperty("rows")
                .EnumerateArray()
                .ToList();

            Assert.Contains(
                rows,
                row => row.GetProperty("orderNumber").GetString() == order.OrderNumber);
        }
    }

    /// <summary>
    /// A search that matches nothing is an empty page, not an error.
    /// </summary>
    [Fact]
    public async Task A_search_that_matches_nothing_is_an_empty_page()
    {
        await using var host = await StartAsync();
        await PlaceOrderAsync(host);

        using var client = await host.SignInAsync(Permissions.Orders.View);

        var page = await ListAsync(client, "?search=SD-1999-999999");

        Assert.Empty(page.GetProperty("rows").EnumerateArray());
        Assert.Equal(0, page.GetProperty("totalCount").GetInt32());

        // And the counts are still there, because they are over the whole table rather than the filter.
        Assert.True(page.GetProperty("statusCounts").GetProperty(nameof(OrderStatus.PendingPayment)).GetInt32() > 0);
    }

    /// <summary>
    /// <b>A <c>%</c> in the search box finds nothing rather than everything.</b>
    /// </summary>
    /// <remarks>
    /// <b>And this is the only test that can see it, which is why it carries the whole guarantee.</b> The
    /// Application layer does <i>not</i> escape the term — it used to carry two helpers that did, and they
    /// were dead code justified by a false claim about EF. EF 10.0.12 escapes the value itself at run time
    /// and emits an explicit <c>ESCAPE</c> clause, measured with <c>ToQueryString()</c>:
    /// <c>LIKE @text_startswith ESCAPE '\'</c>. So the behaviour is the framework's, a unit test cannot
    /// observe it, and an assertion on the wire is the only kind that would notice EF changing its mind.
    /// </remarks>
    [Fact]
    public async Task A_wildcard_in_the_search_box_is_not_a_wildcard()
    {
        await using var host = await StartAsync();
        await PlaceOrderAsync(host);

        using var client = await host.SignInAsync(Permissions.Orders.View);

        foreach (var term in new[] { "%", "_", "SD-%", "%%%" })
        {
            var page = await ListAsync(client, $"?search={Uri.EscapeDataString(term)}");

            Assert.Equal(0, page.GetProperty("totalCount").GetInt32());
        }
    }

    /// <summary>
    /// <b>EF escapes a <c>LIKE</c> parameter itself, and this pins it rather than asserting it in a
    /// comment.</b>
    /// </summary>
    /// <remarks>
    /// The search relies on the framework for this, and the Application layer deliberately does nothing:
    /// it used to carry two escaping helpers that nothing called, justified by a comment saying EF does
    /// <i>not</i> escape. It does. So the dependency is on EF's behaviour, and a dependency on a
    /// framework's behaviour belongs in a test that reads the generated SQL — not in prose that nobody
    /// re-checks when the package version moves.
    /// <para>
    /// <c>ToQueryString()</c> does not execute anything, so this costs a scope and no round trip. It
    /// asserts the two halves the search needs: the wildcard is in the <b>parameter</b> rather than
    /// concatenated into the text, and an explicit <c>ESCAPE</c> clause is emitted — without which a
    /// <c>%</c> inside the parameter would still be a wildcard.
    /// </para>
    /// <para>
    /// <b>This is the check my own end-of-task sweep did not make.</b> That sweep verified every schema
    /// claim I had written and caught five; it never looked at framework claims, and item 7 of the
    /// tester's verdict is the one it missed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Ef_escapes_a_like_parameter_itself_which_is_what_the_search_relies_on()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The two translations AdminOrderReader.Filter actually produces, with a term carrying every
        // wildcard a staff member could type.
        const string term = "SD-%_[";

        var startsWith = dbContext.Orders
            .Where(order => order.OrderNumber.StartsWith(term))
            .ToQueryString();

        var endsWith = dbContext.Orders
            .Where(order => order.OrderNumber.EndsWith(term))
            .ToQueryString();

        foreach (var sql in new[] { startsWith, endsWith })
        {
            Assert.Contains("LIKE", sql, StringComparison.Ordinal);

            // An explicit ESCAPE clause. Without it the backslashes EF puts in the parameter would be
            // literal backslashes and the wildcards would still be wildcards.
            Assert.Contains("ESCAPE", sql, StringComparison.Ordinal);

            // And the escaping is in the *parameter*, not concatenated into the statement — so the term
            // never reaches the SQL text and the plan is shared across searches.
            Assert.DoesNotContain("'SD-%_[", sql, StringComparison.Ordinal);
        }

        // The declared parameter carries the escaped form, which is the half that makes the wildcard
        // inert. Asserted on the StartsWith translation, where EF appends its own trailing '%'.
        Assert.Contains(@"SD-\%\_\[", startsWith, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Doc 11 P9 step 1 on the wire: the Paid filter comes back oldest first, and says so.</b>
    /// </summary>
    [Fact]
    public async Task The_paid_filter_is_oldest_first_and_the_answer_says_which_sort_was_used()
    {
        await using var host = await StartAsync();

        using var client = await host.SignInAsync(Permissions.Orders.View);

        Assert.Equal("Oldest", (await ListAsync(client, "?status=Paid")).GetProperty("sort").GetString());
        Assert.Equal("Newest", (await ListAsync(client, "?status=Processing")).GetProperty("sort").GetString());
        Assert.Equal("Newest", (await ListAsync(client, string.Empty)).GetProperty("sort").GetString());

        // An explicit sort wins, on the Paid tab too.
        Assert.Equal(
            "Newest",
            (await ListAsync(client, "?status=Paid&sort=newest")).GetProperty("sort").GetString());
    }

    /// <summary>
    /// An unknown status is a 400 naming the field, not "every order".
    /// </summary>
    /// <remarks>
    /// A typo that quietly widened the filter to every order would have a staff member working the wrong
    /// queue with nothing on the page saying so.
    /// </remarks>
    [Fact]
    public async Task An_unknown_status_is_refused_rather_than_ignored()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Orders.View);

        using var response = await client.GetAsync(
            new Uri($"{OrdersUrl}?status=Posted", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
        Assert.True(body.GetProperty("errors").TryGetProperty("status", out _));

        // `all` is accepted as "every status", because a screen may send it explicitly.
        using var all = await client.GetAsync(new Uri($"{OrdersUrl}?status=all", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
    }

    /// <summary>
    /// Paging is clamped rather than refused, which is the decision the bank queue already took.
    /// </summary>
    [Fact]
    public async Task Paging_is_clamped_rather_than_refused()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Orders.View);

        Assert.Equal(1, (await ListAsync(client, "?page=0")).GetProperty("page").GetInt32());
        Assert.Equal(1, (await ListAsync(client, "?page=-5")).GetProperty("page").GetInt32());
        Assert.Equal(100, (await ListAsync(client, "?pageSize=5000")).GetProperty("pageSize").GetInt32());
        Assert.Equal(1, (await ListAsync(client, "?pageSize=0")).GetProperty("pageSize").GetInt32());
    }

    /// <summary>
    /// A staff work list of named customers and their money is never cached.
    /// </summary>
    [Fact]
    public async Task The_list_is_never_cached()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Orders.View);

        using var response = await client.GetAsync(new Uri(OrdersUrl, UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.Contains("Cookie", response.Headers.Vary, StringComparer.OrdinalIgnoreCase);
    }

    // ---- the detail -----------------------------------------------------------------------------

    /// <summary>
    /// <b>Every panel of the detail screen, on a real paid order.</b>
    /// </summary>
    [Fact]
    public async Task The_detail_carries_the_lines_the_customer_the_money_the_payment_and_the_history()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var approver = await host.SignInAsync(Permissions.Orders.Approve);
        await ApproveAsync(approver, order.PublicId);

        using var client = await host.SignInAsync(Permissions.Orders.View);
        var detail = await DetailAsync(client, order.PublicId);

        Assert.Equal(order.PublicId, detail.GetProperty("orderPublicId").GetGuid());
        Assert.Equal(order.OrderNumber, detail.GetProperty("orderNumber").GetString());
        Assert.Equal(nameof(OrderStatus.Paid), detail.GetProperty("status").GetString());
        Assert.Equal("Paid", detail.GetProperty("statusLabel").GetString());
        Assert.Equal(nameof(OrderStockState.Committed), detail.GetProperty("stockState").GetString());

        // What was bought, with everything a picker needs off the shelf.
        var line = Assert.Single(detail.GetProperty("lines").EnumerateArray().ToList());
        Assert.False(string.IsNullOrWhiteSpace(line.GetProperty("sku").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(line.GetProperty("productName").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(line.GetProperty("colourName").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(line.GetProperty("sizeLabel").GetString()));
        Assert.Equal(1, line.GetProperty("quantity").GetInt32());
        Assert.True(line.GetProperty("unitPrice").GetDecimal() > 0);
        Assert.True(line.GetProperty("lineTotal").GetDecimal() > 0);
        Assert.Equal(0, line.GetProperty("refundedQuantity").GetInt32());

        // Who to telephone.
        var customer = detail.GetProperty("customer");
        Assert.Equal("Nimali Perera", customer.GetProperty("name").GetString());
        Assert.Equal("nimali@example.com", customer.GetProperty("email").GetString());
        Assert.Equal("0771234567", customer.GetProperty("phone").GetString());
        Assert.False(customer.GetProperty("hasAccount").GetBoolean());
        Assert.False(customer.GetProperty("isAnonymised").GetBoolean());

        // Where it is going.
        var delivery = detail.GetProperty("delivery");
        Assert.Equal(DeliveryMethod.StandardCode, delivery.GetProperty("methodCode").GetString());
        Assert.False(delivery.GetProperty("isPickup").GetBoolean());
        Assert.Equal("Nimali Perera", delivery.GetProperty("recipientName").GetString());
        Assert.Equal("14 Galle Road", delivery.GetProperty("line1").GetString());
        Assert.Equal("Kollupitiya", delivery.GetProperty("city").GetString());
        Assert.Equal("Colombo", delivery.GetProperty("districtName").GetString());

        // The money, every figure a stored column.
        var money = detail.GetProperty("money");
        Assert.Equal(SalesCurrency.Lkr, money.GetProperty("currency").GetString());
        Assert.Equal(order.GrandTotal, money.GetProperty("grandTotal").GetDecimal());
        Assert.Equal(0m, money.GetProperty("refundedAmount").GetDecimal());

        // The payment, and the staff member who approved it — by email, not by id.
        var payment = Assert.Single(detail.GetProperty("payments").EnumerateArray().ToList());
        Assert.Equal(PaymentProviderCodes.BankTransfer, payment.GetProperty("providerCode").GetString());
        Assert.Equal(nameof(PaymentStatus.Succeeded), payment.GetProperty("status").GetString());
        Assert.True(payment.GetProperty("isOrderSettlement").GetBoolean());
        Assert.Equal(order.GrandTotal, payment.GetProperty("amount").GetDecimal());
        Assert.Contains("@", payment.GetProperty("approvedByUserEmail").GetString()!, StringComparison.Ordinal);
        Assert.NotEqual(JsonValueKind.Null, payment.GetProperty("approvedAtUtc").ValueKind);

        // The timeline, oldest first, with placement's null from-status first.
        var history = detail.GetProperty("history").EnumerateArray().ToList();
        Assert.True(history.Count >= 2);
        Assert.Equal(JsonValueKind.Null, history[0].GetProperty("fromStatus").ValueKind);
        Assert.Equal(nameof(OrderStatus.PendingPayment), history[0].GetProperty("toStatus").GetString());
        Assert.Equal(nameof(OrderStatus.Paid), history[^1].GetProperty("toStatus").GetString());
        Assert.Equal(nameof(OrderStatusChangeSource.Staff), history[^1].GetProperty("source").GetString());
        Assert.Contains("@", history[^1].GetProperty("changedByUserEmail").GetString()!, StringComparison.Ordinal);

        // Nothing has shipped, so there is no dispatch.
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("dispatch").ValueKind);
        Assert.Empty(detail.GetProperty("refunds").EnumerateArray());

        // And the invoice number, which is what the print link turns on.
        Assert.StartsWith("INV-", detail.GetProperty("invoiceNumber").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>With no courier configured, the screen is told so in words and the action is false.</b>
    /// </summary>
    /// <remarks>
    /// Couriers are seeded since issue #33 (owner decision 2026-10-21), but an owner can delete every one, and
    /// the honest answer then is a sentence saying what to go and do, not a disabled control with no
    /// explanation. The test deletes them first, so it is about that state and not about the seed.
    /// </remarks>
    [Fact]
    public async Task With_no_courier_configured_the_blocked_reason_says_what_to_do()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var approver = await host.SignInAsync(Permissions.Orders.Approve);
        await ApproveAsync(approver, order.PublicId);

        using var editor = await host.SignInAsync(Permissions.Orders.Edit, Permissions.Orders.View);
        await TransitionAsync(editor, order.PublicId, "start-processing", HttpStatusCode.OK);

        await DeleteEveryCourierAsync();

        var detail = await DetailAsync(editor, order.PublicId);
        var actions = detail.GetProperty("actions");

        Assert.False(actions.GetProperty("couriersConfigured").GetBoolean());
        Assert.False(actions.GetProperty("canMarkShipped").GetBoolean());

        var reason = actions.GetProperty("blockedReason").GetString();
        Assert.NotNull(reason);
        Assert.Contains("courier", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Settings", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>Issue #124: an expired order is reachable, and it offers Accept late payment.</b>
    /// </summary>
    /// <remarks>
    /// The bank-transfer queue lists <c>Pending</c> payments and expiry closes the payment in the same
    /// transaction as the order, so an expired order is not in that queue at all — and a staff member who
    /// got an <c>order_expired</c> refusal there and pressed Refresh lost the only route to the
    /// transaction permanently. This test is the fix: search the number the customer quotes, open the
    /// order, and the action is offered.
    /// </remarks>
    [Fact]
    public async Task An_expired_order_is_found_by_its_number_and_offers_accept_late_payment()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        await ExpireAsync(order.PublicId);

        using var client = await host.SignInAsync(Permissions.Orders.View);

        // Found by the number the customer quotes, which is the whole point.
        var rows = (await ListAsync(client, $"?search={order.OrderNumber}"))
            .GetProperty("rows")
            .EnumerateArray()
            .ToList();

        var row = Assert.Single(rows);
        Assert.Equal(nameof(OrderStatus.Expired), row.GetProperty("status").GetString());

        var detail = await DetailAsync(client, order.PublicId);
        Assert.True(detail.GetProperty("actions").GetProperty("canAcceptLatePayment").GetBoolean());

        // And the action behind it is the one W3-03 already built: the order is paid with a new invoice
        // number, today's date, and its stock re-checked.
        using var approver = await host.SignInAsync(Permissions.Orders.Approve);
        using var accepted = await approver.PostAsJsonAsync(
            new Uri($"/api/admin/orders/bank-transfers/{order.PublicId:D}/accept-late", UriKind.Relative),
            new { note = "Money arrived on the 9th." },
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var after = await DetailAsync(client, order.PublicId);
        Assert.Equal(nameof(OrderStatus.Paid), after.GetProperty("status").GetString());
        Assert.False(after.GetProperty("actions").GetProperty("canAcceptLatePayment").GetBoolean());
    }

    /// <summary>
    /// A paid order does not offer Accept late payment, which is the other half of the flag.
    /// </summary>
    [Fact]
    public async Task A_paid_order_does_not_offer_accept_late_payment()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var approver = await host.SignInAsync(Permissions.Orders.Approve);
        await ApproveAsync(approver, order.PublicId);

        using var client = await host.SignInAsync(Permissions.Orders.View);
        var detail = await DetailAsync(client, order.PublicId);

        Assert.False(detail.GetProperty("actions").GetProperty("canAcceptLatePayment").GetBoolean());
        Assert.True(detail.GetProperty("actions").GetProperty("canStartProcessing").GetBoolean());
    }

    [Fact]
    public async Task An_order_that_does_not_exist_is_a_bare_404()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Orders.View);

        using var response = await client.GetAsync(
            new Uri($"{OrdersUrl}/{Guid.NewGuid():D}", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- the transitions ------------------------------------------------------------------------

    /// <summary>
    /// The whole happy path on the wire: Paid → Processing → Shipped → Delivered, with the tracking link
    /// rendered from the courier's current template.
    /// </summary>
    [Fact]
    public async Task The_fulfilment_path_works_end_to_end_and_renders_the_tracking_link()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var approver = await host.SignInAsync(Permissions.Orders.Approve);
        await ApproveAsync(approver, order.PublicId);

        using var settings = await host.SignInAsync(Permissions.Settings.Edit, Permissions.Orders.View);
        var courierId = await CreateCourierAsync(settings, "xunit-endpoint-carrier");

        using var editor = await host.SignInAsync(
            Permissions.Orders.Edit,
            Permissions.Orders.View);

        var processing = await TransitionAsync(editor, order.PublicId, "start-processing", HttpStatusCode.OK);
        Assert.Equal(nameof(OrderStatus.Processing), processing.GetProperty("status").GetString());

        var shipped = await TransitionAsync(
            editor,
            order.PublicId,
            "mark-shipped",
            HttpStatusCode.OK,
            new { courierId, trackingNumber = "TRACK-ENDPOINT-1" });
        Assert.Equal(nameof(OrderStatus.Shipped), shipped.GetProperty("status").GetString());

        var detail = await DetailAsync(editor, order.PublicId);
        var dispatch = detail.GetProperty("dispatch");

        Assert.Equal(courierId, dispatch.GetProperty("courierId").GetInt32());
        Assert.Equal("xunit-endpoint-carrier", dispatch.GetProperty("courierName").GetString());
        Assert.Equal("TRACK-ENDPOINT-1", dispatch.GetProperty("trackingNumber").GetString());

        // Doc 08 §3.5: the link is rendered from the courier's *current* template plus the order's number,
        // and the order never stores a built URL.
        Assert.Equal(
            "https://track.example.test/TRACK-ENDPOINT-1",
            dispatch.GetProperty("trackingUrl").GetString());

        var delivered = await TransitionAsync(editor, order.PublicId, "mark-delivered", HttpStatusCode.OK);
        Assert.Equal(nameof(OrderStatus.Delivered), delivered.GetProperty("status").GetString());
    }

    /// <summary>
    /// <b>A renamed courier keeps the order's snapshotted name and still carries its id.</b>
    /// </summary>
    /// <remarks>
    /// Doc 11 P9: the courier's name is copied into the order, so a rename must not rewrite a past
    /// dispatch note. The <i>id</i> travels beside it because the correction form selects by it — the
    /// first version matched on the name against the live list, which came up empty the moment somebody
    /// renamed a carrier and left the form unselected on an order that plainly had a courier.
    /// </remarks>
    [Fact]
    public async Task A_renamed_courier_keeps_the_orders_snapshot_and_still_carries_its_id()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var approver = await host.SignInAsync(Permissions.Orders.Approve);
        await ApproveAsync(approver, order.PublicId);

        using var settings = await host.SignInAsync(Permissions.Settings.Edit, Permissions.Orders.View);
        var courierId = await CreateCourierAsync(settings, $"xunit-before-{Guid.NewGuid():N}"[..24]);

        using var editor = await host.SignInAsync(Permissions.Orders.Edit, Permissions.Orders.View);
        await TransitionAsync(editor, order.PublicId, "start-processing", HttpStatusCode.OK);
        await TransitionAsync(
            editor,
            order.PublicId,
            "mark-shipped",
            HttpStatusCode.OK,
            new { courierId, trackingNumber = "TRACK-RENAME" });

        var shippedWith = (await DetailAsync(editor, order.PublicId))
            .GetProperty("dispatch")
            .GetProperty("courierName")
            .GetString();

        using var renamed = await settings.PutAsJsonAsync(
            new Uri($"{CouriersUrl}/{courierId}", UriKind.Relative),
            new
            {
                name = $"xunit-after-{Guid.NewGuid():N}"[..24],
                trackingUrlTemplate = "https://track.example.test/{trackingNumber}",
                phone = (string?)null,
                isActive = true,
                sortOrder = 0,
            },
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var dispatch = (await DetailAsync(editor, order.PublicId)).GetProperty("dispatch");

        // The snapshot is unchanged, which is what a past dispatch note has to do.
        Assert.Equal(shippedWith, dispatch.GetProperty("courierName").GetString());

        // And the id still points at the row, so the correction form comes up with the right carrier
        // selected rather than blank.
        Assert.Equal(courierId, dispatch.GetProperty("courierId").GetInt32());

        // The link is rendered from the courier's *current* template either way (doc 08 §3.5).
        Assert.Equal(
            "https://track.example.test/TRACK-RENAME",
            dispatch.GetProperty("trackingUrl").GetString());
    }

    /// <summary>
    /// A dispatch with no courier or no tracking number is a 400 naming the field.
    /// </summary>
    [Fact]
    public async Task A_dispatch_without_a_courier_or_a_tracking_number_names_the_field()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var approver = await host.SignInAsync(Permissions.Orders.Approve);
        await ApproveAsync(approver, order.PublicId);

        using var editor = await host.SignInAsync(Permissions.Orders.Edit, Permissions.Orders.View);
        await TransitionAsync(editor, order.PublicId, "start-processing", HttpStatusCode.OK);

        var noCourier = await TransitionAsync(
            editor,
            order.PublicId,
            "mark-shipped",
            HttpStatusCode.BadRequest,
            new { trackingNumber = "TRACK-X" });
        Assert.True(noCourier.GetProperty("errors").TryGetProperty("courierId", out _));

        var noTracking = await TransitionAsync(
            editor,
            order.PublicId,
            "mark-shipped",
            HttpStatusCode.BadRequest,
            new { courierId = 1 });
        Assert.True(noTracking.GetProperty("errors").TryGetProperty("trackingNumber", out _));
    }

    /// <summary>
    /// A transition from the wrong status is a 409 carrying the code and the order's current status.
    /// </summary>
    [Fact]
    public async Task A_refused_transition_carries_the_code_and_the_current_status()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var editor = await host.SignInAsync(Permissions.Orders.Edit, Permissions.Orders.View);

        // Still awaiting payment, so it cannot be processed.
        var refused = await TransitionAsync(
            editor,
            order.PublicId,
            "start-processing",
            HttpStatusCode.Conflict);

        Assert.Equal("order_wrong_status", refused.GetProperty("code").GetString());
        Assert.Equal(nameof(OrderStatus.PendingPayment), refused.GetProperty("orderStatus").GetString());
        Assert.Equal("Awaiting payment", refused.GetProperty("orderStatusLabel").GetString());
    }

    /// <summary>
    /// <b>A request to the internal-note route that leaves the field out is refused, not treated as a
    /// clear.</b>
    /// </summary>
    /// <remarks>
    /// This is the distinction four defects in this project came from collapsing — W1-12's category
    /// images, W1-03's mapper, GLB-10's audit snapshot. The fixture holds <b>real</b> data in the column
    /// first, which is the other half of that lesson: those defects were missed by 1,571 passing tests
    /// because every fixture set the columns null.
    /// </remarks>
    [Fact]
    public async Task A_body_with_no_internal_note_does_not_clear_the_note()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var editor = await host.SignInAsync(Permissions.Orders.Edit, Permissions.Orders.View);

        // Real data in the column before the save that must not touch it.
        await TransitionAsync(
            editor,
            order.PublicId,
            "internal-note",
            HttpStatusCode.OK,
            new { internalNote = "Customer rang on the 8th; sending a new slip." });

        Assert.Equal(
            "Customer rang on the 8th; sending a new slip.",
            (await DetailAsync(editor, order.PublicId)).GetProperty("internalNote").GetString());

        // A body that does not mention it is a 400, which is the only answer that cannot lose the note.
        var refused = await TransitionAsync(
            editor,
            order.PublicId,
            "internal-note",
            HttpStatusCode.BadRequest,
            new { note = "something else" });

        Assert.True(refused.GetProperty("errors").TryGetProperty("internalNote", out _));

        Assert.Equal(
            "Customer rang on the 8th; sending a new slip.",
            (await DetailAsync(editor, order.PublicId)).GetProperty("internalNote").GetString());

        // An explicit null clears it, which is how a staff member says so.
        await TransitionAsync(
            editor,
            order.PublicId,
            "internal-note",
            HttpStatusCode.OK,
            new { internalNote = (string?)null });

        Assert.Equal(
            JsonValueKind.Null,
            (await DetailAsync(editor, order.PublicId)).GetProperty("internalNote").ValueKind);
    }

    // ---- the invoice ----------------------------------------------------------------------------

    /// <summary>
    /// <b>An unset legal detail prints nothing — the invoice does not invent one.</b>
    /// </summary>
    /// <remarks>
    /// This project has twice shipped invented client data as a Major finding. An invoice is a legal
    /// document, so the four <c>Store.*</c> keys are seeded null and come back null, and
    /// <c>seller.isComplete</c> is what lets the screen warn before printing.
    /// </remarks>
    [Fact]
    public async Task An_invoice_prints_nothing_for_an_unset_legal_detail()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var approver = await host.SignInAsync(Permissions.Orders.Approve);
        await ApproveAsync(approver, order.PublicId);

        await ClearStoreLegalDetailsAsync();

        using var client = await host.SignInAsync(Permissions.Orders.View);
        var invoice = await InvoiceAsync(client, order.PublicId);

        var seller = invoice.GetProperty("seller");
        Assert.Equal(JsonValueKind.Null, seller.GetProperty("legalName").ValueKind);
        Assert.Equal(JsonValueKind.Null, seller.GetProperty("brNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, seller.GetProperty("vatNumber").ValueKind);
        Assert.Empty(seller.GetProperty("addressLines").EnumerateArray());
        Assert.False(seller.GetProperty("isComplete").GetBoolean());

        // The document itself still renders: a staff member may want a packing copy today and the legal
        // details next week.
        Assert.StartsWith("INV-", invoice.GetProperty("invoiceNumber").GetString()!, StringComparison.Ordinal);
        Assert.Equal(order.OrderNumber, invoice.GetProperty("orderNumber").GetString());
        Assert.Equal("Bank transfer", invoice.GetProperty("paymentMethodLabel").GetString());
        Assert.Single(invoice.GetProperty("lines").EnumerateArray().ToList());
    }

    /// <summary>
    /// Once the client fills them in, the invoice prints them — with the address split into lines.
    /// </summary>
    [Fact]
    public async Task An_invoice_prints_the_legal_details_the_client_filled_in()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var approver = await host.SignInAsync(Permissions.Orders.Approve);
        await ApproveAsync(approver, order.PublicId);

        try
        {
            await SetStoreLegalDetailsAsync(
                "A Registered Company (Pvt) Ltd",
                "Line one\nLine two\n\nLine three",
                "BR-0000",
                "VAT-0000");

            using var client = await host.SignInAsync(Permissions.Orders.View);
            var seller = (await InvoiceAsync(client, order.PublicId)).GetProperty("seller");

            Assert.Equal("A Registered Company (Pvt) Ltd", seller.GetProperty("legalName").GetString());
            Assert.Equal("BR-0000", seller.GetProperty("brNumber").GetString());
            Assert.Equal("VAT-0000", seller.GetProperty("vatNumber").GetString());
            Assert.True(seller.GetProperty("isComplete").GetBoolean());

            // Blank lines are dropped: a trailing newline in a textarea is the normal way a value ends and
            // an empty line on an invoice reads as a missing field.
            Assert.Equal(
                ["Line one", "Line two", "Line three"],
                seller.GetProperty("addressLines").EnumerateArray().Select(line => line.GetString()).ToList());
        }
        finally
        {
            await ClearStoreLegalDetailsAsync();
        }
    }

    /// <summary>
    /// <b>An unpaid order's invoice is the same 404 as an order that does not exist.</b>
    /// </summary>
    /// <remarks>
    /// Doc 11 P9: invoice printing is available once the order is Paid, because that is when the gapless
    /// number is issued. The two cases are one answer because <c>Orders.PublicId</c> never expires (G-41)
    /// and the response must not become an oracle about which orders are paid.
    /// </remarks>
    [Fact]
    public async Task An_unpaid_orders_invoice_is_a_404()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var client = await host.SignInAsync(Permissions.Orders.View);

        using var unpaid = await client.GetAsync(
            new Uri($"{OrderUrl(order.PublicId)}/invoice", UriKind.Relative),
            Token);
        Assert.Equal(HttpStatusCode.NotFound, unpaid.StatusCode);

        using var unknown = await client.GetAsync(
            new Uri($"{OrdersUrl}/{Guid.NewGuid():D}/invoice", UriKind.Relative),
            Token);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    // ---- the couriers ---------------------------------------------------------------------------

    /// <summary>
    /// <b>From an empty list, a courier is added, edited and deactivated.</b>
    /// </summary>
    /// <remarks>
    /// Issue #33 seeds the shop's starting couriers (owner decision 2026-10-21; see
    /// <c>DeliveryReturnsSeedTests</c>), so this deletes every courier first: the screen must still work for
    /// an owner who removed them all.
    /// </remarks>
    [Fact]
    public async Task A_courier_is_added_edited_and_deactivated_from_an_empty_list()
    {
        await using var host = await StartAsync();
        await DeleteEveryCourierAsync();

        using var client = await host.SignInAsync(Permissions.Settings.Edit, Permissions.Orders.View);

        using var empty = await client.GetAsync(new Uri(CouriersUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Empty((await empty.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).EnumerateArray());

        var courierId = await CreateCourierAsync(client, "xunit-added");

        using var listed = await client.GetAsync(new Uri(CouriersUrl, UriKind.Relative), Token);
        var row = Assert.Single(
            (await listed.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).EnumerateArray().ToList());

        Assert.Equal(courierId, row.GetProperty("id").GetInt32());
        Assert.Equal("xunit-added", row.GetProperty("name").GetString());
        Assert.Equal(
            "https://track.example.test/{trackingNumber}",
            row.GetProperty("trackingUrlTemplate").GetString());
        Assert.True(row.GetProperty("isActive").GetBoolean());
        Assert.Equal(0, row.GetProperty("ordersShipped").GetInt32());

        // Deactivating, which is the only way a courier leaves the picker: there is no delete, because
        // deleting the row would turn every live parcel's tracking link into plain text.
        using var deactivated = await client.PutAsJsonAsync(
            new Uri($"{CouriersUrl}/{courierId}", UriKind.Relative),
            new
            {
                name = "xunit-added",
                trackingUrlTemplate = (string?)null,
                phone = "011 2345678",
                isActive = false,
                sortOrder = 3,
            },
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);

        var saved = await deactivated.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
        Assert.False(saved.GetProperty("isActive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("trackingUrlTemplate").ValueKind);
        Assert.Equal("011 2345678", saved.GetProperty("phone").GetString());
        Assert.Equal(3, saved.GetProperty("sortOrder").GetInt32());
    }

    /// <summary>
    /// A template with no placeholder, or one that is not https, is a 400 naming the field.
    /// </summary>
    [Fact]
    public async Task A_tracking_address_is_checked_before_it_reaches_the_constraint()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        foreach (var template in new[]
        {
            "https://example.test/track",
            "http://example.test/{trackingNumber}",
        })
        {
            using var response = await client.PostAsJsonAsync(
                new Uri(CouriersUrl, UriKind.Relative),
                new
                {
                    name = $"xunit-bad-{Guid.NewGuid():N}"[..20],
                    trackingUrlTemplate = template,
                    phone = (string?)null,
                    isActive = true,
                    sortOrder = 0,
                },
                JsonOptions,
                Token);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
            Assert.True(body.GetProperty("errors").TryGetProperty("trackingUrlTemplate", out _));
        }
    }

    /// <summary>
    /// <b>Reordering is the "Order in the list" number</b> (ADM-22 verification, 2026-10-21): the list a staff
    /// member picks from is active couriers first, then by that number, and changing it moves the courier.
    /// </summary>
    [Fact]
    public async Task Couriers_are_listed_active_first_then_by_their_order_in_the_list_and_an_edit_reorders_them()
    {
        await using var host = await StartAsync();
        await DeleteEveryCourierAsync();

        using var client = await host.SignInAsync(Permissions.Settings.Edit, Permissions.Orders.View);

        var later = await CreateCourierAsync(client, "xunit-order-later", sortOrder: 5);
        var first = await CreateCourierAsync(client, "xunit-order-first", sortOrder: 1);
        var retired = await CreateCourierAsync(client, "xunit-order-retired", sortOrder: 0, isActive: false);

        Assert.Equal([first, later, retired], await CourierIdsAsync(client));

        // Move "later" to the top by giving it a lower number than "first".
        using (var moved = await client.PutAsJsonAsync(
            new Uri($"{CouriersUrl}/{later}", UriKind.Relative),
            new
            {
                name = "xunit-order-later",
                trackingUrlTemplate = "https://track.example.test/{trackingNumber}",
                phone = (string?)null,
                isActive = true,
                sortOrder = 0,
            },
            JsonOptions,
            Token))
        {
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        }

        Assert.Equal([later, first, retired], await CourierIdsAsync(client));
    }

    /// <summary>
    /// <b>A courier added on the admin screen reaches the customer</b> (ADM-22 verification, 2026-10-21): after a
    /// dispatch, the order page a shopper opens by their link and the <c>OrderShipped</c> email both carry the
    /// courier's name and a link built from its template; an edited template moves the order page's link (doc 08
    /// §3.5 renders from the current template); and switching the courier off keeps that link working while
    /// taking the courier out of the next dispatch.
    /// </summary>
    [Fact]
    public async Task A_courier_added_in_admin_reaches_the_order_page_and_the_shipped_email_and_follows_its_template()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var approver = await host.SignInAsync(Permissions.Orders.Approve);
        await ApproveAsync(approver, order.PublicId);

        using var settings = await host.SignInAsync(Permissions.Settings.Edit, Permissions.Orders.View);
        var name = $"xunit-reach-{Guid.NewGuid():N}"[..24];
        var courierId = await CreateCourierAsync(settings, name);

        using var editor = await host.SignInAsync(Permissions.Orders.Edit, Permissions.Orders.View);
        await TransitionAsync(editor, order.PublicId, "start-processing", HttpStatusCode.OK);
        await TransitionAsync(
            editor,
            order.PublicId,
            "mark-shipped",
            HttpStatusCode.OK,
            new { courierId, trackingNumber = "REACH 42/7" });

        // The customer's order page, opened anonymously by the order's own link.
        var tracking = (await PublicOrderAsync(host, order.PublicId)).GetProperty("tracking");
        Assert.Equal(name, tracking.GetProperty("courierName").GetString());
        Assert.Equal("REACH 42/7", tracking.GetProperty("trackingNumber").GetString());
        Assert.Equal(
            $"https://track.example.test/{Uri.EscapeDataString("REACH 42/7")}",
            tracking.GetProperty("trackingUrl").GetString());

        // The shipped email, queued by the dispatch.
        using (var scope = sqlServer.CreateScope())
        {
            var id = order.PublicId.ToString("D");
            var shipped = await scope.ServiceProvider.GetRequiredService<AppDbContext>().EmailOutbox
                .AsNoTracking()
                .Where(message => message.RelatedEntityId == id
                    && message.TemplateCode == SpencerDenim.Domain.Ops.EmailTemplateCodes.OrderShipped)
                .SingleAsync(Token);

            Assert.Contains(name, shipped.BodyText!, StringComparison.Ordinal);
            Assert.Contains(
                $"https://track.example.test/{Uri.EscapeDataString("REACH 42/7")}",
                shipped.BodyText!,
                StringComparison.Ordinal);
            Assert.DoesNotContain("{trackingNumber}", shipped.BodyText!, StringComparison.Ordinal);
        }

        // Correct the template and switch the courier off in one edit.
        using (var edited = await settings.PutAsJsonAsync(
            new Uri($"{CouriersUrl}/{courierId}", UriKind.Relative),
            new
            {
                name,
                trackingUrlTemplate = "https://track.example.test/v2?no={trackingNumber}",
                phone = (string?)null,
                isActive = false,
                sortOrder = 0,
            },
            JsonOptions,
            Token))
        {
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        }

        var after = (await PublicOrderAsync(host, order.PublicId)).GetProperty("tracking");
        Assert.Equal(name, after.GetProperty("courierName").GetString());
        Assert.Equal(
            $"https://track.example.test/v2?no={Uri.EscapeDataString("REACH 42/7")}",
            after.GetProperty("trackingUrl").GetString());

        // Switched off: a new dispatch with it is refused (409) and the order stays Processing.
        var next = await PlaceOrderAsync(host);
        await ApproveAsync(approver, next.PublicId);
        await TransitionAsync(editor, next.PublicId, "start-processing", HttpStatusCode.OK);

        await TransitionAsync(
            editor,
            next.PublicId,
            "mark-shipped",
            HttpStatusCode.Conflict,
            new { courierId, trackingNumber = "REACH-NEXT" });

        Assert.Equal(
            nameof(OrderStatus.Processing),
            (await DetailAsync(editor, next.PublicId)).GetProperty("status").GetString());
    }

    /// <summary>
    /// <b>A courier body that leaves a field out is refused, not read as null.</b>
    /// </summary>
    /// <remarks>
    /// The same lesson as the internal note, and the fixture again holds real data first: a save that did
    /// not mention a field has silently cleared it four times in this project.
    /// </remarks>
    [Fact]
    public async Task A_courier_body_with_a_missing_field_is_refused()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit, Permissions.Orders.View);

        var courierId = await CreateCourierAsync(client, $"xunit-partial-{Guid.NewGuid():N}"[..24]);

        using var response = await client.PutAsJsonAsync(
            new Uri($"{CouriersUrl}/{courierId}", UriKind.Relative),
            new { name = "xunit-renamed" },
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
        var errors = body.GetProperty("errors");

        Assert.True(errors.TryGetProperty("trackingUrlTemplate", out _));
        Assert.True(errors.TryGetProperty("phone", out _));
        Assert.True(errors.TryGetProperty("isActive", out _));
        Assert.True(errors.TryGetProperty("sortOrder", out _));

        // And the row is untouched, which is what the refusal is for.
        using var listed = await client.GetAsync(new Uri(CouriersUrl, UriKind.Relative), Token);
        var rows = (await listed.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token))
            .EnumerateArray()
            .ToList();

        Assert.Contains(
            rows,
            row => row.GetProperty("id").GetInt32() == courierId
                && row.GetProperty("trackingUrlTemplate").ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Two_couriers_cannot_share_a_name()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        var name = $"xunit-dup-{Guid.NewGuid():N}"[..24];
        await CreateCourierAsync(client, name);

        using var second = await client.PostAsJsonAsync(
            new Uri(CouriersUrl, UriKind.Relative),
            NewCourierBody(name),
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);

        var body = await second.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
        Assert.True(body.GetProperty("errors").TryGetProperty("name", out _));
    }

    [Fact]
    public async Task An_edit_aimed_at_a_courier_that_does_not_exist_is_a_404()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Settings.Edit);

        using var response = await client.PutAsJsonAsync(
            new Uri($"{CouriersUrl}/987654321", UriKind.Relative),
            NewCourierBody("xunit-missing"),
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static string OrderUrl(Guid orderPublicId) =>
        $"{OrdersUrl}/{orderPublicId.ToString("D", CultureInfo.InvariantCulture)}";

    private static object NewCourierBody(string name, short sortOrder = 0, bool isActive = true) => new
    {
        name,
        trackingUrlTemplate = "https://track.example.test/{trackingNumber}",
        phone = (string?)null,
        isActive,
        sortOrder,
    };

    private static async Task<List<int>> CourierIdsAsync(HttpClient client)
    {
        var list = await client.GetFromJsonAsync<JsonElement>(new Uri(CouriersUrl, UriKind.Relative), JsonOptions, Token);

        return list.EnumerateArray().Select(row => row.GetProperty("id").GetInt32()).ToList();
    }

    /// <summary>The public order payload, read by a shopper with no session — what the order page draws.</summary>
    private static async Task<JsonElement> PublicOrderAsync(MediaTestHost host, Guid orderPublicId)
    {
        using var shopper = host.CreateAnonymousClient();
        using var response = await shopper.GetAsync(new Uri($"/api/orders/{orderPublicId:D}", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
    }

    /// <summary>
    /// Doc 11 P10 flow A on the wire: <c>Orders.Edit</c> cancels an unpaid order, with a reason.
    /// </summary>
    /// <remarks>
    /// <b>The reason is required and a blank one is a 400 naming the field.</b> Doc 11 P10 step 1 has the
    /// staff member "enter a reason", cancelling is irreversible for the customer, and the reason is what
    /// is read back to them if they ask — so the screen asking for it is not enough, because the screen is
    /// not the boundary.
    /// </remarks>
    [Fact]
    public async Task Cancelling_an_unpaid_order_needs_a_reason_and_closes_it()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var editor = await host.SignInAsync(Permissions.Orders.Edit, Permissions.Orders.View);

        // Doc 11 P10 step 2's guards, as the screen sees them.
        var offered = await DetailAsync(editor, order.PublicId);
        Assert.True(offered.GetProperty("actions").GetProperty("canCancel").GetBoolean());

        // A body with no reason, a null one and a blank one are all refused, and each names the field
        // rather than answering "malformed JSON" (issue #47's family).
        foreach (var body in new object[]
        {
            new { note = (string?)null },
            new { cancelReason = (string?)null },
            new { cancelReason = "   " },
        })
        {
            var refused = await TransitionAsync(
                editor,
                order.PublicId,
                "cancel",
                HttpStatusCode.BadRequest,
                body);

            Assert.True(refused.GetProperty("errors").TryGetProperty("cancelReason", out _));
        }

        // And nothing was written by any of the three.
        var untouched = await DetailAsync(editor, order.PublicId);
        Assert.Equal("PendingPayment", untouched.GetProperty("status").GetString());
        Assert.Null(untouched.GetProperty("cancelReason").GetString());

        var applied = await TransitionAsync(
            editor,
            order.PublicId,
            "cancel",
            HttpStatusCode.OK,
            new { cancelReason = "The customer telephoned to cancel." });

        Assert.Equal("Cancelled", applied.GetProperty("status").GetString());
        Assert.Equal("Cancelled", applied.GetProperty("statusLabel").GetString());

        var detail = await DetailAsync(editor, order.PublicId);

        Assert.Equal("Cancelled", detail.GetProperty("status").GetString());
        Assert.Equal("Released", detail.GetProperty("stockState").GetString());
        Assert.Equal(
            "The customer telephoned to cancel.",
            detail.GetProperty("cancelReason").GetString());
        Assert.NotNull(detail.GetProperty("closedAtUtc").GetString());

        // The panel is gone, and that is the assertion that matters on this screen: a Cancel form left on
        // a cancelled order is a control that can only ever fail.
        Assert.False(detail.GetProperty("actions").GetProperty("canCancel").GetBoolean());

        // The timeline says who, which is doc 11 P10 step 3's record.
        var closing = detail.GetProperty("history").EnumerateArray()
            .Last(entry => entry.GetProperty("toStatus").GetString() == "Cancelled");

        Assert.Equal("Staff", closing.GetProperty("source").GetString());
        Assert.NotNull(closing.GetProperty("changedByUserEmail").GetString());

        // A second attempt is a 409 carrying the order's current state, not a 400 and not a 500: nothing
        // is wrong with the request, the world moved.
        var again = await TransitionAsync(
            editor,
            order.PublicId,
            "cancel",
            HttpStatusCode.Conflict,
            new { cancelReason = "Again." });

        Assert.Equal("order_not_cancellable", again.GetProperty("code").GetString());
        Assert.Equal("Cancelled", again.GetProperty("orderStatus").GetString());
    }

    /// <summary>
    /// <b>A cancelled order can still take the transfer that arrives the next day, and there is a control
    /// for it.</b> Doc 08 §4.1's <c>Cancelled → Paid</c> edge, end to end through the screens.
    /// </summary>
    /// <remarks>
    /// <b>This is the gap W3-04b opened and did not close.</b> Cancelling closes the order's open bank
    /// payment to <c>Cancelled</c> (doc 08 §4.3), and <c>AdminOrderActions.CanAcceptLatePayment</c> looked
    /// only for <c>Expired</c> or <c>Pending</c> — so the one documented edge out of <c>Cancelled</c> had
    /// no button anywhere and a shop could cancel an order and then be unable to accept the money that
    /// turned up. The money transaction was never the problem: <c>AcceptLateAsync</c> claims on
    /// <c>Status IN (5,6)</c> and already took a <c>Cancelled</c> payment, which W3-03 put in its list
    /// deliberately and marked latent.
    /// <para>
    /// <b>Both ends on one row, which is the point.</b> A test that only asserted the flag would pass
    /// against a transaction that refused, and a test that only pressed the route would pass against a
    /// screen with nothing on it. This asserts the flag, then presses the action it offers, then asserts
    /// the order is paid with an invoice number and its stock taken again.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_cancelled_order_offers_accept_late_payment_and_applies_it()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var editor = await host.SignInAsync(Permissions.Orders.Edit, Permissions.Orders.View);

        await TransitionAsync(
            editor,
            order.PublicId,
            "cancel",
            HttpStatusCode.OK,
            new { cancelReason = "The customer telephoned to cancel." });

        var cancelled = await DetailAsync(editor, order.PublicId);

        Assert.Equal(nameof(OrderStatus.Cancelled), cancelled.GetProperty("status").GetString());

        // The payment this flag has to recognise. It is `Cancelled`, not `Expired` and not `Pending`.
        var closed = cancelled.GetProperty("payments").EnumerateArray().Last();
        Assert.Equal(nameof(PaymentStatus.Cancelled), closed.GetProperty("status").GetString());

        // The screen offers it, which is the half that was missing.
        Assert.True(
            cancelled.GetProperty("actions").GetProperty("canAcceptLatePayment").GetBoolean(),
            "a cancelled bank-transfer order must offer Accept late payment (doc 08 §4.1 Cancelled → Paid)");

        // And the action behind it applies, which is the half that already worked.
        using var approver = await host.SignInAsync(Permissions.Orders.Approve);
        using var accepted = await approver.PostAsJsonAsync(
            new Uri($"/api/admin/orders/bank-transfers/{order.PublicId:D}/accept-late", UriKind.Relative),
            new { note = "The transfer arrived the day after they cancelled." },
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var paid = await DetailAsync(editor, order.PublicId);

        Assert.Equal(nameof(OrderStatus.Paid), paid.GetProperty("status").GetString());
        Assert.Equal(nameof(OrderStockState.Committed), paid.GetProperty("stockState").GetString());
        Assert.NotNull(paid.GetProperty("invoiceNumber").GetString());
        Assert.Null(paid.GetProperty("closedAtUtc").GetString());

        // The flag goes out again once there is nothing left to accept, so the control cannot be pressed
        // twice — the other half of every one of these flags.
        Assert.False(paid.GetProperty("actions").GetProperty("canAcceptLatePayment").GetBoolean());
        Assert.False(paid.GetProperty("actions").GetProperty("canCancel").GetBoolean());
    }

    /// <summary>
    /// <b>A paid order offers no Cancel, and refuses one if asked.</b>
    /// </summary>
    /// <remarks>
    /// Doc 11 P10's table: a paid order is refunded, never cancelled — and <c>Orders.Refund</c> is unbuilt
    /// (W3-05), refunds being manual for v1 and recorded in the order's internal note. So the only honest
    /// screen is one that offers nothing here, and the refusal has to say what to do instead because there
    /// is no button for it.
    /// </remarks>
    [Fact]
    public async Task A_paid_order_offers_no_cancel_and_refuses_one()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var approver = await host.SignInAsync(Permissions.Orders.Approve);
        await ApproveAsync(approver, order.PublicId);

        using var editor = await host.SignInAsync(Permissions.Orders.Edit, Permissions.Orders.View);

        var detail = await DetailAsync(editor, order.PublicId);
        Assert.Equal("Paid", detail.GetProperty("status").GetString());
        Assert.False(detail.GetProperty("actions").GetProperty("canCancel").GetBoolean());

        var refused = await TransitionAsync(
            editor,
            order.PublicId,
            "cancel",
            HttpStatusCode.Conflict,
            new { cancelReason = "They changed their mind." });

        Assert.Equal("order_not_cancellable", refused.GetProperty("code").GetString());
        Assert.Equal("Paid", refused.GetProperty("orderStatus").GetString());
        Assert.Contains(
            "refunded instead",
            refused.GetProperty("detail").GetString()!,
            StringComparison.OrdinalIgnoreCase);

        // Still paid, still committed: a refusal must change nothing, and the thing that must not change
        // here is stock on an order whose money the shop has.
        var after = await DetailAsync(editor, order.PublicId);
        Assert.Equal("Paid", after.GetProperty("status").GetString());
        Assert.Equal("Committed", after.GetProperty("stockState").GetString());
    }

    /// <summary>Every admin route this file guards, with a method that reaches it.</summary>
    private static IEnumerable<(HttpMethod Method, string Path)> AdminRoutes()
    {
        var order = Guid.Empty;

        yield return (HttpMethod.Get, OrdersUrl);
        yield return (HttpMethod.Get, OrderUrl(order));
        yield return (HttpMethod.Get, $"{OrderUrl(order)}/invoice");
        yield return (HttpMethod.Get, CouriersUrl);
        yield return (HttpMethod.Post, CouriersUrl);
        yield return (HttpMethod.Put, $"{CouriersUrl}/1");

        foreach (var segment in new[]
        {
            "start-processing",
            "mark-shipped",
            "ready-for-collection",
            "mark-delivered",
            "dispatch",
            "internal-note",
            "cancel",
        })
        {
            yield return (HttpMethod.Post, $"{OrderUrl(order)}/{segment}");
        }
    }

    private async Task<MediaTestHost> StartAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        // MediaTestHost rather than a bare API host, because signing a staff user in and creating an
        // anonymous shopper on the *same* host is what AdminBankTransferEndpointTests established: two
        // hosts over one database would silently not share anything cached per host, effective permissions
        // above all, and a permission test would then prove nothing about the shop.
        return MediaTestHost.Create(sqlServer.ConnectionString);
    }

    private static async Task<JsonElement> ListAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync(new Uri($"{OrdersUrl}{query}", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
    }

    private static async Task<JsonElement> DetailAsync(HttpClient client, Guid orderPublicId)
    {
        using var response = await client.GetAsync(new Uri(OrderUrl(orderPublicId), UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
    }

    private static async Task<JsonElement> InvoiceAsync(HttpClient client, Guid orderPublicId)
    {
        using var response = await client.GetAsync(
            new Uri($"{OrderUrl(orderPublicId)}/invoice", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
    }

    private static async Task<JsonElement> TransitionAsync(
        HttpClient client,
        Guid orderPublicId,
        string segment,
        HttpStatusCode expected,
        object? body = null)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri($"{OrderUrl(orderPublicId)}/{segment}", UriKind.Relative),
            body ?? new { note = (string?)null },
            JsonOptions,
            Token);

        Assert.Equal(expected, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
    }

    private static async Task ApproveAsync(HttpClient approver, Guid orderPublicId)
    {
        using var response = await approver.PostAsJsonAsync(
            new Uri($"/api/admin/orders/bank-transfers/{orderPublicId:D}/approve", UriKind.Relative),
            new { note = "Paid, for the endpoint fixture." },
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<int> CreateCourierAsync(
        HttpClient client,
        string name,
        short sortOrder = 0,
        bool isActive = true)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri(CouriersUrl, UriKind.Relative),
            NewCourierBody(name, sortOrder, isActive),
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        return body.GetProperty("id").GetInt32();
    }

    /// <summary>
    /// Expires one order through the real store, with "now" past its deadline.
    /// </summary>
    /// <remarks>
    /// The clock is moved rather than the deadline, because the deadline is the thing under test —
    /// <c>BankTransferTestBase.ExpireAsync</c>'s own argument. 49 hours is one past the 48 checkout gives
    /// a bank transfer.
    /// </remarks>
    private async Task ExpireAsync(Guid orderPublicId)
    {
        using var scope = sqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var orderId = await db.Orders
            .AsNoTracking()
            .Where(order => order.PublicId == orderPublicId)
            .Select(order => order.Id)
            .SingleAsync(Token);

        Assert.True(
            await scope.ServiceProvider
                .GetRequiredService<SpencerDenim.Application.Sales.Payments.Abstractions.IOrderExpiryStore>()
                .ExpireAsync(orderId, DateTime.UtcNow.AddHours(49), Token));
    }

    /// <summary>
    /// Removes every courier, so a test about "none configured" is about that and not about a neighbour.
    /// </summary>
    /// <remarks>
    /// <b>It also removes the couriers seeded since issue #33</b> from the shared database, which is harmless:
    /// <c>Seed.CouriersVersion</c> records that they were offered, so they are not re-created, and no other test
    /// relies on them (the seed's own tests run on throwaway databases). The orders that point at one have their <c>CourierId</c> nulled first, because the FK
    /// would otherwise refuse; the <c>CourierName</c> snapshot, which is what a dispatch note shows,
    /// stays.
    /// </remarks>
    private async Task DeleteEveryCourierAsync()
    {
        using var scope = sqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Orders
            .Where(order => order.CourierId != null)
            .ExecuteUpdateAsync(order => order.SetProperty(row => row.CourierId, (int?)null), Token);

        await db.Couriers.ExecuteDeleteAsync(Token);
    }

    private async Task SetStoreLegalDetailsAsync(
        string? legalName,
        string? address,
        string? brNumber,
        string? vatNumber)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [SettingKeys.Store.LegalName] = legalName,
            [SettingKeys.Store.Address] = address,
            [SettingKeys.Store.BRNumber] = brNumber,
            [SettingKeys.Store.VatNumber] = vatNumber,
        };

        using var scope = sqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var (key, value) in values)
        {
            await db.Settings
                .Where(setting => setting.Key == key)
                .ExecuteUpdateAsync(setting => setting.SetProperty(row => row.Value, value), Token);
        }
    }

    /// <summary>
    /// Puts the four <c>Store.*</c> legal keys back to null, which is what the seeder writes.
    /// </summary>
    /// <remarks>
    /// Every test that sets them must call this in a <c>finally</c>: the assembly shares one database, so
    /// a left-behind registered address is a neighbour's fixture — and the neighbour in question is the
    /// test that asserts an unset key prints nothing.
    /// </remarks>
    private Task ClearStoreLegalDetailsAsync() =>
        SetStoreLegalDetailsAsync(null, null, null, null);

    /// <summary>
    /// Places one real order through the public endpoints a shopper uses.
    /// </summary>
    /// <remarks>
    /// <b>Not inserted by hand.</b> Every invariant in this file depends on the order, its lines and its
    /// reservation having been written by the real placement path; an order whose status was set by a
    /// fixture would let a transition "move" an order that had never been paid.
    /// </remarks>
    private async Task<PlacedOrder> PlaceOrderAsync(MediaTestHost host)
    {
        using var shopper = host.CreateAnonymousClient();

        var variantId = await InStockVariantIdAsync();

        using var added = await shopper.PostAsJsonAsync(
            new Uri("/api/cart/items", UriKind.Relative),
            new { variantId, quantity = 1 },
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        var (districtId, deliveryMethodId) = await ColomboStandardAsync();

        using var quote = await shopper.GetAsync(
            new Uri(
                $"/api/checkout?districtId={districtId}&deliveryMethodId={deliveryMethodId}",
                UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.OK, quote.StatusCode);
        var session = await quote.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        using var placed = await shopper.PostAsJsonAsync(
            new Uri("/api/checkout/orders", UriKind.Relative),
            new
            {
                checkoutKey = session.GetProperty("checkoutKey").GetGuid(),
                firstName = "Nimali",
                lastName = "Perera",
                email = "nimali@example.com",
                phone = "0771234567",
                deliveryMethodId,
                districtId,
                paymentMethodCode = PaymentProviderCodes.BankTransfer,
                expectedGrandTotal = session.GetProperty("totals").GetProperty("grandTotal").GetDecimal(),
                address = new
                {
                    recipientName = "Nimali Perera",
                    phone = "0771234567",
                    line1 = "14 Galle Road",
                    city = "Kollupitiya",
                },
            },
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);

        var body = await placed.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        return new PlacedOrder(
            body.GetProperty("publicId").GetGuid(),
            body.GetProperty("orderNumber").GetString()!,
            body.GetProperty("grandTotal").GetDecimal());
    }

    /// <summary>
    /// How far down the descending list to start.
    /// </summary>
    /// <remarks>
    /// <c>AdminBankTransferEndpointTests</c> records in full what taking the lowest ids cost: four
    /// <c>CartStaleItemTests</c> red in a full run and green alone, because an order is never deleted
    /// (doc 08 §1.6) and holds its reservation for ever. That suite starts twelve down; this one starts
    /// further again so the two cannot meet either.
    /// </remarks>
    private const int VariantBandOffset = 24;

    private async Task<int> InStockVariantIdAsync()
    {
        using var scope = sqlServer.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProductVariants
            .AsNoTracking()
            .Where(variant => variant.IsActive && variant.Product!.IsPublished)
            .Where(variant =>
                variant.Product!.Department!.IsActive && variant.Product!.PrimaryCategory!.IsActive)
            .Where(variant => variant.StockLevel != null && variant.StockLevel.Available > 2)
            .OrderByDescending(variant => variant.Id)
            .Skip(VariantBandOffset)
            .Select(variant => variant.Id)
            .FirstAsync(Token);
    }

    private async Task<(int DistrictId, int DeliveryMethodId)> ColomboStandardAsync()
    {
        using var scope = sqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var districtId = await db.Districts
            .AsNoTracking()
            .Where(district => district.Name == "Colombo")
            .Select(district => district.Id)
            .SingleAsync(Token);

        var methodId = await db.DeliveryMethods
            .AsNoTracking()
            .Where(method => method.Code == DeliveryMethod.StandardCode)
            .Select(method => method.Id)
            .SingleAsync(Token);

        return (districtId, methodId);
    }

    private sealed record PlacedOrder(Guid PublicId, string OrderNumber, decimal GrandTotal);
}
