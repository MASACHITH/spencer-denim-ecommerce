using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SpencerDenim.Application.Accounts.Abstractions;
using SpencerDenim.Application.Accounts.Contracts;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Ops.Email;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.IntegrationTests.Sales;

namespace SpencerDenim.IntegrationTests.Api.Accounts;

/// <summary>
/// Four gaps the W3-01 tester pass found in the account suite, each closed by a test that kills a named
/// mutation the existing suite let through.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// <b>The reset half of the stale-link fix had no test.</b> Making <c>CustomerPasswordStore.ResetAsync</c>
/// delete the stored row when a token does not verify left all 104 account tests green.
/// </description></item>
/// <item><description>
/// <b>The confirmation claim's row count was killed only sometimes.</b>
/// <c>Twelve_parallel_redemptions_of_one_link_confirm_once</c> caught it on one run in two; the store-level
/// race below, in parallel DI scopes, catches it every time.
/// </description></item>
/// <item><description>
/// <b>G-03 was only tested on rows seeded by hand</b>, with the template code typed as a literal. This one
/// goes forgot-password → <c>ops.EmailOutbox</c> → <c>SendEmailsService</c> → transport, and then uses the
/// link that was sent.
/// </description></item>
/// <item><description>
/// <b>The no-antiforgery decision rests on "JSON-only bodies"</b>, and nothing asserted it. The three body
/// types an HTML form can send without a preflight must each be refused.
/// </description></item>
/// </list>
/// </remarks>
public sealed class CustomerAccountTesterGapTests(SqlServerDatabaseFixture sqlServer)
    : CustomerAccountTestBase(sqlServer)
{
    [Fact]
    public async Task A_stale_reset_link_leaves_the_live_one_working()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var email = UniqueEmail("stale-reset");
        await RegisterAsync(client, email);

        using (var first = await client.PostAsJsonAsync("/api/account/forgot-password", new { email }, Token))
        {
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        }

        var stale = await ReadLinkTokenAsync(factory, Templates.PasswordReset, email);

        using (var second = await client.PostAsJsonAsync("/api/account/forgot-password", new { email }, Token))
        {
            Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        }

        var live = await ReadLinkTokenAsync(factory, Templates.PasswordReset, email);
        Assert.NotNull(stale);
        Assert.NotNull(live);
        Assert.NotEqual(stale, live);

        // The old message, and a forged verifier for the same account: both refused, neither spends
        // anything.
        var userId = live![..live.IndexOf('.', StringComparison.Ordinal)];

        foreach (var token in new[] { stale, $"{userId}.not-a-real-verifier" })
        {
            using var refused = await client.PostAsJsonAsync(
                "/api/account/reset-password",
                new { token, password = "Another-Good-Pass-2!" },
                Token);

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        using var accepted = await client.PostAsJsonAsync(
            "/api/account/reset-password",
            new { token = live, password = "Another-Good-Pass-2!" },
            Token);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task Parallel_redemptions_of_one_confirmation_link_confirm_exactly_once()
    {
        await ReadyAsync();

        var reference = await ReferenceAsync();

        for (var round = 0; round < 4; round++)
        {
            var email = UniqueEmail("confirm-race");

            // A guest order, so a second confirmation would have something to link and an audit row to
            // write — the two things a lost claim would duplicate.
            var row = SalesRowWriter.OrderRow(
                reference.PaymentProviderCode,
                reference.StandardDeliveryMethodId,
                DeliveryMethod.StandardCode);
            row["Email"] = email;
            row["EmailNormalized"] = email.ToUpperInvariant();
            row["CustomerUserId"] = null;
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

            await using var factory = CreateHost();
            using var client = CreateClient(factory);
            await RegisterAsync(client, email);
            var token = await ReadLinkTokenAsync(factory, Templates.Confirmation, email);
            Assert.NotNull(token);

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // A DI scope each — its own AppDbContext and connection — which is what makes these overlap
            // at the claim. The HTTP version of this test caught the mutation on one run in two.
            var redemptions = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var confirmation = scope.ServiceProvider.GetRequiredService<ICustomerEmailConfirmation>();
                await gate.Task;
                return await confirmation.ConfirmAsync(token!, Token);
            })).ToList();

            gate.SetResult();
            var outcomes = await Task.WhenAll(redemptions);

            Assert.Single(outcomes, outcome => outcome is CustomerEmailConfirmationResult.Confirmed);
        }
    }

    [Fact]
    public async Task A_reset_email_loses_its_body_when_sent_and_the_sent_link_still_works()
    {
        await ReadyAsync();

        var transport = new CapturingTransport();

        await using var factory = CreateHost(configureServices: services =>
        {
            services.RemoveAll<IEmailTransport>();
            services.AddScoped<IEmailTransport>(_ => transport);
        });

        using var client = CreateClient(factory);
        var email = UniqueEmail("g03-e2e");
        await RegisterAsync(client, email);

        using (var forgot = await client.PostAsJsonAsync("/api/account/forgot-password", new { email }, Token))
        {
            Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        }

        var token = await ReadLinkTokenAsync(factory, Templates.PasswordReset, email);
        Assert.NotNull(token);

        var sender = factory.Services.GetServices<IHostedService>().OfType<SendEmailsService>().Single();

        // Swept until the queue is empty rather than once: one sweep claims a batch, and the order the
        // three messages are claimed in is not this test's business.
        for (var sweep = 0; sweep < 3; sweep++)
        {
            await sender.SweepAsync(Token);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var rows = await db.EmailOutbox
                .AsNoTracking()
                .Where(message => message.ToEmail == email)
                .Select(message => new { message.TemplateCode, message.Status, message.BodyHtml, message.BodyText })
                .ToListAsync(Token);

            var reset = Assert.Single(rows, message => message.TemplateCode == Templates.PasswordReset);
            Assert.Equal(EmailOutboxStatus.Sent, reset.Status);
            Assert.Null(reset.BodyHtml);
            Assert.Null(reset.BodyText);

            var confirmation = Assert.Single(rows, message => message.TemplateCode == Templates.Confirmation);
            Assert.Equal(EmailOutboxStatus.Sent, confirmation.Status);
            Assert.Null(confirmation.BodyHtml);
            Assert.Null(confirmation.BodyText);

            // The control: a template with no credential in it keeps its body, so the two nulls above are
            // the rule and not every body being lost.
            var welcome = Assert.Single(rows, message => message.TemplateCode == Templates.Welcome);
            Assert.NotNull(welcome.BodyText);
        }

        // The link really went out, and works although the stored copy is gone.
        Assert.Contains(transport.Sent, message => (message.BodyText ?? string.Empty).Contains(Uri.EscapeDataString(token!), StringComparison.Ordinal));

        using var redeemed = await client.PostAsJsonAsync(
            "/api/account/reset-password",
            new { token, password = "Another-Good-Pass-2!" },
            Token);

        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/account/addresses")]
    [InlineData("PUT", "/api/account/profile")]
    [InlineData("POST", "/api/account/change-password")]
    [InlineData("POST", "/api/account/login")]
    [InlineData("POST", "/api/account/register")]
    [InlineData("POST", "/api/account/confirm-email")]
    public async Task A_body_an_html_form_can_send_is_refused(string method, string url)
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var email = UniqueEmail("csrf");
        await RegisterAsync(client, email);
        await SignInAsync(client, email);

        foreach (var type in new[] { "text/plain", "application/x-www-form-urlencoded", "multipart/form-data; boundary=x" })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(url, UriKind.Relative))
            {
                Content = new StringContent("{\"firstName\":\"Changed\"}"),
            };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);

            using var response = await client.SendAsync(request, Token);

            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        }
    }

    private sealed class CapturingTransport : IEmailTransport
    {
        public List<OutboundEmail> Sent { get; } = [];

        public bool IsConfigured => true;

        public Task SendAsync(OutboundEmail email, CancellationToken cancellationToken)
        {
            Sent.Add(email);
            return Task.CompletedTask;
        }
    }
}
