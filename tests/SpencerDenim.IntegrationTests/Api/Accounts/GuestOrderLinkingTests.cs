using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Identity.Customers;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.IntegrationTests.Sales;

namespace SpencerDenim.IntegrationTests.Api.Accounts;

/// <summary>
/// Doc 11 P11 step A.4 and doc 08 §3.1 <b>D5</b>: confirming an address links the guest orders placed
/// with it, and <b>only</b> those.
/// </summary>
/// <remarks>
/// <b>D5 is one SQL statement, and the whole value of this file is in the orders it proves are
/// untouched.</b> A test that confirmed an address and counted "three orders linked" would pass against a
/// statement that linked every unlinked order in the table. So every case here seeds decoys — another
/// customer's order, an order that already belongs to somebody, an order under a different address — and
/// asserts their owners afterwards.
/// <para>
/// <b>The confirmation link is the real one, read out of the real queued email.</b> See
/// <c>CustomerAccountTestBase.ReadLinkTokenAsync</c>: a test that minted its own token would pass with a
/// composer that wrote a broken link.
/// </para>
/// </remarks>
public sealed class GuestOrderLinkingTests(SqlServerDatabaseFixture sqlServer)
    : CustomerAccountTestBase(sqlServer)
{
    private const string ConfirmUrl = "/api/account/confirm-email";

    /// <summary>
    /// Doc 11 P11's own exception: "Customer registers with the email used for 3 guest orders → after
    /// confirming, all 3 appear in My account."
    /// </summary>
    [Fact]
    public async Task Confirming_links_the_guest_orders_under_that_address_and_nothing_else()
    {
        await ReadyAsync();

        var reference = await ReferenceAsync();

        var mine = UniqueEmail("d5-mine");
        var someoneElse = UniqueEmail("d5-other");

        // Three guest orders under the address that is about to be confirmed.
        var ownedBefore = new List<int>
        {
            await InsertGuestOrderAsync(reference, mine),
            await InsertGuestOrderAsync(reference, mine),
            await InsertGuestOrderAsync(reference, mine),
        };

        // A decoy under a different address, with no owner. D5's predicate keys on the address, so this
        // one must be untouched — and a statement that forgot the address clause would take it.
        var otherAddress = await InsertGuestOrderAsync(reference, someoneElse);

        // A decoy under the *same* address that already belongs to somebody. D5's
        // `CustomerUserId IS NULL` clause is what protects it, and doc 11 P11's next exception is the
        // same rule from the customer's side: "orders placed under the old email stay linked (they
        // already have the account ID)".
        const int strangerUserId = 1;
        var alreadyOwned = await InsertGuestOrderAsync(reference, mine, customerUserId: strangerUserId);

        await using var factory = CreateHost();
        using var client = CreateClient(factory);

        await RegisterAsync(client, mine);
        var token = await ReadLinkTokenAsync(factory, Templates.Confirmation, mine);
        Assert.NotNull(token);

        using var response = await client.PostAsJsonAsync(ConfirmUrl, new { token }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        var linked = body.GetProperty("linkedOrderNumbers").EnumerateArray()
            .Select(element => element.GetString()!)
            .ToList();

        var customer = await ReadCustomerAsync(factory, mine);
        Assert.NotNull(customer);
        Assert.True(customer.EmailConfirmed);

        // Exactly the three, and the response names them so the shopper can see it worked.
        Assert.Equal(3, linked.Count);

        foreach (var orderId in ownedBefore)
        {
            Assert.Equal(customer.Id, await ReadOwnerAsync(orderId));
        }

        // The two decoys are exactly as they were.
        Assert.Null(await ReadOwnerAsync(otherAddress));
        Assert.Equal(strangerUserId, await ReadOwnerAsync(alreadyOwned));
    }

    /// <summary>
    /// Doc 08 §3.1 D5: "The linked order IDs are written to AuditLogs (Action <c>LinkGuestOrders</c>)."
    /// </summary>
    /// <remarks>
    /// <b>The ids in the row are compared against the ids that actually moved</b>, not merely counted. An
    /// audit row naming three orders when two moved is a false record, and a count assertion would not
    /// see it — which is the shape of the thirteen tests that could not fail.
    /// </remarks>
    [Fact]
    public async Task The_linked_order_ids_are_written_to_the_audit_log()
    {
        await ReadyAsync();

        var reference = await ReferenceAsync();
        var email = UniqueEmail("d5-audit");

        var first = await InsertGuestOrderAsync(reference, email);
        var second = await InsertGuestOrderAsync(reference, email);

        await using var factory = CreateHost();
        using var client = CreateClient(factory);

        await RegisterAsync(client, email);
        var token = await ReadLinkTokenAsync(factory, Templates.Confirmation, email);

        using var response = await client.PostAsJsonAsync(ConfirmUrl, new { token }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var customer = await ReadCustomerAsync(factory, email);
        Assert.NotNull(customer);

        var entry = await DbContext.AuditLogs
            .AsNoTracking()
            .Where(row => row.Action == AuditActions.LinkGuestOrders && row.UserId == customer.Id)
            .OrderByDescending(row => row.Id)
            .Select(row => row.AfterJson)
            .FirstOrDefaultAsync(Token);

        Assert.NotNull(entry);

        using var json = JsonDocument.Parse(entry);
        var ids = json.RootElement.GetProperty("orderIds").EnumerateArray()
            .Select(element => element.GetInt32())
            .Order()
            .ToList();

        Assert.Equal<IEnumerable<int>>([first, second], ids);

        // The numbers are there too, so a staff member reading the trail can type one into the admin
        // order search without a second lookup.
        Assert.Equal(2, json.RootElement.GetProperty("orderNumbers").GetArrayLength());
    }

    /// <summary>
    /// A shopper who checked out with one casing and registered with another is one person, and their
    /// orders link.
    /// </summary>
    /// <remarks>
    /// <b>What this proves, and what it does not.</b> It proves the behaviour: a guest order placed as
    /// <c>NIMAL.PERERA@…</c> links to an account registered as <c>nimal.perera@…</c>. It does <b>not</b>
    /// prove that D5 reads <c>EmailNormalized</c> rather than <c>Email</c> — the mutation that swaps the
    /// two columns leaves this test green, and that is not a weakness in the test but a fact about the
    /// database: the collation is <c>Latin1_General_100_CI_AS_SC</c> (<c>SqlServerDatabaseFixture</c>,
    /// doc 08 §1.1), so <c>=</c> is case-insensitive and the two columns answer identically for any value
    /// that differs only in case. Checked by running that mutation: 14/14 still passed.
    /// <para>
    /// <b>The column choice is still right and is still doc 08 §3.1 D5's own</b>, because it does not
    /// depend on a collation nobody will remember is case-insensitive: <c>EmailNormalized</c> is upper
    /// case by construction, so the comparison is correct under any collation the database might later be
    /// restored with. That is an argument, not a test, and this remark is where it is written down rather
    /// than claimed as coverage.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_differently_cased_address_is_the_same_address()
    {
        await ReadyAsync();

        var reference = await ReferenceAsync();
        var lower = UniqueEmail("d5-case");
        var mixed = char.ToUpperInvariant(lower[0]) + lower[1..].ToUpperInvariant();

        // The guest order was placed with the shouted form; the account is registered with the quiet one.
        var orderId = await InsertGuestOrderAsync(reference, mixed);

        await using var factory = CreateHost();
        using var client = CreateClient(factory);

        await RegisterAsync(client, lower);
        var token = await ReadLinkTokenAsync(factory, Templates.Confirmation, lower);

        using var response = await client.PostAsJsonAsync(ConfirmUrl, new { token }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var customer = await ReadCustomerAsync(factory, lower);
        Assert.NotNull(customer);
        Assert.Equal(customer.Id, await ReadOwnerAsync(orderId));
    }

    /// <summary>
    /// Doc 11 P11's exception: "Customer never confirms → can sign in but guest orders are not linked."
    /// </summary>
    /// <remarks>
    /// <b>This is the test that proves confirmation is the gate</b> rather than a formality, and it is the
    /// negative half of every other test in this file. Without it, a change that linked orders at
    /// <i>registration</i> would make every positive test here still pass while handing a stranger's
    /// orders to anybody who could type their address.
    /// </remarks>
    [Fact]
    public async Task Signing_in_without_confirming_links_nothing()
    {
        await ReadyAsync();

        var reference = await ReferenceAsync();
        var email = UniqueEmail("d5-unconfirmed");
        var orderId = await InsertGuestOrderAsync(reference, email);

        await using var factory = CreateHost();
        using var client = CreateClient(factory);

        await RegisterAsync(client, email);
        await SignInAsync(client, email);

        Assert.Null(await ReadOwnerAsync(orderId));

        using var orders = await client.GetAsync(new Uri("/api/account/orders", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, orders.StatusCode);

        var body = await orders.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
    }

    /// <summary>
    /// The link is single-use: the second redemption is refused and nothing happens twice.
    /// </summary>
    /// <remarks>
    /// <b>The audit row is counted, not just the reply.</b> A second confirmation that answered 400 while
    /// still writing a second <c>LinkGuestOrders</c> row would be a trail that said an order was linked
    /// twice, and a status-code assertion would miss it.
    /// </remarks>
    [Fact]
    public async Task A_confirmation_link_works_once()
    {
        await ReadyAsync();

        var reference = await ReferenceAsync();
        var email = UniqueEmail("d5-once");
        await InsertGuestOrderAsync(reference, email);

        await using var factory = CreateHost();
        using var client = CreateClient(factory);

        await RegisterAsync(client, email);
        var token = await ReadLinkTokenAsync(factory, Templates.Confirmation, email);

        using var first = await client.PostAsJsonAsync(ConfirmUrl, new { token }, Token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await client.PostAsJsonAsync(ConfirmUrl, new { token }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);

        var body = await second.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal("invalid_link", body.GetProperty("code").GetString());

        var customer = await ReadCustomerAsync(factory, email);
        Assert.NotNull(customer);

        var auditRows = await DbContext.AuditLogs
            .AsNoTracking()
            .CountAsync(
                row => row.Action == AuditActions.LinkGuestOrders && row.UserId == customer.Id,
                Token);

        Assert.Equal(1, auditRows);

        // And the token row is gone, which is what "single-use" means in the database rather than in a
        // comment.
        Assert.Equal(
            0,
            await CountTokensAsync(factory, customer.Id, CustomerAccountTokens.EmailConfirmationName));
    }

    /// <summary>A token that is not one of ours, and one that names a user who has no pending link.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("rubbish")]
    [InlineData("1.rubbish")]
    [InlineData("0.rubbish")]
    [InlineData("-1.rubbish")]
    [InlineData("99999999.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task A_token_that_is_not_ours_is_one_opaque_refusal(string token)
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);

        using var response = await client.PostAsJsonAsync(ConfirmUrl, new { token }, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A confirmation link is time-boxed, and an expired one is refused and removed.
    /// </summary>
    /// <remarks>
    /// <b>The window is configured to zero hours rather than waited out.</b> The default is seven days
    /// (<c>CustomerSecurityOptions.EmailConfirmationTokenHours</c>), which no test can wait for; the
    /// options validator refuses zero, so the clock is moved instead — the token is minted with a
    /// one-hour window and the stored expiry is then rewritten into the past, which is the one thing a
    /// test can do that a shopper's calendar does for real.
    /// </remarks>
    [Fact]
    public async Task An_expired_confirmation_link_is_refused_and_removed()
    {
        await ReadyAsync();

        var email = UniqueEmail("d5-expired");

        await using var factory = CreateHost(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CustomerSecurity:EmailConfirmationTokenHours"] = "1",
        });

        using var client = CreateClient(factory);
        await RegisterAsync(client, email);

        var token = await ReadLinkTokenAsync(factory, Templates.Confirmation, email);
        Assert.NotNull(token);

        var customer = await ReadCustomerAsync(factory, email);
        Assert.NotNull(customer);

        await ExpireTokenAsync(customer.Id, CustomerAccountTokens.EmailConfirmationName);

        using var response = await client.PostAsJsonAsync(ConfirmUrl, new { token }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var after = await ReadCustomerAsync(factory, email);
        Assert.NotNull(after);
        Assert.False(after.EmailConfirmed);

        // Spent on the way past, so a dead credential does not sit in the table until something else
        // happens to the account.
        Assert.Equal(
            0,
            await CountTokensAsync(factory, customer.Id, CustomerAccountTokens.EmailConfirmationName));
    }

    /// <summary>
    /// Asking for a new link invalidates the old one, so the newest message is always the one that works.
    /// </summary>
    [Fact]
    public async Task Asking_for_a_new_link_invalidates_the_previous_one()
    {
        await ReadyAsync();

        var email = UniqueEmail("d5-resend");

        await using var factory = CreateHost();
        using var client = CreateClient(factory);

        await RegisterAsync(client, email);
        var first = await ReadLinkTokenAsync(factory, Templates.Confirmation, email);

        await SignInAsync(client, email);

        using var resend = await client.PostAsync(
            new Uri("/api/account/resend-confirmation", UriKind.Relative),
            null,
            Token);

        Assert.Equal(HttpStatusCode.Accepted, resend.StatusCode);

        var second = await ReadLinkTokenAsync(factory, Templates.Confirmation, email);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);

        // One pending token per account, so the first link is dead.
        using var stale = await client.PostAsJsonAsync(ConfirmUrl, new { token = first }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);

        using var fresh = await client.PostAsJsonAsync(ConfirmUrl, new { token = second }, Token);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    /// <summary>
    /// Two tabs submitting the same link produce one confirmation, one audit row and one owner change.
    /// </summary>
    /// <remarks>
    /// <b>#146's lesson, applied to this claim.</b> Its round-1 tester found a version where making the
    /// pending-sign-in close idempotent let one recovery code open twelve sessions. The confirmation
    /// claim is the same shape — a conditional delete whose affected-row count is the claim — and this is
    /// the test that says so rather than the comment.
    /// </remarks>
    [Fact]
    public async Task Twelve_parallel_redemptions_of_one_link_confirm_once()
    {
        await ReadyAsync();

        var reference = await ReferenceAsync();
        var email = UniqueEmail("d5-parallel");
        await InsertGuestOrderAsync(reference, email);

        await using var factory = CreateHost();
        using var client = CreateClient(factory);

        await RegisterAsync(client, email);
        var token = await ReadLinkTokenAsync(factory, Templates.Confirmation, email);

        // Separate clients, so twelve independent browsers rather than twelve requests sharing a cookie
        // jar — a shared jar would have the later ones carrying the session the first one established.
        var clients = Enumerable.Range(0, 12).Select(_ => CreateClient(factory)).ToList();

        try
        {
            var responses = await Task.WhenAll(clients.Select(async candidate =>
            {
                using var response = await candidate.PostAsJsonAsync(ConfirmUrl, new { token }, Token);
                return response.StatusCode;
            }));

            Assert.Equal(1, responses.Count(status => status == HttpStatusCode.OK));
            Assert.Equal(11, responses.Count(status => status == HttpStatusCode.BadRequest));
        }
        finally
        {
            foreach (var candidate in clients)
            {
                candidate.Dispose();
            }
        }

        var customer = await ReadCustomerAsync(factory, email);
        Assert.NotNull(customer);
        Assert.True(customer.EmailConfirmed);

        var auditRows = await DbContext.AuditLogs
            .AsNoTracking()
            .CountAsync(
                row => row.Action == AuditActions.LinkGuestOrders && row.UserId == customer.Id,
                Token);

        Assert.Equal(1, auditRows);
    }

    /// <summary>
    /// One guest order under <paramref name="email"/>, with a line so the history's item count is real.
    /// </summary>
    /// <remarks>
    /// <b><c>EmailNormalized</c> is written as Identity would write it</b> — upper case, invariant — so
    /// the match D5 makes is the match the application makes and not a convenience of the fixture.
    /// </remarks>
    private async Task<int> InsertGuestOrderAsync(
        SalesReferenceData reference,
        string email,
        int? customerUserId = null,
        OrderStatus status = OrderStatus.PendingPayment)
    {
        var row = SalesRowWriter.OrderRow(
            reference.PaymentProviderCode,
            reference.StandardDeliveryMethodId,
            DeliveryMethod.StandardCode);

        row["Email"] = email;
        row["EmailNormalized"] = email.ToUpperInvariant();
        row["CustomerUserId"] = customerUserId;
        row["Status"] = (byte)status;

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

    private async Task<int?> ReadOwnerAsync(int orderId)
    {
        // Read through a fresh query rather than a tracked entity: the host under test wrote through its
        // own context and a tracked copy here would answer with what this context last saw.
        DbContext.ChangeTracker.Clear();

        return await DbContext.Orders
            .AsNoTracking()
            .Where(order => order.Id == orderId)
            .Select(order => order.CustomerUserId)
            .FirstAsync(Token);
    }

    /// <summary>
    /// Rewrites a stored token's expiry into the past, which is what the passage of time does.
    /// </summary>
    /// <remarks>
    /// The stored value is <c>{hash}|{expiry unix seconds}[|{address}]</c>
    /// (<c>CustomerAccountTokens</c>), so only the middle field moves — the hash has to stay exactly as it
    /// was or the verifier would fail for the wrong reason and the test would pass without having tested
    /// expiry at all.
    /// </remarks>
    private async Task ExpireTokenAsync(int userId, string name)
    {
        DbContext.ChangeTracker.Clear();

        var row = await DbContext.UserTokens
            .FirstAsync(
                token => token.UserId == userId
                    && token.LoginProvider == CustomerAccountTokens.LoginProvider
                    && token.Name == name,
                Token);

        var parts = row.Value!.Split('|');
        parts[1] = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

        row.Value = string.Join('|', parts);

        await DbContext.SaveChangesAsync(Token);
    }
}
