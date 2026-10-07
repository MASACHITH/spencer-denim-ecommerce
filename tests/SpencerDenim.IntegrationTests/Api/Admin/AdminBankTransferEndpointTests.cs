using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Media;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.Tests.MediaFixtures;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// "Orders → Bank transfers awaiting approval" and its three decisions, plus the customer's half of
/// doc 11 P7 step 3, end to end against a real SQL Server (PAY-01, ADM-09).
/// <para>
/// <b>One host, two kinds of caller.</b> A shopper places the order through the public endpoints with an
/// anonymous client, and a staff user signs in on the <i>same</i> host. Two hosts over one database would
/// also work and would be worse: anything cached per host — effective permissions above all — would
/// silently not be shared, and a permission test would then prove nothing about the shop.
/// </para>
/// <para>
/// A real <see cref="MediaTestHost"/>, because the slip is a real upload on to a real private volume. The
/// fixture creates the two volumes as siblings, which is what <c>MediaStorageRoots</c> insists on.
/// </para>
/// </summary>
public sealed class AdminBankTransferEndpointTests(SqlServerDatabaseFixture sqlServer)
{
    private const string QueueUrl = "/api/admin/orders/bank-transfers";
    private const string BankSettingsUrl = "/api/admin/settings/bank";
    private const string SettingsUrl = "/api/admin/settings";

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
    /// A signed-in staff user without the right permission is refused.
    /// </summary>
    /// <remarks>
    /// The role grants two unrelated permissions, so what is being proved is that the <i>permission</i> is
    /// the gate rather than the session. A role with none at all would pass this test against an endpoint
    /// that only checked for a cookie.
    /// </remarks>
    [Fact]
    public async Task The_queue_and_the_decisions_refuse_a_staff_user_without_the_permission()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Products.View, Permissions.Categories.View);

        foreach (var (method, path) in AdminRoutes())
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

            if (method == HttpMethod.Post)
            {
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            }

            using var response = await client.SendAsync(request, Token);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    /// <summary>
    /// <c>Orders.View</c> reads the queue and <c>Orders.Approve</c> decides, and the two are separate
    /// (doc 11 P7's permissions table).
    /// </summary>
    [Fact]
    public async Task Orders_View_can_read_the_queue_but_cannot_approve()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var viewer = await host.SignInAsync(Permissions.Orders.View);

        using var queue = await viewer.GetAsync(new Uri(QueueUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);

        using var approve = await viewer.PostAsJsonAsync(
            new Uri(DecisionUrl(order.PublicId, "approve"), UriKind.Relative),
            new { },
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);

        // And nothing happened to the order.
        Assert.Equal(OrderStatus.PendingPayment, await StatusAsync(order.PublicId));
    }

    // ---- the queue ------------------------------------------------------------------------------

    [Fact]
    public async Task The_queue_shows_what_doc_11_P7_step_4_lists()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var client = await host.SignInAsync(Permissions.Orders.Approve, Permissions.Orders.View);

        // The customer's reference, so the row has one to show.
        await SetReferenceAsync(host, order.PublicId, "CBC-778812");

        var row = await QueueRowAsync(client, order.OrderNumber);

        Assert.Equal(order.PublicId, row.GetProperty("orderPublicId").GetGuid());
        Assert.Equal(order.OrderNumber, row.GetProperty("orderNumber").GetString());
        Assert.Equal("Nimali Perera", row.GetProperty("customerName").GetString());
        Assert.Equal("nimali@example.com", row.GetProperty("customerEmail").GetString());
        Assert.Equal(order.GrandTotal, row.GetProperty("amountLkr").GetDecimal());
        Assert.Equal("LKR", row.GetProperty("currency").GetString());
        Assert.Equal("CBC-778812", row.GetProperty("payerReference").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("slipAssetPublicId").ValueKind);

        // Time left, computed on the server. A newly placed order has 48 hours, so this is a large
        // positive number — and the assertion is a range rather than a value, because the figure moves
        // between the placement and the read.
        var secondsLeft = row.GetProperty("secondsUntilExpiry").GetInt64();
        Assert.InRange(secondsLeft, (47 * 60 * 60) + 1, 48 * 60 * 60);
    }

    [Fact]
    public async Task An_approved_transfer_leaves_the_queue_and_a_rejected_one_leaves_it_too()
    {
        await using var host = await StartAsync();
        var approved = await PlaceOrderAsync(host);
        var rejected = await PlaceOrderAsync(host);
        var waiting = await PlaceOrderAsync(host);

        using var client = await host.SignInAsync(Permissions.Orders.Approve, Permissions.Orders.View);

        Assert.NotNull(await TryQueueRowAsync(client, approved.OrderNumber));
        Assert.NotNull(await TryQueueRowAsync(client, rejected.OrderNumber));

        await DecideAsync(client, approved.PublicId, "approve", HttpStatusCode.OK);
        await DecideAsync(client, rejected.PublicId, "reject", HttpStatusCode.OK, "No credit found.");

        // The queue is `Status = Pending` bank payments. Both decisions close their row, so both orders
        // leave — and the one nobody has looked at is still there, which is what makes this an assertion
        // about the filter rather than about an empty list.
        Assert.Null(await TryQueueRowAsync(client, approved.OrderNumber));
        Assert.Null(await TryQueueRowAsync(client, rejected.OrderNumber));
        Assert.NotNull(await TryQueueRowAsync(client, waiting.OrderNumber));
    }

    [Fact]
    public async Task The_queue_is_oldest_first()
    {
        await using var host = await StartAsync();

        var first = await PlaceOrderAsync(host);
        var second = await PlaceOrderAsync(host);
        var third = await PlaceOrderAsync(host);

        using var client = await host.SignInAsync(Permissions.Orders.View);

        var numbers = await QueueNumbersAsync(client);

        // Only this test's three, in the order they were placed. Filtered to them because the assembly
        // shares one database and every unpaid order a neighbouring test placed is in this queue too.
        var mine = numbers
            .Where(number => number == first.OrderNumber
                || number == second.OrderNumber
                || number == third.OrderNumber)
            .ToList();

        Assert.Equal([first.OrderNumber, second.OrderNumber, third.OrderNumber], mine);
    }

    // ---- the decisions --------------------------------------------------------------------------

    [Fact]
    public async Task Approving_answers_the_order_and_the_invoice_number()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var client = await host.SignInAsync(Permissions.Orders.Approve);

        var body = await DecideAsync(client, order.PublicId, "approve", HttpStatusCode.OK, "Seen at 14:02.");

        Assert.Equal(order.OrderNumber, body.GetProperty("orderNumber").GetString());
        Assert.Matches(@"^INV-\d{4}-\d{6,}$", body.GetProperty("invoiceNumber").GetString()!);

        Assert.Equal(OrderStatus.Paid, await StatusAsync(order.PublicId));
    }

    [Fact]
    public async Task Rejecting_without_a_reason_is_a_400_naming_the_field()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var client = await host.SignInAsync(Permissions.Orders.Approve);

        foreach (var body in new[] { "{}", """{"note":null}""", """{"note":"   "}""" })
        {
            var (status, payload) = await PostRawAsync(
                client,
                DecisionUrl(order.PublicId, "reject"),
                body);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.True(
                payload.GetProperty("errors").TryGetProperty("note", out _),
                $"Body {body} should name the 'note' field.");
        }

        // Doc 11 P7 step 6b: the reason is the only record of why money a customer believes they sent was
        // refused, so a rejection without one must change nothing at all.
        Assert.Equal(OrderStatus.PendingPayment, await StatusAsync(order.PublicId));
        Assert.Equal(PaymentStatus.Pending, await PaymentStatusAsync(order.PublicId));
    }

    [Fact]
    public async Task A_second_approval_is_a_409_carrying_the_code_and_both_statuses()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var client = await host.SignInAsync(Permissions.Orders.Approve);

        await DecideAsync(client, order.PublicId, "approve", HttpStatusCode.OK);

        var (status, payload) = await PostRawAsync(
            client,
            DecisionUrl(order.PublicId, "approve"),
            "{}");

        Assert.Equal(HttpStatusCode.Conflict, status);

        // The code is what lets the screen offer the right next action instead of printing a sentence
        // (doc 11 P7's business rule: "the approver is told and the screen reloads").
        Assert.Equal("order_already_paid", payload.GetProperty("code").GetString());
        Assert.Equal(nameof(OrderStatus.Paid), payload.GetProperty("orderStatus").GetString());
        Assert.Equal(nameof(PaymentStatus.Succeeded), payload.GetProperty("paymentStatus").GetString());
    }

    [Fact]
    public async Task A_decision_on_a_token_that_names_nothing_is_a_bare_404()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Orders.Approve);

        foreach (var action in new[] { "approve", "reject", "accept-late" })
        {
            var (status, _) = await PostRawAsync(
                client,
                DecisionUrl(Guid.NewGuid(), action),
                """{"note":"anything"}""");

            Assert.Equal(HttpStatusCode.NotFound, status);
        }
    }

    [Fact]
    public async Task A_decision_without_the_antiforgery_header_is_refused()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var client = await host.SignInAsync(Permissions.Orders.Approve);
        client.DefaultRequestHeaders.Remove(MediaTestHost.AntiforgeryHeader);

        using var response = await client.PostAsJsonAsync(
            new Uri(DecisionUrl(order.PublicId, "approve"), UriKind.Relative),
            new { },
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(OrderStatus.PendingPayment, await StatusAsync(order.PublicId));
    }

    /// <summary>
    /// Issue #47's family on the decision routes: a client mistake is never a 5xx, in either environment.
    /// </summary>
    [Fact]
    public async Task A_malformed_decision_body_is_never_a_500()
    {
        foreach (var environment in new[] { "Testing", "Development" })
        {
            await using var host = await StartAsync(environment);
            var order = await PlaceOrderAsync(host);

            using var client = await host.SignInAsync(Permissions.Orders.Approve);

            foreach (var body in new[]
                     {
                         "{bad", "null", "[1,2,3]", "\"a string\"", "42", "true",
                         """{"note":123}""", """{"note":{"nested":true}}""",
                     })
            {
                var (status, _) = await PostRawAsync(
                    client,
                    DecisionUrl(order.PublicId, "reject"),
                    body);

                Assert.True(
                    (int)status is >= 400 and < 500,
                    $"{environment}: reject with body {body} answered {(int)status}.");
            }
        }
    }

    // ---- the bank settings ----------------------------------------------------------------------

    /// <summary>
    /// <c>Settings.Payments</c> writes the bank details, and <c>Settings.Edit</c> — which every default
    /// role except Super Admin holds — cannot.
    /// </summary>
    [Fact]
    public async Task Bank_details_need_Settings_Payments_and_Settings_Edit_is_not_enough()
    {
        await using var host = await StartAsync();

        try
        {
            using var editor = await host.SignInAsync(Permissions.Settings.Edit);

            var (refused, _) = await PutRawAsync(
                editor,
                BankSettingsUrl,
                """{"bankName":"Not theirs to set"}""");

            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

            // And the group is absent from what they can read, not merely empty — an empty Bank group
            // would confirm that bank details exist and name the five keys to go looking for.
            var groups = await GroupKeysAsync(editor);
            Assert.DoesNotContain(SettingKeys.Bank.Group, groups);

            using var payments = await host.SignInAsync(Permissions.Settings.Payments);

            Assert.Contains(SettingKeys.Bank.Group, await GroupKeysAsync(payments));

            var (saved, _) = await PutRawAsync(
                payments,
                BankSettingsUrl,
                $$"""
                {
                  "bankName": "Commercial Bank of Ceylon PLC",
                  "branch": "Kollupitiya",
                  "accountName": "Spencer Denim Industries (Pvt) Ltd",
                  "accountNumber": "8001 2345 6789",
                  "instructions": "CDM deposits take a working day.",
                  "rowVersion": {{JsonSerializer.Serialize(await BankVersionAsync(payments))}}
                }
                """);

            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

            Assert.Equal("8001 2345 6789", await SettingValueAsync(SettingKeys.Bank.AccountNumber));
        }
        finally
        {
            await ClearBankDetailsAsync();
        }
    }

    /// <summary>
    /// The W1-12 defect family, on the group where it costs real money: a body that <b>omits</b> a field
    /// must not clear it.
    /// </summary>
    /// <remarks>
    /// <b>The fixture holds real values in every field, deliberately.</b> W1-12's blocker survived 1,571
    /// passing tests because every fixture set the columns it cleared to <c>null</c>, so the round trip
    /// compared null with null and agreed. A fixture with a real account number in it is the only kind
    /// that can fail.
    /// </remarks>
    [Fact]
    public async Task A_save_that_omits_a_field_leaves_it_alone()
    {
        await using var host = await StartAsync();

        try
        {
            using var client = await host.SignInAsync(Permissions.Settings.Payments);

            var (seeded, _) = await PutRawAsync(
                client,
                BankSettingsUrl,
                $$"""
                {
                  "bankName": "Commercial Bank of Ceylon PLC",
                  "branch": "Kollupitiya",
                  "accountName": "Spencer Denim Industries (Pvt) Ltd",
                  "accountNumber": "8001 2345 6789",
                  "instructions": "CDM deposits take a working day.",
                  "rowVersion": {{JsonSerializer.Serialize(await BankVersionAsync(client))}}
                }
                """);

            Assert.Equal(HttpStatusCode.NoContent, seeded.StatusCode);

            // One field, and only that field. This is the request a screen whose caller cannot read the
            // group would send if it sent anything at all.
            var (changed, _) = await PutRawAsync(
                client,
                BankSettingsUrl,
                $$"""
                {
                  "branch": "Bambalapitiya",
                  "rowVersion": {{JsonSerializer.Serialize(await BankVersionAsync(client))}}
                }
                """);

            Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

            Assert.Equal("Bambalapitiya", await SettingValueAsync(SettingKeys.Bank.Branch));
            Assert.Equal("Commercial Bank of Ceylon PLC", await SettingValueAsync(SettingKeys.Bank.BankName));
            Assert.Equal(
                "Spencer Denim Industries (Pvt) Ltd",
                await SettingValueAsync(SettingKeys.Bank.AccountName));
            Assert.Equal("8001 2345 6789", await SettingValueAsync(SettingKeys.Bank.AccountNumber));
            Assert.Equal(
                "CDM deposits take a working day.",
                await SettingValueAsync(SettingKeys.Bank.Instructions));

            // And an explicit null does clear it, because "we have no branch" has to be sayable.
            var (cleared, _) = await PutRawAsync(
                client,
                BankSettingsUrl,
                $$"""
                {
                  "branch": null,
                  "rowVersion": {{JsonSerializer.Serialize(await BankVersionAsync(client))}}
                }
                """);

            Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
            Assert.Null(await SettingValueAsync(SettingKeys.Bank.Branch));
            Assert.Equal("8001 2345 6789", await SettingValueAsync(SettingKeys.Bank.AccountNumber));
        }
        finally
        {
            await ClearBankDetailsAsync();
        }
    }

    [Fact]
    public async Task An_account_number_that_is_not_a_number_is_refused_and_a_grouped_one_is_kept()
    {
        await using var host = await StartAsync();

        try
        {
            using var client = await host.SignInAsync(Permissions.Settings.Payments);

            foreach (var bad in new[] { "to be confirmed", "ask accounts", "####" })
            {
                var (status, payload) = await PutRawAsync(
                    client,
                    BankSettingsUrl,
                    $$"""
                    {
                      "accountNumber": {{JsonSerializer.Serialize(bad)}},
                      "rowVersion": {{JsonSerializer.Serialize(await BankVersionAsync(client))}}
                    }
                    """);

                Assert.Equal(HttpStatusCode.BadRequest, status.StatusCode);
                Assert.True(payload.GetProperty("errors").TryGetProperty("accountNumber", out _));
            }

            // The grouping the client types is kept, because a shopper copies it into online banking and
            // the shop does not know the format better than the client does.
            var (saved, _) = await PutRawAsync(
                client,
                BankSettingsUrl,
                $$"""
                {
                  "accountNumber": "8001-2345-6789",
                  "rowVersion": {{JsonSerializer.Serialize(await BankVersionAsync(client))}}
                }
                """);

            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
            Assert.Equal("8001-2345-6789", await SettingValueAsync(SettingKeys.Bank.AccountNumber));
        }
        finally
        {
            await ClearBankDetailsAsync();
        }
    }

    // ---- the slip -------------------------------------------------------------------------------

    [Fact]
    public async Task A_slip_is_uploaded_privately_shown_to_staff_and_never_served_from_media()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var shopper = host.CreateAnonymousClient();
        var slip = await UploadSlipAsync(shopper, order.PublicId, TestImages.UniqueJpeg(900, 1400), "slip.jpg");

        Assert.Equal("image/jpeg", slip.GetProperty("contentType").GetString());
        Assert.Equal("slip.jpg", slip.GetProperty("fileName").GetString());

        var assetId = slip.GetProperty("slipPublicId").GetGuid();

        // Doc 08 §3.3: a private asset lives on the volume the reverse proxy does not mount, and
        // /media refuses it with a bare 404 — "no such file" and "that one is private" must be the same
        // answer.
        var storageKey = await StorageKeyAsync(assetId);
        Assert.StartsWith("private/", storageKey, StringComparison.Ordinal);

        using var fromMedia = await shopper.GetAsync(
            new Uri($"/media/{storageKey}", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.NotFound, fromMedia.StatusCode);

        // The customer can fetch their own slip through the order's link, and staff through the
        // permission. Both get it as a download that nothing caches.
        foreach (var (client, url) in new[]
                 {
                     (shopper, $"/api/orders/{order.PublicId:D}/payment-slip"),
                     (await host.SignInAsync(Permissions.Orders.View), $"{QueueUrl}/{order.PublicId:D}/slip"),
                 })
        {
            using var file = await client.GetAsync(new Uri(url, UriKind.Relative), Token);

            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.Equal("image/jpeg", file.Content.Headers.ContentType?.MediaType);
            Assert.Equal("attachment", file.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Contains("no-store", file.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.NotEmpty(await file.Content.ReadAsByteArrayAsync(Token));
        }

        // And the queue row now shows it.
        using var staff = await host.SignInAsync(Permissions.Orders.View);
        var row = await QueueRowAsync(staff, order.OrderNumber);
        Assert.Equal(assetId, row.GetProperty("slipAssetPublicId").GetGuid());
        Assert.Equal("image/jpeg", row.GetProperty("slipContentType").GetString());
    }

    [Fact]
    public async Task A_PDF_slip_is_accepted_and_a_text_file_is_not()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var shopper = host.CreateAnonymousClient();

        // A minimal real PDF. The signature is what decides, never the filename or the client's content
        // type, so the next case renames a text file to .pdf and claims application/pdf.
        var pdf = Encoding.ASCII.GetBytes(
            "%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n"
            + new string(' ', 64));

        var slip = await UploadSlipAsync(shopper, order.PublicId, pdf, "receipt.pdf", "application/pdf");
        Assert.Equal("application/pdf", slip.GetProperty("contentType").GetString());

        using var form = MediaTestHost.FilePart(
            "file",
            Encoding.ASCII.GetBytes("I promise this is my bank slip."),
            "receipt.pdf",
            "application/pdf");

        using var refused = await shopper.PostAsync(
            new Uri($"/api/orders/{order.PublicId:D}/payment-slip", UriKind.Relative),
            form,
            Token);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, refused.StatusCode);
    }

    [Fact]
    public async Task A_new_slip_replaces_the_previous_one()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var shopper = host.CreateAnonymousClient();

        var first = await UploadSlipAsync(
            shopper, order.PublicId, TestImages.UniqueJpeg(800, 1200), "first.jpg");
        var second = await UploadSlipAsync(
            shopper, order.PublicId, TestImages.UniqueJpeg(810, 1210), "second.jpg");

        var firstId = first.GetProperty("slipPublicId").GetGuid();
        var secondId = second.GetProperty("slipPublicId").GetGuid();
        Assert.NotEqual(firstId, secondId);

        // Doc 08 §3.3 N10: "at most one current file per payment". The payment points at the new one and
        // the old one is soft-deleted — not hard-deleted, because it is still a customer's evidence until
        // the retention window closes (doc 08 §8.4).
        using var staff = await host.SignInAsync(Permissions.Orders.View);
        var row = await QueueRowAsync(staff, order.OrderNumber);
        Assert.Equal(secondId, row.GetProperty("slipAssetPublicId").GetGuid());

        Assert.True(await IsDeletedAsync(firstId));
        Assert.False(await IsDeletedAsync(secondId));
    }

    [Fact]
    public async Task A_slip_cannot_be_uploaded_to_an_order_that_has_been_paid()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var client = await host.SignInAsync(Permissions.Orders.Approve);
        await DecideAsync(client, order.PublicId, "approve", HttpStatusCode.OK);

        using var shopper = host.CreateAnonymousClient();
        using var form = MediaTestHost.FilePart(
            "file", TestImages.UniqueJpeg(700, 1000), "late.jpg", "image/jpeg");

        using var response = await shopper.PostAsync(
            new Uri($"/api/orders/{order.PublicId:D}/payment-slip", UriKind.Relative),
            form,
            Token);

        // 404, the same answer a token naming nothing gets: doc 08 §3.3 N10 allows an upload only while
        // the order is PendingPayment with an open bank payment, and the order link never expires, so a
        // stranger must not learn that a guessed token names a real order which happens to be paid.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_sixth_slip_in_an_hour_is_refused_with_a_retry_after()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var shopper = host.CreateAnonymousClient();

        // Doc 08 §3.3 N10: five per hour per order. Five distinct images, because identical bytes are
        // answered from the existing asset and the sixth has to be refused by the limit rather than by
        // deduplication.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await UploadSlipAsync(
                shopper,
                order.PublicId,
                TestImages.UniqueJpeg(600 + attempt, 900 + attempt),
                $"slip-{attempt}.jpg");
        }

        using var form = MediaTestHost.FilePart(
            "file", TestImages.UniqueJpeg(777, 999), "slip-6.jpg", "image/jpeg");

        using var refused = await shopper.PostAsync(
            new Uri($"/api/orders/{order.PublicId:D}/payment-slip", UriKind.Relative),
            form,
            Token);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);
    }

    // ---- the customer's view --------------------------------------------------------------------

    [Fact]
    public async Task The_order_page_shows_the_bank_details_the_reference_and_the_amount()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var shopper = host.CreateAnonymousClient();

        try
        {
            // Before the client fills their details in, the order page has no instructions — and that is
            // a real state, not an error: doc 11 P7 makes "bank details set by a Super Admin" a
            // precondition and a shop can be deployed before they arrive.
            var unset = await OrderPayloadAsync(shopper, order.PublicId);
            Assert.Equal(JsonValueKind.Null, unset.GetProperty("payment").ValueKind);

            await SetBankDetailsAsync();

            var payload = await OrderPayloadAsync(shopper, order.PublicId);
            var payment = payload.GetProperty("payment");

            Assert.Equal(PaymentProviderCodes.BankTransfer, payment.GetProperty("providerCode").GetString());

            // Doc 11 P7 step 1: the order number is the reference to quote. It is the only thing tying an
            // anonymous bank credit to an order.
            Assert.Equal(order.OrderNumber, payment.GetProperty("reference").GetString());
            Assert.Equal(order.GrandTotal, payment.GetProperty("amountLkr").GetDecimal());
            Assert.Equal(order.PaymentDueAtUtc, payment.GetProperty("dueAtUtc").GetDateTime());
            Assert.True(payment.GetProperty("acceptsProof").GetBoolean());
            Assert.Equal(JsonValueKind.Null, payment.GetProperty("redirectUrl").ValueKind);

            var fields = payment.GetProperty("fields").EnumerateArray()
                .ToDictionary(
                    field => field.GetProperty("label").GetString()!,
                    field => field.GetProperty("value").GetString()!,
                    StringComparer.Ordinal);

            Assert.Equal("Commercial Bank of Ceylon PLC", fields["Bank"]);
            Assert.Equal("Kollupitiya", fields["Branch"]);
            Assert.Equal("Spencer Denim Industries (Pvt) Ltd", fields["Account name"]);
            Assert.Equal("8001 2345 6789", fields["Account number"]);
        }
        finally
        {
            await ClearBankDetailsAsync();
        }
    }

    /// <summary>
    /// Half-filled bank details show nothing at all.
    /// </summary>
    /// <remarks>
    /// Deliberate, and the expensive direction is the other one: a shopper who sees a bank name and an
    /// empty account number may transfer to whatever they find, and the money then has to be traced by
    /// hand. The three indispensable values are the bank, the account name and the account number.
    /// </remarks>
    [Fact]
    public async Task Half_filled_bank_details_show_no_instructions_at_all()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var shopper = host.CreateAnonymousClient();

        try
        {
            await SetBankDetailsAsync(accountNumber: null);

            var payload = await OrderPayloadAsync(shopper, order.PublicId);
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("payment").ValueKind);

            // The branch and the instructions are optional, so leaving them out does show the rest.
            await SetBankDetailsAsync(branch: null, instructions: null);

            var complete = await OrderPayloadAsync(shopper, order.PublicId);
            var fields = complete.GetProperty("payment").GetProperty("fields").EnumerateArray()
                .Select(field => field.GetProperty("label").GetString())
                .ToList();

            Assert.DoesNotContain("Branch", fields);
            Assert.Contains("Account number", fields);
            Assert.Equal(
                JsonValueKind.Null,
                complete.GetProperty("payment").GetProperty("notes").ValueKind);
        }
        finally
        {
            await ClearBankDetailsAsync();
        }
    }

    [Fact]
    public async Task A_paid_order_shows_no_payment_instructions()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var staff = await host.SignInAsync(Permissions.Orders.Approve);
        using var shopper = host.CreateAnonymousClient();

        try
        {
            await SetBankDetailsAsync();
            await DecideAsync(staff, order.PublicId, "approve", HttpStatusCode.OK);

            var payload = await OrderPayloadAsync(shopper, order.PublicId);

            // There is nothing for the customer to do, so neither the provider nor the proof is asked
            // for — which also keeps the order page's cost unchanged for every order that is not waiting
            // for money.
            Assert.Equal(nameof(OrderStatus.Paid), payload.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("payment").ValueKind);
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("proof").ValueKind);
        }
        finally
        {
            await ClearBankDetailsAsync();
        }
    }

    [Fact]
    public async Task The_customer_s_reference_is_kept_shown_back_and_clearable()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var shopper = host.CreateAnonymousClient();

        await SetReferenceAsync(host, order.PublicId, "  CBC-778812  ");

        var payload = await OrderPayloadAsync(shopper, order.PublicId);
        Assert.Equal("CBC-778812", payload.GetProperty("proof").GetProperty("reference").GetString());

        // Clearing it is a real action: a customer who mistyped it has to be able to take it back rather
        // than leave a wrong reference for staff to match against.
        await SetReferenceAsync(host, order.PublicId, null);

        var cleared = await OrderPayloadAsync(shopper, order.PublicId);
        Assert.Equal(
            JsonValueKind.Null,
            cleared.GetProperty("proof").GetProperty("reference").ValueKind);
    }

    [Fact]
    public async Task A_reference_on_an_order_that_cannot_be_paid_is_a_404()
    {
        await using var host = await StartAsync();
        var order = await PlaceOrderAsync(host);

        using var staff = await host.SignInAsync(Permissions.Orders.Approve);
        await DecideAsync(staff, order.PublicId, "approve", HttpStatusCode.OK);

        using var shopper = host.CreateAnonymousClient();

        using var response = await shopper.PutAsJsonAsync(
            new Uri($"/api/orders/{order.PublicId:D}/payment-reference", UriKind.Relative),
            new { reference = "too late" },
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // And an unknown token answers the same, so the route is not an oracle for which orders exist.
        using var unknown = await shopper.PutAsJsonAsync(
            new Uri($"/api/orders/{Guid.NewGuid():D}/payment-reference", UriKind.Relative),
            new { reference = "anything" },
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static string DecisionUrl(Guid orderPublicId, string action) =>
        $"{QueueUrl}/{orderPublicId.ToString("D", CultureInfo.InvariantCulture)}/{action}";

    /// <summary>Every admin route this file guards, with a method that reaches it.</summary>
    private static IEnumerable<(HttpMethod Method, string Path)> AdminRoutes()
    {
        var order = Guid.Empty;

        yield return (HttpMethod.Get, QueueUrl);
        yield return (HttpMethod.Get, $"{QueueUrl}/{order:D}/slip");
        yield return (HttpMethod.Post, DecisionUrl(order, "approve"));
        yield return (HttpMethod.Post, DecisionUrl(order, "reject"));
        yield return (HttpMethod.Post, DecisionUrl(order, "accept-late"));
    }

    private async Task<MediaTestHost> StartAsync(string environment = "Testing")
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        return MediaTestHost.Create(sqlServer.ConnectionString, environment: environment);
    }

    /// <summary>
    /// Places one real order on <paramref name="host"/>, through the public endpoints a shopper uses.
    /// </summary>
    /// <remarks>
    /// <b>Not inserted by hand.</b> Every invariant in this file depends on the order and its reservation
    /// having been written by the real placement path; an order whose stock was arranged by a fixture
    /// would let an approval "deduct" stock nobody held.
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
            new Uri($"/api/checkout?districtId={districtId}&deliveryMethodId={deliveryMethodId}", UriKind.Relative),
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
            body.GetProperty("grandTotal").GetDecimal(),
            body.GetProperty("paymentDueAtUtc").GetDateTime());
    }

    private async Task<int> InStockVariantIdAsync()
    {
        using var scope = sqlServer.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProductVariants
            .AsNoTracking()
            .Where(variant => variant.IsActive && variant.Product!.IsPublished)
            .Where(variant =>
                variant.Product!.Department!.IsActive && variant.Product!.PrimaryCategory!.IsActive)
            .Where(variant => variant.StockLevel != null && variant.StockLevel.Available > 2)
            .OrderBy(variant => variant.Id)
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

    private static async Task<JsonElement> DecideAsync(
        HttpClient client,
        Guid orderPublicId,
        string action,
        HttpStatusCode expected,
        string? note = null)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri(DecisionUrl(orderPublicId, action), UriKind.Relative),
            new { note },
            JsonOptions,
            Token);

        Assert.Equal(expected, response.StatusCode);

        return await ReadBodyAsync(response);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostRawAsync(
        HttpClient client,
        string path,
        string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, Token);

        return (response.StatusCode, await ReadBodyAsync(response));
    }

    private static async Task<(HttpResponseMessage StatusCode, JsonElement Body)> PutRawAsync(
        HttpClient client,
        string path,
        string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        var response = await client.SendAsync(request, Token);

        return (response, await ReadBodyAsync(response));
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync(Token);

        return string.IsNullOrWhiteSpace(payload)
            ? default
            : JsonSerializer.Deserialize<JsonElement>(payload, JsonOptions);
    }

    private static async Task<JsonElement> UploadSlipAsync(
        HttpClient client,
        Guid orderPublicId,
        byte[] bytes,
        string fileName,
        string contentType = "image/jpeg")
    {
        using var form = MediaTestHost.FilePart("file", bytes, fileName, contentType);

        using var response = await client.PostAsync(
            new Uri($"/api/orders/{orderPublicId:D}/payment-slip", UriKind.Relative),
            form,
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
    }

    private static async Task SetReferenceAsync(MediaTestHost host, Guid orderPublicId, string? reference)
    {
        using var client = host.CreateAnonymousClient();

        using var response = await client.PutAsJsonAsync(
            new Uri($"/api/orders/{orderPublicId:D}/payment-reference", UriKind.Relative),
            new { reference },
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<JsonElement> OrderPayloadAsync(HttpClient client, Guid orderPublicId)
    {
        using var response = await client.GetAsync(
            new Uri($"/api/orders/{orderPublicId:D}", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
    }

    private static async Task<JsonElement> QueueRowAsync(HttpClient client, string orderNumber) =>
        await TryQueueRowAsync(client, orderNumber)
        ?? throw new InvalidOperationException($"Order {orderNumber} is not in the queue.");

    /// <summary>
    /// The queue row for one order number, or null.
    /// </summary>
    /// <remarks>
    /// Paged through rather than read off the first page: the assembly shares one database and every unpaid
    /// order a neighbouring test placed is in this queue too, so a row this test is looking for can be on
    /// page four. A first-page-only helper passed while the suite was small and would start failing as it
    /// grew, which is the worst kind of test to leave behind.
    /// </remarks>
    private static async Task<JsonElement?> TryQueueRowAsync(HttpClient client, string orderNumber)
    {
        for (var page = 1; page <= 50; page++)
        {
            using var response = await client.GetAsync(
                new Uri($"{QueueUrl}?page={page}&pageSize=100", UriKind.Relative),
                Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
            var rows = body.GetProperty("rows").EnumerateArray().ToList();

            if (rows.Count == 0)
            {
                return null;
            }

            foreach (var row in rows)
            {
                if (row.GetProperty("orderNumber").GetString() == orderNumber)
                {
                    return row;
                }
            }
        }

        return null;
    }

    private static async Task<List<string>> QueueNumbersAsync(HttpClient client)
    {
        var numbers = new List<string>();

        for (var page = 1; page <= 50; page++)
        {
            using var response = await client.GetAsync(
                new Uri($"{QueueUrl}?page={page}&pageSize=100", UriKind.Relative),
                Token);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
            var rows = body.GetProperty("rows").EnumerateArray().ToList();

            if (rows.Count == 0)
            {
                break;
            }

            numbers.AddRange(rows.Select(row => row.GetProperty("orderNumber").GetString()!));
        }

        return numbers;
    }

    private static async Task<IReadOnlyList<string>> GroupKeysAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri(SettingsUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        return body.GetProperty("groups").EnumerateArray()
            .Select(group => group.GetProperty("groupKey").GetString()!)
            .ToList();
    }

    private static async Task<string> BankVersionAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri(SettingsUrl, UriKind.Relative), Token);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        return body.GetProperty("groups").EnumerateArray()
            .Single(group => group.GetProperty("groupKey").GetString() == SettingKeys.Bank.Group)
            .GetProperty("rowVersion").GetString()!;
    }

    private async Task<OrderStatus> StatusAsync(Guid publicId)
    {
        using var scope = sqlServer.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
            .AsNoTracking()
            .Where(order => order.PublicId == publicId)
            .Select(order => order.Status)
            .SingleAsync(Token);
    }

    private async Task<PaymentStatus> PaymentStatusAsync(Guid publicId)
    {
        using var scope = sqlServer.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Payments
            .AsNoTracking()
            .Where(payment => payment.Order!.PublicId == publicId)
            .OrderByDescending(payment => payment.Id)
            .Select(payment => payment.Status)
            .FirstAsync(Token);
    }

    private async Task<string?> SettingValueAsync(string key)
    {
        using var scope = sqlServer.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Settings
            .AsNoTracking()
            .Where(setting => setting.Key == key)
            .Select(setting => setting.Value)
            .SingleAsync(Token);
    }

    private async Task<string> StorageKeyAsync(Guid assetPublicId)
    {
        using var scope = sqlServer.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().MediaAssets
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(asset => asset.PublicId == assetPublicId)
            .Select(asset => asset.StorageKey)
            .SingleAsync(Token);
    }

    private async Task<bool> IsDeletedAsync(Guid assetPublicId)
    {
        using var scope = sqlServer.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().MediaAssets
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(asset => asset.PublicId == assetPublicId)
            .Select(asset => asset.IsDeleted)
            .SingleAsync(Token);
    }

    private async Task SetBankDetailsAsync(
        string? bankName = "Commercial Bank of Ceylon PLC",
        string? branch = "Kollupitiya",
        string? accountName = "Spencer Denim Industries (Pvt) Ltd",
        string? accountNumber = "8001 2345 6789",
        string? instructions = "CDM deposits can take a working day to appear.")
    {
        var values = new (string Key, string? Value)[]
        {
            (SettingKeys.Bank.BankName, bankName),
            (SettingKeys.Bank.Branch, branch),
            (SettingKeys.Bank.AccountName, accountName),
            (SettingKeys.Bank.AccountNumber, accountNumber),
            (SettingKeys.Bank.Instructions, instructions),
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
    /// Puts the five <c>Bank.*</c> rows back to null.
    /// </summary>
    /// <remarks>
    /// <b>In a <c>finally</c> in every test that sets them.</b> The assembly shares one database and
    /// classes run sequentially, so a left-behind account number is a neighbour's fixture — the shape that
    /// made four <c>CartStaleItemTests</c> red in a full run and green alone.
    /// </remarks>
    private async Task ClearBankDetailsAsync()
    {
        if (!sqlServer.IsAvailable)
        {
            return;
        }

        string[] keys =
        [
            SettingKeys.Bank.BankName,
            SettingKeys.Bank.Branch,
            SettingKeys.Bank.AccountName,
            SettingKeys.Bank.AccountNumber,
            SettingKeys.Bank.Instructions,
        ];

        using var scope = sqlServer.CreateScope();

        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Settings
            .Where(setting => keys.Contains(setting.Key))
            .ExecuteUpdateAsync(
                setting => setting.SetProperty(row => row.Value, (string?)null),
                CancellationToken.None);
    }

    private sealed record PlacedOrder(
        Guid PublicId,
        string OrderNumber,
        decimal GrandTotal,
        DateTime PaymentDueAtUtc);
}
